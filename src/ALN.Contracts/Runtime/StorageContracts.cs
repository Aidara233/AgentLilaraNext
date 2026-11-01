namespace ALN.Contracts;

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
