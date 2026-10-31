using System.Text.Json;
using ALN.Contracts;

namespace ALN.Channel;

public sealed class ChannelManager : IChannelManager
{
    private readonly IAdapterManager _adapters;
    private readonly IEventSink _events;
    private readonly IInstanceBindings _bindings;
    private readonly Dictionary<Address, ChannelInstance> _channels = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ChannelManager(IAdapterManager adapters, IEventSink events, IInstanceBindings bindings)
    { _adapters = adapters; _events = events; _bindings = bindings; }

    public Result<ChannelInfo> Get(Address address)
    {
        lock (_gate)
            return _channels.TryGetValue(address, out var channel)
                ? Result<ChannelInfo>.Success(channel.ToInfo())
                : Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, $"Channel not found: {address}"));
    }
    public IReadOnlyList<ChannelInfo> List()
    { lock (_gate) return _channels.Values.Select(c => c.ToInfo()).ToArray(); }
    public bool IsBound(Address channelAddress, Address adapter, string platformConversationId)
    {
        lock (_gate)
            return _channels.TryGetValue(channelAddress, out var channel) && channel.Running &&
                   channel.Bindings.Any(b => b.Adapter == adapter && b.PlatformConversationId == platformConversationId);
    }

    public async ValueTask<Result<ChannelInfo>> CreateAsync(ChannelSpec spec, CancellationToken cancellationToken)
    {
        if (spec.Address.Module != "channel" || spec.Address.Kind != "logical" || string.IsNullOrWhiteSpace(spec.Address.Instance))
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "Channel address must be channel.logical#<id>."));
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Bindings is null || spec.Bindings.Count == 0)
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "Channel requires a name and at least one Adapter binding."));
        if (spec.Bindings.Any(b => !b.Adapter.IsInstance || !IsValidConversationId(b.PlatformConversationId)))
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "Channel binding contains an invalid Adapter address or conversation id."));
        if (spec.Bindings.Distinct().Count() != spec.Bindings.Count)
            return Result<ChannelInfo>.Failure(new(ErrorCode.Conflict, "Channel contains duplicate bindings."));
        foreach (var binding in spec.Bindings)
            if (!_adapters.Get(binding.Adapter).Ok)
                return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, $"Bound Adapter not found: {binding.Adapter}"));

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_channels.ContainsKey(spec.Address))
                    return Result<ChannelInfo>.Failure(new(ErrorCode.Conflict, $"Channel already exists: {spec.Address}"));
                foreach (var binding in spec.Bindings)
                    if (_channels.Values.Any(c => c.Bindings.Contains(binding)))
                        return Result<ChannelInfo>.Failure(new(ErrorCode.Conflict, "An Adapter/conversation binding can belong to only one Channel."));
                _channels.Add(spec.Address, new ChannelInstance(spec));
            }
            var mounted = await _bindings.AttachAsync(spec.Address, cancellationToken).ConfigureAwait(false);
            if (!mounted.Ok)
            {
                lock (_gate) _channels.Remove(spec.Address);
                return Result<ChannelInfo>.Failure(mounted.Error!);
            }
            return Get(spec.Address);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<ChannelInfo>.Failure(new(ErrorCode.Internal, "Channel creation failed.", ex.Message)); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask<Result> StartAsync(Address address, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (!_channels.TryGetValue(address, out var channel)) return Result.Failure(new(ErrorCode.Invalid, $"Channel not found: {address}"));
                channel.Running = true;
                return Result.Success();
            }
        }
        finally { _lifecycle.Release(); }
    }
    public async ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (!_channels.TryGetValue(address, out var channel)) return Result.Failure(new(ErrorCode.Invalid, $"Channel not found: {address}"));
                channel.Running = false;
                return Result.Success();
            }
        }
        finally { _lifecycle.Release(); }
    }
    public async ValueTask<Result> DestroyAsync(Address address, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ChannelInstance channel;
            lock (_gate)
            {
                if (!_channels.TryGetValue(address, out channel!)) return Result.Failure(new(ErrorCode.Invalid, $"Channel not found: {address}"));
                channel.Draining = true;
            }
            Result detached;
            try { detached = await _bindings.DetachAsync(address, cancellationToken).ConfigureAwait(false); }
            catch
            {
                lock (_gate) channel.Draining = false;
                throw;
            }
            if (!detached.Ok)
            {
                lock (_gate) channel.Draining = false;
                return detached;
            }
            lock (_gate)
            {
                channel.Running = false;
                _channels.Remove(address);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(new(ErrorCode.Internal, "Channel destruction failed.", ex.Message)); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask<Result<int>> AcceptAsync(AdapterEnvelope envelope, CancellationToken cancellationToken)
    {
        ChannelInstance[] matched;
        lock (_gate)
            matched = _channels.Values.Where(c => c.Running && !c.Draining && c.Bindings.Any(b =>
                b.Adapter == envelope.Adapter && b.PlatformConversationId == envelope.PlatformConversationId)).ToArray();
        var accepted = 0;
        foreach (var channel in matched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = channel.Bindings.Single(b => b.Adapter == envelope.Adapter && b.PlatformConversationId == envelope.PlatformConversationId);
            var message = new ChannelMessage(envelope.EventId, channel.Address, binding, envelope.PlatformMessageId, envelope.Text, envelope.At);
            var routed = new RoutedEvent("channel.message.accepted", channel.Address, envelope.EventId,
                JsonSerializer.Serialize(message, JsonOptions));
            var queued = await _events.PublishAsync(routed, cancellationToken).ConfigureAwait(false);
            if (!queued.Ok) return Result<int>.Failure(queued.Error!);
            accepted++;
        }
        return Result<int>.Success(accepted);
    }

    private static bool IsValidConversationId(string value) =>
        value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private sealed class ChannelInstance(ChannelSpec spec)
    {
        public Address Address { get; } = spec.Address;
        public string Name { get; } = spec.Name;
        public IReadOnlyList<ChannelBinding> Bindings { get; } = Array.AsReadOnly(spec.Bindings.ToArray());
        public bool Running { get; set; }
        public bool Draining { get; set; }
        public ChannelInfo ToInfo() => new(Address, Name, Bindings, Running);
    }
}
