using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Graph.Contracts;

public enum MountMode { Default, Manual }
public enum TrustDomain { Builtin, Trusted, Restricted }

public sealed record ScriptManifest(
    string Id,
    int Version,
    string TargetType,
    MountMode Mount,
    string[] Events,
    string EntryPoint,
    string[] Capabilities,
    TrustDomain TrustDomain = TrustDomain.Trusted,
    int MaxStatements = 10_000,
    int TimeoutMilliseconds = 1_000,
    long MemoryLimitBytes = 16 * 1024 * 1024);

public sealed record ScriptPackage(ScriptManifest Manifest, string Source, string Fingerprint = "",
    /// <summary>Where it came from when that is not the operator's own script tree: "plugin:&lt;id&gt;".</summary>
    string? Origin = null,
    /// <summary>
    /// What this script is trusted with, decided by where it lives rather than by what its manifest says: the manifest
    /// is a file the model can write, so a level read out of it would be a level the model grants itself. A script in
    /// <c>scripts/draft</c> is the model's; one in <c>scripts/trusted</c> is a person's, vouched for by hand.
    ///
    /// The loader always sets this. The default is <see cref="AccessLevel.Full"/> because a package built in code is
    /// code, and the folders are the only place the question has a second answer.
    /// </summary>
    AccessLevel Level = AccessLevel.Full);

/// <summary>
/// A rule resolved onto one instance. <paramref name="RuleId"/> is null for bundle mounts, which are not
/// rules: they follow their package's lifecycle and are re-derived on every start.
/// </summary>
public sealed record MountedScript(ScriptPackage Package, Address Target, string? RuleId = null, int? Remaining = null);

public interface IGraphRuntime : IInstanceBindings
{
    ValueTask<Result> LoadAsync(IEnumerable<ScriptPackage> packages, CancellationToken cancellationToken);

    /// <summary>Mount a manual script on one instance. Equivalent to adding an instance-scoped rule.</summary>
    ValueTask<Result> MountAsync(string scriptId, Address target, CancellationToken cancellationToken,
        AccessLevel callerLevel = AccessLevel.Full);

    /// <summary>Drop every mount of one script on one instance; an instance-scoped rule that produced them goes with it.</summary>
    ValueTask<Result> UnmountAsync(string scriptId, Address target, CancellationToken cancellationToken);

    IReadOnlyList<MountedScript> Mounted { get; }

    /// <summary>
    /// Every instance the graph can mount onto: the addresses a rule may name. It includes instances nothing is mounted
    /// on, which is the whole point — a mount needs an address before anything has been mounted anywhere.
    /// </summary>
    IReadOnlyList<Address> Instances { get; }

    /// <summary>Every durable rule, including those whose scope currently matches nothing.</summary>
    IReadOnlyList<MountRule> Rules { get; }

    /// <summary>Rules that resolved to no target yet: the instance they are waiting for does not exist.</summary>
    IReadOnlyList<MountRule> PendingRules { get; }

    /// <summary>Every catalogued script with its library and whether it is allowed to run.</summary>
    IReadOnlyList<(ScriptPackage Package, bool Enabled)> Scripts { get; }

    /// <summary>
    /// Scripts the last load refused, and why. They are not in <see cref="Scripts"/>: a rejected script was never
    /// catalogued, and saying so is the difference between "the draft did nothing" and "the draft was not loadable".
    /// </summary>
    IReadOnlyList<ScriptIssue> RejectedScripts { get; }

    /// <summary>Switches one script (<c>id</c>) or one library (<c>bundled</c>, <c>user</c>, <c>plugin:<id></c>) on or off.</summary>
    ValueTask<Result> SetScriptEnabledAsync(string target, bool enabled, CancellationToken cancellationToken);

    ValueTask<Result<MountRule>> AddRuleAsync(MountRequest request, CancellationToken cancellationToken);
    ValueTask<Result> RemoveRuleAsync(string ruleId, CancellationToken cancellationToken);

    ValueTask<Result> DispatchAsync(RoutedEvent routedEvent, CancellationToken cancellationToken);
}

public interface IScriptExecutor
{
    ValueTask<Result> ValidateAsync(ScriptPackage package, CancellationToken cancellationToken);
    ValueTask<Result> ExecuteAsync(ScriptPackage package, Address target, RoutedEvent routedEvent,
        IScriptApi api, CancellationToken cancellationToken);
}

public interface IScriptApi
{
    Task<string> RunSessionAsync(string sessionSpecJson, string userInput, CancellationToken cancellationToken);

    /// <summary>
    /// Wake the Session for this Channel without handing it content: what a turn carries is pulled when
    /// the request is assembled, not when the signal is sent.
    /// </summary>
    Task<string> ActivateSessionAsync(string sessionSpecJson, CancellationToken cancellationToken);
    Task<string> CompleteModelAsync(string profileId, string requestJson, CancellationToken cancellationToken);
    Task<string> SendSelfAsync(string outboundMessageJson, CancellationToken cancellationToken);
    Task<string> AcceptIntoChannelAsync(CancellationToken cancellationToken);
    /// <summary>Deliver the current Adapter message to its Channel, creating that Channel if no binding exists yet.</summary>
    Task<string> RouteIntoChannelAsync(string optionsJson, CancellationToken cancellationToken);
    Task<string> ReplyThroughChannelAsync(string outboundMessageJson, CancellationToken cancellationToken);
    Task<string> PollSelfAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The mounted script's own memory: JSON text, namespaced by (rule, target) unless the rule declares a
    /// shared state scope. This is what lets a script survive a restart — "already greeted this one",
    /// "last seen id" — without the host having to know what any of it means.
    /// </summary>
    /// <returns>The stored JSON, or an empty string when the key was never written.</returns>
    Task<string> ReadStateAsync(string key, CancellationToken cancellationToken);
    Task SetStateAsync(string key, string json, CancellationToken cancellationToken);
    Task DeleteStateAsync(string key, CancellationToken cancellationToken);

    /// <summary>Point log for script business narration. Level is info, warn or error; never throws.</summary>
    void Log(string level, string message, string? detailJson);

    /// <summary>
    /// The action table, as a JSON answer: what exists, what level each verb needs, and which of them this script can
    /// reach. JSON in and JSON out because a script decides what a value means, and the host only knows the table.
    /// </summary>
    Task<string> ListActionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs one action as this script. Returns JSON — <c>{ ok, result }</c> or <c>{ ok: false, error }</c> — rather than
    /// throwing: a refusal is something a script can route around, and an exception inside a script is a script that
    /// stops mid-thought.
    /// </summary>
    Task<string> CallActionAsync(string action, string argumentsJson, CancellationToken cancellationToken);
}

/// <summary>
/// One script file that could not be used, and why. Reported instead of refusing the catalogue: a host that will not
/// start because one edited script has a typo costs every other script too (docs/25 §6).
/// </summary>
public sealed record ScriptIssue(string Path, string Reason);

public interface IGraphScriptCatalog
{
    ValueTask<Result<IReadOnlyList<ScriptPackage>>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>What the last load could not use: one entry per skipped file, with the file it came from.</summary>
    IReadOnlyList<ScriptIssue> LastIssues { get; }
}

public sealed record ScriptFileManifest(
    string Id, int Version, string TargetType, MountMode Mount, string[] Events,
    string EntryPoint, string[] Capabilities, TrustDomain TrustDomain,
    int MaxStatements = 10_000, int TimeoutMilliseconds = 1_000, long MemoryLimitBytes = 16 * 1024 * 1024);
public static class ScriptEvents
{
    public const string AdapterEnvelope = "adapter.envelope";
    public const string ChannelMessageAccepted = "channel.message.accepted";
}
public static class ScriptCapabilities
{
    public const string SessionRun = "session.run";
    public const string SessionActivate = "session.activate";
    public const string ModelComplete = "model.complete";
    public const string SelfAdapterSend = "self.adapter.send";
    public const string ChannelAccept = "channel.accept";
    public const string ChannelRoute = "channel.route";
    public const string ChannelReply = "channel.bound-send";
    public const string SelfAdapterPoll = "self.adapter.poll";
}

/// <summary>
/// What a reply should arrive as. A mode is not a transport detail: the Adapter sends one message per call,
/// always, and the mode decides how many messages one reply becomes — which is why it sits above the Adapter,
/// where a longer answer can later become something else entirely (a picture, a file) without the Adapter
/// growing a branch for each one.
///
/// The default is <see cref="Segments"/>: a chat window is not a page, and a model that keeps its answer in
/// one blob reads like a memo dropped into a conversation.
/// </summary>
public static class ChannelReplyModes
{
    /// <summary>The whole text as one message, verbatim.</summary>
    public const string Text = "text";

    /// <summary>Blank line = the next message; a single newline stays a line break inside one message.</summary>
    public const string Segments = "segments";

    public const string Default = Segments;

    public static bool IsKnown(string mode) =>
        string.Equals(mode, Text, StringComparison.Ordinal) || string.Equals(mode, Segments, StringComparison.Ordinal);
}

/// <summary>
/// The reply a script asks for. <paramref name="Mode"/> is the script's (or the model's, when a speak tool
/// forwards it) choice; leaving it out means the default mode, which is the one chat wants.
/// </summary>
public sealed record ChannelReplyRequest(string PlatformConversationId, string Text, string? Mode = null,
    string? ReplyTo = null);

/// <summary>
/// The action ids the graph answers to. The mount verbs were graph.* until the graph grew modules; the old names stay
/// registered for one release and point at the same handlers, so a client that says graph.mount still gets a mount.
/// </summary>
public static class GraphActions
{
    public const string EdgeList = "modules.edge.list";
    public const string EdgeMount = "modules.edge.mount";
    public const string EdgeUnmount = "modules.edge.unmount";
    public const string DeprecatedList = "graph.list";
    public const string DeprecatedMount = "graph.mount";
    public const string DeprecatedUnmount = "graph.unmount";

    /// <summary>The mount/unmount argument shapes, written once: the deprecated aliases take exactly the same arguments.</summary>
    public const string MountParams = """
        {"type":"object","properties":{
          "script":{"type":"string"},
          "target":{"type":"string"},
          "scope":{"type":"string","enum":["instance","type","module"]},
          "selector":{"type":"string"},
          "budget":{"type":"integer","minimum":1},
          "stateScope":{"type":"string","enum":["perTarget","shared"]}},
         "required":["script"],"additionalProperties":false}
        """;

    public const string UnmountParams = """
        {"type":"object","properties":{
          "rule":{"type":"string"},
          "script":{"type":"string"},
          "target":{"type":"string"}},
         "additionalProperties":false}
        """;
}
