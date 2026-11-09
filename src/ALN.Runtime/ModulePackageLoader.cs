using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Loads module packages from directories containing plugin.json and an
/// entry assembly. Contract assemblies already loaded by the host are shared;
/// package-private dependencies are resolved beside the entry assembly.
/// </summary>
public sealed class ModulePackageLoader : IAsyncDisposable, IModuleCatalog, IModulePackageLoader, IModulePackageMaintenance
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private readonly ModuleUnloadCoordinator _unloads;
    private readonly IModuleServiceRegistry _services;
    private readonly IModuleTypeRegistry _types;
    private readonly IModuleReloadStateStore? _reloadState;
    private readonly IReadOnlyList<IModulePackageResourceRegistry> _resourceRegistries;
    private readonly IAdapterManager? _adapters;
    private readonly IForceInstanceBindings? _forceBindings;
    /// <summary>Where dropped-in packages live. Optional: a loader wired without it can still load directories it is handed.</summary>
    private readonly string? _pluginsRoot;
    /// <summary>Who owns which verb, so a package that registered actions can give them back. Optional: without it a
    /// replaced package would leave its verbs pointing into an assembly nothing can release.</summary>
    private readonly IActionTableOwner? _actions;
    private readonly Dictionary<string, LoadedPackage> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _replaceGate = new(1, 1);

    public ModulePackageLoader(ModuleUnloadCoordinator unloads, IModuleServiceRegistry services, IModuleTypeRegistry types,
        IModuleReloadStateStore? reloadState = null,
        IEnumerable<IModulePackageResourceRegistry>? resourceRegistries = null,
        IAdapterManager? adapters = null,
        IForceInstanceBindings? forceBindings = null,
        string? pluginsRoot = null,
        IActionTableOwner? actionTable = null)
    {
        _unloads = unloads;
        _services = services;
        _types = types;
        _reloadState = reloadState;
        _resourceRegistries = resourceRegistries?.ToArray() ?? [];
        _adapters = adapters;
        _forceBindings = forceBindings;
        _pluginsRoot = string.IsNullOrWhiteSpace(pluginsRoot) ? null : Path.GetFullPath(pluginsRoot);
        _actions = actionTable;
    }

    public IReadOnlyList<ModulePackageManifest> Loaded
    {
        get
        {
            _gate.Wait();
            try { return _loaded.Values.Select(p => p.Manifest).ToArray(); }
            finally { _gate.Release(); }
        }
    }

    public string? PluginsRoot => _pluginsRoot;

    /// <inheritdoc />
    public IReadOnlyList<LoadedPackageInfo> LoadedPackages
    {
        get
        {
            _gate.Wait();
            try
            {
                return _loaded.Values.Select(package => new LoadedPackageInfo(package.Manifest.Id, package.Manifest.Version,
                        package.Manifest.PackageKind, package.Manifest.ReloadPolicy, package.Manifest.Provides,
                        package.Manifest.Dependencies, package.PackageDirectory))
                    .OrderBy(info => info.Id, StringComparer.Ordinal)
                    .ToArray();
            }
            finally { _gate.Release(); }
        }
    }

    public ValueTask<Result<ModulePackageManifest>> LoadAsync(
        string packageDirectory,
        ModulePackageKind packageKind,
        CancellationToken cancellationToken) => LoadInternalAsync(packageDirectory, packageKind, allowReserved: false, cancellationToken);

    private async ValueTask<Result<ModulePackageManifest>> LoadInternalAsync(
        string packageDirectory,
        ModulePackageKind packageKind,
        bool allowReserved,
        CancellationToken cancellationToken)
    {
        if (packageKind is not (ModulePackageKind.Bundled or ModulePackageKind.Plugin))
            return Result<ModulePackageManifest>.Failure(new(ErrorCode.Denied, "Only bundled extensions and plugins can be loaded as packages."));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var parsed = ReadManifest(packageDirectory);
            if (!parsed.Ok || parsed.Value is null) return Result<ModulePackageManifest>.Failure(parsed.Error!);
            var file = parsed.Value;
            if (!allowReserved && _reserved.Contains(file.Id))
                return Result<ModulePackageManifest>.Failure(new(ErrorCode.Unavailable, $"Module package is being replaced: {file.Id}"));
            if (_loaded.ContainsKey(file.Id))
                return Result<ModulePackageManifest>.Failure(new(ErrorCode.Conflict, $"Module package already loaded: {file.Id}"));
            if (packageKind == ModulePackageKind.Bundled && file.ReloadPolicy != ModuleReloadPolicy.None ||
                packageKind == ModulePackageKind.Plugin && file.ReloadPolicy == ModuleReloadPolicy.None)
                return Result<ModulePackageManifest>.Failure(new(ErrorCode.Invalid, "Package reload policy does not match its trusted source."));

            var dependencies = _unloads.ListPackages().ToDictionary(p => p.Id, StringComparer.Ordinal);
            foreach (var dependency in file.Dependencies.Where(d => !d.Optional))
            {
                if (!dependencies.TryGetValue(dependency.Id, out var found))
                    return Result<ModulePackageManifest>.Failure(new(ErrorCode.Unavailable, $"Required package is not loaded: {dependency.Id}"));
                if (dependency.VersionRange is { Length: > 0 } range && range != "*" && !MatchesVersion(found.Version, range))
                    return Result<ModulePackageManifest>.Failure(new(ErrorCode.Conflict,
                        $"Package {dependency.Id} version {found.Version} does not satisfy {dependency.VersionRange}."));
            }

            var manifest = new ModulePackageManifest(file.Id, file.Version, packageKind,
                file.Provides, file.Dependencies, file.ReloadPolicy);
            var assemblyPath = ResolvePackagePath(packageDirectory, file.EntryAssembly);
            if (assemblyPath is null || !File.Exists(assemblyPath))
                return Result<ModulePackageManifest>.Failure(new(ErrorCode.Invalid, "Entry assembly is missing or escapes the package directory."));

            var loadContext = new ModuleAssemblyLoadContext(assemblyPath);
            IModulePlugin? plugin = null;
            var packageRegistered = false;
            var typeProvisionsRegistered = false;
            var participantRegistered = false;
            var pluginStarted = false;
            var loadSucceeded = false;
            try
            {
                var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
                var entryType = assembly.GetType(file.EntryType, throwOnError: false, ignoreCase: false);
                if (entryType is null || entryType.IsAbstract || !typeof(IModulePlugin).IsAssignableFrom(entryType))
                    return Result<ModulePackageManifest>.Failure(new(ErrorCode.Invalid, "Entry type must be a concrete IModulePlugin."));
                if (Activator.CreateInstance(entryType) is not IModulePlugin instance)
                    return Result<ModulePackageManifest>.Failure(new(ErrorCode.Invalid, "Plugin entry type requires a public parameterless constructor."));
                plugin = instance;
                if (!MetadataMatches(plugin.Manifest, manifest))
                    return Result<ModulePackageManifest>.Failure(new(ErrorCode.Invalid, "Plugin entry manifest does not match plugin.json."));

                var registered = _unloads.RegisterPackage(manifest);
                if (!registered.Ok) return Result<ModulePackageManifest>.Failure(registered.Error!);
                packageRegistered = true;
                var registeredTypes = _types.RegisterPackageTypes(file.Id, file.Provides);
                if (!registeredTypes.Ok) return Result<ModulePackageManifest>.Failure(registeredTypes.Error!);
                typeProvisionsRegistered = true;
                var participant = _unloads.RegisterParticipant(file.Id, plugin);
                if (!participant.Ok) return Result<ModulePackageManifest>.Failure(participant.Error!);
                participantRegistered = true;

                var context = new PackageContext(file.Id, _services);
                pluginStarted = true;
                // What the table held before this package spoke. Anything new after StartAsync is its verb, which is
                // enough to take them back later: action ids are unique across the process, so a verb that appeared
                // while this package was starting appeared because of it.
                var actionsBefore = _actions?.ActionIds;
                var started = await plugin.StartAsync(context, cancellationToken).ConfigureAwait(false);
                AdoptActions(file.Id, actionsBefore);
                if (!started.Ok) return Result<ModulePackageManifest>.Failure(started.Error!);
                _loaded.Add(file.Id, new LoadedPackage(manifest, Path.GetFullPath(packageDirectory), plugin, loadContext, context));
                loadSucceeded = true;
                plugin = null;
                return Result<ModulePackageManifest>.Success(manifest);
            }
            finally
            {
                if (!loadSucceeded)
                {
                    if (pluginStarted && plugin is not null)
                    {
                        try { await plugin.DisconnectAsync(true, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                        catch { }
                    }
                    _services.RemovePackageServices(file.Id);
                    _actions?.RemovePackage(file.Id);
                    if (participantRegistered && plugin is not null) _unloads.UnregisterParticipant(plugin.ParticipantId);
                    if (typeProvisionsRegistered) _types.RemovePackageTypes(file.Id);
                    if (packageRegistered) _unloads.UnregisterPackage(file.Id);
                    loadContext.Unload();
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<ModulePackageManifest>.Failure(new(ErrorCode.Internal, "Module package loading failed.", ex.Message));
        }
        finally { _gate.Release(); }
    }

    public ValueTask<Result<ModuleUnloadReport>> UnloadAsync(
        string packageId, TimeSpan timeout, bool force, CancellationToken cancellationToken) =>
        UnloadInternalAsync(packageId, timeout, force, allowReserved: false, cancellationToken);

    private async ValueTask<Result<ModuleUnloadReport>> UnloadInternalAsync(
        string packageId, TimeSpan timeout, bool force, bool allowReserved, CancellationToken cancellationToken, bool replacing = false)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!allowReserved && _reserved.Contains(packageId))
                return Result<ModuleUnloadReport>.Failure(new(ErrorCode.Unavailable, $"Module package is being replaced: {packageId}"));
            if (!_loaded.ContainsKey(packageId))
                return Result<ModuleUnloadReport>.Failure(new(ErrorCode.Invalid, $"Module package is not loaded: {packageId}"));
            var result = await _unloads.UnloadAsync(packageId, timeout, force, cancellationToken, replacing).ConfigureAwait(false);
            if (!result.Ok || result.Value is not { Completed: true }) return result;

            var unloadReport = result.Value;
            var cleanupErrors = new List<string>();
            foreach (var affectedPackage in unloadReport.AffectedPackages)
            {
                if (!_loaded.Remove(affectedPackage, out var affectedHandle)) continue;
                if (unloadReport.Forced)
                    cleanupErrors.AddRange(await ForceCleanupAsync(affectedHandle.Manifest, timeout).ConfigureAwait(false));
                foreach (var registry in _resourceRegistries)
                {
                    var removedResources = registry.RemovePackage(affectedPackage);
                    if (!removedResources.Ok) cleanupErrors.Add($"{affectedPackage} resource cleanup: {removedResources.Error?.Message}");
                }
                var typesRemoved = _types.RemovePackageTypes(affectedPackage);
                if (!typesRemoved.Ok)
                {
                    _loaded.Add(affectedPackage, affectedHandle);
                    return Result<ModuleUnloadReport>.Failure(typesRemoved.Error!);
                }
                _services.RemovePackageServices(affectedPackage);
                _actions?.RemovePackage(affectedPackage);
                affectedHandle.Release();
            }
            if (!replacing)
                foreach (var affectedPackage in unloadReport.AffectedPackages) _reloadState?.Clear(affectedPackage);
            return Result<ModuleUnloadReport>.Success(unloadReport with
            { Errors = unloadReport.Errors.Concat(cleanupErrors).ToArray() });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<ModuleUnloadReport>.Failure(new(ErrorCode.Internal, "Module package unload failed.", ex.Message));
        }
        finally { _gate.Release(); }
    }

    private async Task<List<string>> ForceCleanupAsync(ModulePackageManifest manifest, TimeSpan timeout)
    {
        var errors = new List<string>();
        if (_adapters is not null && _forceBindings is not null)
        {
            foreach (var provision in manifest.Provides.Where(p => IsAdapterType(p.TypeId)))
            {
                var addresses = _adapters.List().Where(a => a.TypeId == provision.TypeId).Select(a => a.Address).ToArray();
                foreach (var address in addresses)
                {
                    try
                    {
                        var detached = await _forceBindings.ForceDetachAsync(address, CancellationToken.None).AsTask()
                            .WaitAsync(timeout).ConfigureAwait(false);
                        if (!detached.Ok) errors.Add($"{address}: route detach failed: {detached.Error?.Message}");
                    }
                    catch (Exception ex) { errors.Add($"{address}: route detach failed: {ex.Message}"); }
                }
                try
                {
                    var destroyed = await _adapters.ForceDestroyByTypeAsync(provision.TypeId, CancellationToken.None).AsTask()
                        .WaitAsync(timeout).ConfigureAwait(false);
                    if (!destroyed.Ok) errors.Add($"{provision.TypeId}: forced instance removal failed: {destroyed.Error?.Message}");
                }
                catch (Exception ex) { errors.Add($"{provision.TypeId}: forced instance removal failed: {ex.Message}"); }
            }
        }
        else if (manifest.Provides.Any(p => IsAdapterType(p.TypeId)))
        {
            errors.Add("Adapter force cleanup is unavailable; an adapter instance may still reference the package.");
        }
        return errors;
    }

    /// <summary>Whether a provisioned type answers the adapter contract. Asked of the type tree, never of the spelling
    /// of the type id: a package may call its adapter type anything it likes (C-04, C-06).</summary>
    private bool IsAdapterType(string typeId) => _types.IsAssignableTo(typeId, "adapter");

    /// <summary>
    /// Replaces a running package with the one in another directory, and rolls back to the original set if anything
    /// about the new one fails. The candidate must be a separate, immutable directory: a package whose files are
    /// overwritten in place cannot be unloaded and reloaded on Windows, where a mapped assembly is a locked file.
    /// </summary>
    public async ValueTask<Result<ModuleReloadReport>> ReplaceFromDirectoryAsync(
        string packageId,
        string candidateDirectory,
        TimeSpan timeout,
        bool force,
        CancellationToken cancellationToken)
    {
        await _replaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<LoadedSnapshot> originals = [];
        string previousVersion = "";
        // What was loaded before this attempt. A replacement that fails half way must not leave a package behind that
        // this attempt loaded: the report would say "failed" while the new code answered requests, which is worse
        // than either outcome on its own.
        HashSet<string> loadedBefore;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { loadedBefore = _loaded.Keys.ToHashSet(StringComparer.Ordinal); }
        finally { _gate.Release(); }
        var reservedIds = new List<string>();
        try
        {
            var candidateResult = ReadManifest(candidateDirectory);
            if (!candidateResult.Ok || candidateResult.Value is null)
                return Result<ModuleReloadReport>.Failure(candidateResult.Error!);
            var candidate = candidateResult.Value;
            if (candidate.Id != packageId || candidate.ReloadPolicy != ModuleReloadPolicy.Replace)
                return Result<ModuleReloadReport>.Failure(new(ErrorCode.Invalid, "Replacement package id or reload policy does not match."));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_loaded.TryGetValue(packageId, out var current))
                    return Result<ModuleReloadReport>.Failure(new(ErrorCode.Invalid, $"Module package is not loaded: {packageId}"));
                if (current.Manifest.PackageKind != ModulePackageKind.Plugin || current.Manifest.ReloadPolicy != ModuleReloadPolicy.Replace)
                    return Result<ModuleReloadReport>.Failure(new(ErrorCode.Denied, "Only replaceable plugins can be hot reloaded."));
                if (StringComparer.Ordinal.Equals(current.PackageDirectory, Path.GetFullPath(candidateDirectory)))
                    return Result<ModuleReloadReport>.Failure(new(ErrorCode.Invalid, "Replacement must come from a separate immutable package directory."));
                if (current.Manifest.Version == candidate.Version)
                    return Result<ModuleReloadReport>.Failure(new(ErrorCode.Conflict, "Replacement version must differ from the active version."));

                var packageManifests = _unloads.ListPackages().ToDictionary(p => p.Id, StringComparer.Ordinal);
                var affectedIds = GetDependentClosure(packageId, packageManifests);
                foreach (var id in affectedIds)
                {
                    if (!_loaded.TryGetValue(id, out var affected) || affected.Manifest.PackageKind != ModulePackageKind.Plugin ||
                        affected.Manifest.ReloadPolicy != ModuleReloadPolicy.Replace)
                        return Result<ModuleReloadReport>.Failure(new(ErrorCode.Conflict, $"Replacement closure contains a package that cannot be reactivated: {id}"));
                    originals.Add(new LoadedSnapshot(affected.Manifest, affected.PackageDirectory));
                }
                previousVersion = current.Manifest.Version;
                reservedIds.AddRange(affectedIds);
                foreach (var id in reservedIds) _reserved.Add(id);
            }
            finally { _gate.Release(); }

            var unloaded = await UnloadInternalAsync(packageId, timeout, force, allowReserved: true, cancellationToken, replacing: true).ConfigureAwait(false);
            if (!unloaded.Ok || unloaded.Value is not { Completed: true })
                return Result<ModuleReloadReport>.Failure(unloaded.Error ?? new(ErrorCode.Unavailable, "Could not drain the active package for replacement."));

            var replacement = await LoadInternalAsync(candidateDirectory, ModulePackageKind.Plugin, allowReserved: true, cancellationToken).ConfigureAwait(false);
            if (replacement.Ok)
            {
                var reactivated = new List<string>();
                foreach (var original in originals.AsEnumerable().Reverse().Where(o => o.Manifest.Id != packageId))
                {
                    var restoredDependent = await LoadInternalAsync(original.PackageDirectory, original.Manifest.PackageKind,
                        allowReserved: true, cancellationToken).ConfigureAwait(false);
                    if (!restoredDependent.Ok)
                    {
                        // The candidate loaded but a dependent did not come back: the new code is running and the old
                        // set is broken. Take the new one out first, then put the originals back, so that a failure
                        // report and the process state agree with each other.
                        var cleaned = await DropPackagesLoadedByThisAttemptAsync(loadedBefore, timeout).ConfigureAwait(false);
                        var rollbackError = await RestoreOriginalsAsync(packageId, originals, timeout).ConfigureAwait(false);
                        return Result<ModuleReloadReport>.Failure(new(ErrorCode.Unavailable,
                            "Replacement could not reactivate a dependent; the prior package set was restored when possible.",
                            $"Dependent {original.Manifest.Id}: {restoredDependent.Error?.Message}; "
                            + $"new packages removed: {cleaned ?? "succeeded"}; rollback: {rollbackError ?? "succeeded"}"));
                    }
                    reactivated.Add(original.Manifest.Id);
                }
                foreach (var original in originals) _reloadState?.Clear(original.Manifest.Id);
                return Result<ModuleReloadReport>.Success(new(packageId, previousVersion,
                    replacement.Value!.Version, reactivated));
            }

            var cleanedUp = await DropPackagesLoadedByThisAttemptAsync(loadedBefore, timeout).ConfigureAwait(false);
            var restoreError = await RestoreOriginalsAsync(packageId, originals, timeout).ConfigureAwait(false);
            return Result<ModuleReloadReport>.Failure(new(ErrorCode.Unavailable,
                "Replacement failed; the prior package set was restored when possible.",
                $"Candidate: {replacement.Error?.Message}; new packages removed: {cleanedUp ?? "succeeded"}; "
                + $"rollback: {restoreError ?? "succeeded"}"));
        }
        catch (OperationCanceledException)
        {
            await DropPackagesLoadedByThisAttemptAsync(loadedBefore, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (originals.Count > 0)
                await RestoreOriginalsAsync(packageId, originals, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (reservedIds.Count > 0)
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { foreach (var id in reservedIds) _reserved.Remove(id); }
                finally { _gate.Release(); }
            }
            _replaceGate.Release();
        }
    }

    /// <summary>
    /// Switches a package to one of the versions sitting in its folder, and writes down which one is in force.
    ///
    /// The order is the whole point: the switch happens first, the pointer second. A failure in between leaves the
    /// pointer on the version that was already servicing requests, so the next start boots something that worked.
    /// Writing it first would make a rolled-back replacement come back as the new version after a restart.
    /// </summary>
    public async ValueTask<Result<ModuleReloadReport>> ReplaceAsync(string packageId, string version, TimeSpan timeout,
        bool force, CancellationToken cancellationToken)
    {
        if (_pluginsRoot is null)
            return Result<ModuleReloadReport>.Failure(new(ErrorCode.Unavailable,
                "No plugin folder is configured, so there are no versions to switch between."));
        if (!IsPackageName(packageId))
            return Result<ModuleReloadReport>.Failure(new(ErrorCode.Invalid, $"Not a package id: \"{packageId}\"."));
        if (!IsPackageName(version))
            return Result<ModuleReloadReport>.Failure(new(ErrorCode.Invalid, $"Not a version: \"{version}\"."));

        var packageRoot = Path.Combine(_pluginsRoot, packageId);
        var resolved = ModulePackageLayout.Resolve(packageRoot);
        if (!resolved.Ok || resolved.Value is null)
            return Result<ModuleReloadReport>.Failure(resolved.Error!);
        if (resolved.Value.Legacy)
            return Result<ModuleReloadReport>.Failure(new(ErrorCode.Invalid,
                $"Package {packageId} is a single flat folder, not a set of versions: {ModulePackageLayout.ManifestFileName} "
                + $"sits in {packageId}/ itself. Move it into a version directory to be able to switch."));

        var candidate = Path.Combine(packageRoot, version);
        if (!File.Exists(Path.Combine(candidate, ModulePackageLayout.ManifestFileName)))
            return Result<ModuleReloadReport>.Failure(new(ErrorCode.Invalid,
                $"Package {packageId} has no version \"{version}\". Available: {DescribeVersions(resolved.Value.Versions)}."));

        var replaced = await ReplaceFromDirectoryAsync(packageId, candidate, timeout, force, cancellationToken).ConfigureAwait(false);
        if (!replaced.Ok || replaced.Value is null) return replaced;

        // The switch worked. What is left is the pointer, and a pointer that could not be written is worth saying out
        // loud: reporting a clean success the next restart contradicts is how somebody spends an afternoon on it.
        var written = ModulePackageLayout.WriteActive(packageRoot, version);
        return Result<ModuleReloadReport>.Success(written.Ok
            ? replaced.Value
            : replaced.Value with
            {
                Note = $"Running {version}, but {ModulePackageLayout.ActiveFileName} could not be written "
                    + $"({written.Error?.Message}). The next start will run the previous version."
            });
    }

    /// <summary>
    /// Takes note of the verbs the package just put in the table, so unloading it can take them back.
    ///
    /// Called even when <c>StartAsync</c> failed: a package can register a verb and then fail, and the rollback has to
    /// undo that too — a leftover verb is a handler captured over an assembly load context that can then never be
    /// released, which is the whole reason a hot replacement stays pinned.
    /// </summary>
    private void AdoptActions(string packageId, IReadOnlySet<string>? before)
    {
        if (_actions is null || before is null) return;
        var added = _actions.ActionIds.Where(id => !before.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        if (added.Length > 0) _actions.TrackPackage(packageId, added);
    }

    /// <summary>A package id and a version are both one directory name. Anything with a separator is a path, not a name.</summary>
    private static bool IsPackageName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value != "." && value != ".." &&
        value.IndexOfAny(['/', '\\', ':']) < 0 && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string DescribeVersions(IReadOnlyList<string> versions) =>
        versions.Count == 0 ? "none" : string.Join(", ", versions);

    /// <summary>
    /// Unloads every package this replacement attempt added and had not been there before — the candidate, and any
    /// dependent that came back with it. Returns null when there was nothing to do or everything went, and a sentence
    /// when something could not be taken out (which the caller reports, because a half-cleaned rollback is not silent).
    /// </summary>
    private async Task<string?> DropPackagesLoadedByThisAttemptAsync(HashSet<string> loadedBefore, TimeSpan timeout)
    {
        string[] added;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { added = _loaded.Keys.Where(id => !loadedBefore.Contains(id)).ToArray(); }
        finally { _gate.Release(); }
        if (added.Length == 0) return null;

        var errors = new List<string>();
        foreach (var packageId in added)
        {
            // Reserved is on for exactly the ids this attempt is working on; going through the reserved check would
            // refuse to clean up the very package it just loaded.
            var unloaded = await UnloadInternalAsync(packageId, timeout, force: true, allowReserved: true,
                CancellationToken.None, replacing: true).ConfigureAwait(false);
            if (!unloaded.Ok || unloaded.Value is not { Completed: true })
                errors.Add($"{packageId}: {unloaded.Error?.Message ?? "unload did not complete"}");
        }
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private async Task<string?> RestoreOriginalsAsync(string packageId, IReadOnlyList<LoadedSnapshot> originals, TimeSpan timeout)
    {
        var errors = new List<string>();
        foreach (var snapshot in originals)
        {
            bool isLoaded;
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { isLoaded = _loaded.ContainsKey(snapshot.Manifest.Id); }
            finally { _gate.Release(); }
            if (!isLoaded) continue;
            var unload = await UnloadInternalAsync(snapshot.Manifest.Id, timeout, force: true, allowReserved: true, CancellationToken.None, replacing: true).ConfigureAwait(false);
            if (!unload.Ok || unload.Value is not { Completed: true }) errors.Add($"unload {snapshot.Manifest.Id}: {unload.Error?.Message ?? "incomplete"}");
        }
        foreach (var snapshot in originals.Reverse())
        {
            var restored = await LoadInternalAsync(snapshot.PackageDirectory, snapshot.Manifest.PackageKind,
                allowReserved: true, CancellationToken.None).ConfigureAwait(false);
            if (!restored.Ok) errors.Add($"load {snapshot.Manifest.Id}: {restored.Error?.Message}");
        }
        if (errors.Count == 0)
            foreach (var snapshot in originals) _reloadState?.Clear(snapshot.Manifest.Id);
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private static List<string> GetDependentClosure(string packageId, IReadOnlyDictionary<string, ModulePackageManifest> packages)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (!visited.Add(id)) return;
            foreach (var dependent in packages.Values.Where(p => p.Dependencies.Any(d => !d.Optional && d.Id == id))
                .OrderBy(p => p.Id, StringComparer.Ordinal)) Visit(dependent.Id);
            result.Add(id);
        }
        Visit(packageId);
        return result;
    }

    private sealed record LoadedSnapshot(ModulePackageManifest Manifest, string PackageDirectory);

    public async ValueTask DisposeAsync()
    {
        string[] packageIds;
        _gate.Wait();
        try { packageIds = _loaded.Keys.Reverse().ToArray(); }
        finally { _gate.Release(); }
        foreach (var packageId in packageIds)
        {
            try
            {
                ModulePackageKind kind;
                await _gate.WaitAsync().ConfigureAwait(false);
                try { kind = _loaded.TryGetValue(packageId, out var loaded) ? loaded.Manifest.PackageKind : ModulePackageKind.Plugin; }
                finally { _gate.Release(); }
                if (kind == ModulePackageKind.Plugin)
                {
                    await UnloadAsync(packageId, TimeSpan.FromSeconds(10), force: true, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                LoadedPackage? package;
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_loaded.Remove(packageId, out package)) continue;
                }
                finally { _gate.Release(); }
                if (package.Plugin is { } plugin)
                {
                    try { await plugin.DisconnectAsync(true, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                    catch { }
                    _unloads.UnregisterParticipant(plugin.ParticipantId);
                }
                _services.RemovePackageServices(packageId);
                _actions?.RemovePackage(packageId);
                _types.RemovePackageTypes(packageId);
                _unloads.UnregisterPackage(packageId);
                package.Release();
            }
            catch { }
        }
    }

    private static Result<PackageFileManifest> ReadManifest(string packageDirectory)
    {
        try
        {
            var root = Path.GetFullPath(packageDirectory);
            var manifestPath = Path.Combine(root, "plugin.json");
            if (!File.Exists(manifestPath) && File.Exists(Path.Combine(root, "module.json")))
                return Result<PackageFileManifest>.Failure(new(ErrorCode.Invalid,
                    "Found module.json: the manifest was renamed to plugin.json (same fields). Rename the file."));
            var file = JsonSerializer.Deserialize<PackageFileManifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (file is null || string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Version) ||
                string.IsNullOrWhiteSpace(file.EntryAssembly) || string.IsNullOrWhiteSpace(file.EntryType) ||
                file.Provides is null || file.Dependencies is null || file.Provides.Any(t => t is null || string.IsNullOrWhiteSpace(t.TypeId)) ||
                file.Dependencies.Any(d => d is null || string.IsNullOrWhiteSpace(d.Id)) ||
                file.ReloadPolicy is not (ModuleReloadPolicy.None or ModuleReloadPolicy.Replace))
                return Result<PackageFileManifest>.Failure(new(ErrorCode.Invalid, "Invalid plugin.json manifest."));
            if (file.Contracts is { Length: > 0 } contracts && contracts != ModuleContracts.Version)
                return Result<PackageFileManifest>.Failure(new(ErrorCode.Invalid,
                    $"Package targets module contract {contracts}; this host speaks {ModuleContracts.Version}."));
            if (file.Provides.Select(t => t.TypeId).Distinct(StringComparer.Ordinal).Count() != file.Provides.Length ||
                file.Dependencies.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != file.Dependencies.Length)
                return Result<PackageFileManifest>.Failure(new(ErrorCode.Conflict, "Module manifest has duplicate ids."));
            return Result<PackageFileManifest>.Success(file);
        }
        catch (Exception ex) { return Result<PackageFileManifest>.Failure(new(ErrorCode.Invalid, "Could not read plugin.json.", ex.Message)); }
    }

    private static string? ResolvePackagePath(string packageDirectory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) return null;
        var root = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(packageDirectory, relativePath));
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full : null;
    }

    private static bool MatchesVersion(string version, string range) => range == "*" || StringComparer.Ordinal.Equals(version, range);

    private static bool MetadataMatches(ModulePackageManifest declared, ModulePackageManifest file) =>
        declared is not null && declared.Id == file.Id && declared.Version == file.Version &&
        declared.ReloadPolicy == file.ReloadPolicy &&
        declared.Provides.SequenceEqual(file.Provides) &&
        declared.Dependencies.SequenceEqual(file.Dependencies);

    private sealed class PackageContext(string packageId, IModuleServiceRegistry services) : IModulePluginContext
    {
        public string PackageId { get; } = packageId;
        public IServiceProvider HostServices => services.HostServices;
        public Result RegisterService(string serviceId, object service) => services.RegisterService(PackageId, serviceId, service);
        public Result<T> GetService<T>(string serviceId) where T : class => services.GetService<T>(serviceId);
    }

    private sealed record PackageFileManifest(
        string Id,
        string Version,
        string EntryAssembly,
        string EntryType,
        ModuleTypeProvision[] Provides,
        ModuleDependency[] Dependencies,
        ModuleReloadPolicy ReloadPolicy,
        /// <summary>Which contract surface this package was compiled against; absent means "the current one".</summary>
        string? Contracts = null);

    private sealed class LoadedPackage(ModulePackageManifest manifest, string packageDirectory, IModulePlugin plugin,
        ModuleAssemblyLoadContext loadContext, PackageContext context)
    {
        public ModulePackageManifest Manifest { get; } = manifest;
        public string PackageDirectory { get; } = packageDirectory;
        public IModulePlugin? Plugin => _plugin;
        private IModulePlugin? _plugin = plugin;
        private ModuleAssemblyLoadContext? _loadContext = loadContext;
        private PackageContext? _context = context;
        public void Release()
        {
            _plugin = null;
            _context = null;
            var loadContextToUnload = _loadContext;
            _loadContext = null;
            loadContextToUnload?.Unload();
        }
    }

    private sealed class ModuleAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public ModuleAssemblyLoadContext(string mainAssemblyPath) : base(isCollectible: true) => _resolver = new(mainAssemblyPath);
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name;
            if (name is "ALN.Contracts" or "ALN.Graph.Contracts" or "ALN.Runtime")
            {
                var shared = Default.Assemblies.FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName));
                return shared ?? Default.LoadFromAssemblyName(assemblyName);
            }
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

public sealed class ModuleServiceRegistry(IServiceProvider hostServices) : IModuleServiceRegistry
{
    private readonly Dictionary<string, (string PackageId, object Service)> _services = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public IServiceProvider HostServices { get; } = hostServices;

    public Result RegisterService(string packageId, string serviceId, object service)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(serviceId) || service is null)
            return Result.Failure(new(ErrorCode.Invalid, "Module service registration is incomplete."));
        lock (_gate)
        {
            if (_services.ContainsKey(serviceId))
                return Result.Failure(new(ErrorCode.Conflict, $"Module service already registered: {serviceId}"));
            _services.Add(serviceId, (packageId, service));
            return Result.Success();
        }
    }

    public Result<T> GetService<T>(string serviceId) where T : class
    {
        lock (_gate)
        {
            if (_services.TryGetValue(serviceId, out var registered))
                return registered.Service is T typed
                    ? Result<T>.Success(typed)
                    : Result<T>.Failure(new(ErrorCode.Invalid, $"Module service has an incompatible contract: {serviceId}"));
        }
        return HostServices.GetService(typeof(T)) is T hostService
            ? Result<T>.Success(hostService)
            : Result<T>.Failure(new(ErrorCode.Invalid, $"Module service not found: {serviceId}"));
    }

    public void RemovePackageServices(string packageId)
    {
        lock (_gate)
            foreach (var key in _services.Where(pair => pair.Value.PackageId == packageId).Select(pair => pair.Key).ToArray())
                _services.Remove(key);
    }
}
