using System.Text.Json;

namespace ALN.Contracts;

public enum ActorKind { User = 0, Script = 1, System = 2, Model = 3 }

/// <summary>
/// Who is invoking an action. A script carries its mount identity; a user carries an account id.
/// Capabilities are granted by whoever authenticated the actor, never by the caller's payload.
/// </summary>
public sealed record Actor(string Id, ActorKind Kind, IReadOnlyList<string> Capabilities)
{
    public const string Any = "*";

    public static Actor System { get; } = new("system", ActorKind.System, [Any]);

    public bool Allows(string capability)
    {
        if (string.IsNullOrEmpty(capability)) return false;
        foreach (var granted in Capabilities)
        {
            if (Matches(granted, capability)) return true;
            // graph.read / graph.control keep working for the release the action names are being forwarded for: the
            // actions those grants guarded are modules.* now, and a role file that says "graph.control" must not
            // quietly lose the right to mount. One-way on purpose — a new grant cannot buy an old name.
            if (FormerName(capability) is { } former && Matches(granted, former)) return true;
        }
        return false;
    }

    /// <summary>The capability a modules.* name was called before the graph grew modules.</summary>
    private static string? FormerName(string capability) =>
        capability.StartsWith("modules.", StringComparison.Ordinal)
            ? "graph." + capability["modules.".Length..]
            : null;

    /// <summary>
    /// One '*' anywhere: "adapters.*", "*.read" or "*". Anything richer invites patterns nobody
    /// can reason about, so it is not supported and simply fails to match.
    /// </summary>
    private static bool Matches(string pattern, string value)
    {
        var star = pattern.IndexOf('*');
        if (star < 0) return string.Equals(pattern, value, StringComparison.Ordinal);
        if (pattern.IndexOf('*', star + 1) >= 0) return false;
        var prefix = pattern[..star];
        var suffix = pattern[(star + 1)..];
        return value.Length >= prefix.Length + suffix.Length &&
               value.StartsWith(prefix, StringComparison.Ordinal) &&
               value.EndsWith(suffix, StringComparison.Ordinal);
    }
}

/// <summary>
/// One externally reachable action. This is the only thing that crosses the boundary: internal
/// managers stay free to change, the catalog is the contract.
/// </summary>
/// <param name="ParamsJson">
/// The arguments this action takes, as a JSON Schema object — the same shape a model tool carries. It used to be
/// missing, and the only description of an action's arguments was prose inside <paramref name="Summary"/>; a caller
/// that cannot see the fields guesses them, and guessing a field name is the most common way a call fails for a
/// reason that has nothing to do with the call.
/// </param>
/// <param name="Subject">
/// Which argument names the thing being acted on (<c>id</c>, <c>address</c>, <c>script</c>…). Recorded instead of the
/// whole argument object: "who changed what" has to be answerable afterwards, and an argument object is the one
/// place a credential would end up in a log (<c>models.provider.save</c> carries a key).
/// </param>
public sealed record ActionDescriptor(string Id, string Summary, string Capability, bool Mutating,
    string? ParamsJson = null, string? Subject = null);

/// <summary>
/// Single execution point for every front-end (scripts today, the HTTP API next). Authorization,
/// audit and signals happen here once, so no front-end can forget one of them.
/// </summary>
public interface IControlPlane
{
    IReadOnlyList<ActionDescriptor> Catalog { get; }
    ValueTask<Result<JsonElement>> InvokeAsync(Actor actor, string action, JsonElement arguments,
        CancellationToken cancellationToken);
}

/// <summary>A registered action body. Arguments are untrusted JSON; validate them here, not in transport.</summary>
public delegate ValueTask<Result<JsonElement>> ActionHandler(JsonElement arguments, CancellationToken cancellationToken);

/// <summary>Well-known capability names. Roles map onto these; actions name exactly one.</summary>
public static class Capabilities
{
    public const string SystemRead = "system.read";
    public const string AdaptersRead = "adapters.read";
    public const string AdaptersControl = "adapters.control";
    public const string ChannelsRead = "channels.read";
    public const string ChannelsControl = "channels.control";
    public const string MessagesWrite = "messages.write";
    public const string GraphRead = "graph.read";
    public const string GraphControl = "graph.control";
    /// <summary>The module graph: which instances exist, and the edges between them (formerly graph.read).</summary>
    public const string ModulesRead = "modules.read";
    /// <summary>Changing the graph: load, unload, mount, unmount (formerly graph.control).</summary>
    public const string ModulesControl = "modules.control";
    public const string ParamsRead = "params.read";
    public const string ParamsWrite = "params.write";
    public const string ModelsRead = "models.read";
    /// <summary>Changing which provider or profile exists, or what a provider is configured with. Not a read.</summary>
    public const string ModelsControl = "models.control";
    public const string SignalsRead = "signals.read";
    public const string SessionsRead = "sessions.read";
    public const string ArchiveRead = "archive.read";
    public const string ArchiveControl = "archive.control";
    public const string PromptsRead = "prompts.read";
    /// Editing a prompt changes what the next turn says. A viewer with prompts.read may look, not rewrite.
    public const string PromptsWrite = "prompts.write";
}
