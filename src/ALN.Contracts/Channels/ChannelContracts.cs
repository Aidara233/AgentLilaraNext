using ALN.Contracts.Sessions;

namespace ALN.Contracts;

public sealed record ChannelBinding(Address Adapter, string PlatformConversationId);

/// <summary>
/// The action ids the Channel module answers to. They live in contracts for the same reason
/// <see cref="Capabilities"/> does: an id is part of the interface, and the module that implements the verb and the
/// caller that reads the answer must not each spell it out.
///
/// They are here rather than next to the handler because a handler is one implementation of an action: the host used
/// to own all of them, and an id that moves with the implementation would eventually move twice.
/// </summary>
public static class ChannelActions
{
    public const string List = "channels.list";
    public const string Start = "channels.start";
    public const string Stop = "channels.stop";
    public const string Send = "channels.send";
    public const string Create = "channels.create";
    public const string Destroy = "channels.destroy";
    public const string Connect = "channels.connect";
    public const string Disconnect = "channels.disconnect";
}

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
/// <summary>
/// The one Channel implementation there is today. Named in one place so that a spec, a validation and a
/// forwarder all agree on the string — and so that retiring an address-shape check later does not leave a
/// copy of it behind.
/// </summary>
public static class ChannelTypes
{
    public const string Conversation = "channel.conversation";
}

public sealed record ChannelSpec(Address Address, string Name, IReadOnlyList<ChannelBinding> Bindings,
    /// <summary>The type this instance implements. A fact of the spec, never read out of the address.</summary>
    string TypeId,
    int? MaxMessages = null);
public sealed record ChannelInfo(Address Address, string Name, IReadOnlyList<ChannelBinding> Bindings, bool Running,
    /// <summary>The type this instance implements, as declared in its spec. Never read out of the address.</summary>
    string TypeId,
    int? MaxMessages = null,
    /// <summary>Which model profile this Channel talks with; null means "whatever the host resolves".</summary>
    string? ModelProfile = null);
public sealed record ChannelMessage(Guid MessageId, Address Channel, ChannelBinding Binding,
    string? PlatformMessageId, string Text, DateTimeOffset At, string? Sender = null, bool MentionsSelf = false,
    string? SenderRemark = null,
    /// <summary>The name the platform uses for the sender (group card, then nickname), when one was found. Kept apart
    /// from <see cref="Sender"/>, which is the platform's identifier: a name changes and an id does not, and only one
    /// of them is safe to answer somebody by.</summary>
    string? SenderName = null)
{
    /// <summary>
    /// How to address the sender in text: the name that was found (group card, nickname or the account id, in that
    /// order) and, when there is one, the remark in brackets after it. The remark never replaces the name — it is an
    /// extra thing to know about the same person, so both stay visible.
    /// </summary>
    public string DisplaySender => Compose(SenderName, Sender, SenderRemark);

    /// <summary>
    /// The one rule for "how do we call this person", kept in one place because the same question is asked by the
    /// Channel (what to inject) and by the Adapter (what to put on the envelope).
    /// </summary>
    public static string Compose(string? name, string? id, string? remark)
    {
        var primary = string.IsNullOrWhiteSpace(name) ? id : name;
        if (string.IsNullOrWhiteSpace(primary)) return remark ?? "";
        return string.IsNullOrWhiteSpace(remark) ? primary : $"{primary}({remark})";
    }
}

/// <summary>Messages accepted for one Channel that a Session has not consumed yet.</summary>
/// <param name="Messages">At most the Channel's window; the newest ones.</param>
/// <param name="Omitted">How many older messages were dropped from this hand-out to fit the window.</param>
public sealed record ChannelDelta(Address Channel, IReadOnlyList<StoredMessage> Messages, int Omitted = 0)
{
    public bool IsEmpty => Messages.Count == 0;
}

/// <summary>
/// One hand-out, plus what has to happen for it to count as delivered.
///
/// The claim is what makes a lost turn recoverable: reading does not move the cursor, so a request that dies — a
/// provider 422, a timeout, a crash — leaves the messages exactly where they were and the next turn gets them
/// again. <see cref="Commit"/> advances the cursor, and it runs only after the turn it fed has been written down.
/// </summary>
public sealed record ChannelDeltaClaim(ChannelDelta Delta, ITurnCommit Commit);

/// <summary>
/// The Channel side of activation: content is pulled, never pushed.
///
/// A Channel decides whether a message is worth waking a Session for, and that decision travels as a
/// signal with no payload. The messages themselves are handed over later — when the Session is actually
/// assembling the context it is about to send — so that what the model sees is "everything up to the
/// moment we started", not "whatever happened to arrive with the signal". Pull is also what lets a batch
/// of messages become one turn instead of N turns.
///
/// Handing out is <b>at-least-once</b>, closed by the turn's commit: reading does not move the cursor, so a request
/// that fails is asked for again next turn instead of swallowing what people said. Delivering twice is safe because
/// the Session timeline deduplicates by platform message id; dropping once is not, because nothing can recover what
/// the model never saw.
/// </summary>
public interface IChannelDeltaSource
{
    /// <param name="maxMessages">Window for this hand-out; null means "no window". Older messages beyond it are
    /// elided but still counted as delivered when the turn commits, so a backlog is not re-elided forever.</param>
    ValueTask<Result<ChannelDeltaClaim>> ClaimDeltaAsync(Address channel, int? maxMessages, CancellationToken cancellationToken);
}

public interface IChannelManager
{
    /// <summary>The type this instance implements, as declared in its spec.</summary>
    /// <summary>Stand-ins (test fakes) may legitimately know nothing; a real manager overrides this.</summary>
    string? TypeOf(Address address) => null;
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
