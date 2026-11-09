using System.Text.Json;

namespace ALN.Contracts;

/// <summary>
/// One plugin package root, resolved to the directory that actually holds the code.
///
/// A root is a directory under <c>&lt;dataRoot&gt;/plugins</c>. Two shapes are legal, and the shape is decided by
/// what is on disk, not by a version of this program:
///
/// <code>
/// plugins/&lt;id&gt;/plugin.json            ← one version, flat ("legacy"): what shipped before versions existed
/// plugins/&lt;id&gt;/active.json            ← the version that is in force
/// plugins/&lt;id&gt;/&lt;version&gt;/plugin.json   ← one version, side by side with the others
/// </code>
///
/// Versions are directories with names; which one is in force is written down in <c>active.json</c> and nowhere
/// else. The alternative — "the highest version wins" — reads well until somebody drops a hand-built copy of an
/// older version in and cannot tell why it is being ignored. A pointer can be read, edited, reverted, and it makes
/// a crash harmless: it is written only after a replacement has succeeded, so a failure leaves it pointing at the
/// version that was already running.
///
/// <see cref="Problem"/> is a sentence for a person, never an exception: a package that cannot be resolved is
/// skipped and said so, because the host has to be able to start with a broken package in the folder.
/// </summary>
public sealed record PluginPackageRoot(
    string Id,
    string RootDirectory,
    string ActiveDirectory,
    string? Version,
    bool Legacy,
    IReadOnlyList<string> Versions,
    string? Problem)
{
    public bool Resolved => Problem is null && Version is not null;
}

/// <summary>
/// Reads the plugin package layout. One place decides what a directory means, so the loader and the script
/// catalogue cannot disagree about which version is running — they disagreed once, and the symptom was a hot
/// replacement that swapped the module while its scripts kept running the old body.
/// </summary>
public static class ModulePackageLayout
{
    public const string ManifestFileName = "plugin.json";
    /// <summary>The file's old name. Named here so a package in the folder can be told to rename it, not ignored.</summary>
    public const string LegacyManifestFileName = "module.json";
    public const string ActiveFileName = "active.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Every package root under a <c>plugins</c> folder, in the order a person sees them.</summary>
    public static IReadOnlyList<string> PackageRoots(string pluginsRoot) =>
        Directory.Exists(pluginsRoot)
            ? Directory.GetDirectories(pluginsRoot).Order(StringComparer.Ordinal).ToArray()
            : [];

    /// <summary>
    /// Resolves one package root to the directory that holds the code to load.
    ///
    /// The one inference it makes: a root with exactly one version directory and no pointer means that version.
    /// There is nothing else it could mean, and refusing would make "drop it in and it runs" depend on a file the
    /// person has no reason to know about. Two or more candidates with no pointer is a refusal instead: choosing
    /// there is how a host quietly runs last week's code.
    /// </summary>
    public static Result<PluginPackageRoot> Resolve(string packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
            return Result<PluginPackageRoot>.Failure(new(ErrorCode.Invalid, "A package root is required."));
        var root = Path.GetFullPath(packageRoot);
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var flatManifest = Path.Combine(root, ManifestFileName);
        var pointer = Path.Combine(root, ActiveFileName);
        var flat = File.Exists(flatManifest);
        var pointed = File.Exists(pointer);
        var versions = ListVersions(root);

        if (flat && (pointed || versions.Count > 0))
            return Failed(root,
                $"{ManifestFileName} sits beside {(pointed ? ActiveFileName + " and " : "")}version directories: a package "
                + "root cannot be both one version and a collection of them. Move the manifest into a version directory, "
                + "then write the one to run into " + ActiveFileName + ".");

        if (flat)
        {
            var identity = ReadIdentity(root);
            if (!identity.Ok || identity.Value is null)
                return Failed(root, identity.Error?.Message ?? $"{ManifestFileName} could not be read.");
            if (!StringComparer.Ordinal.Equals(identity.Value.Id, name))
                return Failed(root,
                    $"plugin.json says id \"{identity.Value.Id}\" but the folder is \"{name}\": the folder name is what "
                    + "scripts and audit records point at, so the two have to agree.");
            return Result<PluginPackageRoot>.Success(
                new(name, root, root, identity.Value.Version, Legacy: true, versions, null));
        }

        if (!pointed)
        {
            // The old manifest name is worth its own sentence: it is a package somebody wrote for an older host, and
            // "nothing here" would send them looking in the wrong place.
            if (File.Exists(Path.Combine(root, LegacyManifestFileName)))
            {
                var renamed = ReadIdentity(root);
                return Failed(root, renamed.Error?.Message ?? $"{ManifestFileName} is missing.");
            }
            if (versions.Count == 0)
                return Failed(root,
                    $"No {ManifestFileName} here, and no version directories either. Available: "
                    + $"{Describe(Directory.GetDirectories(root))}.");
            if (versions.Count > 1)
                return Failed(root,
                    $"Several versions and no {ActiveFileName} says which one is in force: {string.Join(", ", versions)}. "
                    + "Write the one to run into " + ActiveFileName + ".");
        }

        string? declared = null;
        if (pointed)
        {
            var read = ReadActive(root);
            if (!read.Ok) return Failed(root, read.Error?.Message ?? $"{ActiveFileName} is invalid.");
            declared = read.Value;
        }

        var chosen = declared ?? (versions.Count == 1 ? versions[0] : null);
        if (chosen is null)
            return Failed(root, $"{ActiveFileName} names no version.");
        var directory = Path.Combine(root, chosen);
        if (!Directory.Exists(directory))
            return Failed(root,
                $"{ActiveFileName} names \"{chosen}\", which is not a directory. Available: {Describe(versions)}.");

        var found = ReadIdentity(directory);
        if (!found.Ok || found.Value is null)
            return Failed(root, found.Error?.Message ?? $"{ManifestFileName} could not be read.");
        if (!StringComparer.Ordinal.Equals(found.Value.Id, name))
            return Failed(root,
                $"version \"{chosen}\": plugin.json says id \"{found.Value.Id}\" but the package folder is \"{name}\".");
        if (!StringComparer.Ordinal.Equals(found.Value.Version, chosen))
            return Failed(root,
                $"version directory \"{chosen}\" holds a plugin.json that says version \"{found.Value.Version}\": "
                + "the directory name is what active.json points at, so the two have to agree.");

        return Result<PluginPackageRoot>.Success(
            new(name, root, directory, chosen, Legacy: false, versions, null));
    }

    /// <summary>The version directories of one root, whether or not any of them is in force.</summary>
    public static IReadOnlyList<string> ListVersions(string packageRoot)
    {
        var root = Path.GetFullPath(packageRoot);
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root)
            .Where(directory => File.Exists(Path.Combine(directory, ManifestFileName)))
            .Select(directory => Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Writes which version is in force. Called only after a replacement has succeeded, so a crash in between leaves
    /// the pointer on the version that was already servicing requests.
    ///
    /// Written through a temporary file and moved into place: a half-written pointer is a package that does not
    /// start, and the file is small enough that "atomic" costs nothing.
    /// </summary>
    public static Result WriteActive(string packageRoot, string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Result.Failure(new(ErrorCode.Invalid, "A version is required."));
        var root = Path.GetFullPath(packageRoot);
        if (!Directory.Exists(root))
            return Result.Failure(new(ErrorCode.Invalid, $"Package root does not exist: {root}"));
        var directory = Path.Combine(root, version);
        if (!Directory.Exists(directory))
            return Result.Failure(new(ErrorCode.Invalid, $"Version directory does not exist: {directory}"));
        var temp = Path.Combine(root, ActiveFileName + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new ActiveVersionFile(version), JsonOptions));
            File.Move(temp, Path.Combine(root, ActiveFileName), overwrite: true);
            return Result.Success();
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return Result.Failure(new(ErrorCode.Internal, "Could not write the active version.", ex.Message));
        }
    }

    /// <summary>The version a root's pointer names, or null when there is no pointer. Invalid JSON is a failure.</summary>
    public static Result<string?> ReadActive(string packageRoot)
    {
        var root = Path.GetFullPath(packageRoot);
        var path = Path.Combine(root, ActiveFileName);
        if (!File.Exists(path)) return Result<string?>.Success(null);
        try
        {
            var text = File.ReadAllText(path).Trim();
            // Both shapes are accepted because this is a file a person edits: a bare version on one line is what
            // somebody will write by hand, and refusing it would be a rule with no reason behind it.
            if (text.Length > 0 && text[0] != '{')
                return Result<string?>.Success(text);
            var file = JsonSerializer.Deserialize<ActiveVersionFile>(text, JsonOptions);
            return string.IsNullOrWhiteSpace(file?.Version)
                ? Result<string?>.Failure(new(ErrorCode.Invalid, $"{ActiveFileName} has no \"version\"."))
                : Result<string?>.Success(file.Version);
        }
        catch (Exception ex)
        {
            return Result<string?>.Failure(new(ErrorCode.Invalid, $"{ActiveFileName} could not be read.", ex.Message));
        }
    }

    /// <summary>
    /// The id a package directory declares, or null. Used where only the name matters — the script catalogue stamps
    /// a package's scripts with <c>plugin:&lt;id&gt;</c>, and the id has to be the package's own, not whatever the
    /// folder happens to be called.
    /// </summary>
    public static string? DeclaredId(string packageDirectory)
    {
        var identity = ReadIdentity(packageDirectory);
        return identity.Ok ? identity.Value?.Id : null;
    }

    private static Result<PackageIdentity> ReadIdentity(string directory)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            if (File.Exists(Path.Combine(directory, LegacyManifestFileName)))
                return Result<PackageIdentity>.Failure(new(ErrorCode.Invalid,
                    $"Found {LegacyManifestFileName}: the manifest was renamed to {ManifestFileName} (same fields). Rename the file."));
            return Result<PackageIdentity>.Failure(new(ErrorCode.Invalid, $"{ManifestFileName} is missing."));
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            var id = Text(root, "id");
            var version = Text(root, "version");
            if (string.IsNullOrWhiteSpace(id))
                return Result<PackageIdentity>.Failure(new(ErrorCode.Invalid, $"{ManifestFileName} has no \"id\"."));
            if (string.IsNullOrWhiteSpace(version))
                return Result<PackageIdentity>.Failure(new(ErrorCode.Invalid, $"{ManifestFileName} has no \"version\"."));
            return Result<PackageIdentity>.Success(new(id.Trim(), version.Trim()));
        }
        catch (Exception ex)
        {
            return Result<PackageIdentity>.Failure(new(ErrorCode.Invalid, $"{ManifestFileName} could not be read.", ex.Message));
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Describe(IReadOnlyList<string> names) =>
        names.Count == 0 ? "nothing" : string.Join(", ", names.Select(Path.GetFileName));

    /// <summary>One sentence for a person, with the directory it is about — never an exception.</summary>
    private static Result<PluginPackageRoot> Failed(string root, string problem) =>
        Result<PluginPackageRoot>.Failure(new(ErrorCode.Invalid, problem, root));

    private sealed record PackageIdentity(string Id, string Version);

    private sealed record ActiveVersionFile(string Version);
}
