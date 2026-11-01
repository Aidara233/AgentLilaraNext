namespace ALN.Contracts;

public enum MessageDirection { Inbound = 0, Outbound = 1 }

/// <summary>
/// A message as seen by an owner (an Adapter or a Channel). Owners keep their own history, so a
/// channel can be rebuilt from its own records rather than from a mailbox directory.
/// </summary>
public sealed record StoredMessage(
    string Id,
    Address Owner,
    string PlatformConversationId,
    string? PlatformMessageId,
    string? Sender,
    string Text,
    DateTimeOffset At,
    MessageDirection Direction = MessageDirection.Inbound);

/// <summary>Per-owner message history, append-only JSONL under the data root.</summary>
public interface IMessageStore
{
    ValueTask<Result> AppendAsync(StoredMessage message, CancellationToken cancellationToken);
    ValueTask<Result<IReadOnlyList<StoredMessage>>> ReadAsync(Address owner, CancellationToken cancellationToken);
}

/// <summary>Adapter specs survive restarts; the mailbox directory is data, not configuration.</summary>
public interface IAdapterSpecStore
{
    ValueTask<Result> SaveAsync(AdapterSpec spec, CancellationToken cancellationToken);
    ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken);
    ValueTask<Result<IReadOnlyList<AdapterSpec>>> LoadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Channel specs survive restarts. Channels are created by scripts, so without this file a
/// channel would vanish on the next boot and its binding would be re-derived from folders.
/// </summary>
public interface IChannelSpecStore
{
    ValueTask<Result> SaveAsync(ChannelSpec spec, CancellationToken cancellationToken);
    ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken);
    ValueTask<Result<IReadOnlyList<ChannelSpec>>> LoadAllAsync(CancellationToken cancellationToken);
}

public sealed record StoredArtifact(string RelativePath, long Bytes);

/// <summary>
/// Storage module surface for content that is too large for the signal log but must survive:
/// model calls, raw payloads, assembled prompts. It knows bytes and paths only — never a feature
/// module's types — so storage never depends on the thing it stores.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Writes <paramref name="content"/> under category/&lt;yyyyMMdd&gt;/&lt;name&gt;.json.
    /// <paramref name="name"/> is a caller-chosen stable id; it must not contain path separators.
    /// </summary>
    ValueTask<Result<StoredArtifact>> StoreAsync(string category, string name, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
}
