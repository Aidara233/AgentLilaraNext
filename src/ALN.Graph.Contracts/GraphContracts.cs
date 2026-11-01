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
    string? Origin = null);

/// <summary>
/// A rule resolved onto one instance. <paramref name="RuleId"/> is null for bundle mounts, which are not
/// rules: they follow their package's lifecycle and are re-derived on every start.
/// </summary>
public sealed record MountedScript(ScriptPackage Package, Address Target, string? RuleId = null, int? Remaining = null);

public interface IGraphRuntime : IInstanceBindings
{
    ValueTask<Result> LoadAsync(IEnumerable<ScriptPackage> packages, CancellationToken cancellationToken);

    /// <summary>Mount a manual script on one instance. Equivalent to adding an instance-scoped rule.</summary>
    ValueTask<Result> MountAsync(string scriptId, Address target, CancellationToken cancellationToken);

    /// <summary>Drop every mount of one script on one instance; an instance-scoped rule that produced them goes with it.</summary>
    ValueTask<Result> UnmountAsync(string scriptId, Address target, CancellationToken cancellationToken);

    IReadOnlyList<MountedScript> Mounted { get; }

    /// <summary>Every durable rule, including those whose scope currently matches nothing.</summary>
    IReadOnlyList<MountRule> Rules { get; }

    /// <summary>Rules that resolved to no target yet: the instance they are waiting for does not exist.</summary>
    IReadOnlyList<MountRule> PendingRules { get; }

    /// <summary>Every catalogued script with its library and whether it is allowed to run.</summary>
    IReadOnlyList<(ScriptPackage Package, bool Enabled)> Scripts { get; }

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

public interface IScriptApi : ISessionToolExecutor
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
}

public interface IGraphScriptCatalog
{
    ValueTask<Result<IReadOnlyList<ScriptPackage>>> LoadAsync(CancellationToken cancellationToken);
}

public sealed record ScriptFileManifest(
    string Id, int Version, string TargetType, MountMode Mount, string[] Events,
    string EntryPoint, string[] Capabilities, TrustDomain TrustDomain,
    int MaxStatements = 10_000, int TimeoutMilliseconds = 1_000, long MemoryLimitBytes = 16 * 1024 * 1024);
public static class ScriptEvents
{
    public const string AdapterEnvelope = "adapter.envelope";
    public const string ChannelMessageAccepted = "channel.message.accepted";
    public const string ClockPulse = "clock.pulse";
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
