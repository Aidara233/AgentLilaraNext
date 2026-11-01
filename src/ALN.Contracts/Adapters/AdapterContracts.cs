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
    string? PlatformMessageId, string Text, DateTimeOffset At, string? Sender = null)
{
    public Guid EventId { get; init; } = Guid.NewGuid();
}
public sealed record OutboundMessage(string PlatformConversationId, string Text, string? ReplyTo = null);
public sealed record SendResult(bool Sent, string AttemptId, string? Detail = null);
public sealed record AdapterInfo(Address Address, AdapterDescriptor Descriptor, AdapterState State);
public sealed record AdapterSpec(Address Address, AdapterDescriptor Descriptor);

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
    ValueTask<IReadOnlyList<MailboxInput>> ReadPendingAsync(CancellationToken cancellationToken);
    ValueTask CompleteAsync(string conversationId, string token, bool accepted, string? error, CancellationToken cancellationToken);
    ValueTask<string> WriteOutputAsync(OutboundMessage message, CancellationToken cancellationToken);
}
