namespace ALN.Contracts;

/// <summary>One entry of a directory listing: a name inside a directory, not a path.</summary>
/// <param name="Bytes">Zero for a directory: a directory's size is a question about its contents, not about itself.</param>
public sealed record FileEntry(string Name, bool IsDirectory, long Bytes, DateTimeOffset ModifiedAt);

/// <summary>
/// One node of a subtree walk, with its path relative to where the walk started. The path is what a caller passes
/// back in, so it is the only identity worth carrying.
/// </summary>
public sealed record FileNode(string Path, bool IsDirectory, long Bytes, DateTimeOffset ModifiedAt);

/// <summary>
/// A file as it was read. <see cref="Bytes"/> is the size on disk, which is not the same as the text's length in
/// characters — a caller deciding "is this too big to show" wants the former.
/// </summary>
/// <param name="Path">The canonical relative path within the scope it was read from, not a scope-qualified one: the
/// caller passed the scope, so repeating it here would only invite pasting it back into the wrong argument.</param>
public sealed record FileText(string Path, string Text, long Bytes, DateTimeOffset ModifiedAt);

/// <summary>
/// The only way a module touches the disk. <c>scope</c> names a root the host registered; everything below it is a
/// relative path. A module never sees, builds or is handed a real absolute path — that is what keeps "which
/// directories this process may read" a host decision instead of a convention (C-20).
///
/// Two refusals are deliberately different, and callers must not blur them:
/// <list type="bullet">
///   <item><see cref="ErrorCode.Denied"/> — the scope is not one this caller may use. Not a typo, an attempt.</item>
///   <item><see cref="ErrorCode.Invalid"/> — the path is malformed (absolute, <c>..</c>, a reserved name, …). A
///   caller can fix this one, so the message says which segment was wrong.</item>
/// </list>
///
/// Text only, on purpose. This is the surface for "the model may tidy files up", and a byte-level interface on it
/// would only invite storing things the model cannot read back.
/// </summary>
public interface IFileSystemAccess
{
    /// <summary>Longest path this surface accepts, separators included.</summary>
    const int MaxPathLength = 512;

    /// <summary>Longest single segment.</summary>
    const int MaxSegmentLength = 96;

    /// <summary>
    /// Largest file this surface will read or write whole. A file beyond this is not "a document a model reads",
    /// it is data with a different owner.
    /// </summary>
    const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>Most nodes one subtree walk returns. A walk that exceeds it fails rather than truncating silently.</summary>
    const int MaxTreeNodes = 2000;

    /// <summary>The scopes this caller may touch, in the order the host registered them.</summary>
    IReadOnlyList<string> Scopes { get; }

    /// <summary>The immediate children of a directory, names only, ordered by name. A missing directory is an error.</summary>
    ValueTask<Result<IReadOnlyList<FileEntry>>> ListAsync(string scope, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Everything under <paramref name="path"/>, depth first, directories included. An empty path walks the scope's
    /// root. Paths in the answer are relative to where the walk started, which is what makes them usable as input.
    /// </summary>
    ValueTask<Result<IReadOnlyList<FileNode>>> TreeAsync(string scope, string path, CancellationToken cancellationToken);

    /// <summary>Null when there is no such file. A directory where a file was expected is an error, not a null.</summary>
    ValueTask<Result<FileText?>> ReadAsync(string scope, string path, CancellationToken cancellationToken);

    /// <summary>Stat without reading. Null when nothing is there.</summary>
    ValueTask<Result<FileEntry?>> StatAsync(string scope, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the whole file, creating it and its parents if needed. Nothing is left half-written: the caller sees
    /// either the old content or the new one (docs/17).
    /// </summary>
    ValueTask<Result<FileText>> WriteAsync(string scope, string path, string text, CancellationToken cancellationToken);

    /// <summary>
    /// Adds to the end of a file, creating it and its parents if needed. Appends from different callers do not
    /// interleave: one call's text is one contiguous run. Returns the file's size afterwards.
    /// </summary>
    ValueTask<Result<long>> AppendAsync(string scope, string path, string text, CancellationToken cancellationToken);

    /// <summary>Renames or moves one entry. The destination's parent must exist; an existing destination is a conflict.</summary>
    ValueTask<Result> MoveAsync(string scope, string from, string to, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a file, or a directory. A directory that still has something in it is a conflict unless
    /// <paramref name="recursive"/> is set — deleting a subtree by accident is not something a caller can be
    /// protected from later, so it has to be asked for.
    /// </summary>
    ValueTask<Result> DeleteAsync(string scope, string path, bool recursive, CancellationToken cancellationToken);
}

/// <summary>
/// The path rule, in the contract rather than in one implementation: it decides what "the same file" means, and two
/// backends answering that differently would let a caller write one file and read another.
///
/// Refusal, never rewriting: turning <c>a/b</c> into <c>a_b</c> is how two different names end up colliding on disk,
/// and the caller would never learn which one it actually got. This is the same policy as <see cref="ModuleId"/>.
/// </summary>
public static class FilePaths
{
    /// <summary>Both separators count as separators on every platform: a path that means one thing here and another on Windows is a hole.</summary>
    private static readonly char[] Separators = ['/', '\\'];

    private static readonly char[] Forbidden = [':', '*', '?', '"', '<', '>', '|'];

    private static readonly string[] Reserved =
    [
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    ];

    /// <summary>Splits a path into its segments after checking it. An empty path is legal and means "the root itself".</summary>
    public static Result<IReadOnlyList<string>> Split(string? path)
    {
        if (path is null) return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid, "A path is required; use an empty string for the root."));
        if (path.Length > IFileSystemAccess.MaxPathLength)
            return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid,
                $"Path is longer than {IFileSystemAccess.MaxPathLength} characters."));
        if (path.Any(char.IsControl))
            return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid, "Path contains a control character."));
        if (path.Length > 0 && (path[0] == '/' || path[0] == '\\'))
            return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid,
                "Path must be relative to the scope; absolute paths are not accepted."));
        if (path.Length > 1 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid, "Path looks like a drive letter."));

        // A trailing separator is a habit, not a meaning: "a/b/" and "a/b" are the same entry.
        var trimmed = path.TrimEnd(Separators);
        if (trimmed.Length == 0) return Result<IReadOnlyList<string>>.Success([]);

        var segments = new List<string>();
        foreach (var segment in trimmed.Split(Separators, StringSplitOptions.None))
        {
            if (segment.Length == 0)
                return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid,
                    $"Path has an empty segment: {path}. Two separators in a row make a name nobody meant."));
            if (segment is "." or "..")
                return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid,
                    $"Path segment '{segment}' is not allowed; a caller may not refer to a parent directory."));
            if (segment.Length > IFileSystemAccess.MaxSegmentLength)
                return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid,
                    $"Path segment is longer than {IFileSystemAccess.MaxSegmentLength} characters."));
            if (segment.IndexOfAny(Forbidden) >= 0)
                return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid,
                    $"Path segment '{segment}' contains a character that is not allowed in a name."));
            if (segment[0] == '.')
                return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid,
                    $"Path segment '{segment}' starts with a dot; hidden entries are not part of this surface."));
            if (segment[^1] is ' ' or '.')
                return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid,
                    $"Path segment '{segment}' ends with a space or a dot, which some file systems silently drop."));
            if (Reserved.Contains(segment, StringComparer.OrdinalIgnoreCase))
                return Result<IReadOnlyList<string>>.Failure(new(ErrorCode.Invalid,
                    $"Path segment '{segment}' is a reserved device name on Windows."));
            segments.Add(segment);
        }
        return Result<IReadOnlyList<string>>.Success(segments);
    }

    /// <summary>The canonical spelling of a path, or null when it is not legal. One separator, no trailing one.</summary>
    public static string? Canonical(string? path) =>
        Split(path).Value is { } segments ? string.Join('/', segments) : null;

    public static Result Validate(string? path) =>
        Split(path) is { Ok: false } failed ? Result.Failure(failed.Error!) : Result.Success();
}
