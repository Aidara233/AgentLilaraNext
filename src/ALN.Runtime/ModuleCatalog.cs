using System.Reflection;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The address book. Managers and infrastructure are registered here; the instances a manager holds are asked for
/// through <see cref="IModuleIndex"/>, never copied. Everything else finds things here.
///
/// One lock around a dictionary is enough because nothing in this class calls out while holding it — the rows
/// are data, and a caller that wants to talk to a module does so after the lookup returns. A catalog that
/// dispatched inside its own lock would turn every module's event handling into a deadlock risk.
/// </summary>
public sealed class ModuleCatalog : ICatalog
{
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IModuleIndex> _indexes = [];
    private readonly object _gate = new();

    private sealed record Entry(IModule Module, string TypeId, string Category, ModuleRole Role);

    public IModule? Find(ModuleId id)
    {
        if (id.IsEmpty) return null;

        Entry? own;
        IModuleIndex[] indexes;
        lock (_gate)
        {
            own = _byKey.TryGetValue(id.Value, out var entry) ? entry : null;
            indexes = [.. _indexes];
        }
        if (own is not null) return own.Module;

        // Asked outside the lock: an owner that answered by calling back in here would otherwise deadlock.
        IModule? found = null;
        foreach (var index in indexes)
        {
            var candidate = index.Locate(id);
            if (candidate is null) continue;
            // Two owners answering for one name is a bug in whoever created it, not something to resolve by
            // picking the first answer: a lookup that silently chooses is how the wrong object gets called.
            if (found is not null)
                throw new InvalidOperationException(
                    $"Two owners hold {id}: {found.GetType().Name} and {candidate.GetType().Name}.");
            found = candidate;
        }
        return found;
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
        Entry[] own;
        IModuleIndex[] indexes;
        lock (_gate)
        {
            own = [.. _byKey.Values];
            indexes = [.. _indexes];
        }

        IEnumerable<CatalogEntry> rows = own.Select(entry => new CatalogEntry(entry.Module.Id, entry.TypeId, entry.Category, entry.Role));
        foreach (var index in indexes) rows = rows.Concat(index.Entries());

        return rows
            .Where(row => category is null || string.Equals(row.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(row => typeId is null || string.Equals(row.TypeId, typeId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public Result Attach(IModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        // What it is called, what type it is and which category it belongs to are the class's own business. The
        // host used to pass them in, which made the registration call a second place the truth could be wrong.
        var type = module.GetType();
        var declared = type.GetCustomAttribute<ModuleAttribute>(inherit: false);
        if (declared is null)
            return Result.Failure(new(ErrorCode.Invalid,
                $"{type.Name} does not declare [Module], so the catalog knows nothing about it."));
        if (string.IsNullOrWhiteSpace(declared.TypeId) || string.IsNullOrWhiteSpace(declared.Category))
            return Result.Failure(new(ErrorCode.Invalid, $"{type.Name} declares [Module] without a type or category."));
        // A singleton's id is part of the contract, so it cannot be an arbitrary name: callers look it up by the
        // type they agreed on. An instance id is a name and nothing else, which is why only singletons are checked.
        if (declared.Singleton && !string.Equals(module.Id.Value, declared.TypeId, StringComparison.Ordinal))
            return Result.Failure(new(ErrorCode.Invalid,
                $"A singleton must be named after its own type: expected {declared.TypeId}, got {module.Id.Value}."));

        if (module.Id.IsEmpty || !ModuleId.IsLegal(module.Id.Value))
            return Result.Failure(new(ErrorCode.Invalid, $"Not a legal module id: {module.Id.Value ?? "(null)"}"));

        lock (_gate)
        {
            // Case-insensitive because the file system is: two ids differing only in case would be one directory,
            // and the second one would quietly take over the first one's row.
            if (_byKey.TryGetValue(module.Id.Value, out var existing))
                return Result.Failure(new(ErrorCode.Conflict,
                    $"Another module is already registered as {existing.Module.Id}.",
                    existing.TypeId));
            _byKey.Add(module.Id.Value, new Entry(module, declared.TypeId, declared.Category, declared.Role));
            return Result.Success();
        }
    }

    /// <summary>
    /// Registers an owner of instances. Only the owner is registered — its instances are asked for, never copied,
    /// so nothing here can go stale when something is destroyed.
    /// </summary>
    public Result Observe(IModuleIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (string.IsNullOrWhiteSpace(index.Category))
            return Result.Failure(new(ErrorCode.Invalid, $"{index.GetType().Name} observes instances without saying in which category."));
        lock (_gate)
        {
            if (_indexes.Contains(index)) return Result.Success();
            // Two owners for one namespace would make "which of them has this name" a coin toss.
            var clash = _indexes.FirstOrDefault(existing =>
                string.Equals(existing.Category, index.Category, StringComparison.OrdinalIgnoreCase));
            if (clash is not null)
                return Result.Failure(new(ErrorCode.Conflict,
                    $"Category {index.Category} already has an owner: {clash.GetType().Name}."));
            _indexes.Add(index);
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

    /// <summary>Registered modules only — owners are not counted here, their instances are not rows of this map.</summary>
    public int Count
    {
        get { lock (_gate) return _byKey.Count; }
    }
}
