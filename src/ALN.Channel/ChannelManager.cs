using System.Text.Json;
using ALN.Contracts;

namespace ALN.Channel;

public sealed class ChannelManager(IAdapterManager adapters, IEventSink events, IInstanceBindings bindings,
    IChannelSpecStore? specs = null, IMessageStore? messages = null, IParameterRegistry? parameters = null)
    : IChannelManager, ILifecycleParticipant
{
    private readonly IAdapterManager _adapters = adapters;
    private readonly IEventSink _events = events;
    private readonly IInstanceBindings _bindings = bindings;
    private readonly IChannelSpecStore? _specs = specs;
    private readonly IMessageStore? _messages = messages;
    private readonly IParameterRegistry? _parameters = parameters;
    private readonly Dictionary<Address, ChannelInstance> _channels = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public Result<ChannelInfo> FindBound(Address adapter, string platformConversationId)
    {
        lock (_gate)
        {
            var channel = _channels.Values.FirstOrDefault(c => c.Running && !c.Draining && c.Bindings.Any(b =>
                b.Adapter == adapter && b.PlatformConversationId == platformConversationId));
            return channel is null
                ? Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "No running Channel is bound to this Adapter conversation."))
                : Result<ChannelInfo>.Success(channel.ToInfo());
        }
    }

    public async ValueTask<Result<ChannelInfo>> CreateAsync(ChannelSpec spec, CancellationToken cancellationToken)
    {
        if (spec.Address.Module != "channel" || spec.Address.Kind != "conversation" || string.IsNullOrWhiteSpace(spec.Address.Instance))
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "Channel address must be channel.conversation#<id>."));
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Bindings is null || spec.Bindings.Count == 0)
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "Channel requires a name and at least one Adapter binding."));
        // A window bounds what one turn may carry; zero and negatives are not "no window", they are a bug in
        // the config, and silently treating them as unlimited is how a typo becomes a huge prompt.
        if (spec.MaxMessages is <= 0)
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, $"Channel maxMessages must be positive: {spec.MaxMessages}"));
        if (spec.Bindings.Any(b => !b.Adapter.IsInstance || !IsValidConversationId(b.PlatformConversationId)))
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "Channel binding contains an invalid Adapter address or conversation id."));
        if (spec.Bindings.Distinct().Count() != spec.Bindings.Count)
            return Result<ChannelInfo>.Failure(new(ErrorCode.Conflict, "Channel contains duplicate bindings."));
        // The bound Adapter is NOT required to exist yet: startup order is not guaranteed and a
        // Channel may be created before its Adapter. Availability is checked at delivery instead.

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
                var instance = new ChannelInstance(spec);
                _channels.Add(spec.Address, instance);
                ApplyParameters(instance);
            }
            var mounted = await _bindings.AttachAsync(spec.Address, cancellationToken).ConfigureAwait(false);
            if (!mounted.Ok)
            {
                lock (_gate) _channels.Remove(spec.Address);
                return Result<ChannelInfo>.Failure(mounted.Error!);
            }
            if (_specs is not null)
            {
                var persisted = await _specs.SaveAsync(spec, cancellationToken).ConfigureAwait(false);
                if (!persisted.Ok)
                {
                    lock (_gate) _channels.Remove(spec.Address);
                    await _bindings.DetachAsync(spec.Address, CancellationToken.None).ConfigureAwait(false);
                    return Result<ChannelInfo>.Failure(persisted.Error!);
                }
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
            if (_specs is not null)
            {
                var removed = await _specs.DeleteAsync(address, cancellationToken).ConfigureAwait(false);
                if (!removed.Ok) return removed;
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
            // Delivery is where availability actually matters: the Channel may have been created
            // before its Adapter, so this is the first moment we can honestly check it.
            if (!_adapters.Get(binding.Adapter).Ok)
                return Result<int>.Failure(new(ErrorCode.Unavailable,
                    $"Bound Adapter is not available, message not delivered: {binding.Adapter}"));
            var message = new ChannelMessage(envelope.EventId, channel.Address, binding, envelope.PlatformMessageId,
                envelope.Text, envelope.At, envelope.Sender, envelope.MentionsSelf);
            if (_messages is not null)
            {
                // The channel keeps its own history; the mailbox folder is transport, not storage.
                var recorded = await _messages.AppendAsync(new StoredMessage(envelope.EventId.ToString("N"), channel.Address,
                    envelope.PlatformConversationId, envelope.PlatformMessageId, envelope.Sender, envelope.Text,
                    envelope.At), cancellationToken).ConfigureAwait(false);
                if (!recorded.Ok) return Result<int>.Failure(recorded.Error!);
            }
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

    /// <summary>
    /// Manager startup: revive every stored Channel, then give each one the same signal so its
    /// mounted scripts start too. Startup does not invent Channels — a folder is not a Channel.
    /// </summary>
    public string Name => "channels";

    public async ValueTask<Result> StartAsync(LifecycleSignal signal, CancellationToken cancellationToken)
    {
        if (_specs is null) return Result.Success();
        var stored = await _specs.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!stored.Ok) return Result.Failure(stored.Error!);
        foreach (var spec in stored.Value!)
        {
            if (Get(spec.Address).Ok) continue;
            var created = await CreateAsync(spec, cancellationToken).ConfigureAwait(false);
            if (!created.Ok) return Result.Failure(created.Error!);
            var started = await StartAsync(spec.Address, cancellationToken).ConfigureAwait(false);
            if (!started.Ok)
            {
                await DestroyAsync(spec.Address, CancellationToken.None).ConfigureAwait(false);
                return started;
            }
            await _events.PublishAsync(new(EventNames.LifecycleStart, spec.Address, Guid.NewGuid(),
                JsonSerializer.Serialize(signal.For(spec.Address), JsonOptions), spec.Address), cancellationToken).ConfigureAwait(false);
        }
        return Result.Success();
    }

    public async ValueTask<Result> StopAsync(LifecycleSignal signal, CancellationToken cancellationToken)
    {
        foreach (var address in List().Select(c => c.Address))
        {
            await StopAsync(address, cancellationToken).ConfigureAwait(false);
            await _events.PublishAsync(new(EventNames.LifecycleStop, address, Guid.NewGuid(),
                JsonSerializer.Serialize(signal.For(address), JsonOptions), address), cancellationToken).ConfigureAwait(false);
        }
        return Result.Success();
    }

    private sealed class ChannelInstance(ChannelSpec spec)
    {
        public Address Address { get; } = spec.Address;
        public string Name { get; } = spec.Name;
        public IReadOnlyList<ChannelBinding> Bindings { get; } = Array.AsReadOnly(spec.Bindings.ToArray());
        /// <summary>Spec value unless overridden at runtime; the window is read on every activation.</summary>
        public int? MaxMessages { get; set; } = spec.MaxMessages;
        public bool Running { get; set; }
        public bool Draining { get; set; }
        public ChannelInfo ToInfo() => new(Address, Name, Bindings, Running, MaxMessages);

        public IParameterTarget Parameters(ChannelManager owner) => new ChannelParameters(this, owner);
    }

    /// <summary>
    /// The tunables of one Channel. The window is the one that matters today: it is read per activation, so
    /// setting it has effect on the very next turn without restarting anything.
    /// </summary>
    private sealed class ChannelParameters(ChannelInstance channel, ChannelManager owner) : IParameterTarget
    {
        public const string MaxMessagesName = "maxMessages";

        public string ParameterScope => channel.Address.ToString();

        public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [
            new(MaxMessagesName, ParameterKind.Integer, null,
                "一轮最多携带多少条频道消息；更早的折叠成“省略更早的 N 条”。留空表示不限。",
                ParameterApply.Immediate, Secret: false, Minimum: 1, Maximum: 100_000)
        ];

        public string? ReadParameter(string name) =>
            string.Equals(name, MaxMessagesName, StringComparison.Ordinal)
                ? channel.MaxMessages?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;

        public ValueTask<Result> ApplyParameterAsync(string name, string value, CancellationToken cancellationToken)
        {
            _ = owner;
            if (!string.Equals(name, MaxMessagesName, StringComparison.Ordinal))
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"Unknown parameter: {name}")));
            if (string.IsNullOrWhiteSpace(value))
            {
                channel.MaxMessages = null;
                return ValueTask.FromResult(Result.Success());
            }
            if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var window) || window <= 0)
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"maxMessages must be a positive integer: {value}")));
            channel.MaxMessages = window;
            return ValueTask.FromResult(Result.Success());
        }
    }

    /// <summary>Registers a channel's tunables so they can be listed and changed while it runs.</summary>
    private void ApplyParameters(ChannelInstance channel)
    {
        if (_parameters is null) return;
        var registered = _parameters.Register(channel.Parameters(this));
        if (!registered.Ok) return; // A duplicate scope is already somebody else's channel; nothing to do here.
    }
}
