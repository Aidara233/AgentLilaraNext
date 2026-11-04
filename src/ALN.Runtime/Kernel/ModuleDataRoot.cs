using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Where a module keeps the things that live and die with it (docs/22 §2.2): <c>&lt;dataRoot&gt;/modules/&lt;id&gt;/</c>.
///
/// One rule, applied in one place: a module's own directory is for what follows the module's life, and anything
/// that has to be read across modules or across restarts belongs in the store instead. This class does not try to
/// enforce that split — it cannot see what a module writes. What it can do is make the two endings explicit:
/// archive (the module may come back, so its data must survive) and delete (the module is gone, but deleting a
/// module is not a decision to forget what happened, so the directory leaves through the bin).
///
/// Every path it hands out is checked against the root. An id is validated as a name (<see cref="ModuleId"/>),
/// and a file name has to be a single segment — a module that asked for <c>../aln.db</c> would otherwise be
/// writing into somebody else's data with the kernel's blessing.
/// </summary>
public sealed class ModuleDataRoot(string root)
{
    public const string DirectoryName = "modules";
    public const string ArchiveDirectoryName = "archive";

    private static readonly char[] Separators = ['/', '\\'];

    public string Root { get; } = Path.GetFullPath(root);

    public string ModulesRoot => Path.Combine(Root, DirectoryName);

    /// <summary>The directory a module owns. Not created: asking where something would go must not create it.</summary>
    public string Directory(ModuleId id)
    {
        RequireLegal(id);
        return Path.Combine(ModulesRoot, id.Value);
    }

    /// <summary>Creates the directory if it is missing. Idempotent, because a restart is not an event.</summary>
    public string Ensure(ModuleId id)
    {
        var directory = Directory(id);
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>A file inside the module's own directory. The name may not carry a path.</summary>
    public string File(ModuleId id, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Separators) >= 0 ||
            fileName is "." or ".." || fileName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"Not a plain file name: {fileName}", nameof(fileName));
        var path = Path.GetFullPath(Path.Combine(Directory(id), fileName));
        if (!IsInside(path, Directory(id)))
            throw new ArgumentException($"File escapes its module directory: {fileName}", nameof(fileName));
        return path;
    }

    /// <summary>
    /// Moves the module's directory into the bin and returns where it went, or null when there was nothing to move.
    /// Used when a module goes away for a reason worth remembering: the directory survives, but not where a
    /// re-created module would find it and quietly inherit somebody else's history.
    /// </summary>
    public Result<string?> Archive(ModuleId id, string? reason = null)
    {
        var directory = Directory(id);
        if (!System.IO.Directory.Exists(directory)) return Result<string?>.Success(null);
        var target = Path.Combine(Root, ArchiveDirectoryName, DirectoryName,
            $"{id.Value}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            System.IO.Directory.Move(directory, target);
            if (!string.IsNullOrWhiteSpace(reason)) System.IO.File.WriteAllText(Path.Combine(target, "archived-because.txt"), reason);
            return Result<string?>.Success(target);
        }
        catch (Exception ex)
        {
            return Result<string?>.Failure(new(ErrorCode.Internal, $"Could not archive {id}.", ex.Message));
        }
    }

    /// <summary>
    /// Deletes the module's directory. Deliberately separate from <see cref="Archive"/>: a caller that wants both
    /// says so, in that order, and the two steps stay visible in the call instead of hiding inside a flag.
    /// </summary>
    public Result Delete(ModuleId id)
    {
        var directory = Directory(id);
        if (!System.IO.Directory.Exists(directory)) return Result.Success();
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(new(ErrorCode.Internal, $"Could not delete {id}.", ex.Message));
        }
    }

    private static void RequireLegal(ModuleId id)
    {
        if (!ModuleId.IsLegal(id.Value))
            throw new ArgumentException($"Not a legal module id: {id}", nameof(id));
    }

    private static bool IsInside(string path, string directory) =>
        path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
