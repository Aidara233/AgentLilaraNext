using System.Collections.Concurrent;
using System.Threading.Channels;
using ALN.Contracts;

namespace ALN.Runtime;

public sealed class ExecutionJournal : IExecutionJournal
{
    private readonly ConcurrentQueue<ExecutionRecord> _records = new();
    private readonly int _capacity;
    public ExecutionJournal(int capacity = 10_000) => _capacity = Math.Max(1, capacity);
    public void Record(ExecutionRecord record)
    {
        _records.Enqueue(record);
        while (_records.Count > _capacity) _records.TryDequeue(out _);
    }
    public IReadOnlyList<ExecutionRecord> Snapshot() => _records.ToArray();
}

public sealed record AdapterTypeDefinition(string Id, string? ParentId, bool IsAbstract);

public sealed class AdapterTypeCatalog : IModuleTypeCatalog
{
    private readonly Dictionary<string, AdapterTypeDefinition> _types = new(StringComparer.Ordinal)
    {
        ["adapter"] = new("adapter", null, true),
        ["adapter.file"] = new("adapter.file", "adapter", false)
    };
    public bool TryGet(string id, out AdapterTypeDefinition definition) => _types.TryGetValue(id, out definition!);
    public bool IsKnownAbstract(string typeId) => _types.TryGetValue(typeId, out var type) && type.IsAbstract;
    public bool IsConcrete(string id) => _types.TryGetValue(id, out var type) && !type.IsAbstract;
    public bool IsAssignableTo(string concreteTypeId, string abstractTypeId)
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

public sealed class AdapterManager : IAdapterManager
{
    private readonly Dictionary<Address, IChatAdapter> _adapters = new();
    private readonly IModuleTypeCatalog _types;
    private readonly IInstanceBindings _bindings;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();

    public AdapterManager(IModuleTypeCatalog types, IInstanceBindings bindings)
    {
        _types = types;
        _bindings = bindings;
    }
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
    public async ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, Func<AdapterSpec, IChatAdapter> factory, CancellationToken cancellationToken)
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
            var adapter = factory(spec);
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
    public async ValueTask<Result> DestroyAsync(Address address, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var found = Get(address);
            if (!found.Ok || found.Value is null) return Result.Failure(found.Error!);
            var stopped = await found.Value.StopAsync(cancellationToken).ConfigureAwait(false);
            if (!stopped.Ok) return stopped;
            var detached = await _bindings.DetachAsync(address, cancellationToken).ConfigureAwait(false);
            if (!detached.Ok) return detached;
            lock (_gate) _adapters.Remove(address);
            return Result.Success();
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
        if (routedEvent is null || string.IsNullOrWhiteSpace(routedEvent.Name) || !routedEvent.Source.IsInstance || string.IsNullOrWhiteSpace(routedEvent.PayloadJson))
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, "Invalid routed event.")));
        return EnqueueCoreAsync(routedEvent, cancellationToken);
    }
    private async ValueTask<Result> EnqueueCoreAsync(RoutedEvent routedEvent, CancellationToken ct)
    {
        try { await _queue.Writer.WriteAsync(routedEvent, ct).ConfigureAwait(false); return Result.Success(); }
        catch (ChannelClosedException ex) { return Result.Failure(new(ErrorCode.Unavailable, "Event queue is closed.", ex.Message)); }
    }
    public IAsyncEnumerable<RoutedEvent> ReadAllAsync(CancellationToken ct) => _queue.Reader.ReadAllAsync(ct);
    public void Complete() => _queue.Writer.TryComplete();
}

public sealed class EventPump(EventQueue queue, Func<RoutedEvent, CancellationToken, ValueTask<Result>> dispatch)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var routedEvent in queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            await dispatch(routedEvent, cancellationToken).ConfigureAwait(false);
    }
}
