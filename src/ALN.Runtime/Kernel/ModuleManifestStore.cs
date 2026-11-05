using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>The modules a scan found, and everything that kept a directory from becoming one.</summary>
public sealed record ManifestScan(IReadOnlyList<ModuleManifest> Manifests, IReadOnlyList<ModuleIssue> Issues);

/// <summary>
/// Reads <c>&lt;root&gt;/modules/&lt;id&gt;/module.json</c>, one directory per module (docs/22 §2).
///
/// Read-only on purpose: what a module is has exactly one source (its own file), and a loader that also writes
/// would be the second one. Validation is deliberately shallow — the parts the kernel itself parses
/// (id, type, enabled, slaves, dependencies) are checked, and <c>config</c> is passed through untouched, because
/// a kernel that validates a module's configuration owns a copy of it forever.
///
/// A directory that fails to load is an <see cref="ModuleIssue"/>, not a fatal error: the scan returns every
/// module it could read and says which ones it could not, so that one broken manifest costs one module rather
/// than the whole data root (docs/22 §5).
/// </summary>
public sealed class ModuleManifestStore(string root)
{
    public const string FileName = "module.json";
    public const string DirectoryName = "modules";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // A person types these files; a trailing comma or a comment is a typo, not a data model.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string Root { get; } = root;
    public string ModulesRoot => Path.Combine(Root, DirectoryName);

    /// <summary>
    /// Writes a module's file once, if it is not there yet (C-03). Never rewrites: the file belongs to whoever edits
    /// it, and a module declared again in a later build must not overwrite what somebody configured (there is no
    /// migrator — C-24). The directory name and the <c>id</c> come from the same value, so they cannot disagree.
    /// </summary>
    public async ValueTask<Result> WriteIfMissingAsync(ModuleManifest manifest, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(ModulesRoot, manifest.Id.Value);
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path)) return Result.Success();
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                Converters = { new ModuleIdJsonConverter() }
            });
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new(ErrorCode.Internal, $"Could not write {path}: {ex.Message}"));
        }
    }

    public async ValueTask<ManifestScan> ScanAsync(IModuleFactoryRegistry? types, CancellationToken cancellationToken) =>
        await ScanAsync(types, hostOwns: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The same scan, told which types the host instantiates itself.
    ///
    /// Every instance lives in its own module directory too (docs/22 §2), and its <c>module.json</c> names a type the
    /// kernel must not build: the manager does. Without this, every instance would come back as "unknown module type"
    /// — one false issue per adapter, per channel, per provider — and the receipt would be noise nobody could read.
    /// The type's own implementation decides who owns it, so the question is asked of the host, not parsed from the id.
    /// </summary>
    public async ValueTask<ManifestScan> ScanAsync(IModuleFactoryRegistry? types, Func<string, bool>? hostOwns,
        CancellationToken cancellationToken, Func<ModuleId, bool>? declaredByHost = null)
    {
        var manifests = new List<ModuleManifest>();
        var issues = new List<ModuleIssue>();
        if (!Directory.Exists(ModulesRoot)) return new(manifests, issues);

        // Case-insensitive because the file systems are: two directories that differ only in case cannot coexist
        // everywhere, and refusing both here is better than loading one of them on some machines.
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(ModulesRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
            {
                issues.Add(new(directory, $"Not a module: no {FileName}."));
                continue;
            }

            RawManifest? raw;
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                raw = JsonSerializer.Deserialize<RawManifest>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                issues.Add(new(path, $"Not valid JSON: {ex.Message}"));
                continue;
            }
            catch (IOException ex)
            {
                issues.Add(new(path, $"Could not be read: {ex.Message}"));
                continue;
            }

            if (raw is null || string.IsNullOrWhiteSpace(raw.Id) || string.IsNullOrWhiteSpace(raw.Type))
            {
                issues.Add(new(path, "A module needs both \"id\" and \"type\"."));
                continue;
            }

            if (!ModuleId.TryParse(raw.Id, out var id))
            {
                issues.Add(new(path, $"Illegal module id: {raw.Id}"));
                continue;
            }

            // The id doubles as the directory name, so the two have to agree. Picking one of them would mean the
            // graph answers to a name that is not where it lives — and the next person to open the folder would be
            // looking at a different module.
            if (!string.Equals(id.Value, name, StringComparison.Ordinal))
            {
                issues.Add(new(path, $"Id \"{id.Value}\" does not match its directory \"{name}\"."));
                continue;
            }

            if (seen.TryGetValue(id.Value, out var first))
            {
                // Two directories, one name: the first wins and the second is reported. Startup does not fail over
                // it — refusing to boot would cost every other module too.
                issues.Add(new(path, $"Duplicate id \"{id.Value}\"; already loaded from {first}."));
                continue;
            }

            if (declaredByHost is not null && declaredByHost(id))
            {
                // Declared in code by the host: the file is the record of that declaration (C-03), not a second
                // source, and its type is not this store's to judge — the host builds it. Skipped silently; it is
                // already in the graph.
                seen[id.Value] = path;
                continue;
            }

            if (types is not null && !types.IsRegistered(raw.Type))
            {
                // Not the loader's to build: the host owns this type, so the file is that instance's configuration
                // and the manager will announce the instance when it holds it. Skipped silently — an issue here would be
                // one line per instance, which is the same as no receipt at all.
                if (hostOwns is not null && hostOwns(raw.Type)) continue;
                issues.Add(new(path, $"Unknown module type: {raw.Type}"));
                continue;
            }

            var slaves = new List<ModuleSlave>();
            var broken = false;
            foreach (var slave in raw.Slaves ?? [])
            {
                if (!ModuleId.TryParse(slave.Id, out var slaveId))
                {
                    issues.Add(new(path, $"Illegal slave id: {slave.Id}"));
                    broken = true;
                    continue;
                }
                slaves.Add(new ModuleSlave(slaveId, slave.Kinds ?? [], slave.RequireAll ?? true));
            }
            if (broken) continue;

            var dependencies = new List<ModuleId>();
            foreach (var dependency in raw.Dependencies ?? [])
            {
                if (!ModuleId.TryParse(dependency, out var dependencyId))
                {
                    issues.Add(new(path, $"Illegal dependency id: {dependency}"));
                    broken = true;
                    continue;
                }
                dependencies.Add(dependencyId);
            }
            if (broken) continue;

            seen[id.Value] = path;
            manifests.Add(new ModuleManifest(
                id,
                raw.Type,
                raw.Enabled ?? true,
                raw.Config?.GetRawText(),
                slaves,
                dependencies));
        }

        return new(manifests, issues);
    }

    /// <summary>The file's shape, as written. Absent fields stay absent so that "not said" is not read as "false".</summary>
    private sealed record RawManifest(
        string? Id,
        string? Type,
        bool? Enabled,
        JsonElement? Config,
        RawSlave[]? Slaves,
        string[]? Dependencies);

    private sealed record RawSlave(string? Id, string[]? Kinds, bool? RequireAll);
}

/// <summary>Maps a module's declared <c>type</c> to the code that implements it.</summary>
public sealed class ModuleFactoryRegistry : IModuleFactoryRegistry
{
    private readonly Dictionary<string, Func<ModuleManifest, Result<IKernelModule>>> _factories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _owned = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Declares a type whose instances somebody else builds — a manager, which creates, starts and stops them. The
    /// file is then that instance's configuration, not a module the kernel should build. This is what replaces "the
    /// host owns this type" being a predicate written next to the kernel: who builds a type is a fact about the type.
    /// </summary>
    public Result RegisterOwned(string typeId)
    {
        if (string.IsNullOrWhiteSpace(typeId))
            return Result.Failure(new(ErrorCode.Invalid, "A module type needs a name."));
        lock (_gate) _owned.Add(typeId);
        return Result.Success();
    }

    /// <summary>Whether this type's instances are built by their manager rather than by the kernel.</summary>
    public bool IsOwned(string? typeId) => typeId is not null && _owned.Contains(typeId);

    public Result Register(string typeId, Func<ModuleManifest, Result<IKernelModule>> factory)
    {
        if (string.IsNullOrWhiteSpace(typeId))
            return Result.Failure(new(ErrorCode.Invalid, "A module type needs a name."));
        if (factory is null) return Result.Failure(new(ErrorCode.Invalid, "A module type needs a factory."));
        lock (_gate)
        {
            if (!_factories.TryAdd(typeId, factory))
                return Result.Failure(new(ErrorCode.Conflict, $"Module type is already registered: {typeId}"));
        }
        return Result.Success();
    }

    /// <summary>
    /// Creating a module from its manifest. A type that is not registered is an error the caller reports as an
    /// issue — a type is never invented, and there is no default one to fall back to.
    /// </summary>
    public Result<IKernelModule> Create(ModuleManifest manifest)
    {
        Func<ModuleManifest, Result<IKernelModule>>? factory;
        lock (_gate) _factories.TryGetValue(manifest.Type, out factory);
        if (factory is null) return Result<IKernelModule>.Failure(new(ErrorCode.Invalid, $"Unknown module type: {manifest.Type}"));

        Result<IKernelModule> created;
        try { created = factory(manifest); }
        catch (Exception ex)
        {
            return Result<IKernelModule>.Failure(new(ErrorCode.Internal, $"Module factory threw for {manifest.Id}.", ex.Message));
        }
        if (!created.Ok || created.Value is null)
            return Result<IKernelModule>.Failure(created.Error ?? new(ErrorCode.Internal, $"Factory returned nothing for {manifest.Id}."));

        // The manifest's id is the identity: a factory that built something under another name would put the
        // module into the graph under a name nobody can reach it by.
        if (created.Value.Id != manifest.Id)
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                $"Factory for {manifest.Type} built {created.Value.Id} instead of {manifest.Id}."));
        return created;
    }

    public bool IsRegistered(string typeId)
    {
        lock (_gate) return _factories.ContainsKey(typeId);
    }

    public IReadOnlyList<string> List()
    {
        lock (_gate) return _factories.Keys.OrderBy(type => type, StringComparer.Ordinal).ToArray();
    }
}
