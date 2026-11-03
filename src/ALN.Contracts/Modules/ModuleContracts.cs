namespace ALN.Contracts;

/// <summary>
/// What a module class says about itself. Every module declares this — the host does not fill it in, because a
/// registration form the caller fills is a second place the truth can be wrong.
/// </summary>
/// <param name="TypeId">The type this class implements, e.g. <c>AdapterManager</c> or <c>Adapter.QQ</c>.</param>
/// <param name="Category">The namespace it lives in: <c>adapter</c>, <c>channel</c>, <c>session</c>, <c>model</c>.</param>
/// <param name="Singleton">
/// True when this class is brought up exactly once. A singleton's id <b>is</b> its type id, and that id is part
/// of the contract — it is the one id anything is allowed to hard-code. Everything else gets a suffix and is
/// found by type, never by name.
/// </param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ModuleAttribute(string typeId) : Attribute
{
    public string TypeId { get; } = typeId;
    public string Category { get; init; } = "";
    public ModuleRole Role { get; init; } = ModuleRole.Instance;
    public bool Singleton { get; init; }
}

/// <summary>
/// The only root in the system: a manager, an instance and a script instance are all of these. What a module
/// can do comes from the interfaces it also implements (the adapter, channel, session and model contracts);
/// this one only says it exists under a name.
/// </summary>
public interface IModule
{
    ModuleId Id { get; }
}

/// <summary>The category a module belongs to. Data in the catalog, never read out of an id.</summary>
public enum ModuleRole { Manager, Instance, Script }

/// <summary>One row of the catalog: what it is called, what type it is, and which category it lives in.</summary>
public sealed record CatalogEntry(ModuleId Id, string TypeId, string Category, ModuleRole Role);

/// <summary>
/// Every module that exists right now, findable by name or by type.
///
/// It is not a module itself — otherwise "find a module" would require a module, which is a bootstrap circle.
/// The host creates it before anything is loaded and hands it out through host services. Nothing caches a
/// static reference to it: after a package reload such a reference points at the previous generation, and
/// parallel tests overwrite each other's.
/// </summary>
public interface ICatalog
{
    /// <summary>Null when nothing by that name is registered. Prefer <see cref="Require"/> when absence is a bug.</summary>
    IModule? Find(ModuleId id);

    /// <summary>Same as <see cref="Find"/> but absence is an error — which it usually is.</summary>
    Result<IModule> Require(ModuleId id);

    /// <summary>
    /// By type or category, because an instance id cannot be guessed: without this, "having the id is having the
    /// reference" would degrade into "no way to ever get the id".
    /// </summary>
    IReadOnlyList<CatalogEntry> List(string? category = null, string? typeId = null);

    /// <summary>
    /// Registers a module by what its class declared. Two modules may not share a name, compared
    /// case-insensitively, and a singleton must be named after its own type.
    /// </summary>
    Result Attach(IModule module);

    Result Detach(ModuleId id);
}

