using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// A module a package says should exist, without saying anything else about it (docs/22, C5).
///
/// The package knows it needs this module to be there; it does not know — and must not decide — where the person
/// using it wants it configured. So the package declares the shape, the scaffold writes the file once, and from
/// then on the file belongs to whoever edits it.
/// </summary>
public sealed record ModuleSubmodule(string Id, string Type, bool Enabled = true);

/// <summary>
/// Writes the module directories a package declares, but only the ones that are missing.
///
/// This is the one place the system writes a <c>module.json</c>, and it writes exactly once per module: a package
/// upgrade that redeclares a submodule must not overwrite the file somebody spent an hour configuring. "Missing"
/// is the whole condition — no merge, no schema migration, no rewriting. If a scaffolded file is wrong, the person
/// who owns the directory edits it, which is the same thing they would do for a hand-written one.
/// </summary>
public sealed class ModuleScaffolder(ModuleManifestStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ModuleScaffolder(string dataRoot) : this(new ModuleManifestStore(dataRoot))
    {
    }

    /// <summary>Creates whatever is missing. Returns the ids it created, and one issue per declaration it refused.</summary>
    public async ValueTask<Result<ModuleScaffoldResult>> EnsureAsync(IEnumerable<ModuleSubmodule> declared,
        CancellationToken cancellationToken)
    {
        var created = new List<ModuleId>();
        var issues = new List<ModuleIssue>();
        foreach (var submodule in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ModuleId.TryParse(submodule.Id, out var id))
            {
                issues.Add(new(submodule.Id, $"Illegal module id in a package declaration: {submodule.Id}"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(submodule.Type))
            {
                issues.Add(new(submodule.Id, "A declared submodule needs a type."));
                continue;
            }

            var directory = Path.Combine(store.ModulesRoot, id.Value);
            var path = Path.Combine(directory, ModuleManifestStore.FileName);
            if (File.Exists(path))
            {
                // Already there: it may have been edited, and an edit is not something to overwrite.
                continue;
            }

            try
            {
                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(new { id = id.Value, type = submodule.Type, enabled = submodule.Enabled }, JsonOptions);
                // CreateNew: two processes scaffolding at once must not both "succeed" by overwriting each other.
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                created.Add(id);
            }
            catch (IOException ex)
            {
                issues.Add(new(path, $"Could not scaffold: {ex.Message}"));
            }
        }
        return Result<ModuleScaffoldResult>.Success(new(created, issues));
    }
}

public sealed record ModuleScaffoldResult(IReadOnlyList<ModuleId> Created, IReadOnlyList<ModuleIssue> Issues);
