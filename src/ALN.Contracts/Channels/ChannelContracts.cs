namespace ALN.Contracts;

public sealed record ChannelBinding(Address Adapter, string PlatformConversationId);

/// <summary>
/// One tunable, as a component describes it. The description is the contract: a parameter that cannot say
/// what it accepts and when it takes effect is a parameter the UI has to guess about.
/// </summary>
/// <param name="Apply">When a change takes effect — see <see cref="ParameterApply"/>.</param>
/// <param name="Secret">
/// True means "settable, never readable". A token typed into a UI must not come back out of an API that a
/// log, a screenshot or a support ticket can capture.
/// </param>
public sealed record ParameterDescriptor(
    string Name,
    ParameterKind Kind,
    string? DefaultValue,
    string Description,
    ParameterApply Apply = ParameterApply.Immediate,
    bool Secret = false,
    double? Minimum = null,
    double? Maximum = null);

public enum ParameterKind { Integer, Number, Boolean, Text }

/// <summary>
/// When a change actually happens. Saying "immediate" for something that needs a rebind is a lie the caller
/// only discovers later, so every parameter has to declare which of these it is.
/// </summary>
public enum ParameterApply
{
    /// <summary>Read on the next use; nothing needs restarting.</summary>
    Immediate,
    /// <summary>Takes effect on the next round/tick — same process, one cycle later.</summary>
    NextRound,
    /// <summary>Needs the owning connection or listener to be re-established.</summary>
    Reconnect,
    /// <summary>Only read at startup; stored now, honoured after a restart.</summary>
    Restart
}

/// <summary>
/// A component that exposes tunables. Values set at runtime are validated by the component, applied through
/// <see cref="ApplyAsync"/> and persisted by the host, so a restart keeps them — and the component decides
/// what "applied" means for it, because only it knows.
/// </summary>
public interface IParameterTarget
{
    /// <summary>Stable scope, e.g. <c>channel.conversation#my-group</c>.</summary>
    string ParameterScope { get; }

    IReadOnlyList<ParameterDescriptor> Parameters { get; }

    /// <summary>Current effective value as text, or null when unset. Secrets return null.</summary>
    string? ReadParameter(string name);

    /// <summary>Validate-and-apply in one step; a rejected value must leave the old one in place.</summary>
    ValueTask<Result> ApplyParameterAsync(string name, string value, CancellationToken cancellationToken);
}

/// <summary>
/// Where the tunables live at runtime: every registered target, plus whatever has been overridden and
/// persisted. Overrides are applied at startup, before anything reads the value.
/// </summary>
public interface IParameterRegistry
{
    Result Register(IParameterTarget target);
    Result Unregister(string scope);
    IReadOnlyList<ParameterView> List();
    ValueTask<Result> SetAsync(string scope, string name, string value, CancellationToken cancellationToken);
    /// <summary>Re-applies persisted overrides; called once after targets are registered.</summary>
    ValueTask<Result> RestoreAsync(CancellationToken cancellationToken);
}

public sealed record ParameterView(
    string Scope,
    string Name,
    ParameterKind Kind,
    string? Value,
    string? DefaultValue,
    ParameterApply Apply,
    bool Secret,
    double? Minimum,
    double? Maximum,
    string Description);
public sealed record ChannelSpec(Address Address, string Name, IReadOnlyList<ChannelBinding> Bindings, int? MaxMessages = null,
    /// <summary>The type this instance implements. Null only in files written before the field existed.</summary>
    string? TypeId = null);
public sealed record ChannelInfo(Address Address, string Name, IReadOnlyList<ChannelBinding> Bindings, bool Running,
    int? MaxMessages = null,
    /// <summary>Which model profile this Channel talks with; null means "whatever the host resolves".</summary>
    string? ModelProfile = null);
public sealed record ChannelMessage(Guid MessageId, Address Channel, ChannelBinding Binding,
    string? PlatformMessageId, string Text, DateTimeOffset At, string? Sender = null, bool MentionsSelf = false);

/// <summary>Messages accepted for one Channel that a Session has not consumed yet.</summary>
/// <param name="Messages">At most the Channel's window; the newest ones.</param>
/// <param name="Omitted">How many older messages were dropped from this hand-out to fit the window.</param>
public sealed record ChannelDelta(Address Channel, IReadOnlyList<StoredMessage> Messages, int Omitted = 0)
{
    public bool IsEmpty => Messages.Count == 0;
}

/// <summary>
/// The Channel side of activation: content is pulled, never pushed.
///
/// A Channel decides whether a message is worth waking a Session for, and that decision travels as a
/// signal with no payload. The messages themselves are handed over later — when the Session is actually
/// assembling the context it is about to send — so that what the model sees is "everything up to the
/// moment we started", not "whatever happened to arrive with the signal". Pull is also what lets a batch
/// of messages become one turn instead of N turns.
///
/// Handing out is at-most-once: the cursor moves when the delta is delivered, so a failed model call
/// does not repeat the same messages (a repeated answer in a real conversation is worse than a missed one).
/// </summary>
public interface IChannelDeltaSource
{
    /// <param name="maxMessages">Window for this hand-out; null means "no window". Older messages beyond it
    /// are consumed but reported as omitted, never re-delivered later.</param>
    ValueTask<Result<ChannelDelta>> TakeDeltaAsync(Address channel, int? maxMessages, CancellationToken cancellationToken);
}

public interface IChannelManager
{
    Result<ChannelInfo> Get(Address address);
    IReadOnlyList<ChannelInfo> List();
    bool IsBound(Address channel, Address adapter, string platformConversationId);
    /// <summary>Locates the running channel bound to an Adapter conversation; absence is a normal outcome.</summary>
    Result<ChannelInfo> FindBound(Address adapter, string platformConversationId);
    ValueTask<Result<ChannelInfo>> CreateAsync(ChannelSpec spec, CancellationToken cancellationToken);
    ValueTask<Result> StartAsync(Address address, CancellationToken cancellationToken);
    ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken);
    ValueTask<Result> DestroyAsync(Address channel, CancellationToken cancellationToken);
    ValueTask<Result<int>> AcceptAsync(AdapterEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Records a reply the Channel actually sent, as an outbound row pointing at the message it answers.
    /// Best effort by design: a reply that went out is a fact, and failing to write it down must not turn
    /// into a second reply attempt.
    /// </summary>
    ValueTask<Result> RecordReplyAsync(Address channel, Guid answeredMessageId, string platformConversationId,
        string text, string? platformMessageId, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the "accepted but not finished" marker for one message. Called once the event round for that
    /// message is over; until then a restart will replay it.
    /// </summary>
    ValueTask<Result> CompleteAcceptAsync(Address channel, Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-publishes the wake-up for messages that were accepted but whose round never finished. This is the
    /// answer to "the process died between accepting a message and answering it": the message is in the
    /// Channel's history, and this is what makes something come back and look at it again.
    /// </summary>
    ValueTask<Result<int>> ReplayPendingAsync(CancellationToken cancellationToken);
}
