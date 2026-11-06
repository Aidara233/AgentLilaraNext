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
public sealed record AdapterEnvelope(Address Adapter, string PlatformId, string PlatformConversationId,
    string? PlatformMessageId, string Text, DateTimeOffset At,
    /// <summary>The platform's identifier for the sender. Not a name: it does not change when somebody renames.</summary>
    string? Sender = null, bool MentionsSelf = false,
    /// <summary>What the platform calls the sender, when the event carried it (a group card, then a nickname).</summary>
    string? SenderName = null,
    /// <summary>What you call the sender — a remark that belongs to the account rather than to the platform.</summary>
    string? SenderRemark = null)
{
    public Guid EventId { get; init; } = Guid.NewGuid();
}
public sealed record OutboundMessage(string PlatformConversationId, string Text, string? ReplyTo = null);
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
    IReadOnlyDictionary<string, string>? Settings = null);

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
/// Optional: an Adapter that maintains a connection can say whether it is up right now. <see cref="AdapterState"/>
/// only says whether somebody asked it to run — a socket that keeps dropping is still "Running", and a
/// list that shows only that is a list that lies during exactly the incident you are debugging.
/// </summary>
public interface IAdapterHealth
{
    bool Connected { get; }

    /// <summary>Human readable last condition: last error, endpoint, next retry. Never contains secrets.</summary>
    string? HealthDetail { get; }
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

public interface IModuleTypeCatalog
{
    bool IsKnownAbstract(string typeId);
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
public sealed record MailboxInput(string ConversationId, string Token, string Content);
public interface IFileMailbox
{
    ValueTask<IReadOnlyList<string>> GetConversationIdsAsync(CancellationToken cancellationToken);

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
