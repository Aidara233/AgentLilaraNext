namespace ALN.Contracts;

public readonly record struct Address(string Module, string? Kind = null, string? Instance = null)
{
    public string TypeId => Kind is null ? Module : $"{Module}.{Kind}";
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
    string? PlatformMessageId, string Text, DateTimeOffset At, string? Sender = null, bool MentionsSelf = false)
{
    public Guid EventId { get; init; } = Guid.NewGuid();
}
public sealed record OutboundMessage(string PlatformConversationId, string Text, string? ReplyTo = null);
public sealed record SendResult(bool Sent, string AttemptId, string? Detail = null);
public sealed record AdapterInfo(Address Address, AdapterDescriptor Descriptor, AdapterState State);

/// <summary>
/// A stored Adapter instance. <paramref name="Settings"/> is opaque to the core: an endpoint, a token,
/// a polling interval — the core neither reads nor validates the keys, it only hands the dictionary to
/// the factory. Keeping it here rather than in a side file means one persisted unit per instance, and
/// that the value never leaves the process through an action (the DTOs don't carry it).
/// </summary>
public sealed record AdapterSpec(Address Address, AdapterDescriptor Descriptor,
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
public interface IChatAdapter : IInboundAdapter, IOutboundAdapter
{
    Address Address { get; }
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

public interface IModuleTypeCatalog
{
    bool IsKnownAbstract(string typeId);
    bool IsConcrete(string typeId);
    bool IsAssignableTo(string concreteTypeId, string abstractTypeId);
}
public interface IAdapterManager
{
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
public sealed record MailboxInput(string ConversationId, string Token, string Json);
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
