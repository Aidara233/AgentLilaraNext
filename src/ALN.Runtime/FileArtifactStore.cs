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

    /// <summary>
    /// Delete the day folders of one category that are older than <paramref name="keepDays"/>.
    ///
    /// This is what the daily folders were for: retention as a directory delete rather than a scan. It applies to
    /// <b>derived</b> artifacts only — a stored model call is a copy of a request that has already been answered, so
    /// deleting it loses nothing anybody can act on. Sessions, archives and memory are not this method's business: they
    /// are the record, and the record is kept until a person says otherwise.
    ///
    /// A folder name that is not a date is left alone and counted, because guessing what somebody meant by putting it
    /// there is how a cleanup deletes the wrong thing.
    /// </summary>
    public ValueTask<Result<ArtifactPrune>> PruneAsync(string category, int keepDays, DateOnly today,
        CancellationToken cancellationToken)
    {
        if (!IsValidToken(category))
            return ValueTask.FromResult(Result<ArtifactPrune>.Failure(
                new(ErrorCode.Invalid, "Artifact category must be a single path-free token.")));
        if (keepDays < 1)
            return ValueTask.FromResult(Result<ArtifactPrune>.Failure(
                new(ErrorCode.Invalid, "Artifact retention must keep at least one day.")));
        var categoryRoot = Path.Combine(Path.GetFullPath(root), category);
        if (!Directory.Exists(categoryRoot)) return ValueTask.FromResult(Result<ArtifactPrune>.Success(new(0, 0, 0, 0)));
        var removed = 0;
        var kept = 0;
        var unknown = 0;
        long bytes = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(categoryRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DateOnly.TryParseExact(Path.GetFileName(directory), "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
                {
                    unknown++;
                    continue;
                }
                // Age 7 with keepDays 7 means seven days have passed, so it goes: "keep the last seven days" is today
                // plus six, not today plus seven.
                if (today.DayNumber - day.DayNumber < keepDays)
                {
                    kept++;
                    continue;
                }
                bytes += SizeOf(directory);
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            return ValueTask.FromResult(Result<ArtifactPrune>.Success(new(removed, kept, unknown, bytes)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Result<ArtifactPrune>.Failure(
                new(ErrorCode.Unavailable, "Artifact cleanup failed.", ex.Message)));
        }
    }

    private static long SizeOf(string directory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
        }
        return total;
    }
}

/// <summary>
/// What one cleanup pass did. <paramref name="Unknown"/> counts folders whose names are not dates: left alone, and
/// reported, because "not deleted" and "not there" should not look the same afterwards.
/// </summary>
public sealed record ArtifactPrune(int Removed, int Kept, int Unknown, long Bytes);
