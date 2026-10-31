using System.Collections.Concurrent;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>Short-lived, package-keyed state transfer used only during a replacement transaction.</summary>
public sealed class ModuleReloadStateStore : IModuleReloadStateStore
{
    private readonly ConcurrentDictionary<string, string> _states = new(StringComparer.Ordinal);

    public Result Store(string packageId, string stateJson)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(stateJson))
            return Result.Failure(new(ErrorCode.Invalid, "Reload state requires a package id and payload."));
        _states[packageId] = stateJson;
        return Result.Success();
    }

    public bool TryGet(string packageId, out string stateJson) => _states.TryGetValue(packageId, out stateJson!);
    public void Clear(string packageId) => _states.TryRemove(packageId, out _);
}
