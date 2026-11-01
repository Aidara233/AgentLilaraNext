namespace ALN.Contracts;

public sealed record ChannelBinding(Address Adapter, string PlatformConversationId);
public sealed record ChannelSpec(Address Address, string Name, IReadOnlyList<ChannelBinding> Bindings);
public sealed record ChannelInfo(Address Address, string Name, IReadOnlyList<ChannelBinding> Bindings, bool Running);
public sealed record ChannelMessage(Guid MessageId, Address Channel, ChannelBinding Binding,
    string? PlatformMessageId, string Text, DateTimeOffset At, string? Sender = null, bool MentionsSelf = false);

/// <summary>Messages accepted for one Channel that a Session has not consumed yet.</summary>
public sealed record ChannelDelta(Address Channel, IReadOnlyList<StoredMessage> Messages)
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
    ValueTask<Result<ChannelDelta>> TakeDeltaAsync(Address channel, CancellationToken cancellationToken);
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
}
