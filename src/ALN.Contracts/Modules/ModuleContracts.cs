using System.Text.Json;

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
    /// <summary>
    /// Shadows <see cref="Attribute.TypeId"/> on purpose: that one answers "which attribute class is this",
    /// this one answers "which module type does the class implement", and the two are unrelated.
    /// </summary>
    public new string TypeId { get; } = typeId;
    public string Category { get; init; } = "";
    public ModuleRole Role { get; init; } = ModuleRole.Instance;
    public bool Singleton { get; init; }
}

/// <summary>
/// The names of the modules the host brings up exactly once. These are the only ids anything may hard-code: they
/// are the contract — changing one is an API change. An instance id is a name and nothing else, so it is never
/// listed here and never relied on.
///
/// They live in contracts rather than on the manager classes because every module that needs a manager has to
/// compile against a project that does not contain it (the graph does not reference the adapter implementation).
/// </summary>
public static class ModuleNames
{
    public const string AdapterManager = "AdapterManager";
    public const string ChannelManager = "ChannelManager";
    public const string SessionManager = "SessionManager";
    public const string ModelManager = "ModelManager";
    public const string GraphManager = "GraphManager";
    public const string Clock = "Clock";
    public const string Memory = "Memory";
}

/// <summary>
/// A module that was configured with an address. It is the same value as <see cref="IModule.Id"/>, in the spelling
/// specs and transports use — one value with two spellings, not two identities.
///
/// It exists so that anything holding only a name (the catalog, the mount table) can get the address back
/// <b>without taking the name apart</b>. Reading the address out of the name is the one thing the module contract
/// forbids, and it forbids it for a reason: the day the name changes, everything that read it changes meaning.
/// </summary>
public interface IAddressed
{
    Address Address { get; }
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
/// A manager that can answer for the instances it holds: "is this name mine, and what is it".
///
/// It exists so that instances are never copied into the catalog. The manager's own dictionary is the truth
/// (it just created the thing, it will destroy it), and the catalog asks. Copying rows would mean every destroy
/// path — normal, forced, by type, package unload — has to remember to delete the copy, and any path that
/// forgets leaves a name that resolves to something no longer there.
/// </summary>
public interface IModuleIndex
{
    /// <summary>The namespace this owner holds instances in: <c>adapter</c>, <c>channel</c>, <c>model</c>, …</summary>
    string Category { get; }

    /// <summary>The instance by that name and its row, or null when this owner holds nothing by it. Never invents one.</summary>
    LocatedModule? Locate(ModuleId id);

    /// <summary>Every instance held right now. A destroyed one is absent because it is absent from the owner.</summary>
    IReadOnlyList<CatalogEntry> Entries();
}

/// <summary>A module together with the row describing it, so that asking "what is this" and "what is it called"
/// costs one lookup instead of two.</summary>
public sealed record LocatedModule(IModule Module, CatalogEntry Row);

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
    /// The row for that name — what it is, which category, which role — without the object. Null when nothing
    /// answers. A caller that needs the type of an instance asks this; reading it out of the name is not an option.
    /// </summary>
    CatalogEntry? Describe(ModuleId id);

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

    /// <summary>
    /// Registers an owner of instances — a manager — so that the instances it holds can be found here too.
    ///
    /// The catalog does not copy their rows. A copy is a second place the truth can be wrong, and it goes wrong
    /// in the worst way available: a name that still resolves, to something that was destroyed three code paths
    /// ago. Asking the owner costs one dictionary lookup and cannot be stale.
    /// </summary>
    Result Observe(IModuleIndex index);

    Result Detach(ModuleId id);
}


/// <summary>
/// An instance that can say what state it is in right now, in its own words.
///
/// It exists so that "what is this Channel aimed at" has one answer, held by the thing that owns it — rather than a
/// host that reaches into every kind of instance to build a summary it will have to keep in step with all of them. An
/// instance that says nothing is not an error: a tool shows what it can see and says the rest was not offered.
///
/// The shape is a JSON object because the fields differ per kind and nothing here should have to learn them: the
/// reader is a person or a model, not a compiler.
/// </summary>
public interface IInstanceSnapshot
{
    JsonElement Snapshot();
}
