namespace ALN.Contracts;

public sealed record ChannelBinding(Address Adapter, string PlatformConversationId);
public sealed record ChannelSpec(Address Address, string Name, IReadOnlyList<ChannelBinding> Bindings);
public sealed record ChannelInfo(Address Address, string Name, IReadOnlyList<ChannelBinding> Bindings, bool Running);
public sealed record ChannelMessage(Guid MessageId, Address Channel, ChannelBinding Binding,
    string? PlatformMessageId, string Text, DateTimeOffset At);

public interface IChannelManager
{
    Result<ChannelInfo> Get(Address address);
    IReadOnlyList<ChannelInfo> List();
    bool IsBound(Address channel, Address adapter, string platformConversationId);
    ValueTask<Result<ChannelInfo>> CreateAsync(ChannelSpec spec, CancellationToken cancellationToken);
    ValueTask<Result> StartAsync(Address address, CancellationToken cancellationToken);
    ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken);
    ValueTask<Result> DestroyAsync(Address channel, CancellationToken cancellationToken);
    ValueTask<Result<int>> AcceptAsync(AdapterEnvelope envelope, CancellationToken cancellationToken);
}
