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
    private readonly ModuleDataRoot _data;
    private readonly Func<string, bool>? _hostOwnsTypes;
    private readonly Dictionary<ModuleId, KernelNode> _nodes = new();
    private readonly Dictionary<ModuleId, KernelNode> _declared = new();
    private readonly object _gate = new();
    private IReadOnlyList<ModuleIssue> _issues = [];
    // Edges made after startup. The receipt counts them, because "nine edges" that silently becomes eleven is a
    // number a reader would trust and should not.
    private int _runtimeEdges;
    // Who is on a schedule right now: sink → the sources that accepted it. Recorded because the graph cannot draw
    // a trigger as an edge (docs/22 §1.4) but a reader still has to be able to see who is pushing whom.
    private readonly Dictionary<ModuleId, List<(ModuleId Id, string Name)>> _rhythms = new();
    // Edges that never became calls, kept after the receipt was handed out. A bad edge that only exists in one
    // return value is invisible to anybody asking the process how it is doing ten minutes later (docs/22 §5.2).
    private IReadOnlyList<BadEdge> _badEdges = [];
    // Where the phases are. "Before startup" and "inside startup" are different answers for an announcement: before it
    // the graph does not exist and the instance is a declaration; inside it the graph exists and the instance has to
    // walk its own phases now — filing it as a declaration there is how a node ends up in no phase at all.
    private KernelRunState _state;

    public Kernel(string dataRoot, IModuleFactoryRegistry types, ISignalLog? signals = null, TimeSpan? stopTimeout = null,
        Func<string, bool>? hostOwnsTypes = null)
        : this(new ModuleManifestStore(dataRoot), types, signals, stopTimeout, hostOwnsTypes)
    {
    }

    public Kernel(ModuleManifestStore store, IModuleFactoryRegistry types, ISignalLog? signals = null, TimeSpan? stopTimeout = null,
        Func<string, bool>? hostOwnsTypes = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _signals = signals ?? NullSignalLog.Instance;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        _hostOwnsTypes = hostOwnsTypes;
        _data = new ModuleDataRoot(store.Root);
    }

    /// <summary>Every node the last start produced, in receipt form. Asking twice does not re-run anything.</summary>
    public IReadOnlyList<ModuleNodeInfo> Nodes
    {
        get { lock (_gate) return _nodes.Values.Select(node => node.Info()).ToArray(); }
    }

    /// <summary>
    /// The same numbers the receipt carries, read now instead of at startup (docs/22 §5.2): how many nodes there
    /// are, how many work, how much is known to be wrong, and who is on a schedule.
    /// </summary>
    public ModuleGraphStatus Status
    {
        get
        {
            var rhythms = Rhythms;
            lock (_gate)
            {
                return new(_nodes.Count,
                    _nodes.Values.Count(node => node.Phase == ModulePhase.Ready),
                    _badEdges.Count + _issues.Count +
                    _nodes.Values.Count(node => node.Phase is ModulePhase.Failed or ModulePhase.Error),
                    rhythms,
                    _badEdges);
            }
        }
    }

    /// <summary>
    /// Who is being pushed on a schedule, and by what (docs/22 §1.4).
    ///
    /// Read from the kernel's own record of what it managed to attach, not from the sources: a source that refused
    /// a sink is exactly the case worth reporting, and asking the source would report the opposite ("I serve
    /// nobody") in a shape indistinguishable from "nobody asked". The interval is read live from the sink, because
    /// that is the declaration the schedule was built from and a module may change it.
    /// </summary>
    public IReadOnlyList<RhythmInfo> Rhythms
    {
        get
        {
            lock (_gate)
            {
                var rhythms = new List<RhythmInfo>();
                foreach (var (sink, sources) in _rhythms)
                {
                    // Only while it is actually working: a stopped sink is still in the graph, and "on a schedule"
                    // about a module that is not running would be the receipt inventing a fact.
                    if (!_nodes.TryGetValue(sink, out var node) || node.Phase != ModulePhase.Ready) continue;
                    if (SinkOf(node)?.PreferredInterval is not { } interval) continue;
                    foreach (var source in sources) rhythms.Add(new(sink, interval, source.Name));
                }
                return rhythms;
            }
        }
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
        lock (_gate) { _nodes.Clear(); _issues = []; _runtimeEdges = 0; _rhythms.Clear(); _badEdges = []; _state = KernelRunState.Starting; }
        foreach (var declared in Declared()) lock (_gate) _nodes[declared.Manifest.Id] = declared;

        // 0 — Scan: everything the disk says, plus one line per directory that is not a module.
        var scan = await _store.ScanAsync(_types, _hostOwnsTypes, cancellationToken).ConfigureAwait(false);
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
            if (WiringOf(node) is not { } master)
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

        await AttachTriggersAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) { _issues = issues; _badEdges = badEdges.ToArray(); _state = KernelRunState.Running; }
        return Result<ModuleReceipt>.Success(new(Snapshot().Select(node => node.Info()).ToArray(), wired + _runtimeEdges,
            badEdges, cycles, issues, Rhythms));
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

    /// <summary>Keeps the bad edges an operation produced, so a later question sees them too.</summary>
    private void KeepBadEdges(IEnumerable<BadEdge> edges)
    {
        var extra = edges as IReadOnlyList<BadEdge> ?? edges.ToArray();
        if (extra.Count == 0) return;
        lock (_gate) _badEdges = [.. _badEdges, .. extra];
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
            if (WiringOf(node) is { } master)
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
        KeepBadEdges(badEdges);
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
        await AttachTriggersAsync(cancellationToken).ConfigureAwait(false);
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
        DetachTriggers(id);

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
        KeepBadEdges(dropped);
        return Result<IReadOnlyList<BadEdge>>.Success(dropped);
    }

    /// <summary>
    /// Attaches an instance to the manager that owns it: the manager becomes the master, the instance becomes a
    /// slave, and the edge is the same edge the graph already knows how to draw (docs/22 §1.2, docs/23 §4 step 1).
    ///
    /// The instance takes no lifecycle work here: its manager creates, starts and stops it, and always has. What
    /// this adds is that the relation becomes <b>visible</b> — before it, the only place "this manager holds this
    /// instance" existed was a dictionary inside the manager, which nobody outside could read.
    /// </summary>
    public async ValueTask<Result<ModuleAddition>> AdoptAsync(ModuleId masterId, IKernelModule instance,
        IReadOnlyList<string>? kinds = null, string? typeId = null, CancellationToken cancellationToken = default)
    {
        if (instance is null) return Result<ModuleAddition>.Failure(new(ErrorCode.Invalid, "An instance cannot be null."));
        var edge = new ModuleSlave(instance.Id, kinds ?? [], RequireAll: false);
        KernelNode master;
        KernelNode node;
        bool alreadyThere;
        lock (_gate)
        {
            // Before startup the master may only be *declared*: the graph does not exist yet, and refusing here
            // would be answering a question startup has not asked. A provider built from its file belongs to this
            // case — it is held before anything runs.
            if (!_nodes.TryGetValue(masterId, out master!) && !_declared.TryGetValue(masterId, out master!))
                return Result<ModuleAddition>.Failure(new(ErrorCode.Invalid, $"No module answers to \"{masterId}\"."));

            // C-16: an instance may have more than one master, so a second one is not a conflict — holding the same
            // object twice is what "two masters" means. Two *different* objects under one id still is (C-14).
            var same = _nodes.TryGetValue(instance.Id, out var live) ? live
                : _declared.TryGetValue(instance.Id, out var declared) ? declared : null;
            if (same is not null && !ReferenceEquals(same.Module, instance))
                return Result<ModuleAddition>.Failure(new(ErrorCode.Conflict, $"A module already answers to \"{instance.Id}\"."));

            if (same is not null)
            {
                node = same;
                alreadyThere = true;
            }
            else
            {
                // The edge lives on the master, and only there: the master is what holds the instance and what has to
                // accept it during Wire. Writing the master into the instance's own slave list would make the instance a
                // master too — the kernel would then ask it to wire its "slave", and report the refusal as a bad edge.
                node = new KernelNode(new ModuleManifest(instance.Id, typeId ?? instance.Id.Value), instance);
                alreadyThere = false;
                if (_state == KernelRunState.BeforeStart)
                {
                    // Nothing to phase through yet: this is a declaration that happens to come from a manager. Startup
                    // will initialize, wire and start it like any other, so the receipt counts the edge and the master
                    // still has to accept it.
                    _declared[instance.Id] = node;
                    master.AdoptSlave(edge);
                    return Result<ModuleAddition>.Success(new(node.Info(), []));
                }
                _nodes[instance.Id] = node;
            }
        }

        if (alreadyThere)
        {
            // The instance is already in the graph: its phases are the core's business and are not re-run. All this
            // master gets is an edge, and the edge is wired the same way it would be at startup — asked, refused,
            // refused as a bad edge.
            if (node.Phase is not (ModulePhase.Initialized or ModulePhase.Ready))
            {
                // Still starting: the Wire pass will reach this edge because it is on the master's list now.
                lock (_gate) master.AdoptSlave(edge);
                return Result<ModuleAddition>.Success(new(node.Info(), []));
            }
            if (WiringOf(master) is not { } second)
            {
                var dropped = new BadEdge(masterId, instance.Id, BadEdgeReason.MasterDoesNotWire,
                    $"{masterId} declares slaves but does not implement {nameof(IModuleWiring)}.");
                KeepBadEdges([dropped]);
                return Result<ModuleAddition>.Success(new(node.Info(), [dropped]));
            }
            Result refused;
            try { refused = second.WireSlave(instance.Id, kinds ?? [], instance); }
            catch (Exception ex) { refused = Result.Failure(new(ErrorCode.Internal, "Wire threw.", ex.Message)); }
            if (!refused.Ok)
            {
                var bad = new BadEdge(masterId, instance.Id, BadEdgeReason.RejectedByMaster, refused.Error?.Message ?? "Rejected.");
                KeepBadEdges([bad]);
                return Result<ModuleAddition>.Success(new(node.Info(), [bad]));
            }
            lock (_gate) master.AdoptSlave(edge);
            return Result<ModuleAddition>.Success(new(node.Info(), [], 1));
        }

        if (WiringOf(master) is not { } wiring)
            return Result<ModuleAddition>.Failure(new(ErrorCode.Invalid, $"{masterId} does not hold instances."));

        await InitializeAsync(node, cancellationToken).ConfigureAwait(false);
        if (node.Phase != ModulePhase.Initialized)
            return Result<ModuleAddition>.Success(new(node.Info(), []));

        Result accepted;
        try { accepted = wiring.WireSlave(instance.Id, kinds ?? [], instance); }
        catch (Exception ex) { accepted = Result.Failure(new(ErrorCode.Internal, "Wire threw.", ex.Message)); }
        if (!accepted.Ok)
        {
            var bad = new BadEdge(masterId, instance.Id, BadEdgeReason.RejectedByMaster, accepted.Error?.Message ?? "Rejected.");
            KeepBadEdges([bad]);
            lock (_gate) _nodes.Remove(instance.Id);
            return Result<ModuleAddition>.Success(new(node.Info(), [bad]));
        }

        // The edge is recorded on the master too, so the next receipt shows it as a declared slave rather than as
        // something that only exists in this method's local variable.
        lock (_gate)
        {
            master.AdoptSlave(edge);
            _runtimeEdges++;
        }

        await ReadyAsync(node, cancellationToken).ConfigureAwait(false);
        await AttachTriggersAsync(cancellationToken).ConfigureAwait(false);
        return Result<ModuleAddition>.Success(new(node.Info(), [], 1));
    }

    /// <summary>
    /// Takes an instance out of the graph: stopped through its own hook, then gone, and the master stops listing it.
    /// The instance's data is not touched here — that is the manager's call, because only it knows whether this
    /// instance is going to the bin or coming back.
    /// </summary>
    public async ValueTask<Result> ReleaseAsync(ModuleId masterId, ModuleId instanceId, CancellationToken cancellationToken)
    {
        var removed = await RemoveAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!removed.Ok) return Result.Failure(removed.Error!);
        lock (_gate)
            if (_nodes.TryGetValue(masterId, out var master)) master.ReleaseSlave(instanceId);
        return Result.Success();
    }

    /// <summary>
    /// Loads one module from its own file while the graph is running (docs/22, D2): read the manifest, build the
    /// module, then add it the way a late node is added.
    ///
    /// It reads the file rather than taking a manifest as an argument because the file is the truth about what a
    /// module is; a caller that passed one would be describing a module that may not be the one on disk.
    /// </summary>
    public async ValueTask<Result<ModuleAddition>> LoadAsync(ModuleId id, CancellationToken cancellationToken)
    {
        var scan = await _store.ScanAsync(_types, _hostOwnsTypes, cancellationToken).ConfigureAwait(false);
        var manifest = scan.Manifests.FirstOrDefault(candidate => candidate.Id == id);
        if (manifest is null)
        {
            var issue = scan.Issues.FirstOrDefault(problem => problem.Path.Contains(id.Value, StringComparison.Ordinal));
            return Result<ModuleAddition>.Failure(new(ErrorCode.Invalid,
                $"No readable module answers to \"{id}\".", issue?.Message));
        }
        var created = _types.Create(manifest);
        if (!created.Ok || created.Value is null) return Result<ModuleAddition>.Failure(created.Error!);
        return await AddAsync(created.Value, manifest.Slaves, manifest.Dependencies, manifest.Type, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Unloads a module: edges first (reported), then stop, then optionally the bin (docs/22, D2).
    ///
    /// Archiving is a parameter and not a default, because "I want this module to stop" and "I want this module
    /// gone" are two decisions, and the second one is worth stating out loud.
    /// </summary>
    public async ValueTask<Result<IReadOnlyList<BadEdge>>> UnloadAsync(ModuleId id, bool archive,
        CancellationToken cancellationToken)
    {
        var removed = await RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        if (!removed.Ok) return removed;
        if (!archive) return removed;

        var archived = _data.Archive(id, "unloaded");
        return archived.Ok
            ? removed
            : Result<IReadOnlyList<BadEdge>>.Failure(archived.Error ?? new(ErrorCode.Internal, $"Could not archive {id}."));
    }

    /// <summary>
    /// Hooks every node that wants a rhythm onto every node that can provide one.
    ///
    /// After Ready, because a schedule should start against modules that are working, and again whenever the graph
    /// changes: a sink that arrives later still gets its beat. A source that refuses is recorded, not fatal — the same
    /// rule as a bad edge, for the same reason: a missed beat does not stop the rest of the system.
    /// </summary>
    private async ValueTask AttachTriggersAsync(CancellationToken cancellationToken)
    {
        var nodes = Snapshot();
        var sources = nodes.Where(node => SourceOf(node) is { CanSchedule: true }).ToArray();
        if (sources.Length == 0) return;

        foreach (var node in nodes)
        {
            var sink = SinkOf(node);
            if (sink is null || sink.PreferredInterval is null) continue;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Result attached;
                try { attached = SourceOf(source)!.Attach(node.Manifest.Id, sink); }
                catch (Exception ex) { attached = Result.Failure(new(ErrorCode.Internal, "Attach threw.", ex.Message)); }
                if (attached.Ok)
                {
                    // Recorded only on success: the receipt is the kernel's claim about calls it actually made.
                    lock (_gate)
                    {
                        if (!_rhythms.TryGetValue(node.Manifest.Id, out var beats)) _rhythms[node.Manifest.Id] = beats = [];
                        var beat = (source.Manifest.Id, SourceOf(source)!.Source);
                        if (!beats.Contains(beat)) beats.Add(beat);
                    }
                    continue;
                }
                _signals.Write(new()
                {
                    Id = Guid.NewGuid(),
                    ParentId = SignalScope.CurrentId,
                    Group = "kernel",
                    Name = "trigger",
                    From = node.Manifest.Id.Value,
                    To = source.Manifest.Id.Value,
                    Mechanism = "kernel.trigger.attach",
                    Outcome = "failed",
                    Level = SignalLevel.Warn,
                    Detail = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["error"] = attached.Error?.Message ?? "refused"
                    }
                });
            }
        }
        await ValueTask.CompletedTask;
    }

    /// <summary>Takes a departing node off every schedule. The source keeps working for everybody else.</summary>
    private void DetachTriggers(ModuleId id)
    {
        lock (_gate)
        {
            // Both directions: it may be the sink that left, or the source that all those beats named.
            _rhythms.Remove(id);
            foreach (var beats in _rhythms.Values) beats.RemoveAll(beat => beat.Id == id);
        }
        foreach (var node in Snapshot())
        {
            if (SourceOf(node) is not { } source) continue;
            try { source.Detach(id); } catch { /* a source that cannot forget one sink must not block a removal */ }
        }
    }

    /// <summary>
    /// The node's capability, whether the node implements it itself or only carries the module that does.
    ///
    /// Everything the kernel knows about triggers goes through these two lookups, which is why it has no idea a clock
    /// exists: a schedule source is just a node whose module implements ITriggerSource, and a sink is just a node
    /// whose module (or carried module) implements ITriggerSink.
    /// </summary>
    private static ITriggerSink? SinkOf(KernelNode node) =>
        node.Module as ITriggerSink ?? (node.Module as IModuleCarrier)?.Target as ITriggerSink;

    private static ITriggerSource? SourceOf(KernelNode node) =>
        node.Module as ITriggerSource ?? (node.Module as IModuleCarrier)?.Target as ITriggerSource;

    /// <summary>
    /// The same door for the master side: a node that holds slaves may be the manager itself or a carrier around it
    /// (a manager the host runs outside the phase machine is a <see cref="PassiveNode"/>). One lookup, so a new kind
    /// of wrapper never means a new place for the kernel to forget.
    /// </summary>
    private static IModuleWiring? WiringOf(KernelNode node) =>
        node.Module as IModuleWiring ?? (node.Module as IModuleCarrier)?.Target as IModuleWiring;

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

    /// <summary>Where the phase machine is, which is what decides how an announcement is handled.</summary>
    private enum KernelRunState
    {
        /// <summary>Declarations only: nothing has a phase yet.</summary>
        BeforeStart,
        /// <summary>Inside <see cref="StartAsync"/>: the nodes exist, so an instance walks its own phases now.</summary>
        Starting,
        /// <summary>Startup finished; additions and removals are the only way in.</summary>
        Running
    }

    private sealed class KernelNode(ModuleManifest manifest, IKernelModule module)
    {
        public ModuleManifest Manifest { get; private set; } = manifest;
        public IKernelModule Module { get; } = module;
        public ModulePhase Phase { get; set; } = ModulePhase.Loaded;
        public string? Failure { get; set; }

        /// <summary>
        /// A slave that arrived at runtime. The manifest is the in-memory truth for this process; the file on disk
        /// still shows what was there at startup, which is why the two are read differently: one for the graph now,
        /// one for what somebody wrote down.
        /// </summary>
        public void AdoptSlave(ModuleSlave slave) =>
            Manifest = Manifest with { Slaves = [.. (Manifest.Slaves ?? []), slave] };

        public void ReleaseSlave(ModuleId id) =>
            Manifest = Manifest with { Slaves = (Manifest.Slaves ?? []).Where(slave => slave.Id != id).ToArray() };

        public ModuleNodeInfo Info() => new(Manifest.Id, Manifest.Type, Phase, Failure);
    }
}
