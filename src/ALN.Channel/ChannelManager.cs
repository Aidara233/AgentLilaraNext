using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Archive;
using ALN.Contracts.Sessions;

namespace ALN.Channel;

[Module(ChannelManagerId, Category = "channel", Role = ModuleRole.Manager, Singleton = true)]
public sealed partial class ChannelManager(IEventSink events, IInstanceBindings bindings, ICatalog catalog,
    IChannelSpecStore? specs = null, IMessageStore? messages = null, IParameterRegistry? parameters = null,
    IStateStore? state = null, IRestoreReporter? report = null, IAtomicWork? atomic = null, IQueuedMessageStore? queue = null,
    IArchiveRegistry? archive = null, IInstanceHost? instances = null, IModuleTypeCatalog? types = null)
    : IChannelManager, IModuleIndex, IModuleWiring, IInstanceSink, ILifecycleParticipant, IModule, IRequireModules
{
    /// <inheritdoc />
    public string Category => "channel";

    /// <summary>
    /// Wires the port the manager announces its instances through. Set after construction because the graph does not
    /// exist yet when the package that provides this manager is loaded.
    /// </summary>
    public void UseInstanceHost(IInstanceHost instances) => _instances = instances ?? throw new ArgumentNullException(nameof(instances));

    /// <summary>
    /// The master side of the edge: the graph offers an instance and this manager says whether it holds it. It only
    /// accepts what it already has, because a channel is created here and announced afterwards — never the reverse.
    /// </summary>
    public Result WireSlave(ModuleId slaveId, IReadOnlyList<string> kinds, IKernelModule instance)
    {
        lock (_gate)
            return _channels.Values.Any(channel => string.Equals(channel.Address.ToString(), slaveId.Value, StringComparison.Ordinal))
                ? Result.Success()
                : Result.Failure(new(ErrorCode.Invalid, $"{slaveId} is not one of this manager's Channels."));
    }

    public const string ChannelManagerId = ModuleNames.ChannelManager;
    public ModuleId Id { get; } = new(ChannelManagerId);

    /// <summary>Where the wish "this Channel should be running" is kept, so a stop survives a restart.</summary>
    public const string DesiredScope = "channel.desired";

    /// <summary>Accepted messages whose event round has not finished: the list a restart replays.</summary>
    public const string PendingScope = "channel.pending";

    /// <summary>The peers this manager will ask for by name. Nothing is ordered by this list: it is only a claim
    /// that somebody answers, checked by the kernel before the graph is declared ready.</summary>
    public IReadOnlyList<string> Requires => [ModuleNames.AdapterManager, ModuleNames.SessionManager];

    private readonly ICatalog _catalog = catalog;
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
    private readonly IArchiveRegistry? _archive = archive;
    private readonly IModuleTypeCatalog? _types = types;
    private IInstanceHost? _instances = instances;
    /// <summary>Instances by name. The address lives on the instance; the name is the key, so nothing has to take one
    /// apart to look something up.</summary>
    private readonly Dictionary<ModuleId, ChannelInstance> _channels = new();

    private static ModuleId IdOf(Address address) => address.ToModuleId();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Result<ChannelInfo> Get(Address address)
    {
        lock (_gate)
            return _channels.TryGetValue(IdOf(address), out var channel)
                ? Result<ChannelInfo>.Success(channel.ToInfo())
                : Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, $"Channel not found: {address}"));
    }
    public string? TypeOf(Address address)
    { lock (_gate) return _channels.TryGetValue(IdOf(address), out var channel) ? channel.TypeId : null; }
    public IReadOnlyList<ChannelInfo> List()
    { lock (_gate) return _channels.Values.Select(c => c.ToInfo()).ToArray(); }

    /// <inheritdoc />
    public LocatedModule? Locate(ModuleId id)
    {
        lock (_gate)
            return _channels.TryGetValue(id, out var channel)
                ? new LocatedModule(channel, new CatalogEntry(channel.Id, channel.TypeId, Category, ModuleRole.Instance))
                : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<CatalogEntry> Entries()
    { lock (_gate) return _channels.Values.Select(c => new CatalogEntry(c.Id, c.TypeId, Category, ModuleRole.Instance)).ToArray(); }
    public bool IsBound(Address channelAddress, Address adapter, string platformConversationId)
    {
        lock (_gate)
            return _channels.TryGetValue(IdOf(channelAddress), out var channel) && channel.Running &&
                   channel.Binding is { } aimed && aimed.Adapter == adapter && aimed.PlatformConversationId == platformConversationId;
    }

    public Result<ChannelInfo> FindBound(Address adapter, string platformConversationId)
    {
        lock (_gate)
        {
            var channel = _channels.Values.FirstOrDefault(c => c.Running && !c.Draining &&
                c.Binding is { } aimed && aimed.Adapter == adapter &&
                aimed.PlatformConversationId == platformConversationId);
            return channel is null
                ? Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "No running Channel is bound to this Adapter conversation."))
                : Result<ChannelInfo>.Success(channel.ToInfo());
        }
    }

    public async ValueTask<Result<ChannelInfo>> CreateAsync(ChannelSpec spec, CancellationToken cancellationToken)
    {
        if (spec.Address.Instance is null || string.IsNullOrWhiteSpace(spec.TypeId))
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "A Channel instance needs an instance id and the type it implements."));
        // The type tree says what a record is (C-04/C-06). Checked here rather than only in the action that happens to
        // call this, so a create from anywhere — action, startup revive, a script tomorrow — is judged the same way.
        if (_types is { } types && !types.IsConcrete(spec.TypeId))
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, $"Not a registered concrete Channel type: {spec.TypeId}"));
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Bindings is null || spec.Bindings.Count == 0)
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "Channel requires a name and at least one Adapter binding."));
        // One channel serves one conversation. A second binding used to be accepted and then silently unusable:
        // accepting a message had to pick a binding, and sending refused anything but exactly one. Saying no here is
        // cheaper than explaining later which of the two actually answers.
        if (spec.Bindings.Count > 1)
            return Result<ChannelInfo>.Failure(new(ErrorCode.Invalid,
                $"A Channel serves one conversation; this spec has {spec.Bindings.Count} bindings."));
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
                if (_channels.ContainsKey(IdOf(spec.Address)))
                    return Result<ChannelInfo>.Failure(new(ErrorCode.Conflict, $"Channel already exists: {spec.Address}"));
                // One channel serves one conversation, and a conversation belongs to one channel: the same rule read
                // from the other end.
                if (_channels.Values.Any(c => c.Binding == spec.Bindings[0]))
                    return Result<ChannelInfo>.Failure(new(ErrorCode.Conflict, "An Adapter/conversation binding can belong to only one Channel."));
                var instance = new ChannelInstance(spec);
                _channels.Add(instance.Id, instance);
                // A scope that is already taken means a Channel nobody removed still answers to this address; that
                // is a bug in how the old one went away, and it must not be reported as a successful creation with
                // silent tunables.
                var parameters = ApplyParameters(instance);
                if (!parameters.Ok)
                {
                    _channels.Remove(instance.Id);
                    return Result<ChannelInfo>.Failure(parameters.Error!);
                }
            }
            var mounted = await _bindings.AttachAsync(spec.Address, cancellationToken).ConfigureAwait(false);
            if (!mounted.Ok)
            {
                lock (_gate) _channels.Remove(IdOf(spec.Address));
                return Result<ChannelInfo>.Failure(mounted.Error!);
            }
            // The Channel is now a instance with an edge to this manager, so anything that asks the graph "what is
            // mounted on this instance" gets an answer. Failing here is a real failure: a Channel that exists but
            // is invisible is exactly the shape nobody can reason about later.
            if (_instances is not null)
            {
                var announced = await _instances.AttachAsync(new ModuleId(ModuleNames.ChannelManager),
                    new ModuleId(spec.Address.ToString()), spec.TypeId, ["message.accepted"], cancellationToken).ConfigureAwait(false);
                if (!announced.Ok)
                {
                    lock (_gate) _channels.Remove(IdOf(spec.Address));
                    await _bindings.DetachAsync(spec.Address, CancellationToken.None).ConfigureAwait(false);
                    return Result<ChannelInfo>.Failure(announced.Error!);
                }
            }
            if (_specs is not null)
            {
                var persisted = await _specs.SaveAsync(spec, cancellationToken).ConfigureAwait(false);
                if (!persisted.Ok)
                {
                    lock (_gate) _channels.Remove(IdOf(spec.Address));
                    await _bindings.DetachAsync(spec.Address, CancellationToken.None).ConfigureAwait(false);
                    return Result<ChannelInfo>.Failure(persisted.Error!);
                }
            }
            // The spec file records what this Channel was made as; the table records what it is aimed at now. Both are
            // written here so a Channel that exists is a Channel the next boot can aim — a file without a row would
            // come back with an empty binding and look like somebody had disconnected it.
            var table = await ReadBindingsAsync(cancellationToken).ConfigureAwait(false);
            table[spec.Address.ToString()] = spec.Bindings[0];
            var aimed = await WriteBindingsAsync(table, cancellationToken).ConfigureAwait(false);
            if (!aimed.Ok)
            {
                lock (_gate) _channels.Remove(IdOf(spec.Address));
                await _bindings.DetachAsync(spec.Address, CancellationToken.None).ConfigureAwait(false);
                return Result<ChannelInfo>.Failure(aimed.Error!);
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
            if (!stored.Ok)
                return Result.Failure(new(ErrorCode.Unavailable,
                    $"{address} is running but that could not be persisted; it will start anyway on the next boot.",
                    stored.Error?.Message));
            // A person starting a Channel and the host reviving one are the same event to anything listening: "this one
            // is working now". Leaving it to the boot path made `channels.start` silent, so a script that greets when a
            // conversation comes up greeted once per process and never again.
            await AnnounceAsync(address, EventNames.LifecycleStart, LifecyclePhase.Start, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
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
            if (!stored.Ok)
                return Result.Failure(new(ErrorCode.Unavailable,
                    $"{address} is stopped but that could not be persisted; it will be running again after a restart.",
                    stored.Error?.Message));
            // Stopping by hand is a stop like any other: whatever is mounted here should hear it now, not at the next
            // shutdown.
            await AnnounceAsync(address, EventNames.LifecycleStop, LifecyclePhase.Stop, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// One lifecycle event to one Channel — the same shape the manager sends at boot, reused by the verbs that change a
    /// Channel's running state. A failed publish is not turned into a failed start: the Channel's own state is the fact,
    /// and an event nobody could be told about is not a reason to refuse to do what was asked.
    /// </summary>
    private async ValueTask AnnounceAsync(Address address, string name, LifecyclePhase phase,
        CancellationToken cancellationToken) =>
        await _events.PublishAsync(new(name, address, Guid.NewGuid(),
            JsonSerializer.Serialize(new LifecycleSignal(phase, DateTimeOffset.UtcNow).For(address), LifecycleJson.Options),
            address), cancellationToken).ConfigureAwait(false);

    /// <summary>In-memory only: shutdown uses this, because stopping everything is not a change of wish.</summary>
    private Result SetRunning(Address address, bool running)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(IdOf(address), out var channel)) return Result.Failure(new(ErrorCode.Invalid, $"Channel not found: {address}"));
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
                if (!_channels.TryGetValue(IdOf(address), out channel!)) return Result.Failure(new(ErrorCode.Invalid, $"Channel not found: {address}"));
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
                _channels.Remove(IdOf(address));
            }
            // The scope is keyed by the address, so leaving it registered outlives the Channel: a new Channel at
            // the same address then collided with the dead one, its registration was quietly dropped, and it came
            // up with no tunables at all (while a restart replayed the old Channel's saved overrides into it).
            _parameters?.Unregister(address.ToString());
            // Out of the graph too. Data is left alone on purpose: whether this Channel's history goes to the bin
            // is decided below, and it is a different question from whether the instance still exists.
            if (_instances is not null)
                await _instances.ForgetAsync(new ModuleId(ModuleNames.ChannelManager), address.ToModuleId(),
                    CancellationToken.None).ConfigureAwait(false);
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
            {
                await _state.DeleteAsync(DesiredScope, StateName.Encode(address.ToString()), cancellationToken).ConfigureAwait(false);
                // The wiring row goes too: an aim left behind would be inherited by the next Channel at this address,
                // and a Channel inheriting a stranger's conversation is worse than one with none.
                var table = await ReadBindingsAsync(cancellationToken).ConfigureAwait(false);
                if (table.Remove(address.ToString()))
                    await WriteBindingsAsync(table, cancellationToken).ConfigureAwait(false);
            }
            if (_queue is not null) await _queue.ClearAsync(PendingScope, address, cancellationToken).ConfigureAwait(false);
            // The Channel's Sessions go with it: they answer for an address that no longer exists, and their spec
            // is immutable, so a Channel re-created at the same address would inherit one it can never use.
            // They go to the bin, not the void — deleting a Channel is not a decision to forget a conversation.
            // Best effort on purpose: the Channel is already gone, so a failure here is not "the destroy failed".
            var sessions = _catalog.Find(new ModuleId(ModuleNames.SessionManager)) as ISessionManager;
            if (sessions is not null && _archive is not null)
            {
                var listed = await sessions.ListAsync(cancellationToken).ConfigureAwait(false);
                var ids = listed.Value?
                    .Where(session => string.Equals(session.Channel, address.ToString(), StringComparison.Ordinal))
                    .Select(session => session.Id)
                    .ToArray();
                if (ids is { Length: > 0 })
                    await _archive.ArchiveAsync(ArchiveKinds.Session, ids, dryRun: false, cancellationToken).ConfigureAwait(false);
            }
            else if (sessions is not null)
                await sessions.DeleteChannelAsync(address.ToString(), cancellationToken).ConfigureAwait(false);
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
            // One comparison, because a Channel has one binding: there is nothing to pick and nothing to disambiguate.
            matched = _channels.Values.Where(c => c.Running && !c.Draining &&
                c.Binding is { } aimed &&
                aimed.Adapter == envelope.Adapter &&
                aimed.PlatformConversationId == envelope.PlatformConversationId).ToArray();
        var accepted = 0;
        foreach (var channel in matched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = channel.Binding!;
            // Delivery is where availability actually matters: the Channel may have been created
            // before its Adapter, so this is the first moment we can honestly check it. The Adapter manager is asked
            // for by name — modules do not hold each other (C-19).
            var adapterManager = _catalog.Find(new ModuleId(ModuleNames.AdapterManager)) as IAdapterManager;
            if (adapterManager is null || !adapterManager.Get(binding.Adapter).Ok)
                return Result<int>.Failure(new(ErrorCode.Unavailable,
                    $"Bound Adapter is not available, message not delivered: {binding.Adapter}"));
            // The event usually carries a name; when it does not, the Adapter is the only layer that can go and ask. It is
            // asked for by name (C-19) and its answer is best effort: a Channel that dropped a message because it could
            // not learn what to call the sender would be a Channel that values a label over the message.
            var senderName = envelope.SenderName;
            var senderRemark = envelope.SenderRemark;
            if (string.IsNullOrWhiteSpace(senderName) && !string.IsNullOrWhiteSpace(envelope.Sender) &&
                _catalog.Find(binding.Adapter.ToModuleId()) is ITargetNaming naming)
            {
                try
                {
                    if (await naming.ResolveAsync(envelope.Sender, envelope.PlatformConversationId, cancellationToken)
                            .ConfigureAwait(false) is { } resolved)
                    {
                        senderName = resolved.Primary;
                        senderRemark ??= resolved.Remark;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _report.Note("channels", $"the adapter could not name the sender of a message ({ex.Message}); using the id");
                }
            }
            var message = new ChannelMessage(envelope.EventId, channel.Address, binding, envelope.PlatformMessageId,
                envelope.Text, envelope.At, envelope.Sender, envelope.MentionsSelf, senderRemark, senderName);
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
                        envelope.Sender, envelope.Text, envelope.At, SenderName: senderName, SenderRemark: senderRemark),
                        token).ConfigureAwait(false);
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
                // The binding changed under it: leave the marker alone rather than replay it into another conversation.
                // A disconnected channel has none at all, which is the same answer for a different reason.
                var binding = channel.Bindings.Count == 1 ? channel.Bindings[0] : null;
                if (binding is null || binding.Adapter.ToString() != entry.Adapter || binding.PlatformConversationId != entry.Conversation) continue;
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
        var bindings = await ReadBindingsAsync(cancellationToken).ConfigureAwait(false);
        var seeded = 0;
        foreach (var spec in stored.Value!)
        {
            if (Get(spec.Address).Ok) continue;
            // What a Channel is aimed at is state now; the spec file is where it starts, read once and never written
            // again (C-03's documented exception). A row that disagrees with the file wins, and says so — a silent
            // disagreement between two records of the same thing is exactly what this split is supposed to prevent.
            var key = spec.Address.ToString();
            ChannelBinding? aimedAt = null;
            var known = bindings.TryGetValue(key, out aimedAt);
            ChannelBinding seed;
            if (known)
            {
                // The table has an opinion, and it wins — including when the opinion is "nothing".
                seed = aimedAt ?? spec.Bindings[0];
                if (aimedAt is { } stated && spec.Bindings[0] != stated)
                    _report.Note("channels", $"{spec.Address} is aimed at {stated.Adapter} / {stated.PlatformConversationId}, "
                        + $"not at what its file says ({spec.Bindings[0].Adapter} / {spec.Bindings[0].PlatformConversationId}): "
                        + "the wiring table wins, the file is left as it was.");
            }
            else
            {
                seed = spec.Bindings[0];
                seeded++;
                if (spec.Bindings.Count > 1)
                    _report.Note("channels", $"{spec.Address} was stored with {spec.Bindings.Count} bindings and kept the "
                        + $"first ({seed.Adapter} / {seed.PlatformConversationId}): a Channel serves one conversation.");
            }
            var normalized = spec with { Bindings = [seed] };
            var created = await CreateAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (!created.Ok) return Result.Failure(created.Error!);
            // A Channel created from a file its wiring says is disconnected: it comes back with no aim, and the table is
            // written again so the next boot does not have to re-run this dance.
            if (known && aimedAt is null)
            {
                lock (_gate) if (_channels.TryGetValue(IdOf(spec.Address), out var revivedChannel)) revivedChannel.Aim(null);
                var table = await ReadBindingsAsync(cancellationToken).ConfigureAwait(false);
                table[key] = null;
                await WriteBindingsAsync(table, cancellationToken).ConfigureAwait(false);
            }
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
            // Only the ones that are actually working: a Channel somebody left stopped is not starting, and telling its
            // scripts otherwise is the kind of small lie that makes "lifecycle.start" mean nothing.
            if (desired.Value is not false)
                await _events.PublishAsync(new(EventNames.LifecycleStart, spec.Address, Guid.NewGuid(),
                    JsonSerializer.Serialize(signal.For(spec.Address), LifecycleJson.Options), spec.Address), cancellationToken).ConfigureAwait(false);
        }
        if (revived + keptStopped > 0)
            _report.Note("channels", $"{revived} revived running, {keptStopped} kept stopped as they were left");
        // Wiring that came from a file this boot is now state, so the next change is a change of state and not a
        // second guess about which record is the real one.
        if (seeded > 0) _report.Note("channels", $"{seeded} binding(s) taken from their spec files into the wiring table");
        return Result.Success();
    }

    public async ValueTask<Result> StopAsync(LifecycleSignal signal, CancellationToken cancellationToken)
    {
        foreach (var address in List().Select(c => c.Address))
        {
            // Shutdown is not a change of wish: stop in memory, leave the stored state alone.
            SetRunning(address, running: false);
            await _events.PublishAsync(new(EventNames.LifecycleStop, address, Guid.NewGuid(),
                JsonSerializer.Serialize(signal.For(address), LifecycleJson.Options), address), cancellationToken).ConfigureAwait(false);
        }
        return Result.Success();
    }

    private sealed class ChannelInstance(ChannelSpec spec) : IAddressed, IModule, IInstanceSnapshot
    {
        public Address Address { get; } = spec.Address;
        /// <summary>The address is the name; the catalog and the graph reach the instance by this.</summary>
        public ModuleId Id => new(Address.ToString());
        public string Name { get; } = spec.Name;

        /// <summary>
        /// The one conversation this Channel serves.
        ///
        /// Singular because that is the rule: a Channel is one conversation, on one platform. It used to be a list,
        /// which let a Channel exist that could never accept cleanly — matching had to pick one, and sending refused
        /// anything but exactly one. The addresses outside are still a list of one, so what a client sees does not move.
        /// </summary>
        public ChannelBinding? Binding { get; private set; } = spec.Bindings[0];

        /// <summary>Re-aims the Channel; null means "nothing, for now". Called with the manager's gate held, so a move is never half seen.</summary>
        public void Aim(ChannelBinding? binding) => Binding = binding;

        /// <summary>What a person reads: the one binding, shaped the way the interface has always spelled it (empty when disconnected).</summary>
        public IReadOnlyList<ChannelBinding> Bindings => Binding is null ? [] : [Binding];

        /// <summary>Spec value unless overridden at runtime; the window is read on every activation.</summary>
        public int? MaxMessages { get; set; } = spec.MaxMessages;
        public string? ModelProfile { get; set; }
        public bool Running { get; set; }
        public bool Draining { get; set; }
        public string TypeId => spec.TypeId;

        public ChannelInfo ToInfo() => new(Address, Name, Bindings, Running, spec.TypeId, MaxMessages, ModelProfile);

        /// <summary>
        /// What this Channel is right now, in the words a caller can act on: aimed at what, switched on or off, and how
        /// much history one turn may carry. "Nothing" is spelled as an empty binding list rather than a missing field —
        /// a disconnected Channel is a fact, not an absence.
        /// </summary>
        public JsonElement Snapshot() => JsonSerializer.SerializeToElement(new
        {
            address = Address.ToString(),
            type = spec.TypeId,
            name = Name,
            running = Running,
            draining = Draining,
            bindings = Bindings.Select(binding => new
            {
                adapter = binding.Adapter.ToString(),
                conversation = binding.PlatformConversationId
            }),
            maxMessages = MaxMessages,
            modelProfile = ModelProfile
        }, JsonOptions);

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
    private Result ApplyParameters(ChannelInstance channel) =>
        _parameters?.Register(channel.Parameters(this)) ?? Result.Success();
}
