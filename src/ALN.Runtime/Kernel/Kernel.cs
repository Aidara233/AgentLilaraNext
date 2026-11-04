using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Drives modules through the five phases of docs/22 §4: Scan → Initialize (barrier) → Wire → Ready (SCC order)
/// → Stop / Abort.
///
/// What this class refuses to do is as much a part of it as what it does:
/// <list type="bullet">
/// <item>It never parses an id for meaning, so a module called <c>foo</c> is as good as one called <c>channel.x#y</c>.</item>
/// <item>It never lets one module's failure cancel the startup of another — that is why every phase collects
/// results instead of short-circuiting.</item>
/// <item>It never retries. A phase runs once, and what it produced is what the receipt says.</item>
/// <item>It never guesses an order it cannot derive: when modules form a cycle it runs them together and reports
/// the cycle, because a serial order there is a deadlock with extra steps.</item>
/// </list>
/// </summary>
public sealed class Kernel
{
    private readonly ModuleManifestStore _store;
    private readonly IModuleFactoryRegistry _types;
    private readonly ISignalLog _signals;
    private readonly TimeSpan _stopTimeout;
    private readonly Dictionary<ModuleId, KernelNode> _nodes = new();
    private readonly Dictionary<ModuleId, KernelNode> _declared = new();
    private readonly object _gate = new();
    private IReadOnlyList<ModuleIssue> _issues = [];

    public Kernel(string dataRoot, IModuleFactoryRegistry types, ISignalLog? signals = null, TimeSpan? stopTimeout = null)
        : this(new ModuleManifestStore(dataRoot), types, signals, stopTimeout)
    {
    }

    public Kernel(ModuleManifestStore store, IModuleFactoryRegistry types, ISignalLog? signals = null, TimeSpan? stopTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _signals = signals ?? NullSignalLog.Instance;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Every node the last start produced, in receipt form. Asking twice does not re-run anything.</summary>
    public IReadOnlyList<ModuleNodeInfo> Nodes
    {
        get { lock (_gate) return _nodes.Values.Select(node => node.Info()).ToArray(); }
    }

    /// <summary>The module behind an id, for callers that already know which one they mean (docs/22 §3.5).</summary>
    public IKernelModule? Find(ModuleId id)
    {
        lock (_gate) return _nodes.TryGetValue(id, out var node) ? node.Module : null;
    }

    /// <summary>By type or category: the "who can do this" question, which an id cannot answer (docs/22 §3.5).</summary>
    public IReadOnlyList<IKernelModule> OfType(string typeId)
    {
        lock (_gate) return _nodes.Values.Where(node => node.Manifest.Type == typeId).Select(node => node.Module).ToArray();
    }

    /// <summary>The one module of a type that is declared a singleton. Two answers is an error, not a coin toss.</summary>
    public Result<IKernelModule> RequireOfType(string typeId)
    {
        var found = OfType(typeId);
        return found.Count switch
        {
            1 => Result<IKernelModule>.Success(found[0]),
            0 => Result<IKernelModule>.Failure(new(ErrorCode.Unavailable, $"No module implements \"{typeId}\".")),
            _ => Result<IKernelModule>.Failure(new(ErrorCode.Conflict,
                $"More than one module implements \"{typeId}\": {string.Join(", ", found.Select(module => module.Id.Value))}."))
        };
    }

    /// <summary>By id, when the caller already knows which module it means. Absence is an error, not a null.</summary>
    public Result<IKernelModule> Require(ModuleId id)
    {
        var found = Find(id);
        return found is null
            ? Result<IKernelModule>.Failure(new(ErrorCode.Invalid, $"No module answers to \"{id}\"."))
            : Result<IKernelModule>.Success(found);
    }

    /// <summary>
    /// Adds a node the host built itself, before startup. This is how the core's own managers reach the graph
    /// while their manifests are still code rather than files: the moment a module has a directory, it comes from
    /// <see cref="ModuleManifestStore"/> instead, and the two must not both claim the same id.
    /// </summary>
    public Result Declare(IKernelModule module, IReadOnlyList<ModuleSlave>? slaves = null,
        IReadOnlyList<ModuleId>? dependencies = null, string? typeId = null)
    {
        if (module is null) return Result.Failure(new(ErrorCode.Invalid, "A declared module cannot be null."));
        lock (_gate)
        {
            if (_declared.ContainsKey(module.Id))
                return Result.Failure(new(ErrorCode.Conflict, $"Module is already declared: {module.Id}"));
            var manifest = new ModuleManifest(module.Id, typeId ?? module.Id.Value, true, null, slaves ?? [], dependencies ?? []);
            _declared[module.Id] = new KernelNode(manifest, module);
        }
        return Result.Success();
    }

    public async ValueTask<Result<ModuleReceipt>> StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { _nodes.Clear(); _issues = []; }
        foreach (var declared in Declared()) lock (_gate) _nodes[declared.Manifest.Id] = declared;

        // 0 — Scan: everything the disk says, plus one line per directory that is not a module.
        var scan = await _store.ScanAsync(_types, cancellationToken).ConfigureAwait(false);
        var issues = new List<ModuleIssue>(scan.Issues);

        foreach (var manifest in scan.Manifests)
        {
            if (_nodes.ContainsKey(manifest.Id))
            {
                // Declared in code and on disk: the code wins, and the collision is reported. Two nodes with one
                // name is the shape that makes "which one did I just stop" unanswerable.
                issues.Add(new ModuleIssue(manifest.Id.Value, $"Declared by the host and also present on disk: {manifest.Id}."));
                continue;
            }
            var created = _types.Create(manifest);
            if (!created.Ok || created.Value is null)
            {
                issues.Add(new ModuleIssue(manifest.Id.Value, created.Error?.Message ?? "Module could not be created."));
                continue;
            }
            lock (_gate) _nodes[manifest.Id] = new KernelNode(manifest, created.Value);
        }

        // 1 — Initialize: everything at once, and nobody moves on until everyone is done. Not "done successfully":
        // a module that failed still counts as done, because a barrier that waits only for success is not a barrier.
        var initialized = new List<KernelNode>();
        var pending = new List<Task>();
        foreach (var node in Snapshot())
        {
            if (!node.Manifest.Enabled)
            {
                node.Phase = ModulePhase.Disabled;
                continue;
            }
            pending.Add(InitializeAsync(node, cancellationToken));
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
        foreach (var node in Snapshot().Where(node => node.Phase == ModulePhase.Initialized)) initialized.Add(node);

        // 2 — Wire: edges now have objects on both ends, so "does it exist" and "does it take slaves" have answers.
        var badEdges = new List<BadEdge>();
        var wired = 0;
        foreach (var node in initialized.Where(node => node.Manifest.Slaves is { Count: > 0 }))
        {
            if (node.Module is not IModuleWiring master)
            {
                foreach (var slave in node.Manifest.Slaves!)
                    badEdges.Add(new(node.Manifest.Id, slave.Id, BadEdgeReason.MasterDoesNotWire,
                        $"{node.Manifest.Id} declares slaves but does not implement {nameof(IModuleWiring)}."));
                continue;
            }
            foreach (var slave in node.Manifest.Slaves!)
            {
                var resolved = Resolve(slave.Id, badEdges, node.Manifest.Id);
                if (resolved is null) continue;
                Result accepted;
                try { accepted = master.WireSlave(slave.Id, slave.Kinds, resolved.Module); }
                catch (Exception ex) { accepted = Result.Failure(new(ErrorCode.Internal, "Wire threw.", ex.Message)); }
                if (!accepted.Ok)
                {
                    badEdges.Add(new(node.Manifest.Id, slave.Id, BadEdgeReason.RejectedByMaster,
                        accepted.Error?.Message ?? "Rejected."));
                    continue;
                }
                wired++;
            }
        }

        // 3 — Ready: dependencies first, and modules that depend on each other at the same time.
        var components = Components(initialized);
        var cycles = components.Where(component => component.Count > 1)
            .Select(component => new CycleInfo(component.Select(node => node.Manifest.Id).ToArray()))
            .ToArray();
        var unfinishable = new HashSet<ModuleId>();
        foreach (var component in InDependencyOrder(components))
        {
            var blocked = component
                .SelectMany(node => DependenciesOf(node))
                .FirstOrDefault(dependency => unfinishable.Contains(dependency));
            if (blocked != default)
            {
                foreach (var node in component)
                {
                    node.Phase = ModulePhase.Failed;
                    node.Failure = $"Not started: {blocked} did not become ready.";
                    unfinishable.Add(node.Manifest.Id);
                }
                continue;
            }

            var ready = new List<Task>();
            foreach (var node in component) ready.Add(ReadyAsync(node, cancellationToken));
            await Task.WhenAll(ready).ConfigureAwait(false);
            foreach (var node in component.Where(node => node.Phase != ModulePhase.Ready)) unfinishable.Add(node.Manifest.Id);
        }

        lock (_gate) _issues = issues;
        return Result<ModuleReceipt>.Success(new(Snapshot().Select(node => node.Info()).ToArray(), wired, badEdges, cycles, issues));
    }

    /// <summary>
    /// Orderly stop, users before what they use: reverse of the ready order (docs/22 §4). Every module gets its
    /// own timeout, so one module that will not stop cannot keep the rest of the process alive.
    /// </summary>
    public async ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        var components = Components(Snapshot().Where(node => node.Phase == ModulePhase.Ready));
        foreach (var component in Enumerable.Reverse(InDependencyOrder(components)))
        {
            await Task.WhenAll(component.Select(node => StopOneAsync(node, cancellationToken))).ConfigureAwait(false);
        }
        return Result.Success();
    }

    /// <summary>
    /// Stop now: no order, and no waiting (docs/22 §4). The only promise is that every module was told.
    /// </summary>
    public ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        foreach (var node in Snapshot().Where(node => node.Phase is not (ModulePhase.Stopped or ModulePhase.Disabled)))
        {
            // Deliberately not awaited: abort is what happens when waiting is no longer affordable.
            _ = AbortOneAsync(node, cancellationToken);
        }
        return ValueTask.CompletedTask;
    }

    private async Task InitializeAsync(KernelNode node, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(node, nameof(IKernelModule.InitializeAsync),
            token => node.Module.InitializeAsync(token), cancellationToken).ConfigureAwait(false);
        node.Phase = result.Ok ? ModulePhase.Initialized : ModulePhase.Failed;
        node.Failure = result.Ok ? null : Describe(result);
    }

    private async Task ReadyAsync(KernelNode node, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(node, nameof(IKernelModule.ReadyAsync),
            token => node.Module.ReadyAsync(token), cancellationToken).ConfigureAwait(false);
        node.Phase = result.Ok ? ModulePhase.Ready : ModulePhase.Failed;
        node.Failure = result.Ok ? null : Describe(result);
    }

    private async Task StopOneAsync(KernelNode node, CancellationToken cancellationToken)
    {
        node.Phase = ModulePhase.Stopping;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_stopTimeout);
        var result = await InvokeAsync(node, nameof(IKernelModule.StopAsync),
            token => node.Module.StopAsync(token), timeout.Token).ConfigureAwait(false);
        node.Phase = result.Ok ? ModulePhase.Stopped : ModulePhase.Failed;
        node.Failure = result.Ok ? null : Describe(result);
    }

    private async Task AbortOneAsync(KernelNode node, CancellationToken cancellationToken)
    {
        node.Phase = ModulePhase.Stopping;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_stopTimeout);
            await node.Module.AbortAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing is waiting for this call, so there is nobody to hand the exception to. Recording it is the
            // whole of what abort can offer.
            Record(node, nameof(IKernelModule.AbortAsync), ex.Message, ex);
        }
        node.Phase = ModulePhase.Stopped;
    }

    /// <summary>
    /// Calls one phase method and hands whatever it throws to the module's own error hook. If the hook fails too,
    /// the failure is recorded and swallowed: by then there is nobody left to tell (docs/22 §6).
    /// </summary>
    private async ValueTask<Result> InvokeAsync(KernelNode node, string method,
        Func<CancellationToken, ValueTask<Result>> call, CancellationToken cancellationToken)
    {
        try
        {
            var result = await call(cancellationToken).ConfigureAwait(false);
            if (!result.Ok) await ReportErrorAsync(node, new(method, result.Error?.Message ?? "Failed."), CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            node.Phase = ModulePhase.Failed;
            node.Failure = $"{method} was cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(node, new(method, ex.Message, ex), CancellationToken.None).ConfigureAwait(false);
            return Result.Failure(new(ErrorCode.Internal, $"{method} threw: {ex.Message}", ex.Message));
        }
    }

    private async ValueTask ReportErrorAsync(KernelNode node, ModuleError error, CancellationToken cancellationToken)
    {
        Record(node, error.Method, error.Message, error.Exception);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_stopTimeout);
            var handled = await node.Module.OnErrorAsync(error, timeout.Token).ConfigureAwait(false);
            if (!handled.Ok) Record(node, nameof(IKernelModule.OnErrorAsync), handled.Error?.Message ?? "Error hook failed.", null);
        }
        catch (Exception ex)
        {
            // The hook threw or hung. One record, then nothing: retrying an error path is how a diagnostic failure
            // turns into a loop that nobody can stop.
            Record(node, nameof(IKernelModule.OnErrorAsync), ex.Message, ex);
        }
    }

    private void Record(KernelNode node, string method, string message, Exception? exception)
    {
        node.Phase = node.Phase == ModulePhase.Ready ? ModulePhase.Error : ModulePhase.Failed;
        node.Failure ??= $"{method}: {message}";
        _signals.Write(new()
        {
            Id = Guid.NewGuid(),
            ParentId = SignalScope.CurrentId,
            Group = "kernel",
            Name = "module",
            From = node.Manifest.Id.Value,
            To = "kernel",
            Mechanism = $"kernel.{method}",
            Outcome = "failed",
            Level = SignalLevel.Error,
            Detail = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = node.Manifest.Type,
                ["error"] = message,
                ["exception"] = exception?.GetType().Name ?? ""
            }
        });
    }

    private KernelNode? Resolve(ModuleId id, List<BadEdge> badEdges, ModuleId master)
    {
        KernelNode? node;
        lock (_gate) _nodes.TryGetValue(id, out node);
        if (node is null)
        {
            badEdges.Add(new(master, id, BadEdgeReason.MissingModule, $"No module answers to \"{id}\"."));
            return null;
        }
        if (!node.Manifest.Enabled || node.Phase == ModulePhase.Disabled)
        {
            badEdges.Add(new(master, id, BadEdgeReason.DisabledModule, $"\"{id}\" is disabled."));
            return null;
        }
        if (node.Phase is not (ModulePhase.Initialized or ModulePhase.Ready))
        {
            badEdges.Add(new(master, id, BadEdgeReason.MasterFailed, $"\"{id}\" did not initialize: {node.Failure}"));
            return null;
        }
        return node;
    }

    private IEnumerable<ModuleId> DependenciesOf(KernelNode node)
    {
        foreach (var dependency in node.Manifest.Dependencies ?? []) yield return dependency;
        foreach (var slave in node.Manifest.Slaves ?? []) if (slave.RequireAll) yield return slave.Id;
    }

    /// <summary>Strongly connected components (Tarjan), so that a cycle is one runnable unit instead of a deadlock.</summary>
    private List<List<KernelNode>> Components(IEnumerable<KernelNode> nodes)
    {
        var all = nodes.ToArray();
        var index = new Dictionary<ModuleId, int>();
        var low = new Dictionary<ModuleId, int>();
        var onStack = new HashSet<ModuleId>();
        var stack = new Stack<KernelNode>();
        var components = new List<List<KernelNode>>();
        var counter = 0;

        IEnumerable<KernelNode> Next(KernelNode node) =>
            DependenciesOf(node).Select(id => all.FirstOrDefault(candidate => candidate.Manifest.Id == id)).OfType<KernelNode>();

        void Visit(KernelNode node)
        {
            index[node.Manifest.Id] = counter;
            low[node.Manifest.Id] = counter;
            counter++;
            stack.Push(node);
            onStack.Add(node.Manifest.Id);

            foreach (var next in Next(node))
            {
                if (!index.ContainsKey(next.Manifest.Id))
                {
                    Visit(next);
                    low[node.Manifest.Id] = Math.Min(low[node.Manifest.Id], low[next.Manifest.Id]);
                }
                else if (onStack.Contains(next.Manifest.Id))
                {
                    low[node.Manifest.Id] = Math.Min(low[node.Manifest.Id], index[next.Manifest.Id]);
                }
            }

            if (low[node.Manifest.Id] != index[node.Manifest.Id]) return;
            var component = new List<KernelNode>();
            KernelNode member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member.Manifest.Id);
                component.Add(member);
            } while (member.Manifest.Id != node.Manifest.Id);
            components.Add(component);
        }

        foreach (var node in all.Where(node => !index.ContainsKey(node.Manifest.Id))) Visit(node);
        return components;
    }

    /// <summary>
    /// Orders components so that whatever a component depends on comes first. On the condensed graph this is a
    /// plain topological sort, which exists by construction: SCCs of a directed graph cannot form a cycle.
    /// </summary>
    private List<List<KernelNode>> InDependencyOrder(List<List<KernelNode>> components)
    {
        var owner = new Dictionary<ModuleId, List<KernelNode>>();
        foreach (var component in components)
            foreach (var node in component) owner[node.Manifest.Id] = component;

        var ordered = new List<List<KernelNode>>(components.Count);
        var done = new HashSet<List<KernelNode>>();
        var remaining = new List<List<KernelNode>>(components);
        while (remaining.Count > 0)
        {
            var progressed = false;
            foreach (var component in remaining.ToArray())
            {
                var waitsFor = component
                    .SelectMany(DependenciesOf)
                    .Select(id => owner.TryGetValue(id, out var ownerComponent) ? ownerComponent : null)
                    .OfType<List<KernelNode>>()
                    .Where(other => !ReferenceEquals(other, component))
                    .Distinct()
                    .ToArray();
                if (waitsFor.Any(other => !done.Contains(other))) continue;
                ordered.Add(component);
                done.Add(component);
                remaining.Remove(component);
                progressed = true;
            }
            // Condensation is acyclic, so this can only trigger if the caller handed in something that is not the
            // output of Components(). Fail loudly instead of spinning.
            if (!progressed) throw new InvalidOperationException("Dependency order could not be resolved.");
        }
        return ordered;
    }

    /// <summary>
    /// Adds a node while the graph is running (docs/22, D1): initialize it, wire the slaves it declares, then let it
    /// start — but only if what it says it needs is already ready.
    ///
    /// This is not the startup path with fewer phases. Startup waits for everyone because nobody can be trusted
    /// before everyone has built themselves; a node that arrives later finds the graph already established, so the
    /// only thing it may assume is that whatever is ready now is ready.
    /// </summary>
    public async ValueTask<Result<ModuleAddition>> AddAsync(IKernelModule module, IReadOnlyList<ModuleSlave>? slaves = null,
        IReadOnlyList<ModuleId>? dependencies = null, string? typeId = null, CancellationToken cancellationToken = default)
    {
        if (module is null) return Result<ModuleAddition>.Failure(new(ErrorCode.Invalid, "A module cannot be null."));
        KernelNode node;
        lock (_gate)
        {
            if (_nodes.ContainsKey(module.Id))
                return Result<ModuleAddition>.Failure(new(ErrorCode.Conflict, $"A module already answers to \"{module.Id}\"."));
            node = new KernelNode(new ModuleManifest(module.Id, typeId ?? module.Id.Value, true, null,
                slaves ?? [], dependencies ?? []), module);
            _nodes[module.Id] = node;
        }

        await InitializeAsync(node, cancellationToken).ConfigureAwait(false);
        if (node.Phase != ModulePhase.Initialized)
            return Result<ModuleAddition>.Success(new(node.Info(), []));

        var badEdges = new List<BadEdge>();
        var wired = 0;
        if (node.Manifest.Slaves is { Count: > 0 })
        {
            if (module is IModuleWiring master)
            {
                foreach (var slave in node.Manifest.Slaves!)
                {
                    var resolved = Resolve(slave.Id, badEdges, node.Manifest.Id);
                    if (resolved is null) continue;
                    Result accepted;
                    try { accepted = master.WireSlave(slave.Id, slave.Kinds, resolved.Module); }
                    catch (Exception ex) { accepted = Result.Failure(new(ErrorCode.Internal, "Wire threw.", ex.Message)); }
                    if (!accepted.Ok)
                    {
                        badEdges.Add(new(node.Manifest.Id, slave.Id, BadEdgeReason.RejectedByMaster,
                            accepted.Error?.Message ?? "Rejected."));
                        continue;
                    }
                    wired++;
                }
            }
            else
            {
                foreach (var slave in node.Manifest.Slaves!)
                    badEdges.Add(new(node.Manifest.Id, slave.Id, BadEdgeReason.MasterDoesNotWire,
                        $"{node.Manifest.Id} declares slaves but does not implement {nameof(IModuleWiring)}."));
            }
        }

        // What it declared it needs has to be ready *now*: there is no later wave to wait for. Two different
        // verdicts, because they are two different mistakes: a named dependency that does not exist is a broken
        // configuration, while a slave that is not there is a dropped edge (reported above, and the node may still
        // work without it).
        var missingDependency = (node.Manifest.Dependencies ?? [])
            .FirstOrDefault(id => Snapshot().All(candidate => candidate.Manifest.Id != id));
        if (missingDependency != default)
        {
            node.Phase = ModulePhase.Failed;
            node.Failure = $"Not started: declared dependency \"{missingDependency}\" does not exist.";
            return Result<ModuleAddition>.Success(new(node.Info(), badEdges, wired));
        }
        var unready = DependenciesOf(node)
            .Select(id => Snapshot().FirstOrDefault(candidate => candidate.Manifest.Id == id))
            .FirstOrDefault(candidate => candidate is not null && candidate.Phase != ModulePhase.Ready);
        if (unready is not null)
        {
            node.Phase = ModulePhase.Failed;
            node.Failure = $"Not started: {unready.Manifest.Id} is {unready.Phase}.";
            return Result<ModuleAddition>.Success(new(node.Info(), badEdges, wired));
        }

        await ReadyAsync(node, cancellationToken).ConfigureAwait(false);
        return Result<ModuleAddition>.Success(new(node.Info(), badEdges, wired));
    }

    /// <summary>
    /// Stops a node and takes it out of the graph, reporting every edge that pointed at it (docs/22, D1).
    ///
    /// The edges are reported rather than repaired: a master that still lists this id in its manifest has a file
    /// that no longer matches the graph, and rewriting somebody else's file is not this method's business.
    /// </summary>
    public async ValueTask<Result<IReadOnlyList<BadEdge>>> RemoveAsync(ModuleId id, CancellationToken cancellationToken)
    {
        KernelNode? node;
        lock (_gate) _nodes.TryGetValue(id, out node);
        if (node is null) return Result<IReadOnlyList<BadEdge>>.Failure(new(ErrorCode.Invalid, $"No module answers to \"{id}\"."));

        await StopOneAsync(node, cancellationToken).ConfigureAwait(false);

        var dropped = new List<BadEdge>();
        lock (_gate)
        {
            _nodes.Remove(id);
            _declared.Remove(id);
            foreach (var other in _nodes.Values)
                foreach (var slave in other.Manifest.Slaves ?? [])
                    if (slave.Id == id)
                        dropped.Add(new(other.Manifest.Id, id, BadEdgeReason.MissingModule, "Removed while still declared as a slave."));
        }
        return Result<IReadOnlyList<BadEdge>>.Success(dropped);
    }

    private KernelNode[] Snapshot()
    {
        lock (_gate) return _nodes.Values.ToArray();
    }

    private KernelNode[] Declared()
    {
        lock (_gate) return _declared.Values.ToArray();
    }

    private static string? Describe(Result result) =>
        result.Ok ? null : result.Error?.Detail is { } detail ? $"{result.Error.Message} {detail}" : result.Error?.Message;

    private sealed class KernelNode(ModuleManifest manifest, IKernelModule module)
    {
        public ModuleManifest Manifest { get; } = manifest;
        public IKernelModule Module { get; } = module;
        public ModulePhase Phase { get; set; } = ModulePhase.Loaded;
        public string? Failure { get; set; }

        public ModuleNodeInfo Info() => new(Manifest.Id, Manifest.Type, Phase, Failure);
    }
}
