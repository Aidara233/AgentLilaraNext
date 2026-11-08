using System.Text.Json.Serialization;
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
    long MemoryLimitBytes = 16 * 1024 * 1024,
    /// <summary>
    /// The function a Session calls when it uses this script as a tool. Absent means "this script only answers events":
    /// being callable is a decision made in the file, not a property of every script.
    /// </summary>
    string? Call = null,
    /// <summary>
    /// What the tool is called and what it takes. Optional even when <see cref="Call"/> is set: a script may instead
    /// export <c>describe()</c> and keep the definition next to the code. The manifest wins when both are present,
    /// because it is the one a person can read without running anything.
    /// </summary>
    ScriptToolSpec? Tool = null);

/// <summary>
/// One tool a script offers: what a Session sees in its tool list, and what the model has to fill in to call it.
///
/// The name is namespaced on purpose and the loader enforces it. A tool name is global to every Session's tool list, and
/// the safest thing to say about that is nothing at all: two sources offering one name is a failed turn in every
/// conversation, so a name that could collide is refused when the script is loaded, not discovered at 3am.
/// </summary>
public sealed record ScriptToolSpec(string Name, string Description, string ParametersJson,
    /// <summary>
    /// Who may call it, decided by whoever wrote the tool. Default <see cref="AccessLevel.Agent"/>: anybody.
    ///
    /// This is a <b>barrier</b>, not a grant, so a maker may only set it at or below its own level — that is what keeps
    /// "the tool's own permission never exceeds its maker's" true in both directions. A person can build a tool out of
    /// interfaces the model cannot reach and still open it to the model; they can also build one and keep it to
    /// themselves. What nobody can do is hand out a tool that reaches further than they do: the tool runs at the maker's
    /// level, and this field only narrows who gets to ask it for things.
    /// </summary>
    [property: JsonConverter(typeof(AccessLevelJson))] AccessLevel Access = AccessLevel.Agent);

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
    AccessLevel Level = AccessLevel.Full,
    /// <summary>
    /// The tool definition, resolved once when this package was loaded: from the manifest, or from the script's own
    /// <c>describe()</c>. Null means "not callable". Resolved at load rather than per round because a definition that
    /// changed shape between two rounds would make the same conversation see two different tools.
    /// </summary>
    ScriptToolSpec? Tool = null);

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

    /// <summary>
    /// Every script that is loaded and says it can be called as a tool, with the definition resolved at load.
    /// Per round, like every other tool source: a reload changes this list, and the next round sees the change.
    /// </summary>
    IReadOnlyList<ScriptPackage> ToolScripts { get; }

    /// <summary>
    /// Call one of those scripts as a tool. The code runs with the <b>script's</b> authority, never the calling Session's:
    /// a tool written by a shallow component must not become a way for a deep Session to run it with deep reach.
    /// </summary>
    ValueTask<Result<string>> InvokeToolAsync(string scriptId, string argumentsJson, CancellationToken cancellationToken);

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

    /// <summary>
    /// The tool definition a script describes for itself, as JSON text. Only called for a package that says it is
    /// callable and does not spell the definition out in its manifest; a script with no <c>describe()</c> answers
    /// <c>null</c> rather than failing, and the loader decides what that means.
    /// </summary>
    ValueTask<Result<string>> DescribeAsync(ScriptPackage package, CancellationToken cancellationToken);

    /// <summary>
    /// Call a script as a tool: one entry point, one argument object, one answer.
    ///
    /// A timeout here is <b>not</b> "nothing happened" — the call may already have reached the host and taken effect —
    /// so it comes back as <see cref="ErrorCode.Timeout"/> rather than as a plain failure, and the caller is expected to
    /// say so in words. That distinction is the groundwork for async tools: the day a call can outlive its turn, "did it
    /// happen" is the only question left, and it will be asked of exactly this shape.
    /// </summary>
    ValueTask<Result<string>> InvokeAsync(ScriptPackage package, Address target, string argumentsJson,
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

    /// <summary>
    /// The Session for a Channel — the one that serves it, or one created to serve it — with no requirement that this
    /// script be mounted on that Channel. Two questions in one verb because they are one act: "is there somebody for
    /// this conversation, and if not, may I make one". Creation carries the same ceiling it always did (a spec cannot
    /// name a level above the folder the script came from), and nothing about the Channel's own state is assumed: a
    /// Channel that does not exist yet gets a Session that is waiting for it.
    /// </summary>
    Task<string> OpenSessionAsync(string optionsJson, CancellationToken cancellationToken);

    /// <summary>
    /// Hand one Session a message from this script, optionally asking it to look now.
    ///
    /// This is the inward door, and it works in every direction: a draft script may hand a Full Session a request for
    /// something it cannot do itself, and the Full Session decides. What the message is <b>not</b> is authority — the
    /// receiving turn is told who sent it and how deep the sender was, and is told that a request from something
    /// shallower cannot require anything of it.
    /// </summary>
    Task<string> MessageSessionAsync(string optionsJson, CancellationToken cancellationToken);
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
    int MaxStatements = 10_000, int TimeoutMilliseconds = 1_000, long MemoryLimitBytes = 16 * 1024 * 1024,
    /// <summary>The function a Session calls when this script is used as a tool. Absent: events only.</summary>
    string? Call = null,
    /// <summary>What that tool is. Absent: ask the script's own <c>describe()</c>.</summary>
    ScriptToolSpec? Tool = null);
public static class ScriptEvents
{
    public const string AdapterEnvelope = "adapter.envelope";
    public const string ChannelMessageAccepted = "channel.message.accepted";
}
public static class ScriptCapabilities
{
    public const string SessionRun = "session.run";
    public const string SessionActivate = "session.activate";
    /// <summary>The Session serving a Channel, created if there is none. Not tied to a Channel-message event.</summary>
    public const string SessionOpen = "session.open";
    /// <summary>Hand one Session a message, optionally waking it. Allowed in every direction, by design.</summary>
    public const string SessionMessage = "session.message";
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
