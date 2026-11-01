using System.Collections.Concurrent;
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
        ["model"] = new("model", null, true)
    };
    private readonly Dictionary<string, HashSet<string>> _ownedTypes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool TryGet(string id, out AdapterTypeDefinition definition)
    { lock (_gate) return _types.TryGetValue(id, out definition!); }
    public bool IsKnownAbstract(string typeId)
    { lock (_gate) return _types.TryGetValue(typeId, out var type) && type.IsAbstract; }
    public bool IsConcrete(string id)
    { lock (_gate) return _types.TryGetValue(id, out var type) && !type.IsAbstract; }
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

public sealed class AdapterManager : IAdapterManager
{
    private readonly Dictionary<Address, IChatAdapter> _adapters = new();
    private readonly IModuleTypeCatalog _types;
    private readonly IInstanceBindings _bindings;
    private readonly IAdapterFactoryRegistry? _factories;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();

    public AdapterManager(IModuleTypeCatalog types, IInstanceBindings bindings, IAdapterFactoryRegistry? factories = null)
    {
        _types = types;
        _bindings = bindings;
        _factories = factories;
    }

    public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, CancellationToken cancellationToken) =>
        _factories is null
            ? ValueTask.FromResult(Result<IChatAdapter>.Failure(new(ErrorCode.Unavailable, "No Adapter factory registry is configured.")))
            : CreateCoreAsync(spec, s => _factories.Create(s.Address.TypeId, s), cancellationToken);
    public Result<IChatAdapter> Get(Address address)
    {
        lock (_gate)
            return _adapters.TryGetValue(address, out var adapter) ? Result<IChatAdapter>.Success(adapter)
                : Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, $"Adapter not found: {address}"));
    }
    public IReadOnlyList<AdapterInfo> List()
    {
        lock (_gate) return _adapters.Values.Select(a => new AdapterInfo(a.Address, a.Descriptor, a.State)).ToArray();
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
        if (spec.Address.Module != "adapter" || spec.Address.Kind is null || spec.Address.Instance is null || !_types.IsConcrete(spec.Address.TypeId))
            return Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, "Only registered concrete Adapter types with an instance id can be created."));
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_adapters.ContainsKey(spec.Address)) return Result<IChatAdapter>.Failure(new(ErrorCode.Conflict, $"Adapter already exists: {spec.Address}"));
                if (_adapters.Values.Any(a => a.Descriptor.PlatformId == spec.Descriptor.PlatformId && a.Descriptor.AccountId == spec.Descriptor.AccountId))
                    return Result<IChatAdapter>.Failure(new(ErrorCode.Conflict, "Platform/account is already registered."));
            }
            var created = factory(spec);
            if (!created.Ok || created.Value is null) return Result<IChatAdapter>.Failure(created.Error ?? new(ErrorCode.Internal, "Adapter factory failed."));
            var adapter = created.Value;
            if (adapter.Address != spec.Address || adapter.Descriptor != spec.Descriptor)
                return Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, "Factory output does not match its AdapterSpec."));
            lock (_gate) _adapters.Add(spec.Address, adapter);
            var attached = await _bindings.AttachAsync(spec.Address, cancellationToken).ConfigureAwait(false);
            if (!attached.Ok)
            {
                lock (_gate) _adapters.Remove(spec.Address);
                return Result<IChatAdapter>.Failure(attached.Error!);
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
    public async ValueTask<Result<IReadOnlyList<AdapterInfo>>> SnapshotByTypeAsync(string concreteTypeId, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Result<IReadOnlyList<AdapterInfo>>.Success(List().Where(a => a.Address.TypeId == concreteTypeId).ToArray()); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask<Result> DestroyByTypeAsync(string concreteTypeId, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var addresses = List().Where(a => a.Address.TypeId == concreteTypeId).Select(a => a.Address).ToArray();
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
                    lock (_gate) _adapters.Remove(address);
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
            var addresses = List().Where(a => a.Address.TypeId == concreteTypeId).Select(a => a.Address).ToArray();
            foreach (var address in addresses)
            {
                var detached = _bindings is IForceInstanceBindings forceBindings
                    ? await forceBindings.ForceDetachAsync(address, cancellationToken).ConfigureAwait(false)
                    : await _bindings.DetachAsync(address, cancellationToken).ConfigureAwait(false);
                if (!detached.Ok) return detached;
                lock (_gate) _adapters.Remove(address);
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
                lock (_gate) _adapters.Remove(address);
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
        if (!_queue.Writer.TryWrite(routedEvent.EnqueuedAt is null
                ? routedEvent with { EnqueuedAt = DateTimeOffset.UtcNow }
                : routedEvent))
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "Event queue is full or closed.", Retryable: true)));
        return ValueTask.FromResult(Result.Success());
    }
    public IAsyncEnumerable<RoutedEvent> ReadAllAsync(CancellationToken ct) => _queue.Reader.ReadAllAsync(ct);
    public void Complete() => _queue.Writer.TryComplete();
}

public sealed class EventPump(EventQueue queue, Func<RoutedEvent, CancellationToken, ValueTask<Result>> dispatch, ISignalLog signalLog)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var routedEvent in queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            signalLog.Write(new()
            {
                Group = "queue",
                Name = routedEvent.Name,
                StepId = routedEvent.EventId,
                From = routedEvent.Source.ToString(),
                To = routedEvent.Target?.ToString() ?? "",
                Mechanism = "dequeue",
                Outcome = "ok",
                DurationMs = routedEvent.EnqueuedAt is { } enqueued ? (now - enqueued).TotalMilliseconds : 0,
                Level = SignalLevel.Debug
            });
            var dispatched = await dispatch(routedEvent, cancellationToken).ConfigureAwait(false);
            if (!dispatched.Ok)
                signalLog.Write(new()
                {
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
        }
    }
}
