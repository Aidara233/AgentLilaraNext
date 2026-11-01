using ALN.Contracts;

namespace ALN.Graph.Contracts;

/// <summary>Where a mount applies: one instance, every instance of a concrete type, or every type of a module.</summary>
public enum MountScopeKind { Instance, Type, Module }

/// <summary>
/// A selector, not a resolved list. "channel.chat on every Channel" is one rule, and it keeps being one
/// after a restart and after a new Channel appears — which is the whole reason the graph persists rules
/// instead of the mounts they produce.
/// </summary>
public sealed record MountScope(MountScopeKind Kind, string Selector)
{
    public static MountScope ForInstance(Address target) => new(MountScopeKind.Instance, target.ToString());
    public static MountScope ForType(string typeId) => new(MountScopeKind.Type, typeId);
    public static MountScope ForModule(string module) => new(MountScopeKind.Module, module);
}

/// <summary>
/// Whether a script mounted by one rule shares its state across every instance the rule reaches.
/// A per-target namespace is the default because two channels are two conversations; a shared one is for
/// what really is one thing — a dedupe table, a rate-limit window, a mode switch.
/// </summary>
public enum ScriptStateScope { PerTarget, Shared }

/// <summary>
/// A durable intent: "this script belongs on whatever matches this scope, for this many dispatches".
///
/// <see cref="Budget"/> counts dispatches <i>per resolved target</i>, not per rule: "one greeting per new
/// channel" stays expressible, and a one-shot on a single instance is just budget 1. It is decremented and
/// written before the handler runs, so a crash costs one unused dispatch at worst — never a second run of
/// something that was supposed to happen once.
/// </summary>
public sealed record MountRule(
    string Id,
    string ScriptId,
    MountScope Scope,
    string Source,
    DateTimeOffset CreatedAt,
    int? Budget = null,
    ScriptStateScope StateScope = ScriptStateScope.PerTarget,
    IReadOnlyDictionary<string, int>? Remaining = null)
{
    public const string ManualSource = "manual";
    public const string BundleSource = "bundle";

    /// <summary>Dispatches still allowed on <paramref name="target"/>; null budget means "as many as asked for".</summary>
    public int? RemainingFor(Address target)
    {
        if (Budget is null) return null;
        var key = target.ToString();
        if (Remaining is not null && Remaining.TryGetValue(key, out var left)) return left;
        return Budget;
    }

    public MountRule WithRemaining(Address target, int left)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (Remaining is not null) foreach (var pair in Remaining) map[pair.Key] = pair.Value;
        map[target.ToString()] = left;
        return this with { Remaining = map };
    }

    /// <summary>True once every target the rule can still reach has an exhausted budget.</summary>
    public bool IsSpent(Address target) => RemainingFor(target) is <= 0;
}

/// <summary>A request to add a rule. The id and the timestamp belong to the graph, not the caller.</summary>
public sealed record MountRequest(string ScriptId, MountScope Scope, int? Budget = null,
    ScriptStateScope StateScope = ScriptStateScope.PerTarget);
