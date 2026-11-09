using System.Text.Json.Serialization;
using System.Text.Json;
namespace ALN.Contracts;

public readonly record struct Address(string Module, string? Kind = null, string? Instance = null)
{
    /// <summary>Two-part spelling kept for <see cref="ToString"/> and for the spec files. Reading a type or a
    /// category out of it is what the constitution forbids (C-04/C-06): ask the module, not its name.</summary>
    public string TypeId => Kind is null ? Module : $"{Module}.{Kind}";

    /// <summary>
    /// The one place an address becomes a module id: the id is the address's spelling, joined here and not read back
    /// apart anywhere. Callers that need to know what a module is ask the module, not this string.
    /// </summary>
    public ModuleId ToModuleId() => new(ToString());
    public override string ToString() => Instance is null ? TypeId : $"{TypeId}#{Instance}";
    public bool IsInstance => !string.IsNullOrWhiteSpace(Module) && !string.IsNullOrWhiteSpace(Kind) && !string.IsNullOrWhiteSpace(Instance);

    /// <summary>Inverse of <see cref="ToString"/>: "adapter.file#file-1" → (adapter, file, file-1).</summary>
    public static bool TryParse(string? text, out Address address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hash = text.IndexOf('#');
        var typeId = hash < 0 ? text : text[..hash];
        var instance = hash < 0 ? null : text[(hash + 1)..];
        if (hash >= 0 && string.IsNullOrWhiteSpace(instance)) return false;
        var dot = typeId.IndexOf('.');
        if (dot < 0)
        {
            if (string.IsNullOrWhiteSpace(typeId)) return false;
            address = new Address(typeId, null, instance);
            return true;
        }
        var module = typeId[..dot];
        var kind = typeId[(dot + 1)..];
        if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(kind) || kind.Contains('.')) return false;
        address = new Address(module, kind, instance);
        return true;
    }
}
public enum ErrorCode { Invalid, Conflict, Unavailable, Timeout, Denied, Internal }
public sealed record Error(ErrorCode Code, string Message, string? Detail = null, bool Retryable = false);
public readonly record struct Result(bool Ok, Error? Error = null)
{
    public static Result Success() => new(true);
    public static Result Failure(Error error) => new(false, error);
}
public readonly record struct Result<T>(T? Value, Error? Error)
{
    public bool Ok => Error is null && Value is not null;
    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(Error error) => new(default, error);
}
public enum AdapterDirection { Inbound, Outbound, Both }
public enum AdapterState { Created, Running, Stopped, Faulted, Destroyed }
public sealed record AdapterDescriptor(string PlatformId, string AccountId, AdapterDirection Direction);

/// <summary>
/// Something that came in with a message and is not words: the bytes, and what the Adapter thinks they are.
///
/// It carries <b>bytes</b> rather than a reference because the Adapter has no business knowing where media is stored:
/// it asked the platform for a picture and got one, and naming it is the core's job (one hash, one limit, one place
/// that decides what a run of bytes is — <see cref="IMediaStore"/>). The bytes cross exactly one boundary, from the
/// Adapter to whoever takes the message in, and never reach a durable record.
/// </summary>
/// <param name="Kind">
/// What the Adapter says it is. A claim, not the answer: the bytes are probed on the way in, and a "picture" that is
/// not one is stored as a file and never shown to a model.
/// </param>
public sealed record InboundAttachment(MediaKind Kind, string MediaType, byte[] Bytes);

public sealed record AdapterEnvelope(Address Adapter, string PlatformId, string PlatformConversationId,
    string? PlatformMessageId, string Text, DateTimeOffset At,
    /// <summary>The platform's identifier for the sender. Not a name: it does not change when somebody renames.</summary>
    string? Sender = null, bool MentionsSelf = false,
    /// <summary>What the platform calls the sender, when the event carried it (a group card, then a nickname).</summary>
    string? SenderName = null,
    /// <summary>What you call the sender — a remark that belongs to the account rather than to the platform.</summary>
    string? SenderRemark = null,
    /// <summary>
    /// Non-text content the message arrived with. <see cref="Text"/> may then be empty — a picture with no caption is
    /// a message, and a Channel that dropped it as "empty" would be dropping exactly what somebody sent (docs/28 §1).
    /// </summary>
    IReadOnlyList<InboundAttachment>? Attachments = null)
{
    public Guid EventId { get; init; } = Guid.NewGuid();
}
/// <summary>
/// One piece of an outgoing message, in the order it should appear. <b>Blocks are layout, not boundaries:</b> two text
/// blocks separated by a mention are one message that reads "…@someone…", which is why this exists at all — the
/// alternative was three messages, and a platform shows three messages as three.
///
/// A block names what should appear, not how the platform spells it. <c>at</c> is "mention this person", <c>face</c> is
/// "this platform's own emoji id" — the one block that is deliberately dialect, because there is no portable name for
/// "the 微笑 emoji" and a mapping table between platforms is a second thing to maintain and get wrong (docs/28 §6).
///
/// <see cref="OutboundMessage.Text"/> stays the plain-text view of the same thing: what a platform that can only send
/// text would send, what the transcript records, and what an adapter's日志 shows. A message is never block-only.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(MentionBlock), "mention")]
[JsonDerivedType(typeof(ImageBlock), "image")]
[JsonDerivedType(typeof(FaceBlock), "face")]
[JsonDerivedType(typeof(FileBlock), "file")]
public abstract record MessageBlock
{
    /// <summary>What this block is, in the one spelling the plain-text view uses.</summary>
    public abstract string AsText { get; }

    /// <summary>
    /// The whole message as text: blocks concatenated, with no separator invented between them. A producer that wants a
    /// line break writes a text block containing one — guessing insertions here would make the text view differ from
    /// what the platform shows.
    /// </summary>
    public static string Flatten(IEnumerable<MessageBlock>? blocks) =>
        blocks is null ? string.Empty : string.Concat(blocks.Select(block => block.AsText));
}

/// <summary>Words, including punctuation and line breaks. An empty block is refused by the senders, not here.</summary>
public sealed record TextBlock(string Text) : MessageBlock
{
    public override string AsText => Text;
}

/// <summary>
/// "Say this person's name out loud." The id is the platform's own, resolved by whoever knew it — the tool that answers
/// a message knows who sent it, and a model that could name anybody by typing a number would be a model that can ping
/// strangers (docs/28 §6).
/// </summary>
public sealed record MentionBlock(string Id) : MessageBlock
{
    public override string AsText => $"@{Id}";
}

/// <summary>
/// A picture, by reference: the bytes are already in the media area (the model's own output, or something that arrived).
/// Never the bytes themselves — an <see cref="OutboundMessage"/> is written to a mailbox file by some adapters, and a
/// picture inlined there would be a second copy on disk for every send (docs/28 §3).
/// </summary>
public sealed record ImageBlock(MediaRef Picture) : MessageBlock
{
    public override string AsText => $"[图片：{Picture.Locator}]";
}

/// <summary>
/// The platform's own face/emoji id, passed through untouched: <c>1</c> is 微笑 on QQ and meaningless anywhere else.
/// Kept because inventing a portable emoji vocabulary is a project of its own, and refusing faces would mean refusing
/// the one thing people actually type in a group chat.
/// </summary>
public sealed record FaceBlock(string Id) : MessageBlock
{
    public override string AsText => "[表情]";
}

/// <summary>
/// A file, by reference. <b>Not a picture:</b> nothing shows it inline, and on some platforms it does not even travel as
/// part of the message — QQ delivers it as an upload of its own and the chat never mentions it (<c>docs/28 §6.6</c>).
///
/// The block says <i>what</i> to deliver. How many calls that takes, and whether the bytes go as a URL, a path, base64
/// or a request for the bridge to fetch them itself, is the Adapter's business like every other part — which is the
/// whole reason this is a block and not a second send verb (<c>I-33</c>): a sender that had to know which platforms
/// attach a file to a message and which upload it separately would be a sender holding a model of every Adapter.
/// </summary>
/// <param name="Name">
/// What the platform should store it as. Null means "the file's own name", which is what a receiver expects to see. It
/// is separate from the path because a name is a thing people read and a path is a thing the file rules decide.
/// </param>
public sealed record FileBlock(MediaRef File, string? Name = null) : MessageBlock
{
    public override string AsText => $"[文件：{File.Locator}]";

    /// <summary>What to call it on the far side: the name that was given, or the last piece of the path.</summary>
    public string SuggestedName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name) is false) return Name!;
            var slash = File.Path.LastIndexOf('/');
            return slash < 0 ? File.Path : File.Path[(slash + 1)..];
        }
    }
}

public sealed record OutboundMessage(string PlatformConversationId, string Text, string? ReplyTo = null,
    /// <summary>
    /// The layout, when the sender has one. Null means "this is one run of text", which is what every caller that has
    /// nothing richer to say produces — and what an adapter falls back to when it cannot render blocks.
    /// </summary>
    IReadOnlyList<MessageBlock>? Blocks = null);
public sealed record SendResult(bool Sent, string AttemptId, string? Detail = null,
    /// <summary>The platform's own id for what was just sent, when the transport has one.</summary>
    string? PlatformMessageId = null);
public sealed record AdapterInfo(Address Address, AdapterDescriptor Descriptor, AdapterState State,
    /// <summary>The type this instance implements, as declared in its spec — the only row of truth for "what is it".
    /// Callers that filter by type must read this, not the spelling of the address.</summary>
    string TypeId);

/// <summary>
/// A stored Adapter instance. <paramref name="Settings"/> is opaque to the core: an endpoint, a token,
/// a polling interval — the core neither reads nor validates the keys, it only hands the dictionary to
/// the factory. Keeping it here rather than in a side file means one persisted unit per instance, and
/// that the value never leaves the process through an action (the DTOs don't carry it).
/// </summary>
public sealed record AdapterSpec(Address Address, AdapterDescriptor Descriptor,
    /// <summary>
    /// The type this instance implements. A fact of the spec, stored in the file, never worked out from the
    /// address: the address is a name, and a name whose spelling decides what the thing is cannot be changed.
    /// </summary>
    string TypeId,
    IReadOnlyDictionary<string, string>? Settings = null,
    /// <summary>
    /// Which of the platform's own operations this instance will carry, and how deep each one sits. Absent means "none
    /// named here" — the door's level (<see cref="RawAccess"/>) applies instead, which is <see cref="AccessLevel.Full"/>
    /// until somebody says otherwise.
    ///
    /// This is a person's written decision, not the adapter's opinion. Its job is the exception, not the inventory: an
    /// instance that wants the model to have the bridge's verbs writes one number, and only the handful it wants to keep
    /// away from something shallower get a line here.
    /// </summary>
    IReadOnlyDictionary<string, AccessLevel>? RawActions = null,
    /// <summary>
    /// How deep the whole door sits for this instance, for every verb <see cref="RawActions"/> does not name.
    ///
    /// Full by default: this path is a convenience, not a capability — it exists so that a bridge gaining a feature does
    /// not need a release from us, and the deployment says who may use it. Lower it on an instance that should not hand
    /// the platform to whoever can reach the action table.
    /// </summary>
    [property: JsonConverter(typeof(AccessLevelJson))] AccessLevel RawAccess = AccessLevel.Full);

public sealed record RoutedEvent(string Name, Address Source, Guid EventId, string PayloadJson, Address? Target = null)
{
    /// <summary>Set by the event queue on acceptance; lets the log measure queue backlog.</summary>
    public DateTimeOffset? EnqueuedAt { get; init; }

    /// <summary>
    /// Record that emitted this event, captured from the ambient signal scope at publish time.
    /// Without it an event published inside a handler becomes an unrelated second root instead of
    /// a branch of the handler that caused it.
    /// </summary>
    public Guid? CauseSignalId { get; init; }
}
public interface IEventSink
{
    // Acceptance into a bounded queue, NOT durable processing acknowledgment.
    ValueTask<Result> PublishAsync(RoutedEvent routedEvent, CancellationToken cancellationToken);
}
public interface IInboundAdapter { }

/// <summary>
/// The one way a message leaves this process. <b>There is no other, and nothing in front of it decides anything.</b>
///
/// That is deliberate, and it is the whole design: a sender — a Session's <c>speak</c>, a script, a Channel — hands over
/// what it wants to say and this method is where it stops being its business. <b>What can actually be sent is the
/// Adapter's question to answer, not the sender's to guess:</b> whether a picture becomes a segment, a URL, base64, a
/// file beside a mailbox entry, or nothing at all, is a fact about one platform's protocol and belongs in the one class
/// that speaks it.
///
/// What that rules out, because it was tried and it was worse: an <b>up-front capability declaration</b> that a sender
/// consults before calling. Two costs, both real. It made every send a question and then an answer — two calls to learn
/// something one call would have said. And the declaration was a second place that had to agree with the code, so it
/// could be wrong in the dishonest direction: a mailbox that writes whole <see cref="OutboundMessage"/>s to disk was
/// declaring "text only" and being refused pictures it could deliver perfectly well. A sender that is expected not to
/// think about Adapters is a sender that must not be handed a model of them.
///
/// So a part that cannot go out is answered on this path, by the Adapter that knows why, in one of two ways:
/// <list type="bullet">
///   <item><b>Refuse</b> — <see cref="Result{T}"/> failure when the message would be a lie: nothing arrives, and the
///   reason says which part and why. A model that reads it can rewrite what it said.</item>
///   <item><b>Degrade and say so</b> — success with <see cref="SendResult.Detail"/> naming what fell back to its text
///   form. Losing a whole message because one picture was too big is worse than arriving with a sentence where the
///   picture was; arriving <i>quietly</i> is worse than both.</item>
/// </list>
/// </summary>
public interface IOutboundAdapter
{
    ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// What an Adapter can do. It is also a module: an instance is a thing with a name, and the name is how the
/// catalog and the graph reach it. The behavior lives here; "it exists under a name" comes from
/// <see cref="IModule"/>, and <see cref="IModule.Id"/> is the same name as <see cref="Address"/>.
/// </summary>
public interface IChatAdapter : IInboundAdapter, IOutboundAdapter, IAddressed, IModule
{
    AdapterDescriptor Descriptor { get; }
    AdapterState State { get; }

    ValueTask<Result> StartAsync(CancellationToken cancellationToken);
    ValueTask<Result> StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional for an Adapter whose platform has operations we do not model — OneBot has dozens, and wrapping each one would
/// mean the host grows a verb every time a bridge gains a feature.
///
/// The host's counterpart is one action (<c>adapters.raw</c>) plus a per-instance allowlist that says which verbs may be
/// called and at what level; the adapter only has to send one and hand back the answer. An adapter that cannot
/// meaningfully do that does not implement this, and saying so is the answer.
/// </summary>
public interface IRawAdapter
{
    /// <summary>
    /// One platform operation, verbatim: <paramref name="action"/> is the platform's own name for it, and the answer is
    /// the platform's own data. Nothing on this path translates, which is the point and also the risk.
    /// </summary>
    ValueTask<Result<JsonElement>> RawAsync(string action, JsonElement parameters, CancellationToken cancellationToken);
}

/// <summary>
/// Optional: an Adapter that maintains a connection can say whether it is up right now. <see cref="AdapterState"/>
/// only says whether somebody asked it to run — a socket that keeps dropping is still "Running", and a
/// list that shows only that is a list that lies during exactly the incident you are debugging.
/// </summary>
public interface IAdapterHealth
{
    bool Connected { get; }

    /// <summary>Human readable last condition: last error, endpoint, next retry. Never contains secrets.</summary>
    string? HealthDetail { get; }

    /// <summary>
    /// Waits until <see cref="Connected"/>, or until <paramref name="timeout"/> runs out; false means it did not, which
    /// is a fact about the platform rather than an error to retry forever.
    ///
    /// A default implementation, because every adapter that can answer <see cref="Connected"/> can answer this the same
    /// way and none of them should have to write it: the connection state is not push-notified anywhere, and inventing a
    /// notification for one caller would be a second mechanism for a fact this one property already states.
    /// </summary>
    async ValueTask<bool> WaitUntilConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Connected) return true;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            if (Connected) return true;
        }
        return Connected;
    }
}

/// <summary>
/// Optional: an Adapter that logs in somewhere can tell us which account it ended up as. The spec's
/// <c>accountId</c> is then a label rather than a fact to maintain — a QQ number is something the
/// platform already knows, and typing it into a config file is a chance to type it wrong.
/// </summary>
public interface IAdapterIdentity
{
    /// <summary>Platform account id, once the platform has told us; null before that.</summary>
    string? AccountId { get; }

    /// <summary>Display name from the platform, if it gave one.</summary>
    string? DisplayName { get; }
}
public interface IAdapterFactory
{
    string TypeId { get; }
    IChatAdapter Create(AdapterSpec spec);
}

public interface IAdapterFactoryRegistry : IModulePackageResourceRegistry
{
    Result Register(string packageId, IAdapterFactory factory);
    Result BeginDrain(string packageId);
    Result Resume(string packageId);
    Result<IChatAdapter> Create(string typeId, AdapterSpec spec);
}

/// <summary>One type the process knows about: what a spec may name, and what its instances are.</summary>
/// <param name="ParentTypeId">The abstract type it belongs to ("adapter.file" → "adapter"); null for the abstract ones.</param>
/// <param name="IsAbstract">An abstract type names a family; only concrete ones can be instantiated.</param>
public sealed record ModuleTypeEntry(string TypeId, string? ParentTypeId, bool IsAbstract);

/// <summary>
/// The action ids the Adapter module answers to. Here for the same reason as <see cref="ChannelActions"/>: an id is
/// part of the interface, and the module that implements the verb must not be the only place it is written down.
/// </summary>
public static class AdapterActions
{
    public const string List = "adapters.list";
    public const string Start = "adapters.start";
    public const string Stop = "adapters.stop";
    public const string Create = "adapters.create";
    public const string Destroy = "adapters.destroy";

    /// <summary>
    /// One of the platform's own operations, passed through verbatim. Callable only for the verbs an instance lists under
    /// <c>rawActions</c>, at the level written there; anything unnamed is an operator's.
    /// </summary>
    public const string Raw = "adapters.raw";
}
public interface IModuleTypeCatalog
{    bool IsKnownAbstract(string typeId);
    bool IsConcrete(string typeId);
    bool IsAssignableTo(string concreteTypeId, string abstractTypeId);

    /// <summary>
    /// Every type registered right now. Defaulted to empty because most stand-ins know nothing; a catalog that
    /// holds types but answers empty is the one thing a create form cannot work around, so real ones implement it.
    /// </summary>
    IReadOnlyList<ModuleTypeEntry> ListTypes() => [];
}
public interface IAdapterManager
{
    /// <summary>The type this instance implements, as declared in its spec; null when no instance has that address.
    /// The alternative — reading it out of the address — is what the module contract forbids.</summary>
    /// <remarks>
    /// Defaulted so that the hand-written stand-ins in tests (and anything implementing only part of this
    /// interface) still compile. A stand-in that answers null for an instance it does hold is lying, so this
    /// default is a placeholder for "not implemented", not a legal answer.
    /// </remarks>
    string? TypeOf(Address address) => null;
    Result<IChatAdapter> Get(Address address);
    IReadOnlyList<AdapterInfo> List();
    ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, CancellationToken cancellationToken);
    ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, Func<AdapterSpec, IChatAdapter> factory, CancellationToken cancellationToken);
    ValueTask<Result<IReadOnlyList<AdapterInfo>>> SnapshotByTypeAsync(string concreteTypeId, CancellationToken cancellationToken);
    ValueTask<Result> DestroyByTypeAsync(string concreteTypeId, CancellationToken cancellationToken);
    ValueTask<Result> ForceDestroyByTypeAsync(string concreteTypeId, CancellationToken cancellationToken);
    ValueTask<Result> StartAsync(Address address, CancellationToken cancellationToken);
    ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken);
    ValueTask<Result> DestroyAsync(Address address, CancellationToken cancellationToken);
}
public interface IInstanceBindings
{
    ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken);
    ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken);
}
public interface IForceInstanceBindings : IInstanceBindings
{
    ValueTask<Result> ForceDetachAsync(Address target, CancellationToken cancellationToken);
}
public interface IInstanceBindingsProxy : IForceInstanceBindings
{
    void Bind(IInstanceBindings inner);
}

// File IO is supplied by the host. Adapters cannot choose arbitrary OS paths.
/// <summary>
/// One input the mailbox has claimed and handed over.
///
/// <paramref name="Token"/> is the message id the mailbox assigned, not part of the file name: a file name is a way to
/// drop something in, not an identity, and taking it as one made two folders with the same file name a collision.
/// <paramref name="Content"/> is the file's text as it was written — no envelope, no JSON to parse.
/// </summary>
public sealed record MailboxInput(string ConversationId, string Token, string Content,
    /// <summary>
    /// What the file turned out to be when it was not text. Empty for an ordinary message, and <see cref="Content"/>
    /// is then allowed to be empty — a picture with no caption is still something somebody sent (docs/28 §1).
    /// </summary>
    IReadOnlyList<InboundAttachment>? Attachments = null);

/// <summary>
/// Hands out the mailbox for one account. The host owns where mailboxes live; the adapter package only asks for one
/// by account id, which it already has in its spec.
///
/// It exists because one mailbox for every file Adapter meant the second instance read the first one's inbox. The
/// path was always derivable from the account; what was missing was a way to ask for it without the adapter package
/// taking a dependency on the host's storage implementation.
/// </summary>
public interface IFileMailboxFactory
{
    /// <summary>The mailbox for that account, created (or remembered) — one per account, never one for everybody.</summary>
    IFileMailbox For(string accountId);
}

public interface IFileMailbox
{    ValueTask<IReadOnlyList<string>> GetConversationIdsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Claims the pending inputs and hands them over. Claiming is a rename (inbox → inflight), so a second
    /// reader cannot see them and a crash leaves them claimed rather than lost: the next boot reclaims them.
    /// </summary>
    ValueTask<IReadOnlyList<MailboxInput>> ReadPendingAsync(CancellationToken cancellationToken);

    /// <summary>Moves a claimed input to processed or rejected. Nothing is final before this.</summary>
    ValueTask CompleteAsync(string conversationId, string token, bool accepted, string? error, CancellationToken cancellationToken);

    /// <summary>Puts claimed-but-unfinished inputs back in the inbox. Called once at boot, before polling.</summary>
    ValueTask<int> ReclaimAsync(CancellationToken cancellationToken);

    /// <summary>Gives one claimed input back (publication failed): it is offered again on the next poll.</summary>
    ValueTask ReleaseAsync(string conversationId, string token, CancellationToken cancellationToken);

    ValueTask<string> WriteOutputAsync(OutboundMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Acknowledge an inbound delivery only after the event it produced has been handled.
///
/// The reason this exists at all: an Adapter can say "I published this" but only the pump knows the event
/// round is over, and "published" is not "handled" — a crash in between would otherwise drop the wake-up
/// while the mailbox already believes the file was dealt with.
/// </summary>
public interface IInboundAcks
{
    /// <summary>Registers what to do once the event with this id has been dispatched.</summary>
    void Expect(Guid eventId, Func<CancellationToken, ValueTask> settle);

    /// <summary>Cancels a registration whose event never made it out (publish failed).</summary>
    void Forget(Guid eventId);

    ValueTask<Result> SettleAsync(Guid eventId, CancellationToken cancellationToken);
}
