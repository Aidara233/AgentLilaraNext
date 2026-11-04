using System.Text.Json;

namespace ALN.Contracts;

public enum ActorKind { User = 0, Script = 1, System = 2 }

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
            if (Matches(granted, capability)) return true;
        return false;
    }

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
public sealed record ActionDescriptor(string Id, string Summary, string Capability, bool Mutating);

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
}
