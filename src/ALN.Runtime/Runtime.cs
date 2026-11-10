using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ALN.Contracts;

namespace ALN.Runtime;

public sealed record AdapterTypeDefinition(string Id, string? ParentId, bool IsAbstract);

public sealed class AdapterTypeCatalog : IModuleTypeRegistry
{
    private readonly Dictionary<string, AdapterTypeDefinition> _types = new(StringComparer.Ordinal)
    {
        ["adapter"] = new("adapter", null, true),
        ["channel"] = new("channel", null, true),
        ["clock"] = new("clock", null, true),
        ["graph"] = new("graph", null, true),
        ["model"] = new("model", null, true),
        // Sessions are instances of one type. It is a type here rather than a manager-only fact because mount rules
        // and type lookups ask the same question about a Session as about an Adapter, and "not in the tree" would
        // make the answer depend on which module happened to ask.
        ["session"] = new("session", null, true),
        ["session.chat"] = new("session.chat", "session", false)
    };
    private readonly Dictionary<string, HashSet<string>> _ownedTypes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool TryGet(string id, out AdapterTypeDefinition definition)
    { lock (_gate) return _types.TryGetValue(id, out definition!); }
    public bool IsKnownAbstract(string typeId)
    { lock (_gate) return _types.TryGetValue(typeId, out var type) && type.IsAbstract; }
    public bool IsConcrete(string id)
    { lock (_gate) return _types.TryGetValue(id, out var type) && !type.IsAbstract; }
    public IReadOnlyList<ModuleTypeEntry> ListTypes()
    {
        lock (_gate) return _types.Values
            .OrderBy(type => type.Id, StringComparer.Ordinal)
            .Select(type => new ModuleTypeEntry(type.Id, type.ParentId, type.IsAbstract))
            .ToArray();
    }

    public bool IsAssignableTo(string concreteTypeId, string abstractTypeId)
    {
        lock (_gate)
        {
            var current = concreteTypeId;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (_types.TryGetValue(current, out var definition) && seen.Add(current))
            {
                if (StringComparer.Ordinal.Equals(current, abstractTypeId)) return true;
                if (definition.ParentId is null) return false;
                current = definition.ParentId;
            }
            return false;
        }
    }

    public Result RegisterPackageTypes(string packageId, IReadOnlyList<ModuleTypeProvision> types)
    {
        if (string.IsNullOrWhiteSpace(packageId) || types is null || types.Any(t => t is null || string.IsNullOrWhiteSpace(t.TypeId)))
            return Result.Failure(new(ErrorCode.Invalid, "Module type registration is incomplete."));
        lock (_gate)
        {
            if (_ownedTypes.ContainsKey(packageId))
                return Result.Failure(new(ErrorCode.Conflict, $"Package types are already registered: {packageId}"));
            if (types.Select(t => t.TypeId).Distinct(StringComparer.Ordinal).Count() != types.Count ||
                types.Any(t => _types.ContainsKey(t.TypeId)))
                return Result.Failure(new(ErrorCode.Conflict, "Module type id is already registered."));

            var candidate = new Dictionary<string, AdapterTypeDefinition>(_types, StringComparer.Ordinal);
            foreach (var type in types) candidate.Add(type.TypeId, new(type.TypeId, type.ParentTypeId, type.IsAbstract));
            foreach (var type in types)
            {
                if (type.ParentTypeId is not null && !candidate.ContainsKey(type.ParentTypeId))
                    return Result.Failure(new(ErrorCode.Invalid, $"Parent module type is not registered: {type.ParentTypeId}"));
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var current = type.TypeId;
                while (candidate.TryGetValue(current, out var definition))
                {
                    if (!seen.Add(current)) return Result.Failure(new(ErrorCode.Invalid, $"Module type inheritance cycle at {current}."));
                    if (definition.ParentId is null) break;
                    current = definition.ParentId;
                }
            }

            foreach (var type in types) _types.Add(type.TypeId, new(type.TypeId, type.ParentTypeId, type.IsAbstract));
            _ownedTypes.Add(packageId, types.Select(t => t.TypeId).ToHashSet(StringComparer.Ordinal));
            return Result.Success();
        }
    }

    public Result RemovePackageTypes(string packageId)
    {
        lock (_gate)
        {
            if (!_ownedTypes.TryGetValue(packageId, out var owned)) return Result.Success();
            var dependents = _types.Values.Where(t => t.ParentId is not null && owned.Contains(t.ParentId) && !owned.Contains(t.Id))
                .Select(t => t.Id).ToArray();
            if (dependents.Length > 0)
                return Result.Failure(new(ErrorCode.Conflict, $"Module types still have registered children: {string.Join(", ", dependents)}"));
            foreach (var id in owned) _types.Remove(id);
            _ownedTypes.Remove(packageId);
            return Result.Success();
        }
    }
}

[Module(AdapterManagerId, Category = "adapter", Role = ModuleRole.Manager, Singleton = true)]
public sealed partial class AdapterManager : IAdapterManager, IModuleIndex, IModuleWiring, IInstanceSink, ILifecycleParticipant, IModule
{
    public const string AdapterManagerId = ModuleNames.AdapterManager;
    public ModuleId Id { get; } = new(AdapterManagerId);

    public string Category => "adapter";

    /// <summary>One live instance and what its spec said it is. The name is the key: an address is a name, and a
    /// name says nothing about what it is, so the type travels next to the object instead of in a parallel map.</summary>
    private sealed record LiveAdapter(IChatAdapter Adapter, string TypeId, AdapterSpec Spec);

    private readonly Dictionary<ModuleId, LiveAdapter> _adapters = new();

    private static ModuleId IdOf(Address address) => address.ToModuleId();
    private readonly IModuleTypeCatalog _types;
    private readonly IInstanceBindings _bindings;
    private readonly IAdapterFactoryRegistry? _factories;
    private readonly IAdapterSpecStore? _specs;
    private IEventSink? _events;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    // Adapters that declare tunables register themselves here, the same way Channels do.
    private readonly IParameterRegistry? _parameters;
    private IInstanceHost? _instances;

    public AdapterManager(IModuleTypeCatalog types, IInstanceBindings bindings, IAdapterFactoryRegistry? factories = null,
        IAdapterSpecStore? specs = null, IEventSink? events = null, IParameterRegistry? parameters = null)
    {
        _types = types;
        _bindings = bindings;
        _factories = factories;
        _specs = specs;
        _events = events;
        _parameters = parameters;
    }

    /// <summary>
    /// Wires the event sink the manager forwards lifecycle signals through. It cannot be given at construction
    /// time because it lives in a module package resolved later. Must be called before the lifecycle signal arrives.
    /// </summary>
    public void UseEvents(IEventSink events) => _events = events ?? throw new ArgumentNullException(nameof(events));

    /// <inheritdoc />
    public void UseInstanceHost(IInstanceHost instances) => _instances = instances ?? throw new ArgumentNullException(nameof(instances));

    /// <summary>
    /// The master side of the edge: the graph offers an instance and this manager says whether it holds it. It only
    /// accepts what it already has, because an Adapter is created here and announced afterwards.
    /// </summary>
    public Result WireSlave(ModuleId slaveId, IReadOnlyList<string> kinds, IKernelModule instance)
    {
        lock (_gate)
            return _adapters.Values.Any(live => string.Equals(live.Adapter.Address.ToString(), slaveId.Value, StringComparison.Ordinal))
                ? Result.Success()
                : Result.Failure(new(ErrorCode.Invalid, $"{slaveId} is not one of this manager's Adapters."));
    }

    /// <inheritdoc />
    public string? TypeOf(Address address)
    {
        lock (_gate) return _adapters.TryGetValue(IdOf(address), out var live) ? live.TypeId : null;
    }

    public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, CancellationToken cancellationToken) =>
        _factories is null
            ? ValueTask.FromResult(Result<IChatAdapter>.Failure(new(ErrorCode.Unavailable, "No Adapter factory registry is configured.")))
            : CreateCoreAsync(spec, s => _factories.Create(s.TypeId, s), cancellationToken);
    public Result<IChatAdapter> Get(Address address)
    {
        lock (_gate)
            return _adapters.TryGetValue(IdOf(address), out var live) ? Result<IChatAdapter>.Success(live.Adapter)
                : Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, $"Adapter not found: {address}"));
    }
    public IReadOnlyList<AdapterInfo> List()
    {
        lock (_gate) return _adapters.Values
            .Select(live => new AdapterInfo(live.Adapter.Address, live.Adapter.Descriptor, live.Adapter.State, live.TypeId))
            .ToArray();
    }

    /// <inheritdoc />
    public LocatedModule? Locate(ModuleId id)
    {
        lock (_gate)
            return _adapters.TryGetValue(id, out var live)
                ? new LocatedModule(live.Adapter, new CatalogEntry(live.Adapter.Id, live.TypeId, Category, ModuleRole.Instance))
                : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<CatalogEntry> Entries()
    {
        lock (_gate) return _adapters.Values
            .Select(live => new CatalogEntry(live.Adapter.Id, live.TypeId, Category, ModuleRole.Instance))
            .ToArray();
    }
    public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, Func<AdapterSpec, IChatAdapter> factory, CancellationToken cancellationToken) =>
        CreateCoreAsync(spec, s =>
        {
            try { return Result<IChatAdapter>.Success(factory(s)); }
            catch (Exception ex) { return Result<IChatAdapter>.Failure(new(ErrorCode.Internal, "Adapter factory failed.", ex.Message)); }
        }, cancellationToken);

    private async ValueTask<Result<IChatAdapter>> CreateCoreAsync(AdapterSpec spec,
        Func<AdapterSpec, Result<IChatAdapter>> factory, CancellationToken cancellationToken)
    {
        if (spec.Address.Instance is null || !_types.IsConcrete(spec.TypeId) || !_types.IsAssignableTo(spec.TypeId, "adapter"))
            return Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, "Only registered concrete Adapter types with an instance id can be created."));
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_adapters.ContainsKey(IdOf(spec.Address))) return Result<IChatAdapter>.Failure(new(ErrorCode.Conflict, $"Adapter already exists: {spec.Address}"));
                if (_adapters.Values.Any(live => live.Adapter.Descriptor.PlatformId == spec.Descriptor.PlatformId && live.Adapter.Descriptor.AccountId == spec.Descriptor.AccountId))
                    return Result<IChatAdapter>.Failure(new(ErrorCode.Conflict, "Platform/account is already registered."));
            }
            var created = factory(spec);
            if (!created.Ok || created.Value is null) return Result<IChatAdapter>.Failure(created.Error ?? new(ErrorCode.Internal, "Adapter factory failed."));
            var adapter = created.Value;
            if (adapter.Address != spec.Address || adapter.Descriptor != spec.Descriptor)
                return Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, "Factory output does not match its AdapterSpec."));
            lock (_gate)
            {
                _adapters.Add(adapter.Id, new LiveAdapter(adapter, spec.TypeId, spec));
            }
            if (_parameters is not null && adapter is IParameterTarget tunable) _parameters.Register(tunable);
            var attached = await _bindings.AttachAsync(spec.Address, cancellationToken).ConfigureAwait(false);
            if (!attached.Ok)
            {
                lock (_gate) _adapters.Remove(IdOf(spec.Address));
                return Result<IChatAdapter>.Failure(attached.Error!);
            }
            if (_specs is not null)
            {
                // The Adapter is a configured object, not a side effect of a folder existing.
                var persisted = await _specs.SaveAsync(spec, cancellationToken).ConfigureAwait(false);
                if (!persisted.Ok)
                {
                    lock (_gate) _adapters.Remove(IdOf(spec.Address));
                    await _bindings.DetachAsync(spec.Address, CancellationToken.None).ConfigureAwait(false);
                    return Result<IChatAdapter>.Failure(persisted.Error!);
                }
            }
            // Announced to the graph, so "what is connected to this manager" stops being a fact only the dictionary
            // knows. The instance carries the Adapter's own trigger capability: an Adapter that wants a rhythm says so by
            // implementing ITriggerSink, and a wrapper must not hide that.
            if (_instances is not null)
            {
                var announced = await _instances.AttachAsync(new ModuleId(ModuleNames.AdapterManager),
                    new ModuleId(spec.Address.ToString()), spec.TypeId, [EventNames.AdapterEnvelope], cancellationToken,
                    adapter as ITriggerSink).ConfigureAwait(false);
                if (!announced.Ok)
                {
                    lock (_gate) _adapters.Remove(IdOf(spec.Address));
                    await _bindings.DetachAsync(spec.Address, CancellationToken.None).ConfigureAwait(false);
                    return Result<IChatAdapter>.Failure(announced.Error!);
                }
            }
            return Result<IChatAdapter>.Success(adapter);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<IChatAdapter>.Failure(new(ErrorCode.Internal, "Adapter creation failed.", ex.Message)); }
        finally { _lifecycle.Release(); }
    }
    public async ValueTask<Result> StartAsync(Address address, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var found = Get(address);
            if (!found.Ok || found.Value is null) return Result.Failure(found.Error!);
            try { return await found.Value.StartAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Result.Failure(new(ErrorCode.Internal, "Adapter start failed.", ex.Message)); }
        }
        finally { _lifecycle.Release(); }
    }
    public async ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var found = Get(address);
            if (!found.Ok || found.Value is null) return Result.Failure(found.Error!);
            try { return await found.Value.StopAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Result.Failure(new(ErrorCode.Internal, "Adapter stop failed.", ex.Message)); }
        }
        finally { _lifecycle.Release(); }
    }
    /// <summary>
    /// Manager startup: revive every stored Adapter, then hand the same start signal down to each
    /// one. Children that need a heartbeat (polling adapters) get their clock subscription here, not
    /// in the host — the host has no idea what an adapter needs.
    /// </summary>
    public string Name => "adapters";

    public async ValueTask<Result> StartAsync(LifecycleSignal signal, CancellationToken cancellationToken)
    {
        if (_specs is null) return Result.Success();
        var stored = await _specs.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!stored.Ok) return Result.Failure(stored.Error!);
        foreach (var spec in stored.Value!)
        {
            if (!Get(spec.Address).Ok)
            {
                var created = await CreateAsync(spec, cancellationToken).ConfigureAwait(false);
                if (!created.Ok) return Result.Failure(created.Error!);
            }
            var started = await StartAsync(spec.Address, cancellationToken).ConfigureAwait(false);
            if (!started.Ok) return started;
            var children = await StartChildrenAsync(spec.Address, signal, cancellationToken).ConfigureAwait(false);
            if (!children.Ok) return children;
        }
        return Result.Success();
    }

    /// <summary>
    /// Hands the same start signal one level down. Nothing about pacing is here: an Adapter that wants a rhythm says
    /// so by implementing ITriggerSink and reporting its interval, and the trigger source attaches it. A manager that
    /// guessed at heartbeats is exactly how every polled Adapter used to be polled twice per interval.
    /// </summary>
    private async ValueTask<Result> StartChildrenAsync(Address address, LifecycleSignal signal, CancellationToken cancellationToken)
    {
        if (_events is null) return Result.Success();
        // The same signal, one level down: whatever is mounted on this Adapter sees lifecycle.start.
        return await _events.PublishAsync(new(EventNames.LifecycleStart, address, Guid.NewGuid(),
            JsonSerializer.Serialize(signal.For(address), LifecycleJson.Options), address),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result> StopAsync(LifecycleSignal signal, CancellationToken cancellationToken)
    {
        Result firstFailure = Result.Success();
        foreach (var address in List().Select(a => a.Address))
        {
            var stopped = await StopAsync(address, cancellationToken).ConfigureAwait(false);
            if (!stopped.Ok && firstFailure.Ok) firstFailure = stopped;
            if (_events is not null)
                await _events.PublishAsync(new(EventNames.LifecycleStop, address, Guid.NewGuid(),
                    JsonSerializer.Serialize(signal.For(address), LifecycleJson.Options), address),
                    cancellationToken).ConfigureAwait(false);
        }
        return firstFailure;
    }

    public async ValueTask<Result<IReadOnlyList<AdapterInfo>>> SnapshotByTypeAsync(string concreteTypeId, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Result<IReadOnlyList<AdapterInfo>>.Success(List().Where(a => a.TypeId == concreteTypeId).ToArray()); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask<Result> DestroyByTypeAsync(string concreteTypeId, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var addresses = List().Where(a => a.TypeId == concreteTypeId).Select(a => a.Address).ToArray();
            foreach (var address in addresses)
            {
                var found = Get(address);
                if (!found.Ok || found.Value is null) continue;
                var detached = await _bindings.DetachAsync(address, cancellationToken).ConfigureAwait(false);
                if (!detached.Ok) return detached;
                try
                {
                    var stopped = await found.Value.StopAsync(cancellationToken).ConfigureAwait(false);
                    if (!stopped.Ok)
                    {
                        await _bindings.AttachAsync(address, CancellationToken.None).ConfigureAwait(false);
                        return stopped;
                    }
                    lock (_gate) _adapters.Remove(IdOf(address));
                }
                catch
                {
                    await _bindings.AttachAsync(address, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(new(ErrorCode.Internal, "Adapter type destruction failed.", ex.Message)); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask<Result> ForceDestroyByTypeAsync(string concreteTypeId, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var addresses = List().Where(a => a.TypeId == concreteTypeId).Select(a => a.Address).ToArray();
            foreach (var address in addresses)
            {
                var detached = _bindings is IForceInstanceBindings forceBindings
                    ? await forceBindings.ForceDetachAsync(address, cancellationToken).ConfigureAwait(false)
                    : await _bindings.DetachAsync(address, cancellationToken).ConfigureAwait(false);
                if (!detached.Ok) return detached;
                lock (_gate) _adapters.Remove(IdOf(address));
                _parameters?.Unregister(address.ToString());
                // Out of the graph too: a removed Adapter that is still on a schedule is a module that exists on paper.
                if (_instances is not null)
                    await _instances.ForgetAsync(new ModuleId(ModuleNames.AdapterManager), address.ToModuleId(),
                        CancellationToken.None).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(new(ErrorCode.Internal, "Forced Adapter disconnection failed.", ex.Message)); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask<Result> DestroyAsync(Address address, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var found = Get(address);
            if (!found.Ok || found.Value is null) return Result.Failure(found.Error!);
            var detached = await _bindings.DetachAsync(address, cancellationToken).ConfigureAwait(false);
            if (!detached.Ok) return detached;
            try
            {
                var stopped = await found.Value.StopAsync(cancellationToken).ConfigureAwait(false);
                if (!stopped.Ok)
                {
                    await _bindings.AttachAsync(address, CancellationToken.None).ConfigureAwait(false);
                    return stopped;
                }
                lock (_gate) _adapters.Remove(IdOf(address));
                _parameters?.Unregister(address.ToString());
                if (_specs is not null)
                {
                    var removed = await _specs.DeleteAsync(address, cancellationToken).ConfigureAwait(false);
                    if (!removed.Ok) return removed;
                }
                return Result.Success();
            }
            catch
            {
                await _bindings.AttachAsync(address, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(new(ErrorCode.Internal, "Adapter destruction failed.", ex.Message)); }
        finally { _lifecycle.Release(); }
    }
}

public sealed class EventQueue : IEventSink
{
    private readonly Channel<RoutedEvent> _queue;
    public EventQueue(int capacity = 256)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _queue = Channel.CreateBounded<RoutedEvent>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    }
    public ValueTask<Result> PublishAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (routedEvent is null || string.IsNullOrWhiteSpace(routedEvent.Name) || !routedEvent.Source.IsInstance ||
            string.IsNullOrWhiteSpace(routedEvent.PayloadJson) || routedEvent.Target is { IsInstance: false })
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, "Invalid routed event.")));
        if (!_queue.Writer.TryWrite(routedEvent with
            {
                EnqueuedAt = routedEvent.EnqueuedAt ?? DateTimeOffset.UtcNow,
                CauseSignalId = routedEvent.CauseSignalId ?? SignalScope.CurrentId
            }))
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "Event queue is full or closed.", Retryable: true)));
        return ValueTask.FromResult(Result.Success());
    }
    public IAsyncEnumerable<RoutedEvent> ReadAllAsync(CancellationToken ct) => _queue.Reader.ReadAllAsync(ct);
    public bool TryRead(out RoutedEvent routedEvent) => _queue.Reader.TryRead(out routedEvent!);
    public void Complete() => _queue.Writer.TryComplete();
}

/// <param name="interceptor">
/// One look at an event before it is dispatched, or null for "nobody needs one". Instructions live here
/// (<c>ALN.Commands</c>): they answer a message and the message then goes nowhere — no Channel, no script, no Session —
/// which is only possible before the first dispatcher sees it.
/// </param>
public sealed class EventPump(EventQueue queue, Func<RoutedEvent, CancellationToken, ValueTask<Result>> dispatch, ISignalLog signalLog, IEventSettler? settler = null, IEnvelopeInterceptor? interceptor = null)
{
    /// <summary>
    /// How many events may be in flight at once, across every lane.
    ///
    /// Not a throughput knob: it is what keeps a burst of events from becoming a burst of tasks. The pump used to run
    /// one event at a time, which is why a forty-second compaction inside one conversation stopped every other
    /// conversation, every instruction and the lifecycle events queued behind it.
    /// </summary>
    private const int MaxInFlight = 64;

    /// <summary>The last event queued for each lane; the next one waits on it, which is what keeps a lane in order.</summary>
    private readonly Dictionary<string, Task> _tails = new(StringComparer.Ordinal);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var inFlight = new HashSet<Task>();
        try
        {
            await foreach (var routedEvent in queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                inFlight.RemoveWhere(static task => task.IsCompleted);
                inFlight.Add(Enqueue(LaneOf(routedEvent), routedEvent, cancellationToken));
                if (inFlight.Count >= MaxInFlight) await Task.WhenAny(inFlight).ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever was already taken off the queue finishes: half a round leaves a Channel row nobody answers and a
            // mailbox file nobody acknowledges, and both of those are replayed on the next boot.
            if (inFlight.Count > 0) await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The address an event belongs to: its target when it has one, its source otherwise.
    ///
    /// This is the thing a person would call "the conversation it is about", and it is the right key because the two
    /// halves of the answer differ. Events about one conversation have to stay in order — two turns of one Session
    /// interleaved would each read half a conversation and answer it. Events about different conversations have
    /// nothing to say to each other, and making them wait is what turned one slow turn into a silent host.
    ///
    /// It is also where instructions get their head start, without a rule of their own: an instruction arrives as an
    /// Adapter envelope (<c>docs/32</c>), whose lane is the Adapter, so it is never queued behind a Channel's turn.
    /// </summary>
    private static string LaneOf(RoutedEvent routedEvent) =>
        (routedEvent.Target ?? routedEvent.Source).ToString();

    /// <summary>
    /// Queues one event behind the previous event of its lane, starting it as soon as that one finishes.
    ///
    /// Called from the pump's own loop and nothing else, which is what makes a plain dictionary the right container.
    /// </summary>
    private Task Enqueue(string lane, RoutedEvent routedEvent, CancellationToken cancellationToken)
    {
        var previous = _tails.TryGetValue(lane, out var tail) ? tail : Task.CompletedTask;
        var next = AfterAsync(previous, routedEvent, cancellationToken);
        _tails[lane] = next;
        return next;
    }

    private async Task AfterAsync(Task previous, RoutedEvent routedEvent, CancellationToken cancellationToken)
    {
        // A lane whose previous event failed carries on. The failure was recorded where it happened; dropping everything
        // after it would turn one bad message into a conversation that stopped answering.
        try { await previous.ConfigureAwait(false); }
        catch (Exception) { /* already reported by whichever event threw */ }

        try { await HandleAsync(routedEvent, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // A dispatch answers with a Result, so reaching here is a bug rather than a refusal. Saying so is what keeps
            // it from disappearing into an abandoned task.
            signalLog.Write(new()
            {
                Group = "queue",
                Name = routedEvent.Name,
                StepId = routedEvent.EventId,
                From = routedEvent.Source.ToString(),
                Mechanism = "pump",
                Outcome = "crashed",
                Level = SignalLevel.Error,
                Detail = new Dictionary<string, string>
                {
                    ["type"] = ex.GetType().Name,
                    ["message"] = ex.Message
                }
            });
        }
    }

    private async ValueTask HandleAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var intakeId = Guid.NewGuid();
        signalLog.Write(new()
        {
            Id = intakeId,
            // If a handler emitted this event, the intake is a branch of that handler, not a new root.
            ParentId = routedEvent.CauseSignalId ?? SignalScope.CurrentId,
            Group = "queue",
            Name = routedEvent.Name,
            StepId = routedEvent.EventId,
            From = routedEvent.Source.ToString(),
            To = routedEvent.Target?.ToString() ?? "",
            Mechanism = "dequeue",
            Outcome = "ok",
            DurationMs = routedEvent.EnqueuedAt is { } enqueued ? (now - enqueued).TotalMilliseconds : 0,
            Level = SignalLevel.Info
        });
        Result dispatched;
        using (SignalScope.Enter(intakeId, routedEvent.EventId))
        {
            // The one look before dispatch. A handled event still settles (below): the mailbox file must still be
            // acknowledged, or a restart would replay a message that has already been answered.
            Result<EnvelopeDisposition>? intercepted = interceptor is null
                ? null
                : await interceptor.InterceptAsync(routedEvent, cancellationToken).ConfigureAwait(false);
            dispatched = intercepted switch
            {
                null => await dispatch(routedEvent, cancellationToken).ConfigureAwait(false),
                // Mine, and it went wrong: finished with an error, not passed on. An instruction that failed must
                // not fall through to a script and be read as text.
                { Ok: false, Error: { } failure } => Result.Failure(failure),
                { Value: EnvelopeDisposition.Handled } => Result.Success(),
                _ => await dispatch(routedEvent, cancellationToken).ConfigureAwait(false)
            };
        }
        if (!dispatched.Ok)
            signalLog.Write(new()
            {
                ParentId = intakeId,
                Group = "queue",
                Name = routedEvent.Name,
                StepId = routedEvent.EventId,
                From = routedEvent.Source.ToString(),
                Mechanism = "pump",
                Outcome = "failed",
                Level = SignalLevel.Warn,
                Detail = new Dictionary<string, string>
                {
                    ["code"] = dispatched.Error?.Code.ToString() ?? "unknown",
                    ["message"] = dispatched.Error?.Message ?? ""
                }
            });
        // Settling happens for a finished round whatever its outcome: a poisoned event that failed is
        // worse to retry forever than to acknowledge once and leave the failure in the log.
        if (settler is null) return;
        var settled = await settler.SettleAsync(routedEvent, cancellationToken).ConfigureAwait(false);
        if (!settled.Ok)
            signalLog.Write(new()
            {
                ParentId = intakeId,
                Group = "queue",
                Name = routedEvent.Name,
                StepId = routedEvent.EventId,
                From = routedEvent.Source.ToString(),
                Mechanism = "settle",
                Outcome = "failed",
                Level = SignalLevel.Warn,
                Detail = new Dictionary<string, string>
                {
                    ["code"] = settled.Error?.Code.ToString() ?? "unknown",
                    ["message"] = settled.Error?.Message ?? ""
                }
            });
    }
}
