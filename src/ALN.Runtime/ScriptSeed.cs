using System.Text.Json;
using System.Text.Json.Nodes;

namespace ALN.Runtime;

/// <summary>
/// What seeding did: which shipped scripts were copied, which were left alone because the operator already has one
/// under that name, and which could not be written. A failure here is reported, never thrown: a copy that could not
/// be made is a script the operator can still add by hand, not a reason to refuse to start.
/// </summary>
public sealed record ScriptSeedReport(
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Kept,
    IReadOnlyList<(string Id, string Reason)> Failed)
{
    public bool AddedAnything => Copied.Count > 0;
}

/// <summary>
/// Puts a copy of each shipped script into <c>&lt;dataRoot&gt;/scripts/trusted</c>, once, and never touches it
/// again (C-03, the same rule <see cref="ModuleManifestStore.WriteIfMissingAsync"/> follows for module files).
///
/// Why copy at all, rather than keep reading the folder next to the binary. A script is behaviour a person edits,
/// and the folder next to the binary is not theirs: the next deploy replaces it wholesale, so an edit there lasts
/// until the next build and looks like it was forgotten. A copy in the data root is the only place where "edit it,
/// then reload" means anything — and the only place an operator can see what actually runs.
///
/// The copy is deliberately not byte-identical: <c>trustDomain</c> is rewritten from <c>builtin</c> to
/// <c>trusted</c>. That is the point of copying. "trusted" means a person put it there and may change it, and a
/// manifest claiming to ship from a folder a person can edit is exactly what the catalogue refuses.
/// </summary>
public sealed class ScriptSeed(string programRoot, string dataRoot)
{
    public const string ManifestFileName = "manifest.json";
    public const string SourceFileName = "main.js";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string BundledRoot => Path.Combine(programRoot, "resources", "scripts", "builtin");
    public string TrustedRoot => Path.Combine(dataRoot, "scripts", "trusted");

    public async ValueTask<ScriptSeedReport> CopyMissingAsync(CancellationToken cancellationToken)
    {
        var copied = new List<string>();
        var kept = new List<string>();
        var failed = new List<(string Id, string Reason)>();
        if (!Directory.Exists(BundledRoot)) return new(copied, kept, failed);

        foreach (var source in Directory.EnumerateDirectories(BundledRoot).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(source);
            var manifestPath = Path.Combine(source, ManifestFileName);
            var sourcePath = Path.Combine(source, SourceFileName);
            if (!File.Exists(manifestPath) || !File.Exists(sourcePath)) continue;

            var target = Path.Combine(TrustedRoot, id);
            // Already here: it is the operator's file now. Rewriting it would undo an edit silently — the one thing
            // this method exists to prevent — and would also make every start a potential change of behaviour.
            if (File.Exists(Path.Combine(target, ManifestFileName))) { kept.Add(id); continue; }

            try
            {
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false))
                    ?? throw new JsonException($"The shipped manifest is empty: {manifestPath}");
                manifest["trustDomain"] = "trusted";

                Directory.CreateDirectory(target);
                // The body lands first. A directory holding a manifest with no main.js is a catalogue that refuses
                // to load — and the host exits on that — so the order makes a crash in between leave a folder the
                // catalogue simply does not see, which is recoverable by the next start.
                await File.WriteAllBytesAsync(Path.Combine(target, SourceFileName),
                    await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(target, ManifestFileName),
                    manifest.ToJsonString(WriteOptions), cancellationToken).ConfigureAwait(false);
                copied.Add(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                failed.Add((id, ex.Message));
            }
        }

        return new(copied, kept, failed);
    }
}
