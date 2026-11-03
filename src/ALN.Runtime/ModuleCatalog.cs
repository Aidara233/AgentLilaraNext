using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The address book. Managers report themselves and their instances here; everything else finds things here.
///
/// One lock around a dictionary is enough because nothing in this class calls out while holding it — the rows
/// are data, and a caller that wants to talk to a module does so after the lookup returns. A catalog that
/// dispatched inside its own lock would turn every module's event handling into a deadlock risk.
/// </summary>
public sealed class ModuleCatalog : ICatalog
{
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private sealed record Entry(IModule Module, string TypeId, string Category, ModuleRole Role);

    public IModule? Find(ModuleId id)
    {
        if (id.IsEmpty) return null;
        lock (_gate) return _byKey.TryGetValue(id.Value, out var entry) ? entry.Module : null;
    }

    public Result<IModule> Require(ModuleId id)
    {
        var found = Find(id);
        return found is not null
            ? Result<IModule>.Success(found)
            : Result<IModule>.Failure(new(ErrorCode.Invalid, $"No module is registered as {id}."));
    }

    public IReadOnlyList<CatalogEntry> List(string? category = null, string? typeId = null)
    {
        lock (_gate)
            return _byKey.Values
                .Where(entry => category is null || string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
                .Where(entry => typeId is null || string.Equals(entry.TypeId, typeId, StringComparison.OrdinalIgnoreCase))
                .Select(entry => new CatalogEntry(entry.Module.Id, entry.TypeId, entry.Category, entry.Role))
                .ToArray();
    }

    public Result Attach(IModule module, string typeId, string category, ModuleRole role)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.Id.IsEmpty || !ModuleId.IsLegal(module.Id.Value))
            return Result.Failure(new(ErrorCode.Invalid, $"Not a legal module id: {module.Id.Value ?? "(null)"}"));
        if (string.IsNullOrWhiteSpace(typeId) || string.IsNullOrWhiteSpace(category))
            return Result.Failure(new(ErrorCode.Invalid, "A module needs both a type and a category."));

        lock (_gate)
        {
            // Case-insensitive because the file system is: two ids differing only in case would be one directory,
            // and the second one would quietly take over the first one's row.
            if (_byKey.TryGetValue(module.Id.Value, out var existing))
                return Result.Failure(new(ErrorCode.Conflict,
                    $"Another module is already registered as {existing.Module.Id}.",
                    existing.TypeId));
            _byKey.Add(module.Id.Value, new Entry(module, typeId, category, role));
            return Result.Success();
        }
    }

    public Result Detach(ModuleId id)
    {
        lock (_gate)
            return _byKey.Remove(id.Value)
                ? Result.Success()
                : Result.Failure(new(ErrorCode.Invalid, $"No module is registered as {id}."));
    }

    public int Count
    {
        get { lock (_gate) return _byKey.Count; }
    }
}
