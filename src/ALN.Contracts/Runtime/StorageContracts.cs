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

/// <summary>
/// Small durable state that belongs to whoever produced it: a cursor, a rate-limit window, a last-seen
/// marker. Modules and host components ask for it through host services instead of inventing paths —
/// the host owns where the data root is, the caller owns only a scope name and a key, so nothing can
/// wander outside the data root and backups stay a single directory.
///
/// Values are JSON text: the store does not know what they mean, and a reader that cannot parse one
/// gets to decide whether that is a reset or a failure.
/// </summary>
public interface IStateStore
{
    /// <summary>Null when the key was never written.</summary>
    ValueTask<Result<string?>> ReadAsync(string scope, string key, CancellationToken cancellationToken);
    ValueTask<Result> WriteAsync(string scope, string key, string json, CancellationToken cancellationToken);
    ValueTask<Result> DeleteAsync(string scope, string key, CancellationToken cancellationToken);
}

/// <summary>
/// Turns an address or a scope name into something a state store will accept as a file name. Callers keep
/// their own readable scope ("channel.desired", "graph"); this maps the part that names an instance.
///
/// Two callers want exactly this rule — the parameter registry (scope = component address) and the graph
/// (scope = mount rule and target) — so it lives here rather than being copied until the copies drift.
/// </summary>
public static class StateName
{
    public const int MaxLength = 64;
    private const int MaxKeptPrefix = 48;

    /// <summary>Keeps [A-Za-z0-9_.-], replaces anything else, and disambiguates long or graffiti-ish input with a hash suffix.</summary>
    public static string Encode(string value)
    {
        var safe = new string(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
        if (safe == value && safe.Length is > 0 and <= MaxLength) return safe;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
        var trimmed = safe.Length > MaxKeptPrefix ? safe[..MaxKeptPrefix] : safe;
        return $"{trimmed}-{hash}";
    }
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
