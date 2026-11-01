using System.Text.Json;
using ALN.Contracts;

namespace ALN.Channel;

public sealed class ChannelManager(IAdapterManager adapters, IEventSink events, IInstanceBindings bindings,
    IChannelSpecStore? specs = null, IMessageStore? messages = null, IParameterRegistry? parameters = null,
    IStateStore? state = null, IRestoreReporter? report = null, IAtomicWork? atomic = null, IQueuedMessageStore? queue = null)
    : IChannelManager, ILifecycleParticipant
{
    /// <summary>Where the wish "this Channel should be running" is kept, so a stop survives a restart.</summary>
    public const string DesiredScope = "channel.desired";

    /// <summary>Accepted messages whose event round has not finished: the list a restart replays.</summary>
    public const string PendingScope = "channel.pending";

    private readonly IAdapterManager _adapters = adapters;
    private readonly IEventSink _events = events;
    private readonly IInstanceBindings _bindings = bindings;
    private readonly IChannelSpecStore? _specs = specs;
    private readonly IMessageStore? _messages = messages;
    private readonly IParameterRegistry? _parameters = parameters;
    private readonly IStateStore? _state = state;
    private readonly IRestoreReporter _report = report ?? NullRestoreReporter.Instance;
    private readonly IAtomicWork? _atomic = atomic;
    // Accepted-but-unfinished markers are their own rows: one per message, not a list rewritten per message.
    private readonly IQueuedMessageStore? _queue = queue;
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
            var set = SetRunning(address, running: true);
            if (!set.Ok) return set;
            // Running is a wish as much as a fact, so it is written down: a Channel stopped on purpose must
            // not come back to life just because the process restarted.
            var stored = await PersistDesiredAsync(address, running: true, cancellationToken).ConfigureAwait(false);
            return stored.Ok
                ? Result.Success()
                : Result.Failure(new(ErrorCode.Unavailable,
                    $"{address} is running but that could not be persisted; it will start anyway on the next boot.",
                    stored.Error?.Message));
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var set = SetRunning(address, running: false);
            if (!set.Ok) return set;
            var stored = await PersistDesiredAsync(address, running: false, cancellationToken).ConfigureAwait(false);
            return stored.Ok
                ? Result.Success()
                : Result.Failure(new(ErrorCode.Unavailable,
                    $"{address} is stopped but that could not be persisted; it will be running again after a restart.",
                    stored.Error?.Message));
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>In-memory only: shutdown uses this, because stopping everything is not a change of wish.</summary>
    private Result SetRunning(Address address, bool running)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(address, out var channel)) return Result.Failure(new(ErrorCode.Invalid, $"Channel not found: {address}"));
            channel.Running = running;
            return Result.Success();
        }
    }

    private async ValueTask<Result> PersistDesiredAsync(Address address, bool running, CancellationToken cancellationToken)
    {
        if (_state is null) return Result.Success();
        var json = JsonSerializer.Serialize(new DesiredState(running), JsonOptions);
        return await _state.WriteAsync(DesiredScope, StateName.Encode(address.ToString()), json, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<bool?>> ReadDesiredAsync(Address address, CancellationToken cancellationToken)
    {
        if (_state is null) return Result<bool?>.Success(null);
        var read = await _state.ReadAsync(DesiredScope, StateName.Encode(address.ToString()), cancellationToken).ConfigureAwait(false);
        if (read.Error is { } readError) return Result<bool?>.Failure(readError);
        // Null value with no error means "never written": Result<bool?>.Ok is false in that case, so the
        // absence has to be read off Error, not Ok.
        if (read.Value is null) return Result<bool?>.Success(null);
        try
        {
            return Result<bool?>.Success(JsonSerializer.Deserialize<DesiredState>(read.Value, JsonOptions)?.Running);
        }
        catch (JsonException ex)
        {
            return Result<bool?>.Failure(new(ErrorCode.Invalid, "Stored Channel state is unreadable.", ex.Message));
        }
    }

    private sealed record DesiredState(bool Running);
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
            // A destroyed Channel takes its wish with it; otherwise a new Channel with the same address would
            // inherit "stopped" from one that no longer exists.
            // A destroyed Channel takes its wish and its unfinished markers with it; otherwise a new Channel
            // with the same address would inherit them from one that no longer exists.
            if (_state is not null)
                await _state.DeleteAsync(DesiredScope, StateName.Encode(address.ToString()), cancellationToken).ConfigureAwait(false);
            if (_queue is not null) await _queue.ClearAsync(PendingScope, address, cancellationToken).ConfigureAwait(false);
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
            // One fact, one transaction: the history row and the "accepted but unfinished" marker are written
            // together. Half of them is either a message nobody will look at or a marker naming nothing.
            var duplicate = false;
            var written = await AtomicallyAsync(async token =>
            {
                if (_messages is not null)
                {
                    // The channel keeps its own history; the mailbox folder is transport, not storage.
                    var recorded = await _messages.AppendAsync(new StoredMessage(envelope.EventId.ToString("D"),
                        channel.Address, envelope.PlatformConversationId, envelope.PlatformMessageId,
                        envelope.Sender, envelope.Text, envelope.At), token).ConfigureAwait(false);
                    if (recorded.Error?.Code == ErrorCode.Conflict)
                    {
                        // The same platform message, already recorded. The wake-up for a first delivery
                        // belongs to the pending marker, so there is nothing here worth waking twice.
                        duplicate = true;
                        return Result.Success();
                    }
                    if (!recorded.Ok) return recorded;
                }
                return await AddPendingAsync(channel.Address, new PendingAccept(envelope.EventId.ToString("D"),
                    envelope.PlatformMessageId, binding.Adapter.ToString(), binding.PlatformConversationId,
                    envelope.Text, envelope.At, envelope.Sender), token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            if (!written.Ok) return Result<int>.Failure(written.Error!);
            if (duplicate) continue;
            var routed = new RoutedEvent("channel.message.accepted", channel.Address, envelope.EventId,
                JsonSerializer.Serialize(message, JsonOptions));
            var queued = await _events.PublishAsync(routed, cancellationToken).ConfigureAwait(false);
            if (!queued.Ok) return Result<int>.Failure(queued.Error!);
            accepted++;
        }
        return Result<int>.Success(accepted);
    }

    public async ValueTask<Result> RecordReplyAsync(Address channel, Guid answeredMessageId, string platformConversationId,
        string text, string? platformMessageId, CancellationToken cancellationToken)
    {
        if (_messages is null) return Result.Success();
        var instance = Get(channel);
        if (!instance.Ok || instance.Value is null)
            return Result.Failure(new(ErrorCode.Invalid, $"Channel not found: {channel}"));
        // One row per reply, pointing at the message it answers. That row is what makes "already answered"
        // answerable, which is what keeps a replayed round from replying a second time.
        return await _messages.AppendAsync(new StoredMessage(Guid.NewGuid().ToString("N"), channel, platformConversationId,
            platformMessageId, null, text, DateTimeOffset.UtcNow, MessageDirection.Outbound, answeredMessageId.ToString("D")),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result> CompleteAcceptAsync(Address channel, Guid messageId, CancellationToken cancellationToken)
    {
        // Removing a marker that is not there is the normal case for a message that never became pending
        // (a stopped Channel, a duplicate delivery), so it is not an error.
        if (_queue is null) return Result.Success();
        return await _queue.AcknowledgeAsync(PendingScope, channel, messageId.ToString("D"), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<int>> ReplayPendingAsync(CancellationToken cancellationToken)
    {
        var replayed = 0;
        var alreadyAnswered = 0;
        foreach (var channel in List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = await ReadPendingAsync(channel.Address, cancellationToken).ConfigureAwait(false);
            if (pending.Error is { } readError) return Result<int>.Failure(readError);
            foreach (var entry in pending.Value!)
            {
                if (!Guid.TryParse(entry.MessageId, out var eventId))
                {
                    // A marker we cannot even address: drop it rather than replay it forever.
                    await CompleteAcceptAsync(channel.Address, Guid.Empty, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var binding = channel.Bindings.FirstOrDefault(candidate =>
                    candidate.Adapter.ToString() == entry.Adapter && candidate.PlatformConversationId == entry.Conversation);
                if (binding is null) continue;   // the binding changed under it; leave the marker alone
                // Answered already? Then only the marker survived the crash, and clearing it is the whole job.
                // This is the last window closed: a reply that went out can no longer be sent twice.
                if (_messages is not null)
                {
                    var answered = await _messages.HasReplyAsync(channel.Address, eventId.ToString("D"), cancellationToken).ConfigureAwait(false);
                    if (answered.Error is { } answerError) return Result<int>.Failure(answerError);
                    if (answered.Value)
                    {
                        await CompleteAcceptAsync(channel.Address, eventId, cancellationToken).ConfigureAwait(false);
                        alreadyAnswered++;
                        continue;
                    }
                }
                var message = new ChannelMessage(eventId, channel.Address, binding, entry.PlatformMessageId,
                    entry.Text, entry.At, entry.Sender);
                // The same event id, so the original round and this one are the same round as far as any
                // downstream bookkeeping (and the acknowledgement) is concerned.
                var published = await _events.PublishAsync(new("channel.message.accepted", channel.Address, eventId,
                    JsonSerializer.Serialize(message, JsonOptions)), cancellationToken).ConfigureAwait(false);
                if (!published.Ok) return Result<int>.Failure(published.Error!);
                replayed++;
            }
        }
        if (replayed > 0) _report.Note("channels", $"{replayed} accepted message(s) replayed: they were never finished");
        if (alreadyAnswered > 0) _report.Note("channels", $"{alreadyAnswered} message(s) were already answered; only their marker was left behind");
        return Result<int>.Success(replayed);
    }

    /// <summary>Runs the body as one unit when the storage backend can promise it; sequential otherwise.</summary>
    private ValueTask<Result> AtomicallyAsync(Func<CancellationToken, ValueTask<Result>> body, CancellationToken cancellationToken) =>
        _atomic is null ? body(cancellationToken) : _atomic.AtomicallyAsync(body, cancellationToken);

    private async ValueTask<Result<IReadOnlyList<PendingAccept>>> ReadPendingAsync(Address channel, CancellationToken cancellationToken)
    {
        if (_queue is null) return Result<IReadOnlyList<PendingAccept>>.Success([]);
        var stored = await _queue.ListAsync(PendingScope, channel, cancellationToken).ConfigureAwait(false);
        if (stored.Error is { } readError) return Result<IReadOnlyList<PendingAccept>>.Failure(readError);
        var pending = new List<PendingAccept>(stored.Value!.Count);
        foreach (var entry in stored.Value!)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<PendingAccept>(entry.Payload, JsonOptions);
                if (parsed is not null) pending.Add(parsed);
            }
            catch (JsonException ex)
            {
                // A marker we cannot read would replay forever otherwise. The message is still in history, so
                // the honest move is to drop this one and say so.
                _report.Note("channels", $"a pending marker for {channel} was unreadable ({ex.Message}); dropped");
                await _queue.AcknowledgeAsync(PendingScope, channel, entry.EventId, cancellationToken).ConfigureAwait(false);
            }
        }
        return Result<IReadOnlyList<PendingAccept>>.Success(pending);
    }

    private async ValueTask<Result> AddPendingAsync(Address channel, PendingAccept entry, CancellationToken cancellationToken)
    {
        if (_queue is null) return Result.Success();
        // One row, one insert. The marker is keyed by event id, so a retried accept replaces rather than adds.
        var queued = new QueuedMessage(entry.MessageId, entry.PlatformMessageId, entry.Conversation, entry.Sender,
            entry.Text, entry.At, JsonSerializer.Serialize(entry, JsonOptions));
        return await _queue.EnqueueAsync(PendingScope, channel, queued, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An accepted message whose round never finished. Enough to rebuild the wake-up without the history.</summary>
    private sealed record PendingAccept(string MessageId, string? PlatformMessageId, string Adapter, string Conversation,
        string Text, DateTimeOffset At, string? Sender);

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
        var revived = 0;
        var keptStopped = 0;
        foreach (var spec in stored.Value!)
        {
            if (Get(spec.Address).Ok) continue;
            var created = await CreateAsync(spec, cancellationToken).ConfigureAwait(false);
            if (!created.Ok) return Result.Failure(created.Error!);
            // A Channel comes back in the state somebody left it in, not in a default one.
            var desired = await ReadDesiredAsync(spec.Address, cancellationToken).ConfigureAwait(false);
            if (desired.Error is { } readError) return Result.Failure(readError);
            if (desired.Value == false) keptStopped++; else revived++;
            var set = SetRunning(spec.Address, desired.Value ?? true);
            if (!set.Ok)
            {
                await DestroyAsync(spec.Address, CancellationToken.None).ConfigureAwait(false);
                return set;
            }
            await _events.PublishAsync(new(EventNames.LifecycleStart, spec.Address, Guid.NewGuid(),
                JsonSerializer.Serialize(signal.For(spec.Address), JsonOptions), spec.Address), cancellationToken).ConfigureAwait(false);
        }
        if (revived + keptStopped > 0)
            _report.Note("channels", $"{revived} revived running, {keptStopped} kept stopped as they were left");
        return Result.Success();
    }

    public async ValueTask<Result> StopAsync(LifecycleSignal signal, CancellationToken cancellationToken)
    {
        foreach (var address in List().Select(c => c.Address))
        {
            // Shutdown is not a change of wish: stop in memory, leave the stored state alone.
            SetRunning(address, running: false);
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
        public string? ModelProfile { get; set; }
        public bool Running { get; set; }
        public bool Draining { get; set; }
        public ChannelInfo ToInfo() => new(Address, Name, Bindings, Running, MaxMessages, ModelProfile);

        public IParameterTarget Parameters(ChannelManager owner) => new ChannelParameters(this, owner);
    }

    /// <summary>
    /// The tunables of one Channel. The window is the one that matters today: it is read per activation, so
    /// setting it has effect on the very next turn without restarting anything.
    /// </summary>
    private sealed class ChannelParameters(ChannelInstance channel, ChannelManager owner) : IParameterTarget
    {
        public const string MaxMessagesName = "maxMessages";
        public const string ModelProfileName = "modelProfile";

        public string ParameterScope => channel.Address.ToString();

        public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [
            new(MaxMessagesName, ParameterKind.Integer, null,
                "一轮最多携带多少条频道消息；更早的折叠成“省略更早的 N 条”。留空表示不限。",
                ParameterApply.Immediate, Secret: false, Minimum: 1, Maximum: 100_000),
            new(ModelProfileName, ParameterKind.Text, null,
                "这个频道用哪个模型 profile；留空表示由宿主解析（default 优先，否则 id 序第一个）。下一轮生效。",
                ParameterApply.Immediate, Secret: false)
        ];

        public string? ReadParameter(string name) =>
            string.Equals(name, MaxMessagesName, StringComparison.Ordinal)
                ? channel.MaxMessages?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Equals(name, ModelProfileName, StringComparison.Ordinal) ? channel.ModelProfile : null;

        public ValueTask<Result> ApplyParameterAsync(string name, string value, CancellationToken cancellationToken)
        {
            _ = owner;
            if (string.Equals(name, ModelProfileName, StringComparison.Ordinal))
            {
                // Empty is a real value here: it means "stop naming one and let the host resolve".
                channel.ModelProfile = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                return ValueTask.FromResult(Result.Success());
            }
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
