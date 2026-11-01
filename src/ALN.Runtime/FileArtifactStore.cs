using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>
/// File-backed artifact store under the data root. Daily folders keep directory listing cheap and
/// make retention a directory delete rather than a scan.
/// </summary>
public sealed class FileArtifactStore(string root, long maxBytes = 16 * 1024 * 1024) : IArtifactStore
{
    private static readonly char[] InvalidNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];

    public async ValueTask<Result<StoredArtifact>> StoreAsync(string category, string name,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (!IsValidToken(category))
            return Result<StoredArtifact>.Failure(new(ErrorCode.Invalid, "Artifact category must be a single path-free token."));
        if (!IsValidToken(name))
            return Result<StoredArtifact>.Failure(new(ErrorCode.Invalid, "Artifact name must be a single path-free token."));
        if (content.Length > maxBytes)
            return Result<StoredArtifact>.Failure(new(ErrorCode.Invalid,
                $"Artifact exceeds the {maxBytes} byte limit: {content.Length} bytes."));
        try
        {
            var day = DateOnly.FromDateTime(DateTime.UtcNow);
            var relative = Path.Combine(category, day.ToString("yyyyMMdd"), name + ".json");
            var absolute = Path.Combine(root, relative);
            var directory = Path.GetDirectoryName(absolute)!;
            SafeFileSystem.EnsureDirectory(root);
            SafeFileSystem.EnsureDirectory(directory);
            SafeFileSystem.VerifyNewOrRegularFile(absolute, root);
            // Write-then-rename keeps a half-written artifact from being read as a complete one.
            var temporary = absolute + ".partial";
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, absolute, overwrite: true);
            return Result<StoredArtifact>.Success(new(relative, content.Length));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Result<StoredArtifact>.Failure(new(ErrorCode.Unavailable, "Artifact could not be stored.", ex.Message));
        }
    }

    private static bool IsValidToken(string? value) =>
        value is { Length: > 0 and <= 128 } && value.IndexOfAny(InvalidNameChars) < 0 && value != "." && value != "..";
}
