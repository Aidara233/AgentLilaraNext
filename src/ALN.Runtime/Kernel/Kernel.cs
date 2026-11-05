using System.Reflection;
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
    private readonly Dictionary<ModuleId, KernelInstance> _instances = new();
    private readonly Dictionary<ModuleId, KernelInstance> _declared = new();
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
    // walk its own phases now — filing it as a declaration there is how a instance ends up in no phase at all.
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

    /// <summary>Every instance the last start produced, in receipt form. Asking twice does not re-run anything.</summary>
    public IReadOnlyList<InstanceInfo> Instances
    {
        get { lock (_gate) return _instances.Values.Select(instance => instance.Info()).ToArray(); }
    }

    /// <summary>
    /// The same numbers the receipt carries, read now instead of at startup (docs/22 §5.2): how many instances there
    /// are, how many work, how much is known to be wrong, and who is on a schedule.
    /// </summary>
    public ModuleGraphStatus Status
    {
        get
        {
            var rhythms = Rhythms;
            lock (_gate)
            {
                return new(_instances.Count,
                    _instances.Values.Count(instance => instance.Phase == ModulePhase.Ready),
                    _badEdges.Count + _issues.Count +
                    _instances.Values.Count(instance => instance.Phase is ModulePhase.Failed or ModulePhase.Error),
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
                    if (!_instances.TryGetValue(sink, out var instance) || instance.Phase != ModulePhase.Ready) continue;
                    if (SinkOf(instance)?.PreferredInterval is not { } interval) continue;
                    foreach (var source in sources) rhythms.Add(new(sink, interval, source.Name));
                }
                return rhythms;
            }
        }
    }

    /// <summary>The module behind an id, for callers that already know which one they mean (docs/22 §3.5).</summary>
    /// <summary>
    /// The catalog's row for an instance, or null when nothing answers. Built from whatever declared the instance:
    /// the type from its manifest, the category and role from the module's own [Module]. The kernel does not invent
    /// either — a row that guessed would be worse than no row.
    /// </summary>
    public CatalogEntry? Describe(ModuleId id)
    {
        KernelInstance? instance;
        lock (_gate) _instances.TryGetValue(id, out instance);
        if (instance is null) return null;
        var info = instance.Info();
        return new CatalogEntry(info.Id, info.Type, info.Category, info.Role);
    }

    public IKernelModule? Find(ModuleId id)
    {
        lock (_gate) return _instances.TryGetValue(id, out var instance) ? instance.Module : null;
    }

    /// <summary>By type or category: the "who can do this" question, which an id cannot answer (docs/22 §3.5).</summary>
    public IReadOnlyList<IKernelModule> OfType(string typeId)
    {
        lock (_gate) return _instances.Values.Where(instance => instance.Manifest.Type == typeId).Select(instance => instance.Module).ToArray();
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
    /// Adds a instance the host built itself, before startup. This is how the core's own managers reach the graph
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
            _declared[module.Id] = new KernelInstance(manifest, module);
        }
        return Result.Success();
    }

    public async ValueTask<Result<ModuleReceipt>> StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { _instances.Clear(); _issues = []; _runtimeEdges = 0; _rhythms.Clear(); _badEdges = []; _state = KernelRunState.Starting; }
        foreach (var declared in Declared()) lock (_gate) _instances[declared.Manifest.Id] = declared;

        // C-03: a module declared in code is still a module, so it gets its directory and its file. Written once —
        // from the next start on, the file is the record of what exists and the declaration only says "this one is
        // the host's". Without this, the graph had instances the disk knew nothing about (22 §7 I2).
        foreach (var declared in Declared())
            await _store.WriteIfMissingAsync(declared.Manifest, cancellationToken).ConfigureAwait(false);

        // 0 — Scan: everything the disk says, plus one line per directory that is not a module.
        var scan = await _store.ScanAsync(_types, _hostOwnsTypes, cancellationToken, id => _instances.ContainsKey(id)).ConfigureAwait(false);
        var issues = new List<ModuleIssue>(scan.Issues);

        foreach (var manifest in scan.Manifests)
        {
            if (_instances.TryGetValue(manifest.Id, out var already))
            {
                // Declared in code and recorded on disk is not a collision: it is one module with two marks. The file
                // says what it is (C-03), the declaration says the host builds it. Only a *different* type under one
                // id is a real conflict, and that is what gets reported.
                if (string.Equals(already.Manifest.Type, manifest.Type, StringComparison.Ordinal)) continue;
                issues.Add(new ModuleIssue(manifest.Id.Value,
                    $"Declared by the host as \"{already.Manifest.Type}\" but the file says \"{manifest.Type}\": {manifest.Id}."));
                continue;
            }
            var created = _types.Create(manifest);
            if (!created.Ok || created.Value is null)
            {
                issues.Add(new ModuleIssue(manifest.Id.Value, created.Error?.Message ?? "Module could not be created."));
                continue;
            }
            lock (_gate) _instances[manifest.Id] = new KernelInstance(manifest, created.Value);
        }

        // 1 — Initialize: everything at once, and nobody moves on until everyone is done. Not "done successfully":
        // a module that failed still counts as done, because a barrier that waits only for success is not a barrier.
        var initialized = new List<KernelInstance>();
        var pending = new List<Task>();
        foreach (var instance in Snapshot())
        {
            if (!instance.Manifest.Enabled)
            {
                instance.Phase = ModulePhase.Disabled;
                continue;
            }
            pending.Add(InitializeAsync(instance, cancellationToken));
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
        foreach (var instance in Snapshot().Where(instance => instance.Phase == ModulePhase.Initialized)) initialized.Add(instance);

        // 2 — Wire: edges now have objects on both ends, so "does it exist" and "does it take slaves" have answers.
        var badEdges = new List<BadEdge>();
        var wired = 0;
        foreach (var instance in initialized.Where(instance => instance.Manifest.Slaves is { Count: > 0 }))
        {
            if (WiringOf(instance) is not { } master)
            {
                foreach (var slave in instance.Manifest.Slaves!)
                    badEdges.Add(new(instance.Manifest.Id, slave.Id, BadEdgeReason.MasterDoesNotWire,
                        $"{instance.Manifest.Id} declares slaves but does not implement {nameof(IModuleWiring)}."));
                var before = badEdges.Count - instance.Manifest.Slaves!.Count;
                for (var i = before; i < badEdges.Count; i++)
                    await NotifyEdgeErrorAsync(instance, nameof(IModuleWiring.WireSlave),
                        $"{badEdges[i].Slave}: {badEdges[i].Detail}", cancellationToken).ConfigureAwait(false);
                continue;
            }
            var start = badEdges.Count;
            foreach (var slave in instance.Manifest.Slaves!)
            {
                var resolved = Resolve(slave.Id, badEdges, instance.Manifest.Id);
                if (resolved is null) continue;
                Result accepted;
                try { accepted = master.WireSlave(slave.Id, slave.Kinds, resolved.Module); }
                catch (Exception ex) { accepted = Result.Failure(new(ErrorCode.Internal, "Wire threw.", ex.Message)); }
                if (!accepted.Ok)
                {
                    badEdges.Add(new(instance.Manifest.Id, slave.Id, BadEdgeReason.RejectedByMaster,
                        accepted.Error?.Message ?? "Rejected."));
                    continue;
                }
                wired++;
            }
            // C-13: an edge that did not wire is the module's business, not a silent line in a receipt. Tell the
            // master through its own error hook, one call per bad edge, and let the rest of startup carry on — only
            // the connection that failed is affected.
            for (var i = start; i < badEdges.Count; i++)
                await NotifyEdgeErrorAsync(instance, nameof(IModuleWiring.WireSlave),
                    $"{badEdges[i].Slave}: {badEdges[i].Detail}", cancellationToken).ConfigureAwait(false);
        }

        // 2.5 — Requirements: a module that finds its peers by name says which names it will ask for. Checked once,
        // here, so a missing peer fails the module at startup instead of failing the first message that needed it.
        // Nothing is ordered by this: the question is only "does somebody answer to that name", never "who first".
        foreach (var instance in initialized)
        {
            // The requirement may be declared by the module itself or by the thing a carrier wraps: the host hands
            // over carriers (a passive instance has no phases of its own), and the names belong to the module inside.
            var subject = instance.Module is IModuleCarrier carrier ? carrier.Target : instance.Module;
            if (subject is not IRequireModules requiring)
                continue;
            foreach (var name in requiring.Requires)
            {
                var id = new ModuleId(name);
                KernelInstance? peer;
                lock (_gate) _instances.TryGetValue(id, out peer);
                if (peer is not null && peer.Manifest.Enabled && peer.Phase is ModulePhase.Initialized or ModulePhase.Ready)
                    continue;
                var message = peer is null
                    ? $"Requires \"{name}\", and nothing answers to it."
                    : $"Requires \"{name}\", which is {peer.Phase.ToString().ToLowerInvariant()} rather than up.";
                await MissedRequirementAsync(instance, message, cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        // 3 — Ready: dependencies first, and modules that depend on each other at the same time.
        var components = Components(initialized);
        var cycles = components.Where(component => component.Count > 1)
            .Select(component => new CycleInfo(component.Select(instance => instance.Manifest.Id).ToArray()))
            .ToArray();
        var unfinishable = new HashSet<ModuleId>();
        foreach (var component in InDependencyOrder(components))
        {
            var blocked = component
                .SelectMany(instance => DependenciesOf(instance))
                .FirstOrDefault(dependency => unfinishable.Contains(dependency));
            if (blocked != default)
            {
                foreach (var instance in component)
                {
                    instance.Phase = ModulePhase.Failed;
                    instance.Failure = $"Not started: {blocked} did not become ready.";
                    unfinishable.Add(instance.Manifest.Id);
                }
                continue;
            }

            var ready = new List<Task>();
            // A module that already failed is not asked to become ready: its requirement was not answered, and asking
            // it to prepare would only overwrite that failure with a later one.
            foreach (var instance in component.Where(instance => instance.Phase == ModulePhase.Initialized))
                ready.Add(ReadyAsync(instance, cancellationToken));
            await Task.WhenAll(ready).ConfigureAwait(false);
            foreach (var instance in component.Where(instance => instance.Phase != ModulePhase.Ready)) unfinishable.Add(instance.Manifest.Id);
        }

        await AttachTriggersAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) { _issues = issues; _badEdges = badEdges.ToArray(); _state = KernelRunState.Running; }
        return Result<ModuleReceipt>.Success(new(Snapshot().Select(instance => instance.Info()).ToArray(), wired + _runtimeEdges,
            badEdges, cycles, issues, Rhythms));
    }

    /// <summary>
    /// Orderly stop, users before what they use: reverse of the ready order (docs/22 §4). Every module gets its
    /// own timeout, so one module that will not stop cannot keep the rest of the process alive.
    /// </summary>
    public async ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        var components = Components(Snapshot().Where(instance => instance.Phase == ModulePhase.Ready));
        foreach (var component in Enumerable.Reverse(InDependencyOrder(components)))
        {
            await Task.WhenAll(component.Select(instance => StopOneAsync(instance, cancellationToken))).ConfigureAwait(false);
        }
        return Result.Success();
    }

    /// <summary>
    /// Stop now: no order, and no waiting (docs/22 §4). The only promise is that every module was told.
    /// </summary>
    public ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        foreach (var instance in Snapshot().Where(instance => instance.Phase is not (ModulePhase.Stopped or ModulePhase.Disabled)))
        {
            // Deliberately not awaited: abort is what happens when waiting is no longer affordable.
            _ = AbortOneAsync(instance, cancellationToken);
        }
        return ValueTask.CompletedTask;
    }

    private async Task InitializeAsync(KernelInstance instance, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(instance, nameof(IKernelModule.InitializeAsync),
            token => instance.Module.InitializeAsync(token), cancellationToken).ConfigureAwait(false);
        instance.Phase = result.Ok ? ModulePhase.Initialized : ModulePhase.Failed;
        instance.Failure = result.Ok ? null : Describe(result);
    }

    private async Task ReadyAsync(KernelInstance instance, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(instance, nameof(IKernelModule.ReadyAsync),
            token => instance.Module.ReadyAsync(token), cancellationToken).ConfigureAwait(false);
        instance.Phase = result.Ok ? ModulePhase.Ready : ModulePhase.Failed;
        instance.Failure = result.Ok ? null : Describe(result);
    }

    private async Task StopOneAsync(KernelInstance instance, CancellationToken cancellationToken)
    {
        instance.Phase = ModulePhase.Stopping;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_stopTimeout);
        var result = await InvokeAsync(instance, nameof(IKernelModule.StopAsync),
            token => instance.Module.StopAsync(token), timeout.Token).ConfigureAwait(false);
        instance.Phase = result.Ok ? ModulePhase.Stopped : ModulePhase.Failed;
        instance.Failure = result.Ok ? null : Describe(result);
    }

    private async Task AbortOneAsync(KernelInstance instance, CancellationToken cancellationToken)
    {
        instance.Phase = ModulePhase.Stopping;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_stopTimeout);
            await instance.Module.AbortAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing is waiting for this call, so there is nobody to hand the exception to. Recording it is the
            // whole of what abort can offer.
            Record(instance, nameof(IKernelModule.AbortAsync), ex.Message, ex);
        }
        instance.Phase = ModulePhase.Stopped;
    }

    /// <summary>
    /// Calls one phase method and hands whatever it throws to the module's own error hook. If the hook fails too,
    /// the failure is recorded and swallowed: by then there is nobody left to tell (docs/22 §6).
    /// </summary>
    private async ValueTask<Result> InvokeAsync(KernelInstance instance, string method,
        Func<CancellationToken, ValueTask<Result>> call, CancellationToken cancellationToken)
    {
        try
        {
            var result = await call(cancellationToken).ConfigureAwait(false);
            if (!result.Ok) await ReportErrorAsync(instance, new(method, result.Error?.Message ?? "Failed."), CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            instance.Phase = ModulePhase.Failed;
            instance.Failure = $"{method} was cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(instance, new(method, ex.Message, ex), CancellationToken.None).ConfigureAwait(false);
            return Result.Failure(new(ErrorCode.Internal, $"{method} threw: {ex.Message}", ex.Message));
        }
    }

    private async ValueTask ReportErrorAsync(KernelInstance instance, ModuleError error, CancellationToken cancellationToken)
    {
        Record(instance, error.Method, error.Message, error.Exception);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_stopTimeout);
            var handled = await instance.Module.OnErrorAsync(error, timeout.Token).ConfigureAwait(false);
            if (!handled.Ok) Record(instance, nameof(IKernelModule.OnErrorAsync), handled.Error?.Message ?? "Error hook failed.", null);
        }
        catch (Exception ex)
        {
            // The hook threw or hung. One record, then nothing: retrying an error path is how a diagnostic failure
            // turns into a loop that nobody can stop.
            Record(instance, nameof(IKernelModule.OnErrorAsync), ex.Message, ex);
        }
    }

    /// <summary>Keeps the bad edges an operation produced, so a later question sees them too.</summary>
    private void KeepBadEdges(IEnumerable<BadEdge> edges)
    {
        var extra = edges as IReadOnlyList<BadEdge> ?? edges.ToArray();
        if (extra.Count == 0) return;
        lock (_gate) _badEdges = [.. _badEdges, .. extra];
    }

    private void Record(KernelInstance instance, string method, string message, Exception? exception)
    {
        instance.Phase = instance.Phase == ModulePhase.Ready ? ModulePhase.Error : ModulePhase.Failed;
        instance.Failure ??= $"{method}: {message}";
        _signals.Write(new()
        {
            Id = Guid.NewGuid(),
            ParentId = SignalScope.CurrentId,
            Group = "kernel",
            Name = "module",
            From = instance.Manifest.Id.Value,
            To = "kernel",
            Mechanism = $"kernel.{method}",
            Outcome = "failed",
            Level = SignalLevel.Error,
            Detail = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = instance.Manifest.Type,
                ["error"] = message,
                ["exception"] = exception?.GetType().Name ?? ""
            }
        });
    }

    /// <summary>
    /// Tells a module that one of its edges did not wire. The module is fine — the connection is not — so its phase is
    /// left alone: this is the "your wiring failed" hook, not "you failed". Failure of the hook itself is recorded and
    /// swallowed, like every other error path here.
    /// </summary>
    private async ValueTask NotifyEdgeErrorAsync(KernelInstance instance, string method, string message,
        CancellationToken cancellationToken)
    {
        _signals.Write(new()
        {
            Id = Guid.NewGuid(),
            ParentId = SignalScope.CurrentId,
            Group = "kernel",
            Name = "module",
            From = instance.Manifest.Id.Value,
            To = "kernel",
            Detail = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = instance.Manifest.Type,
                ["error"] = message,
                ["edge"] = "bad"
            }
        });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_stopTimeout);
            await instance.Module.OnErrorAsync(new(method, message), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Same rule as everywhere else: one attempt at telling, then nothing.
            Record(instance, nameof(IKernelModule.OnErrorAsync), ex.Message, ex);
        }
    }

    /// <summary>
    /// A name the module said it needs is not answered. The module itself fails — it told the kernel it could not
    /// work without that name — and its own error hook hears why, so the reason is in the module's log and not only in
    /// a receipt. Everything else keeps going: only the module that cannot work is out.
    /// </summary>
    private async ValueTask MissedRequirementAsync(KernelInstance instance, string message, CancellationToken cancellationToken)
    {
        await ReportErrorAsync(instance, new(nameof(IRequireModules.Requires), message), cancellationToken).ConfigureAwait(false);
        instance.Phase = ModulePhase.Failed;
    }

    private KernelInstance? Resolve(ModuleId id, List<BadEdge> badEdges, ModuleId master)
    {
        KernelInstance? instance;
        lock (_gate) _instances.TryGetValue(id, out instance);
        if (instance is null)
        {
            badEdges.Add(new(master, id, BadEdgeReason.MissingModule, $"No module answers to \"{id}\"."));
            return null;
        }
        if (!instance.Manifest.Enabled || instance.Phase == ModulePhase.Disabled)
        {
            badEdges.Add(new(master, id, BadEdgeReason.DisabledModule, $"\"{id}\" is disabled."));
            return null;
        }
        if (instance.Phase is not (ModulePhase.Initialized or ModulePhase.Ready))
        {
            badEdges.Add(new(master, id, BadEdgeReason.MasterFailed, $"\"{id}\" did not initialize: {instance.Failure}"));
            return null;
        }
        return instance;
    }

    private IEnumerable<ModuleId> DependenciesOf(KernelInstance instance)
    {
        foreach (var dependency in instance.Manifest.Dependencies ?? []) yield return dependency;
        foreach (var slave in instance.Manifest.Slaves ?? []) if (slave.RequireAll) yield return slave.Id;
    }

    /// <summary>Strongly connected components (Tarjan), so that a cycle is one runnable unit instead of a deadlock.</summary>
    private List<List<KernelInstance>> Components(IEnumerable<KernelInstance> instances)
    {
        var all = instances.ToArray();
        var index = new Dictionary<ModuleId, int>();
        var low = new Dictionary<ModuleId, int>();
        var onStack = new HashSet<ModuleId>();
        var stack = new Stack<KernelInstance>();
        var components = new List<List<KernelInstance>>();
        var counter = 0;

        IEnumerable<KernelInstance> Next(KernelInstance instance) =>
            DependenciesOf(instance).Select(id => all.FirstOrDefault(candidate => candidate.Manifest.Id == id)).OfType<KernelInstance>();

        void Visit(KernelInstance instance)
        {
            index[instance.Manifest.Id] = counter;
            low[instance.Manifest.Id] = counter;
            counter++;
            stack.Push(instance);
            onStack.Add(instance.Manifest.Id);

            foreach (var next in Next(instance))
            {
                if (!index.ContainsKey(next.Manifest.Id))
                {
                    Visit(next);
                    low[instance.Manifest.Id] = Math.Min(low[instance.Manifest.Id], low[next.Manifest.Id]);
                }
                else if (onStack.Contains(next.Manifest.Id))
                {
                    low[instance.Manifest.Id] = Math.Min(low[instance.Manifest.Id], index[next.Manifest.Id]);
                }
            }

            if (low[instance.Manifest.Id] != index[instance.Manifest.Id]) return;
            var component = new List<KernelInstance>();
            KernelInstance member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member.Manifest.Id);
                component.Add(member);
            } while (member.Manifest.Id != instance.Manifest.Id);
            components.Add(component);
        }

        foreach (var instance in all.Where(instance => !index.ContainsKey(instance.Manifest.Id))) Visit(instance);
        return components;
    }

    /// <summary>
    /// Orders components so that whatever a component depends on comes first. On the condensed graph this is a
    /// plain topological sort, which exists by construction: SCCs of a directed graph cannot form a cycle.
    /// </summary>
    private List<List<KernelInstance>> InDependencyOrder(List<List<KernelInstance>> components)
    {
        var owner = new Dictionary<ModuleId, List<KernelInstance>>();
        foreach (var component in components)
            foreach (var instance in component) owner[instance.Manifest.Id] = component;

        var ordered = new List<List<KernelInstance>>(components.Count);
        var done = new HashSet<List<KernelInstance>>();
        var remaining = new List<List<KernelInstance>>(components);
        while (remaining.Count > 0)
        {
            var progressed = false;
            foreach (var component in remaining.ToArray())
            {
                var waitsFor = component
                    .SelectMany(DependenciesOf)
                    .Select(id => owner.TryGetValue(id, out var ownerComponent) ? ownerComponent : null)
                    .OfType<List<KernelInstance>>()
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
    /// Adds a instance while the graph is running (docs/22, D1): initialize it, wire the slaves it declares, then let it
    /// start — but only if what it says it needs is already ready.
    ///
    /// This is not the startup path with fewer phases. Startup waits for everyone because nobody can be trusted
    /// before everyone has built themselves; a instance that arrives later finds the graph already established, so the
    /// only thing it may assume is that whatever is ready now is ready.
    /// </summary>
    public async ValueTask<Result<ModuleAddition>> AddAsync(IKernelModule module, IReadOnlyList<ModuleSlave>? slaves = null,
        IReadOnlyList<ModuleId>? dependencies = null, string? typeId = null, CancellationToken cancellationToken = default)
    {
        if (module is null) return Result<ModuleAddition>.Failure(new(ErrorCode.Invalid, "A module cannot be null."));
        KernelInstance instance;
        lock (_gate)
        {
            if (_instances.ContainsKey(module.Id))
                return Result<ModuleAddition>.Failure(new(ErrorCode.Conflict, $"A module already answers to \"{module.Id}\"."));
            instance = new KernelInstance(new ModuleManifest(module.Id, typeId ?? module.Id.Value, true, null,
                slaves ?? [], dependencies ?? []), module);
            _instances[module.Id] = instance;
        }

        await InitializeAsync(instance, cancellationToken).ConfigureAwait(false);
        if (instance.Phase != ModulePhase.Initialized)
            return Result<ModuleAddition>.Success(new(instance.Info(), []));

        var badEdges = new List<BadEdge>();
        var wired = 0;
        if (instance.Manifest.Slaves is { Count: > 0 })
        {
            if (WiringOf(instance) is { } master)
            {
                foreach (var slave in instance.Manifest.Slaves!)
                {
                    var resolved = Resolve(slave.Id, badEdges, instance.Manifest.Id);
                    if (resolved is null) continue;
                    Result accepted;
                    try { accepted = master.WireSlave(slave.Id, slave.Kinds, resolved.Module); }
                    catch (Exception ex) { accepted = Result.Failure(new(ErrorCode.Internal, "Wire threw.", ex.Message)); }
                    if (!accepted.Ok)
                    {
                        badEdges.Add(new(instance.Manifest.Id, slave.Id, BadEdgeReason.RejectedByMaster,
                            accepted.Error?.Message ?? "Rejected."));
                        continue;
                    }
                    wired++;
                }
            }
            else
            {
                foreach (var slave in instance.Manifest.Slaves!)
                    badEdges.Add(new(instance.Manifest.Id, slave.Id, BadEdgeReason.MasterDoesNotWire,
                        $"{instance.Manifest.Id} declares slaves but does not implement {nameof(IModuleWiring)}."));
            }
        }

        // What it declared it needs has to be ready *now*: there is no later wave to wait for. Two different
        // verdicts, because they are two different mistakes: a named dependency that does not exist is a broken
        // configuration, while a slave that is not there is a dropped edge (reported above, and the instance may still
        // work without it).
        KeepBadEdges(badEdges);
        var missingDependency = (instance.Manifest.Dependencies ?? [])
            .FirstOrDefault(id => Snapshot().All(candidate => candidate.Manifest.Id != id));
        if (missingDependency != default)
        {
            instance.Phase = ModulePhase.Failed;
            instance.Failure = $"Not started: declared dependency \"{missingDependency}\" does not exist.";
            return Result<ModuleAddition>.Success(new(instance.Info(), badEdges, wired));
        }
        var unready = DependenciesOf(instance)
            .Select(id => Snapshot().FirstOrDefault(candidate => candidate.Manifest.Id == id))
            .FirstOrDefault(candidate => candidate is not null && candidate.Phase != ModulePhase.Ready);
        if (unready is not null)
        {
            instance.Phase = ModulePhase.Failed;
            instance.Failure = $"Not started: {unready.Manifest.Id} is {unready.Phase}.";
            return Result<ModuleAddition>.Success(new(instance.Info(), badEdges, wired));
        }

        await ReadyAsync(instance, cancellationToken).ConfigureAwait(false);
        await AttachTriggersAsync(cancellationToken).ConfigureAwait(false);
        return Result<ModuleAddition>.Success(new(instance.Info(), badEdges, wired));
    }

    /// <summary>
    /// Stops a instance and takes it out of the graph, reporting every edge that pointed at it (docs/22, D1).
    ///
    /// The edges are reported rather than repaired: a master that still lists this id in its manifest has a file
    /// that no longer matches the graph, and rewriting somebody else's file is not this method's business.
    /// </summary>
    public async ValueTask<Result<IReadOnlyList<BadEdge>>> RemoveAsync(ModuleId id, CancellationToken cancellationToken)
    {
        KernelInstance? instance;
        lock (_gate) _instances.TryGetValue(id, out instance);
        if (instance is null) return Result<IReadOnlyList<BadEdge>>.Failure(new(ErrorCode.Invalid, $"No module answers to \"{id}\"."));

        await StopOneAsync(instance, cancellationToken).ConfigureAwait(false);
        DetachTriggers(id);

        var dropped = new List<BadEdge>();
        lock (_gate)
        {
            _instances.Remove(id);
            _declared.Remove(id);
            foreach (var other in _instances.Values)
                foreach (var slave in (other.Manifest.Slaves ?? []).Where(slave => slave.Id == id).ToArray())
                {
                    // The instance is gone, so no edge may keep pointing at it: the edge is dropped here and the
                    // receipt says why it disappeared rather than leaving a master holding a name nothing answers to.
                    dropped.Add(new(other.Manifest.Id, id, BadEdgeReason.MissingModule, "Removed while still declared as a slave."));
                    other.ReleaseSlave(id);
                }
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
    public async ValueTask<Result<ModuleAddition>> AdoptAsync(ModuleId masterId, IKernelModule module,
        IReadOnlyList<string>? kinds = null, string? typeId = null, CancellationToken cancellationToken = default)
    {
        if (module is null) return Result<ModuleAddition>.Failure(new(ErrorCode.Invalid, "An module cannot be null."));
        var edge = new ModuleSlave(module.Id, kinds ?? [], RequireAll: false);
        KernelInstance master;
        KernelInstance instance;
        bool alreadyThere;
        lock (_gate)
        {
            // Before startup the master may only be *declared*: the graph does not exist yet, and refusing here
            // would be answering a question startup has not asked. A provider built from its file belongs to this
            // case — it is held before anything runs.
            if (!_instances.TryGetValue(masterId, out master!) && !_declared.TryGetValue(masterId, out master!))
                return Result<ModuleAddition>.Failure(new(ErrorCode.Invalid, $"No module answers to \"{masterId}\"."));

            // C-16: an module may have more than one master, so a second one is not a conflict — holding the same
            // object twice is what "two masters" means. Two *different* objects under one id still is (C-14).
            var same = _instances.TryGetValue(module.Id, out var live) ? live
                : _declared.TryGetValue(module.Id, out var declared) ? declared : null;
            if (same is not null && !ReferenceEquals(same.Module, module))
                return Result<ModuleAddition>.Failure(new(ErrorCode.Conflict, $"A module already answers to \"{module.Id}\"."));

            if (same is not null)
            {
                instance = same;
                alreadyThere = true;
            }
            else
            {
                // The edge lives on the master, and only there: the master is what holds the module and what has to
                // accept it during Wire. Writing the master into the module's own slave list would make the module a
                // master too — the kernel would then ask it to wire its "slave", and report the refusal as a bad edge.
                instance = new KernelInstance(new ModuleManifest(module.Id, typeId ?? module.Id.Value), module);
                alreadyThere = false;
                if (_state == KernelRunState.BeforeStart)
                {
                    // Nothing to phase through yet: this is a declaration that happens to come from a manager. Startup
                    // will initialize, wire and start it like any other, so the receipt counts the edge and the master
                    // still has to accept it.
                    _declared[module.Id] = instance;
                    master.AdoptSlave(edge);
                    return Result<ModuleAddition>.Success(new(instance.Info(), []));
                }
                _instances[module.Id] = instance;
            }
        }

        if (alreadyThere)
        {
            // The module is already in the graph: its phases are the core's business and are not re-run. All this
            // master gets is an edge, and the edge is wired the same way it would be at startup — asked, refused,
            // refused as a bad edge.
            if (instance.Phase is not (ModulePhase.Initialized or ModulePhase.Ready))
            {
                // Still starting: the Wire pass will reach this edge because it is on the master's list now.
                lock (_gate) master.AdoptSlave(edge);
                return Result<ModuleAddition>.Success(new(instance.Info(), []));
            }
            if (WiringOf(master) is not { } second)
            {
                var dropped = new BadEdge(masterId, module.Id, BadEdgeReason.MasterDoesNotWire,
                    $"{masterId} declares slaves but does not implement {nameof(IModuleWiring)}.");
                KeepBadEdges([dropped]);
                return Result<ModuleAddition>.Success(new(instance.Info(), [dropped]));
            }
            Result refused;
            try { refused = second.WireSlave(module.Id, kinds ?? [], module); }
            catch (Exception ex) { refused = Result.Failure(new(ErrorCode.Internal, "Wire threw.", ex.Message)); }
            if (!refused.Ok)
            {
                var bad = new BadEdge(masterId, module.Id, BadEdgeReason.RejectedByMaster, refused.Error?.Message ?? "Rejected.");
                KeepBadEdges([bad]);
                return Result<ModuleAddition>.Success(new(instance.Info(), [bad]));
            }
            lock (_gate) master.AdoptSlave(edge);
            return Result<ModuleAddition>.Success(new(instance.Info(), [], 1));
        }

        if (WiringOf(master) is not { } wiring)
            return Result<ModuleAddition>.Failure(new(ErrorCode.Invalid, $"{masterId} does not hold instances."));

        await InitializeAsync(instance, cancellationToken).ConfigureAwait(false);
        if (instance.Phase != ModulePhase.Initialized)
            return Result<ModuleAddition>.Success(new(instance.Info(), []));

        Result accepted;
        try { accepted = wiring.WireSlave(module.Id, kinds ?? [], module); }
        catch (Exception ex) { accepted = Result.Failure(new(ErrorCode.Internal, "Wire threw.", ex.Message)); }
        if (!accepted.Ok)
        {
            var bad = new BadEdge(masterId, module.Id, BadEdgeReason.RejectedByMaster, accepted.Error?.Message ?? "Rejected.");
            KeepBadEdges([bad]);
            lock (_gate) _instances.Remove(module.Id);
            return Result<ModuleAddition>.Success(new(instance.Info(), [bad]));
        }

        // The edge is recorded on the master too, so the next receipt shows it as a declared slave rather than as
        // something that only exists in this method's local variable.
        lock (_gate)
        {
            master.AdoptSlave(edge);
            _runtimeEdges++;
        }

        await ReadyAsync(instance, cancellationToken).ConfigureAwait(false);
        await AttachTriggersAsync(cancellationToken).ConfigureAwait(false);
        return Result<ModuleAddition>.Success(new(instance.Info(), [], 1));
    }

    /// <summary>
    /// Drops one edge. Nothing happens to the instance: a master holding less is not the same event as a module
    /// being destroyed, and only whoever constructed it decides that (C-16, C-41). Absent is not an error — a
    /// manager tearing down is allowed to say so twice.
    /// </summary>
    public ValueTask<Result> ReleaseAsync(ModuleId masterId, ModuleId instanceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(masterId, out var master) || _declared.TryGetValue(masterId, out master))
                master.ReleaseSlave(instanceId);
        }
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>
    /// Loads one module from its own file while the graph is running (docs/22, D2): read the manifest, build the
    /// module, then add it the way a late instance is added.
    ///
    /// It reads the file rather than taking a manifest as an argument because the file is the truth about what a
    /// module is; a caller that passed one would be describing a module that may not be the one on disk.
    /// </summary>
    public async ValueTask<Result<ModuleAddition>> LoadAsync(ModuleId id, CancellationToken cancellationToken)
    {
        var scan = await _store.ScanAsync(_types, _hostOwnsTypes, cancellationToken, id => _instances.ContainsKey(id)).ConfigureAwait(false);
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
    /// Hooks every instance that wants a rhythm onto every instance that can provide one.
    ///
    /// After Ready, because a schedule should start against modules that are working, and again whenever the graph
    /// changes: a sink that arrives later still gets its beat. A source that refuses is recorded, not fatal — the same
    /// rule as a bad edge, for the same reason: a missed beat does not stop the rest of the system.
    /// </summary>
    private async ValueTask AttachTriggersAsync(CancellationToken cancellationToken)
    {
        var instances = Snapshot();
        var sources = instances.Where(instance => SourceOf(instance) is { CanSchedule: true }).ToArray();
        if (sources.Length == 0) return;

        foreach (var instance in instances)
        {
            var sink = SinkOf(instance);
            if (sink is null || sink.PreferredInterval is null) continue;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Result attached;
                try { attached = SourceOf(source)!.Attach(instance.Manifest.Id, sink); }
                catch (Exception ex) { attached = Result.Failure(new(ErrorCode.Internal, "Attach threw.", ex.Message)); }
                if (attached.Ok)
                {
                    // Recorded only on success: the receipt is the kernel's claim about calls it actually made.
                    lock (_gate)
                    {
                        if (!_rhythms.TryGetValue(instance.Manifest.Id, out var beats)) _rhythms[instance.Manifest.Id] = beats = [];
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
                    From = instance.Manifest.Id.Value,
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

    /// <summary>Takes a departing instance off every schedule. The source keeps working for everybody else.</summary>
    private void DetachTriggers(ModuleId id)
    {
        lock (_gate)
        {
            // Both directions: it may be the sink that left, or the source that all those beats named.
            _rhythms.Remove(id);
            foreach (var beats in _rhythms.Values) beats.RemoveAll(beat => beat.Id == id);
        }
        foreach (var instance in Snapshot())
        {
            if (SourceOf(instance) is not { } source) continue;
            try { source.Detach(id); } catch { /* a source that cannot forget one sink must not block a removal */ }
        }
    }

    /// <summary>
    /// The instance's capability, whether the instance implements it itself or only carries the module that does.
    ///
    /// Everything the kernel knows about triggers goes through these two lookups, which is why it has no idea a clock
    /// exists: a schedule source is just a instance whose module implements ITriggerSource, and a sink is just a instance
    /// whose module (or carried module) implements ITriggerSink.
    /// </summary>
    private static ITriggerSink? SinkOf(KernelInstance instance) =>
        instance.Module as ITriggerSink ?? (instance.Module as IModuleCarrier)?.Target as ITriggerSink;

    private static ITriggerSource? SourceOf(KernelInstance instance) =>
        instance.Module as ITriggerSource ?? (instance.Module as IModuleCarrier)?.Target as ITriggerSource;

    /// <summary>
    /// The same door for the master side: a instance that holds slaves may be the manager itself or a carrier around it
    /// (a manager the host runs outside the phase machine is a <see cref="PassiveInstance"/>). One lookup, so a new kind
    /// of wrapper never means a new place for the kernel to forget.
    /// </summary>
    private static IModuleWiring? WiringOf(KernelInstance instance) =>
        instance.Module as IModuleWiring ?? (instance.Module as IModuleCarrier)?.Target as IModuleWiring;

    private KernelInstance[] Snapshot()
    {
        lock (_gate) return _instances.Values.ToArray();
    }

    private KernelInstance[] Declared()
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
        /// <summary>Inside <see cref="StartAsync"/>: the instances exist, so an instance walks its own phases now.</summary>
        Starting,
        /// <summary>Startup finished; additions and removals are the only way in.</summary>
        Running
    }

    private sealed class KernelInstance(ModuleManifest manifest, IKernelModule module)
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

        /// <summary>
        /// What this instance is, as its own code declares it. Read from the module's [Module] attribute once per call
        /// rather than stored: the attribute is on the type, and a second copy of it here could drift from the type.
        /// </summary>
        public InstanceInfo Info()
        {
            var declared = Module.GetType().GetCustomAttribute<ModuleAttribute>();
            return new InstanceInfo(Manifest.Id, Manifest.Type, Phase, Failure,
                declared?.Category ?? "", declared?.Role ?? ModuleRole.Instance);
        }
    }
}
