namespace ALN.Contracts;

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

    /// <summary>Registers a module. Two modules may not share a name, compared case-insensitively.</summary>
    Result Attach(IModule module, string typeId, string category, ModuleRole role);

    Result Detach(ModuleId id);
}

/// <summary>
/// The names a person agreed on in advance. These are the only ids anything is allowed to hard-code: everything
/// else is discovered through the catalog. The constant's name carries the meaning; the string is just a name.
/// </summary>
public static class ModuleNames
{
    public const string AdapterManager = "AdapterManager";
    public const string ChannelManager = "ChannelManager";
    public const string SessionManager = "SessionManager";
    public const string ModelManager = "ModelManager";
    public const string GraphManager = "GraphManager";
    public const string Clock = "Clock";
    public const string Store = "Store";
}
