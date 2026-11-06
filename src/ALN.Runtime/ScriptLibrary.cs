using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The folders a model's scripts move between (docs/25 §2).
///
/// <c>draft</c> is written and not mounted; <c>live</c> is written and mounted. Moving between them is what a mount
/// records, so the folder is the answer to "is this in effect" without opening a database. Only these two are moved by
/// the host: <c>trusted</c> and the shipped folder belong to whoever put the script there, and moving their files would
/// be editing their work.
///
/// This class uses System.IO on purpose: it operates on the data root itself, not on behalf of a session, and the file
/// scope the model is given (<c>scripts/draft</c>) is exactly the one it must not be able to reach around.
/// </summary>
public sealed class ScriptLibrary(string dataRoot)
{
    public const string ManifestFileName = "manifest.json";

    public string DraftRoot => Path.Combine(dataRoot, "scripts", "draft");
    public string LiveRoot => Path.Combine(dataRoot, "scripts", "live");

    /// <summary>The folder under one root whose manifest declares this id, or null. Ordered, so "the first one" is stable.</summary>
    public static string? Find(string root, string scriptId)
    {
        if (!Directory.Exists(root)) return null;
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    string.Equals(id.GetString(), scriptId, StringComparison.Ordinal))
                    return directory;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A folder that cannot be read is not this script: the catalogue reports unreadable files, and this
                // lookup only answers "where is this one".
            }
        }
        return null;
    }

    /// <summary>
    /// Moves one script folder, keeping its directory name. Refuses rather than merging: two folders claiming the same
    /// name in one place is the ambiguity the catalogue already refuses to resolve, and doing it silently here would
    /// decide it by accident.
    /// </summary>
    public Result<string> Move(string sourceDirectory, string targetRoot)
    {
        var name = Path.GetFileName(sourceDirectory);
        var target = Path.Combine(targetRoot, name);
        if (string.Equals(sourceDirectory, target, StringComparison.Ordinal)) return Result<string>.Success(target);
        if (Directory.Exists(target))
            return Result<string>.Failure(new(ErrorCode.Conflict, $"Already a script called {name} in {targetRoot}."));
        try
        {
            Directory.CreateDirectory(targetRoot);
            Directory.Move(sourceDirectory, target);
            return Result<string>.Success(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(new(ErrorCode.Internal, $"Could not move {sourceDirectory}: {ex.Message}"));
        }
    }

    /// <summary>Draft → live, if the script is in the draft folder at all. A script elsewhere is not this method's business.</summary>
    public Result<string>? Promote(string scriptId)
    {
        var folder = Find(DraftRoot, scriptId);
        if (folder is null) return null;
        var moved = Move(folder, LiveRoot);
        return moved.Ok && moved.Value is null
            ? Result<string>.Failure(new(ErrorCode.Internal, "Move produced no path."))
            : moved;
    }

    /// <summary>Live → draft, if the script is in the live folder at all.</summary>
    public Result<string>? Demote(string scriptId)
    {
        var folder = Find(LiveRoot, scriptId);
        if (folder is null) return null;
        return Move(folder, DraftRoot);
    }
}
