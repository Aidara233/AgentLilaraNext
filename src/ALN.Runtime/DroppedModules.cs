using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Loads every module package dropped under a directory.
///
/// Order comes from the manifests, not the directory listing: a pass that loaded something makes another pass
/// worth trying, because a package may depend on one that sorts after it. Nothing here is fatal — a package
/// that cannot load is reported and left out, since a third-party module must never be able to stop the host
/// from starting the modules it already knows.
///
/// Which directory actually holds the code is not decided here: <see cref="ModulePackageLayout"/> owns that, and
/// this walks the same answers the script catalogue walks. A package folder may hold one version flat or several
/// side by side with a pointer saying which is in force; either way exactly one directory per package id is
/// loaded, because two of them would be two things claiming one name.
/// </summary>
public static class DroppedModules
{
    public static async ValueTask<IReadOnlyList<DroppedModule>> LoadAsync(IModulePackageLoader loader, string root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return [];

        var outcomes = new List<DroppedModule>();
        var pending = new List<(string Root, string Directory)>();
        foreach (var packageRoot in ModulePackageLayout.PackageRoots(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = ModulePackageLayout.Resolve(packageRoot);
            if (!resolved.Ok || resolved.Value is null || !resolved.Value.Resolved)
            {
                // A folder that says what is wrong with it, rather than one that silently does not appear: the two
                // likely causes (a hand-edited pointer, two versions and nothing saying which) are both fixable by
                // the person who put the folder there, and neither is visible from a log line that only counts.
                outcomes.Add(new DroppedModule(packageRoot, null,
                    resolved.Error ?? new(ErrorCode.Invalid, "The package layout could not be resolved.", packageRoot),
                    packageRoot));
                continue;
            }
            pending.Add((packageRoot, resolved.Value.ActiveDirectory));
        }

        var failures = new Dictionary<string, Error>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var progressed = false;
            foreach (var (packageRoot, directory) in pending.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var loaded = await loader.LoadAsync(directory, ModulePackageKind.Plugin, cancellationToken).ConfigureAwait(false);
                if (!loaded.Ok || loaded.Value is null)
                {
                    failures[directory] = loaded.Error ?? new(ErrorCode.Internal, "Package loading failed.");
                    continue;
                }
                failures.Remove(directory);
                pending.RemoveAll(candidate => candidate.Directory == directory);
                progressed = true;
                outcomes.Add(new DroppedModule(directory, loaded.Value, null, packageRoot));
            }
            if (!progressed) break;
        }
        foreach (var (packageRoot, directory) in pending)
            outcomes.Add(new DroppedModule(directory, null,
                failures[directory] ?? new(ErrorCode.Internal, "Package loading failed."), packageRoot));
        return outcomes;
    }
}
