using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Loads every module package dropped under a directory.
///
/// Order comes from the manifests, not the directory listing: a pass that loaded something makes another pass
/// worth trying, because a package may depend on one that sorts after it. Nothing here is fatal — a package
/// that cannot load is reported and left out, since a third-party module must never be able to stop the host
/// from starting the modules it already knows.
/// </summary>
public static class DroppedModules
{
    public static async ValueTask<IReadOnlyList<DroppedModule>> LoadAsync(IModulePackageLoader loader, string root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return [];
        var remaining = Directory.GetDirectories(root).OrderBy(directory => directory, StringComparer.Ordinal).ToList();
        var outcomes = new List<DroppedModule>();
        var failures = new Dictionary<string, Error>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var progressed = false;
            foreach (var directory in remaining.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var loaded = await loader.LoadAsync(directory, ModulePackageKind.Plugin, cancellationToken).ConfigureAwait(false);
                if (!loaded.Ok || loaded.Value is null)
                {
                    failures[directory] = loaded.Error ?? new(ErrorCode.Internal, "Package loading failed.");
                    continue;
                }
                failures.Remove(directory);
                remaining.Remove(directory);
                progressed = true;
                outcomes.Add(new(directory, loaded.Value, null));
            }
            if (!progressed) break;
        }
        foreach (var directory in remaining)
            outcomes.Add(new(directory, null, failures[directory]));
        return outcomes;
    }
}
