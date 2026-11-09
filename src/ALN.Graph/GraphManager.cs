using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Graph.Contracts;

namespace ALN.Graph;

[Module(GraphManagerId, Category = "graph", Role = ModuleRole.Manager, Singleton = true)]
public sealed partial class GraphManager : IGraphRuntime, IModule
{
    public const string GraphManagerId = ModuleNames.GraphManager;
    public ModuleId Id { get; } = new(GraphManagerId);

    private static readonly HashSet<string> AllowedCapabilities = new(StringComparer.Ordinal)
    {
        // The script's own ctx, then the action table it may ask about. Both are needed: a list that only knows the ctx
        // could not say "you may read the graph", and a script that cannot name what it reaches cannot be checked.
        ScriptCapabilities.SelfAdapterSend, ScriptCapabilities.SelfAdapterPoll, ScriptCapabilities.ChannelAccept,
        ScriptCapabilities.ChannelRoute, ScriptCapabilities.ChannelReply, ScriptCapabilities.ModelComplete,
        ScriptCapabilities.SessionRun, ScriptCapabilities.SessionActivate, ScriptCapabilities.SessionOpen,
        ScriptCapabilities.SessionMessage,
        Capabilities.SystemRead, Capabilities.AdaptersRead, Capabilities.AdaptersControl, Capabilities.ChannelsRead,
        Capabilities.ChannelsControl, Capabilities.MessagesWrite, Capabilities.GraphRead, Capabilities.GraphControl,
        Capabilities.ModulesRead, Capabilities.ModulesControl, Capabilities.ParamsRead, Capabilities.ParamsWrite,
        Capabilities.ModelsRead, Capabilities.ModelsControl, Capabilities.SignalsRead, Capabilities.SessionsRead,
        Capabilities.SessionsControl, Capabilities.ArchiveRead, Capabilities.ArchiveControl, Capabilities.PromptsRead,
        Capabilities.PromptsWrite
    };
    private readonly ICatalog _catalog;
    private readonly IModuleTypeCatalog _types;
    private readonly IScriptExecutor _executor;
    private readonly IAuditSink _audit;
    private readonly ISignalLog _signalLog;
    private readonly Dictionary<string, ScriptPackage> _packages = new(StringComparer.Ordinal);
    private readonly List<MountedScript> _mounted = new();
    // Durable intents: what should be mounted, not what happens to be mounted right now. A rule whose
    // instance does not exist yet is still a rule; that is how "mount on every Channel" survives a restart
    // that happens before the Channel does.
    private readonly List<MountRule> _rules = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Address> _drainingTargets = new();
    private readonly Dictionary<Address, int> _activeDispatches = new();
    private readonly Dictionary<Address, TaskCompletionSource> _dispatchDrained = new();
    private readonly IStateStore? _state;
    /// <summary>
    /// The action table, kept from <see cref="RegisterActions"/> so a script can reach it. Held here rather than passed
    /// per call because the graph already owns the verbs it registers into it: the plane is the same object, and a
    /// second reference would be a second thing to keep in step.
    /// </summary>
    private IControlPlane? _plane;
    private readonly IRestoreReporter _report;
    private bool _loaded;
    private bool _rulesRestored;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Rules are a file a person may open: enums go out as names, not as their numeric value.
    private static readonly JsonSerializerOptions RuleJson = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    /// <summary>Where rules live. One file, replaced whole: a half-written rule list is a mount nobody meant.</summary>
    public const string StateScope = "graph";
    public const string StateKey = "mounts";

    /// <summary>
    /// The graph knows the address book and four things it cannot get from it (type tree, script runner, audit,
    /// signal log). It holds no manager: a manager is found by its contract name when it is needed, which is also
    /// what keeps "who is allowed to talk to whom" a question about names rather than about constructor graphs.
    /// </summary>
    public GraphManager(ICatalog catalog, IModuleTypeCatalog types, IScriptExecutor executor, IAuditSink audit,
        ISignalLog signalLog, IStateStore? state = null, IRestoreReporter? report = null,
        /// <summary>
        /// How a picture the model wants to send is named. Optional: a graph without it can still send text, and
        /// <c>speak</c> with an image block then says so instead of sending a placeholder (docs/28 §6).
        /// </summary>
        IMediaStore? media = null)
    {
        _catalog = catalog; _types = types; _executor = executor; _audit = audit; _signalLog = signalLog;
        _state = state; _report = report ?? NullRestoreReporter.Instance;
        _media = media;
    }

    /// <summary>Where a picture named by a tool becomes a reference the media area can resolve.</summary>
    private readonly IMediaStore? _media;

    private static ModuleId IdOf(Address address) => address.ToModuleId();

    /// <summary>The one module that answers to that name, by the contract name it was registered under.</summary>
    private T Require<T>(string id) where T : class =>
        _catalog.Find(new ModuleId(id)) as T
        ?? throw new InvalidOperationException(
            $"The graph needs {id}, and the address book has nothing of that name as {typeof(T).Name}.");

    /// <summary>Same, for a manager the host may not have brought up at all.</summary>
    private T? Maybe<T>(string id, string category) where T : class =>
        _catalog.List(category: category).Any(row => row.Role is ModuleRole.Manager) ? _catalog.Find(new ModuleId(id)) as T : null;

    private IAdapterManager Adapters => Require<IAdapterManager>(ModuleNames.AdapterManager);
    private IChannelManager Channels => Require<IChannelManager>(ModuleNames.ChannelManager);
    private IModelManager? Models => Maybe<IModelManager>(ModuleNames.ModelManager, "model");
    private ISessionManager? Sessions => Maybe<ISessionManager>(ModuleNames.SessionManager, "session");

    public IReadOnlyList<MountedScript> Mounted
    { get { _gate.Wait(); try { return _mounted.ToArray(); } finally { _gate.Release(); } } }

    public async ValueTask<Result> LoadAsync(IEnumerable<ScriptPackage> packages, CancellationToken cancellationToken)
    {
        var incoming = packages.ToArray();
        var candidate = new Dictionary<string, ScriptPackage>(StringComparer.Ordinal);
        var rejected = new List<ScriptIssue>();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in incoming)
        {
            // One script at a time, and a refusal that costs only that script. The alternative — refusing the whole
            // catalogue — makes one typo in one edited file a host that will not start, which is worse than a host
            // that starts and names the file it skipped.
            if (WhyRejected(package) is { } reason)
            {
                rejected.Add(new(package.Manifest.Id ?? "", reason));
                continue;
            }
            if (candidate.ContainsKey(package.Manifest.Id!))
            {
                rejected.Add(new(package.Manifest.Id!, "Duplicate script id in one catalogue."));
                continue;
            }
            var validated = await _executor.ValidateAsync(package, cancellationToken).ConfigureAwait(false);
            if (!validated.Ok)
            {
                rejected.Add(new(package.Manifest.Id!, validated.Error?.Message ?? "The script's source is invalid."));
                continue;
            }
            // A script that says it can be called is a tool, and the definition is settled here — once, at load. Two
            // reasons it cannot be settled later: the same conversation must not see two shapes for one tool, and a name
            // that collides is a failed turn in every Session (the resolver refuses duplicates), so a collision has to
            // be a refused script rather than an outage nobody connects to the file they just wrote.
            if (package.Manifest.Call is { Length: > 0 } entry)
            {
                var described = await ResolveToolAsync(package, entry, cancellationToken).ConfigureAwait(false);
                if (described.Error is { } toolError)
                {
                    rejected.Add(new(package.Manifest.Id!, toolError.Message));
                    continue;
                }
                var spec = described.Value!;
                if (WhyNotAName(spec.Name) is { } nameError)
                {
                    rejected.Add(new(package.Manifest.Id!, nameError));
                    continue;
                }
                // A tool's own permission is the author's choice, capped by the author's own level: a maker may keep a
                // tool to itself, or open it to everyone, but may not build one that outranks the folder it came from.
                if (spec.Access > package.Level)
                {
                    rejected.Add(new(package.Manifest.Id!,
                        $"Tool \"{spec.Name}\" asks to be callable by {AccessLevels.Describe(spec.Access)}, and this "
                        + $"script is {AccessLevels.Describe(package.Level)}. A tool's permission may sit at or below its "
                        + "maker's, never above: the tool runs with the maker's reach, and who may call it cannot exceed it."));
                    continue;
                }
                if (toolNames.TryGetValue(spec.Name, out var owner))
                {
                    rejected.Add(new(package.Manifest.Id!,
                        $"Tool name \"{spec.Name}\" is already offered by {owner}. A tool name is global: two of them is "
                        + "a failed turn in every conversation."));
                    continue;
                }
                toolNames[spec.Name] = package.Manifest.Id!;
                // `package` is the loop variable, so the resolved definition is what goes into the candidate: the script
                // that runs and the tool it offers are the same decision, taken once.
                candidate[package.Manifest.Id!] = package with { Tool = spec };
                continue;
            }
            candidate[package.Manifest.Id!] = package;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var restored = await RestoreRulesAsync(cancellationToken).ConfigureAwait(false);
            if (!restored.Ok) return restored;
            await RestoreDisabledAsync(cancellationToken).ConfigureAwait(false);
            _rejected = rejected;
            var previous = new Dictionary<string, ScriptPackage>(_packages, StringComparer.Ordinal);
            _packages.Clear();
            foreach (var pair in candidate) _packages.Add(pair.Key, pair.Value);
            // A refused script keeps the body it already had: refusing an edit is a reason to leave things as they are,
            // not a reason for a channel that was answering to go quiet. A script that was never loaded simply does not
            // appear — there is nothing to fall back to, and the issue says so.
            foreach (var issue in rejected)
            {
                if (!_packages.ContainsKey(issue.Path) && previous.TryGetValue(issue.Path, out var prior))
                    _packages[issue.Path] = prior;
            }
            RebuildMounts();
            _loaded = true;
            ReportRestore();
            return Result.Success();
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<MountRule> Rules
    { get { _gate.Wait(); try { return _rules.ToArray(); } finally { _gate.Release(); } } }

    /// <summary>A rule with nothing to apply to right now: the instance it names does not exist, or its script is gone.</summary>
    public IReadOnlyList<MountRule> PendingRules
    {
        get
        {
            _gate.Wait();
            try
            {
                var targets = Targets();
                return _rules.Where(rule => !targets.Any(target => Resolves(rule, target))).ToArray();
            }
            finally { _gate.Release(); }
        }
    }

    /// <summary>
    /// Adds a durable rule and applies it to whatever already matches. An instance-scoped rule must name a
    /// target that exists — mounting onto a typo is not an intent, it is a mistake that would stay silent
    /// forever. Type and module scopes may match nothing yet: that is exactly how "every new Channel gets
    /// this" is expressed.
    /// </summary>
    public async ValueTask<Result<MountRule>> AddRuleAsync(MountRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_packages.TryGetValue(request.ScriptId, out var package))
                return Result<MountRule>.Failure(new(ErrorCode.Invalid, $"Script not found: {request.ScriptId}"));
            if (package.Manifest.Mount != MountMode.Manual)
                return Result<MountRule>.Failure(new(ErrorCode.Denied, "Default scripts are mounted by instance lifecycle."));
            if (!IsEnabled(package))
                return Result<MountRule>.Failure(new(ErrorCode.Denied, $"Script {request.ScriptId} is switched off."));
            // Mounting is borrowing: the script answers events as itself, with its own capabilities. Letting an agent
            // wire a script that is deeper than the agent would hand it those capabilities one event at a time.
            if (package.Level > request.CallerLevel)
                return Result<MountRule>.Failure(new(ErrorCode.Denied,
                    $"Script {request.ScriptId} is {AccessLevels.Describe(package.Level)} and this caller is "
                    + $"{AccessLevels.Describe(request.CallerLevel)}: a script cannot be mounted from above its own level."));
            if (request.Budget is < 1)
                return Result<MountRule>.Failure(new(ErrorCode.Invalid, $"Budget must be at least 1 or absent: {request.Budget}"));
            if (CheckScope(request.Scope) is { } scopeError) return Result<MountRule>.Failure(scopeError);

            var targets = Targets();
            if (request.Scope.Kind == MountScopeKind.Instance)
            {
                if (!Address.TryParse(request.Scope.Selector, out var named) || !named.IsInstance)
                    return Result<MountRule>.Failure(new(ErrorCode.Invalid, "Instance scope needs a concrete address."));
                if (_drainingTargets.Contains(named))
                    return Result<MountRule>.Failure(new(ErrorCode.Unavailable, "Manual mount target is draining.", Retryable: true));
                if (TargetOf(named) is not { } namedTarget || !IsCompatible(package.Manifest.TargetType, namedTarget))
                    return Result<MountRule>.Failure(new(ErrorCode.Invalid, "Manual mount target is absent or incompatible."));
            }
            else if (targets.Any(target => Matches(request.Scope, target) && IsCompatible(package.Manifest.TargetType, target) &&
                                           _mounted.Any(m => m.Package.Manifest.Id == request.ScriptId && m.Target == target.Address)))
            {
                return Result<MountRule>.Failure(new(ErrorCode.Conflict, "Script is already mounted on a matching target."));
            }

            var rule = new MountRule(NewRuleId(), request.ScriptId, request.Scope, MountRule.ManualSource,
                DateTimeOffset.UtcNow, request.Budget, request.StateScope);
            _rules.Add(rule);
            foreach (var target in targets) ApplyRulesTo(target);

            var persisted = await PersistRulesAsync(cancellationToken).ConfigureAwait(false);
            if (!persisted.Ok)
            {
                // Persist before anything is claimed: a rule that exists only in memory is a rule that
                // disappears at the next restart, and the caller would have no way to know.
                _rules.Remove(rule);
                _mounted.RemoveAll(m => m.RuleId == rule.Id);
                return Result<MountRule>.Failure(persisted.Error!);
            }

            // Conflict is reported from what actually got mounted, so a scope that matched nothing is not a lie.
            if (request.Scope.Kind == MountScopeKind.Instance && _mounted.All(m => m.RuleId != rule.Id))
            {
                _rules.Remove(rule);
                await PersistRulesAsync(CancellationToken.None).ConfigureAwait(false);
                return Result<MountRule>.Failure(new(ErrorCode.Conflict, "Script is already mounted."));
            }
            return Result<MountRule>.Success(rule);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> RemoveRuleAsync(string ruleId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _rules.FindIndex(rule => rule.Id == ruleId);
            if (index < 0) return Result.Failure(new(ErrorCode.Invalid, $"Mount rule not found: {ruleId}"));
            var rule = _rules[index];
            _rules.RemoveAt(index);
            _mounted.RemoveAll(m => m.RuleId == ruleId);
            var persisted = await PersistRulesAsync(cancellationToken).ConfigureAwait(false);
            return persisted.Ok
                ? Result.Success()
                : Result.Failure(new(ErrorCode.Unavailable,
                    $"Rule {rule.ScriptId} was removed from the running graph but could not be persisted; it will come back on restart.",
                    persisted.Error?.Message));
        }
        finally { _gate.Release(); }
    }

    private Error? CheckScope(MountScope scope)
    {
        if (string.IsNullOrWhiteSpace(scope.Selector))
            return new(ErrorCode.Invalid, "Mount scope needs a selector.");
        return scope.Kind switch
        {
            MountScopeKind.Instance => null,
            MountScopeKind.Type => _types.IsConcrete(scope.Selector)
                ? null
                : new(ErrorCode.Invalid, $"Type scope needs a concrete module type: {scope.Selector}"),
            MountScopeKind.Module => _types.IsKnownAbstract(scope.Selector)
                ? null
                : new(ErrorCode.Invalid, $"Module scope needs a module type: {scope.Selector}"),
            _ => new(ErrorCode.Invalid, $"Unknown mount scope: {scope.Kind}")
        };
    }

    private static string NewRuleId() => "r" + Guid.NewGuid().ToString("N")[..8];

    private Target[] Targets() =>
        _catalog.List()
            .Where(row => row.Role is ModuleRole.Instance)
            .Select(row => _catalog.Find(row.Id) is IAddressed addressed ? new Target(addressed.Address, row.TypeId) : (Target?)null)
            .Where(target => target is not null)
            .Select(target => target!.Value)
            .ToArray();

    /// <summary>
    /// A mount target as the address book describes it: where it is, and what it is. The two travel together
    /// because the mount rules match on the type — and the type comes from the catalog row, never from the name.
    /// </summary>
    private readonly record struct Target(Address Address, string TypeId);

    private bool Matches(MountScope scope, Target target) => scope.Kind switch
    {
        MountScopeKind.Instance => Address.TryParse(scope.Selector, out var named) && named == target.Address,
        // "channel" (a module) and "channel.conversation" (a type) are the same walk up the type tree;
        // keeping them separate kinds is about what a person means, and about validating it.
        MountScopeKind.Type or MountScopeKind.Module => _types.IsAssignableTo(target.TypeId, scope.Selector),
        _ => false
    };

    /// <summary>True when this rule actually puts a script on this target right now.</summary>
    private bool Resolves(MountRule rule, Target target) =>
        !rule.IsSpent(target.Address) &&
        Matches(rule.Scope, target) &&
        _packages.TryGetValue(rule.ScriptId, out var package) &&
        IsEnabled(package) &&
        IsCompatible(package.Manifest.TargetType, target);

    /// <summary>Call with the gate held: brings one target in line with every rule that names it.</summary>
    private void ApplyRulesTo(Target target)
    {
        foreach (var rule in _rules)
        {
            if (!Resolves(rule, target)) continue;
            if (_mounted.Any(m => m.Package.Manifest.Id == rule.ScriptId && m.Target == target.Address)) continue;
            _mounted.Add(new(_packages[rule.ScriptId], target.Address, rule.Id, rule.RemainingFor(target.Address)));
        }
    }

    /// <summary>
    /// A script can be switched off, and so can a whole library — a plugin's scripts, the built-ins, or the
    /// operator's own. Off means: not mounted, and not mountable. What is stored is the exception (the ids that
    /// are off), so "everything the host ships" stays on without writing anything down.
    /// </summary>
    public const string DisabledScope = "scripts.disabled";
    private const string DisabledKeyName = "off";

    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);

    /// <summary>What the last load refused: not in <see cref="_packages"/>, and named so the caller can say why.</summary>
    private IReadOnlyList<ScriptIssue> _rejected = [];

    /// <summary>
    /// The tool definition a callable script offers: its manifest when it says, its own <c>describe()</c> when it does
    /// not. A script that claims to be callable and describes nothing is refused here rather than loaded and never
    /// offered — "I am a tool" with no definition is a file that needs fixing, and load time is when somebody is looking.
    /// </summary>
    private async ValueTask<Result<ScriptToolSpec>> ResolveToolAsync(ScriptPackage package, string entry,
        CancellationToken cancellationToken)
    {
        if (package.Manifest.Tool is { } declared)
            return Result<ScriptToolSpec>.Success(declared);

        var described = await _executor.DescribeAsync(package, cancellationToken).ConfigureAwait(false);
        if (!described.Ok)
            return Result<ScriptToolSpec>.Failure(described.Error
                ?? new(ErrorCode.Invalid, $"{package.Manifest.Id}: describe() failed."));
        if (string.IsNullOrWhiteSpace(described.Value) || described.Value.Trim() == "null")
            return Result<ScriptToolSpec>.Failure(new(ErrorCode.Invalid,
                $"{package.Manifest.Id} has \"call\": \"{entry}\" but no \"tool\" in its manifest and no describe()."));

        try
        {
            var spec = JsonSerializer.Deserialize<ScriptToolSpec>(described.Value, JsonOptions);
            return spec is null
                ? Result<ScriptToolSpec>.Failure(new(ErrorCode.Invalid, $"{package.Manifest.Id}: describe() returned null."))
                : Result<ScriptToolSpec>.Success(spec);
        }
        catch (JsonException ex)
        {
            return Result<ScriptToolSpec>.Failure(new(ErrorCode.Invalid,
                $"{package.Manifest.Id}: describe() did not return a tool definition.", ex.Message));
        }
    }

    /// <summary>
    /// Why this tool name cannot be used, or null. Dotted and lowercase, so a tool reads as somebody else's rather than as
    /// one of the Session's own — and so `wait`, `compact` and every module's names are out of reach by construction.
    /// </summary>
    private static string? WhyNotAName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A tool needs a name.";
        if (name.Length > 64) return $"Tool name is longer than 64 characters: {name}";
        return Regex.IsMatch(name, "^[a-z][a-z0-9]*(\\.[a-z0-9][a-z0-9_-]*)+$", RegexOptions.CultureInvariant)
            ? null
            : $"Tool name \"{name}\" must be lowercase and namespaced, like \"tool.summarise\": the dot is what says it "
              + "is somebody else's tool, and what keeps it away from wait, compact and the modules' own names.";
    }

    /// <summary>Every loaded script that can be called, with the definition the loader settled on.</summary>
    public IReadOnlyList<ScriptPackage> ToolScripts
    {
        get
        {
            _gate.Wait();
            try { return _packages.Values.Where(package => package.Tool is not null && IsEnabled(package)).ToArray(); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>
    /// Calls one script as a tool.
    ///
    /// It runs as itself: capabilities and level come from the folder the script came from, never from the Session that
    /// called it. Same rule as mounting, same reason — a tool written by the model must not become a way for a deeper
    /// Session to run it with deeper reach.
    /// </summary>
    public async ValueTask<Result<string>> InvokeToolAsync(string scriptId, string argumentsJson,
        CancellationToken cancellationToken)
    {
        ScriptPackage? package;
        _gate.Wait();
        try { package = _packages.TryGetValue(scriptId, out var found) ? found : null; }
        finally { _gate.Release(); }
        if (package is null || package.Tool is null)
            return Result<string>.Failure(new(ErrorCode.Invalid, $"No callable script is called {scriptId}."));
        if (!IsEnabled(package))
            return Result<string>.Failure(new(ErrorCode.Denied, $"Script {scriptId} is switched off."));

        var target = new Address("script", "tool", package.Manifest.Id);
        var invocationId = Guid.NewGuid();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Result<string> result;
        using (SignalScope.Enter(invocationId, invocationId))
        {
            var api = new ScriptApi(Adapters, Channels, _audit, _signalLog, Models, Sessions,
                new MountedScript(package, target), new RoutedEvent(ToolCallEvent, target, invocationId, argumentsJson),
                $"script.tool:{package.Manifest.Id}", _state, _plane, cancellationToken, _media);
            result = await _executor.InvokeAsync(package, target, argumentsJson, api, cancellationToken)
                .ConfigureAwait(false);
        }

        var outcome = result.Ok ? "allowed" : result.Error!.Code == ErrorCode.Timeout ? "timeout" : "failed";
        await _audit.AppendAsync(new(DateTimeOffset.UtcNow, invocationId, package.Manifest.Id, target, ToolCallEvent,
            outcome, result.Error?.Message), CancellationToken.None).ConfigureAwait(false);
        _signalLog.Write(new()
        {
            Id = invocationId,
            Group = "script",
            Name = package.Manifest.Id,
            From = "session",
            To = $"tool:{package.Tool.Name}",
            Mechanism = ToolCallEvent,
            Outcome = result.Ok ? "ok" : outcome,
            Level = result.Ok ? SignalLevel.Info : SignalLevel.Warn,
            DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Detail = new Dictionary<string, string>
            {
                ["arguments"] = argumentsJson.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        });
        return result;
    }

    /// <summary>The name a tool call carries in the audit and the signal log. Not an event: nothing is dispatched.</summary>
    public const string ToolCallEvent = "script.tool.call";

    /// <summary>
    /// Why one script cannot be used, or null when it can. Checked per script because the answer is per script: a
    /// catalogue is not one document that stands or falls, it is a list of behaviours (docs/25 §6).
    /// </summary>
    private string? WhyRejected(ScriptPackage package)
    {
        var m = package.Manifest;
        if (string.IsNullOrWhiteSpace(m.Id)) return "A script needs an id.";
        if (m.Version < 1) return $"Version must be at least 1 (got {m.Version}).";
        if (string.IsNullOrWhiteSpace(m.TargetType) || !_types.IsKnownAbstract(m.TargetType))
            return $"targetType must be a known abstract type, and '{m.TargetType}' is not one.";
        if (m.Events is null || m.Events.Length == 0) return "A script must declare at least one event.";
        // The vocabulary is the host's, and it is one list (EventNames.All) rather than a whitelist written next to
        // this check: a name the host publishes is a name a script may listen for. Whether the event ever reaches a
        // given script is a different question — that is decided by the mount, which names one instance.
        var unknown = m.Events.FirstOrDefault(e => !EventNames.IsKnown(e));
        if (unknown is not null)
            return $"Unknown event '{unknown}'; this host produces {EventNames.Describe()}.";
        if (m.Events.Contains(EventNames.ScheduleDue, StringComparer.Ordinal) &&
            !string.Equals(m.TargetType, "channel", StringComparison.Ordinal))
            return $"{EventNames.ScheduleDue} is dispatched to a Channel instance; a schedule targeting " +
                   $"'{m.TargetType}' would never reach this script.";
        if (string.IsNullOrWhiteSpace(m.EntryPoint)) return "A script needs an entry point.";
        if (m.MaxStatements is < 1 or > 1_000_000) return $"maxStatements must be 1..1000000 (got {m.MaxStatements}).";
        if (m.TimeoutMilliseconds is < 1 or > 60_000) return $"timeoutMilliseconds must be 1..60000 (got {m.TimeoutMilliseconds}).";
        if (m.MemoryLimitBytes is < 1_048_576 or > 268_435_456)
            return $"memoryLimitBytes must be 1 MiB..256 MiB (got {m.MemoryLimitBytes}).";
        if (m.TrustDomain == TrustDomain.Restricted)
            return "Restricted scripts need the process-isolated runner, which this host does not have.";
        if (m.Capabilities is null || m.Capabilities.Any(c => !AllowedCapabilities.Contains(c)))
            return $"Unsupported capability: {string.Join(", ", m.Capabilities ?? [])}. " +
                   $"Allowed: {string.Join(", ", AllowedCapabilities.Order(StringComparer.Ordinal))}.";
        // What a script declares it may reach, it may only declare at its own level. A draft script lives at the model's
        // level, so "read the graph" is available to it and "rewrite a provider" is not — and the refusal happens when
        // the file is loaded, not when the call is attempted, because a script that can never succeed should say so
        // where somebody is looking at it.
        var tooDeep = m.Capabilities.FirstOrDefault(c => AccessLevels.Of(c) > package.Level);
        if (tooDeep is not null)
            return $"Capability '{tooDeep}' needs {AccessLevels.Describe(AccessLevels.Of(tooDeep))}, and this script is "
                   + $"{AccessLevels.Describe(package.Level)}: a script reaches what the folder it came from reaches "
                   + "(scripts/trusted is a person vouching for it; draft and live are the model's own).";
        if (m.Mount is not (MountMode.Default or MountMode.Manual)) return $"Unknown mount mode: {m.Mount}.";
        if (string.IsNullOrWhiteSpace(package.Source) || package.Source.Length > 256_000) return "Empty or oversized source.";
        // A draft is written by whoever may write the draft folder, so it may not take a type over by itself: what a
        // model can write must be something a mount names (docs/25 §5). A live script is in effect because a mount says
        // so, not because it sits in a folder — so it is manual for the same reason.
        if (string.Equals(package.Origin, "draft", StringComparison.Ordinal) && m.Mount == MountMode.Default)
            return "A draft must be mounted by hand: it cannot attach itself to a type.";
        if (string.Equals(package.Origin, "live", StringComparison.Ordinal) && m.Mount == MountMode.Default)
            return "A live script must stay manual: it is in effect because a mount says so, not because of its folder.";
        return null;
    }

    /// <summary>Every instance the graph knows, mounted or not: exactly what a rule may name as its target.</summary>
    public IReadOnlyList<Address> Instances
    {
        get
        {
            _gate.Wait();
            try { return Targets().Select(target => target.Address).ToArray(); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Every catalogued script, with the library it came from and whether it is currently allowed to run.</summary>
    public IReadOnlyList<(ScriptPackage Package, bool Enabled)> Scripts
    {
        get
        {
            _gate.Wait();
            try { return _packages.Values.Select(package => (package, IsEnabled(package))).ToArray(); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>What the last load refused, and why. Empty when everything offered was usable.</summary>
    public IReadOnlyList<ScriptIssue> RejectedScripts
    {
        get { _gate.Wait(); try { return _rejected.ToArray(); } finally { _gate.Release(); } }
    }

    /// <summary>Named after the package a script arrived in; the operator's own tree is simply "user".</summary>
    private static string LibraryOf(ScriptPackage package) =>
        string.IsNullOrWhiteSpace(package.Origin) ? "user" : package.Origin;

    /// <summary>
    /// The key a switch is stored under. <c>library:</c> and <c>script:</c> are this store's own encoding of two
    /// kinds, not a rule about what a name means: when the caller gives a bare name, the catalog says which kind it
    /// is — a name that a script answers to is a script, otherwise it is a library. Nothing is inferred from how the
    /// name is spelled.
    /// </summary>
    private string SwitchKey(string target)
    {
        if (target.StartsWith(LibraryPrefix, StringComparison.Ordinal) || target.StartsWith(ScriptPrefix, StringComparison.Ordinal))
            return target;
        if (_packages.ContainsKey(target)) return ScriptPrefix + target;
        if (_packages.Values.Select(LibraryOf).Contains(target, StringComparer.Ordinal)) return LibraryPrefix + target;
        // Unknown: report it as a script so the caller gets "nothing matches" rather than a guess that stuck.
        return ScriptPrefix + target;
    }

    private const string LibraryPrefix = "library:";
    private const string ScriptPrefix = "script:";

    private bool IsEnabled(ScriptPackage package) =>
        !_disabled.Contains(ScriptPrefix + package.Manifest.Id) && !_disabled.Contains(LibraryPrefix + LibraryOf(package));

    public async ValueTask<Result> SetScriptEnabledAsync(string target, bool enabled, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target)) return Result.Failure(new(ErrorCode.Invalid, "Which script or library?"));
        var key = SwitchKey(target.Trim());
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var known = key.StartsWith(LibraryPrefix, StringComparison.Ordinal)
                ? _packages.Values.Select(LibraryOf).Distinct(StringComparer.Ordinal).Contains(key[LibraryPrefix.Length..])
                : _packages.ContainsKey(key[ScriptPrefix.Length..]);
            if (!known) return Result.Failure(new(ErrorCode.Invalid, $"Nothing in the catalog matches '{target}'."));

            var changed = enabled ? _disabled.Remove(key) : _disabled.Add(key);
            var stored = changed ? await PersistDisabledAsync(cancellationToken).ConfigureAwait(false) : Result.Success();
            // Off means unmounted; on means the defaults apply again. Rules are kept either way, so switching a
            // library back on restores exactly what was there.
            if (_loaded) RebuildMounts();
            return stored.Ok
                ? Result.Success()
                : Result.Failure(new(ErrorCode.Unavailable,
                    $"{target} was switched {(enabled ? "on" : "off")} in memory but the switch could not be persisted; a restart will undo it.",
                    stored.Error?.Message));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Call with the gate held: bundle defaults for whatever is enabled, then every rule.</summary>
    private void RebuildMounts()
    {
        _mounted.Clear();
        // Two sources, one list: bundle scripts follow their package, rules follow a durable intent.
        foreach (var package in _packages.Values.Where(package => package.Manifest.Mount == MountMode.Default && IsEnabled(package)))
        foreach (var target in Targets())
            if (IsCompatible(package.Manifest.TargetType, target)) _mounted.Add(new(package, target.Address));
        foreach (var target in Targets()) ApplyRulesTo(target);
    }

    private async ValueTask<Result> PersistDisabledAsync(CancellationToken cancellationToken)
    {
        if (_state is null) return Result.Success();
        if (_disabled.Count == 0) return await _state.DeleteAsync(DisabledScope, DisabledKeyName, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(_disabled.Order(StringComparer.Ordinal).ToArray(), JsonOptions);
        return await _state.WriteAsync(DisabledScope, DisabledKeyName, json, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RestoreDisabledAsync(CancellationToken cancellationToken)
    {
        if (_state is null) return;
        var stored = await _state.ReadAsync(DisabledScope, DisabledKeyName, cancellationToken).ConfigureAwait(false);
        if (stored.Error is { } readError)
        {
            _report.Note("graph", $"script switches could not be read ({readError.Message}); everything stays on");
            return;
        }
        if (stored.Value is null) return;
        try
        {
            foreach (var key in JsonSerializer.Deserialize<string[]>(stored.Value, JsonOptions) ?? []) _disabled.Add(key);
        }
        catch (JsonException)
        {
            _report.Note("graph", "stored script switches were unreadable; everything stays on");
            return;
        }
        if (_disabled.Count > 0)
            _report.Note("graph", $"{_disabled.Count} script switch(es) off: {string.Join(", ", _disabled.Order(StringComparer.Ordinal))}");
    }

    private void ReportRestore()
    {
        var pending = _rules.Count(rule => !Targets().Any(target => Resolves(rule, target)));
        var fromRules = _mounted.Count(m => m.RuleId is not null);
        _report.Note("graph", $"{_rules.Count} mount rule(s) → {fromRules} mount(s); {pending} rule(s) waiting for their target; " +
                              $"{_mounted.Count - fromRules} bundle mount(s)");
    }

    private async ValueTask<Result> RestoreRulesAsync(CancellationToken cancellationToken)
    {
        if (_rulesRestored) return Result.Success();
        if (_state is null) { _rulesRestored = true; return Result.Success(); }

        var read = await _state.ReadAsync(StateScope, StateKey, cancellationToken).ConfigureAwait(false);
        if (read.Error is { } readError) return Result.Failure(readError);
        if (read.Value is null) { _rulesRestored = true; _report.Note("graph", "no mount rules stored"); return Result.Success(); }

        StoredRules? stored;
        try { stored = JsonSerializer.Deserialize<StoredRules>(read.Value, RuleJson); }
        catch (JsonException ex)
        {
            // Keep the evidence and start with none: refusing to boot over one state file helps nobody, and
            // pretending it parsed would mount things nobody asked for.
            var keeper = StateName.Encode("unreadable-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await _state.WriteAsync(StateScope, keeper, read.Value, CancellationToken.None).ConfigureAwait(false);
            await _state.DeleteAsync(StateScope, StateKey, CancellationToken.None).ConfigureAwait(false);
            _rulesRestored = true;
            _report.Note("graph", $"stored mount rules were unreadable ({ex.Message}); kept as {StateScope}/{keeper}, started with none");
            return Result.Success();
        }

        var rejected = 0;
        foreach (var rule in stored?.Rules ?? [])
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.ScriptId) ||
                rule.Scope is null || CheckScope(rule.Scope) is not null || rule.Budget is < 1)
            {
                rejected++;
                continue;
            }
            if (_rules.Any(existing => existing.Id == rule.Id)) { rejected++; continue; }
            _rules.Add(rule);
        }
        _rulesRestored = true;
        if (rejected > 0) _report.Note("graph", $"{rejected} stored mount rule(s) were rejected as malformed");
        return Result.Success();
    }

    /// <summary>
    /// Takes one dispatch out of a mount's budget. Called with the gate held, before the handler runs.
    /// An exhausted mount is gone immediately; an exhausted instance rule is deleted, while a broader rule
    /// keeps a zero for this target so a restart does not silently resurrect it here.
    /// </summary>
    private void Spend(MountedScript mount, Address target)
    {
        var left = (mount.Remaining ?? 1) - 1;
        var index = _mounted.FindIndex(m => m.Package.Manifest.Id == mount.Package.Manifest.Id &&
                                            m.Target == target && m.RuleId == mount.RuleId);
        if (index >= 0) _mounted[index] = mount with { Remaining = left };

        var ruleIndex = mount.RuleId is null ? -1 : _rules.FindIndex(rule => rule.Id == mount.RuleId);
        if (ruleIndex >= 0) _rules[ruleIndex] = _rules[ruleIndex].WithRemaining(target, left);
        if (left > 0) return;

        _mounted.RemoveAll(m => m.Package.Manifest.Id == mount.Package.Manifest.Id &&
                                 m.Target == target && m.RuleId == mount.RuleId);
        if (ruleIndex >= 0 && _rules[ruleIndex].Scope.Kind == MountScopeKind.Instance) _rules.RemoveAt(ruleIndex);
    }

    /// <summary>
    /// The state namespace a script sees: per target by default (two channels are two conversations), or one
    /// namespace per rule when the rule says so. Bundle mounts have no rule, so they are always per target.
    /// Keying the per-target case by script and target — not by rule id — means unmounting and remounting a
    /// script does not wipe what it remembered.
    /// </summary>
    private string? StateScopeFor(MountedScript mount)
    {
        if (_state is null) return null;
        var rule = mount.RuleId is null ? null : _rules.FirstOrDefault(existing => existing.Id == mount.RuleId);
        if (rule is not null && rule.StateScope == ScriptStateScope.Shared) return "script." + StateName.Encode(rule.Id);
        return "script." + StateName.Encode(mount.Package.Manifest.Id) + "." + StateName.Encode(mount.Target.ToString());
    }

    private async ValueTask<Result> PersistRulesAsync(CancellationToken cancellationToken)
    {
        if (_state is null) return Result.Success();
        var json = JsonSerializer.Serialize(new StoredRules(1, _rules.ToArray()), RuleJson);
        return await _state.WriteAsync(StateScope, StateKey, json, cancellationToken).ConfigureAwait(false);
    }

    private sealed record StoredRules(int Schema, IReadOnlyList<MountRule> Rules);

    public async ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded) return Result.Failure(new(ErrorCode.Unavailable, "Graph scripts are not loaded."));
            var resolved = TargetOf(target);
            if (resolved is not { } mountTarget || !_types.IsConcrete(mountTarget.TypeId))
                return Result.Failure(new(ErrorCode.Invalid, $"Mount target must be a concrete module instance: {target}"));
            _drainingTargets.Remove(target);
            foreach (var package in _packages.Values.Where(p => p.Manifest.Mount == MountMode.Default && IsEnabled(p) && IsCompatible(p.Manifest.TargetType, mountTarget)))
                if (!_mounted.Any(m => m.Package.Manifest.Id == package.Manifest.Id && m.Target == target)) _mounted.Add(new(package, target));
            // A target that appears now is also a target that some rule was already waiting for.
            var beforeRules = _mounted.Count(m => m.RuleId is not null);
            ApplyRulesTo(mountTarget);
            var applied = _mounted.Count(m => m.RuleId is not null) - beforeRules;
            if (applied > 0) _report.Note("graph", $"{applied} waiting rule(s) applied to {target}");
            return Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken)
    {
        Task? drained = null;
        List<MountedScript> removed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _drainingTargets.Add(target);
            removed = _mounted.Where(m => m.Target == target).ToList();
            _mounted.RemoveAll(m => m.Target == target);
            if (_activeDispatches.TryGetValue(target, out var active) && active > 0)
            {
                if (!_dispatchDrained.TryGetValue(target, out var completion))
                    _dispatchDrained.Add(target, completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                drained = completion.Task;
            }
        }
        finally { _gate.Release(); }

        if (drained is not null)
        {
            try { await drained.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    _drainingTargets.Remove(target);
                    foreach (var mount in removed)
                        if (!_mounted.Any(existing => existing.Package.Manifest.Id == mount.Package.Manifest.Id && existing.Target == target))
                            _mounted.Add(mount);
                }
                finally { _gate.Release(); }
                throw;
            }
        }
        return Result.Success();
    }

    public async ValueTask<Result> ForceDetachAsync(Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _drainingTargets.Add(target);
            _mounted.RemoveAll(m => m.Target == target);
            return Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> MountAsync(string scriptId, Address target, CancellationToken cancellationToken,
        AccessLevel callerLevel = AccessLevel.Full)
    {
        var added = await AddRuleAsync(new MountRequest(scriptId, MountScope.ForInstance(target), CallerLevel: callerLevel),
            cancellationToken).ConfigureAwait(false);
        return added.Ok ? Result.Success() : Result.Failure(added.Error!);
    }

    public async ValueTask<Result> UnmountAsync(string scriptId, Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = _mounted.RemoveAll(m => m.Package.Manifest.Id == scriptId && m.Target == target);
            // An instance-scoped rule for this exact pair has no meaning once its mount is gone; a type or
            // module rule is broader than one target, so it stays and the caller must remove it by id.
            var rulesRemoved = _rules.RemoveAll(rule => rule.ScriptId == scriptId &&
                rule.Scope.Kind == MountScopeKind.Instance &&
                Address.TryParse(rule.Scope.Selector, out var named) && named == target);
            if (rulesRemoved > 0) await PersistRulesAsync(cancellationToken).ConfigureAwait(false);
            return removed == 0 && rulesRemoved == 0
                ? Result.Failure(new(ErrorCode.Invalid, "Mount not found. A type- or module-scoped rule is removed by rule id."))
                : Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> DispatchAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
    {
        var target = routedEvent.Target ?? routedEvent.Source;
        var dispatchEntered = false;
        // Fan-out needs one common parent, otherwise N handlers of the same event look like N
        // unrelated lines. Inside a pump the intake record already is that parent; a direct
        // DispatchAsync caller gets an explicit event record instead.
        var ambient = SignalScope.CurrentId;
        var eventRecordId = ambient ?? Guid.NewGuid();
        IDisposable? eventScope = null;
        if (ambient is null)
        {
            _signalLog.Write(new()
            {
                Id = eventRecordId,
                ParentId = null,
                Group = "event",
                Name = routedEvent.Name,
                StepId = routedEvent.EventId,
                From = routedEvent.Source.ToString(),
                To = routedEvent.Target?.ToString() ?? "",
                Mechanism = "event",
                Outcome = "ok"
            });
            eventScope = SignalScope.Enter(eventRecordId, routedEvent.EventId);
        }
        try
        {
            MountedScript[] handlers;
            string?[] stateScopes;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_drainingTargets.Contains(target))
                    return Result.Failure(new(ErrorCode.Unavailable, $"Graph target is draining: {target}", Retryable: true));
                _activeDispatches[target] = _activeDispatches.GetValueOrDefault(target) + 1;
                dispatchEntered = true;
                handlers = _mounted.Where(m => m.Target == target && m.Package.Manifest.Events.Contains(routedEvent.Name, StringComparer.Ordinal)).OrderBy(m => m.Package.Manifest.Id, StringComparer.Ordinal).ToArray();
                // Namespaces are resolved against the rules while the gate is held: the execution loop runs
                // outside it, and a shared namespace must not change shape halfway through a fan-out.
                stateScopes = handlers.Select(StateScopeFor).ToArray();
                // The budget is spent before any handler runs. A crash mid-handler then costs one unused
                // dispatch at worst; the alternative is running a "once" script twice.
                var budgeted = handlers.Where(handler => handler.Remaining is not null).ToArray();
                if (budgeted.Length > 0)
                {
                    foreach (var handler in budgeted) Spend(handler, target);
                    var stored = await PersistRulesAsync(cancellationToken).ConfigureAwait(false);
                    if (!stored.Ok)
                        _signalLog.Write(new()
                        {
                            Group = "graph", Name = "mount.budget", StepId = routedEvent.EventId,
                            From = target.ToString(), Mechanism = "state", Outcome = "failed", Level = SignalLevel.Warn,
                            Detail = new Dictionary<string, string> { ["message"] = stored.Error?.Message ?? "" }
                        });
                }
            }
            finally { _gate.Release(); }
            Error? firstFailure = null;
            for (var handlerIndex = 0; handlerIndex < handlers.Length; handlerIndex++)
            {
                var mount = handlers[handlerIndex];
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(mount.Package.Manifest.TimeoutMilliseconds));
                // One record per traversed edge — this loop is the only place an edge is followed.
                var edgeId = EdgeId(mount, routedEvent.Name);
                var invocationId = Guid.NewGuid();
                var parentId = SignalScope.CurrentId;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                Result result;
                using (SignalScope.Enter(invocationId, routedEvent.EventId))
                {
                    var api = new ScriptApi(Adapters, Channels, _audit, _signalLog, Models, Sessions, mount, routedEvent,
                        stateScopes[handlerIndex], _state, _plane, deadline.Token, _media);
                    result = await _executor.ExecuteAsync(mount.Package, mount.Target, routedEvent, api, deadline.Token).ConfigureAwait(false);
                }
                // The record settles after the handler returns, but its id existed during it, so
                // everything the handler logged or invoked already points here.
                _signalLog.Write(new()
                {
                    Id = invocationId,
                    ParentId = parentId,
                    Group = "dispatch",
                    Name = edgeId,
                    StepId = routedEvent.EventId,
                    From = routedEvent.Source.ToString(),
                    To = mount.Target.ToString(),
                    Mechanism = "emit",
                    Outcome = result.Ok ? "ok" : deadline.IsCancellationRequested ? "timeout" : "failed",
                    DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Level = result.Ok ? SignalLevel.Info : SignalLevel.Warn,
                    Detail = result.Ok ? null : new Dictionary<string, string>
                    {
                        ["event"] = routedEvent.Name,
                        ["script"] = mount.Package.Manifest.Id,
                        ["code"] = result.Error?.Code.ToString() ?? "unknown",
                        ["message"] = result.Error?.Message ?? "",
                        // Why it failed is the only thing anyone reading this record wants, and it used to be dropped
                        // here: a script that threw said "Script execution failed." and nothing else, which is not a
                        // record of anything. Same reason the host's budget is written down beside it.
                        ["detail"] = result.Error?.Detail ?? "",
                        ["timeoutMs"] = mount.Package.Manifest.TimeoutMilliseconds.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                    }
                });
                if (!result.Ok) firstFailure ??= result.Error;
            }
            return firstFailure is null ? Result.Success() : Result.Failure(firstFailure);
        }
        finally
        {
            eventScope?.Dispose();
            if (dispatchEntered) await EndDispatchAsync(target).ConfigureAwait(false);
        }
    }

    private async ValueTask EndDispatchAsync(Address target)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_activeDispatches.TryGetValue(target, out var active)) return;
            if (active > 1)
            {
                _activeDispatches[target] = active - 1;
                return;
            }
            _activeDispatches.Remove(target);
            if (_dispatchDrained.Remove(target, out var completion)) completion.TrySetResult();
        }
        finally { _gate.Release(); }
    }

    private bool IsCompatible(string targetType, Target target) => _types.IsAssignableTo(target.TypeId, targetType);
    /// <summary>Stable edge identity derived from assembly-time data, not runtime names.</summary>
    internal static string EdgeId(MountedScript mount, string eventName) =>
        $"{mount.Package.Manifest.Id}@{mount.Target}#{eventName}";    /// <summary>The target as the address book describes it, or null when nothing answering that name can be mounted
    /// on. The type comes from the catalog row; reading it out of the address is not an option.
    private Target? TargetOf(Address address)
    {
        var id = IdOf(address);
        var row = _catalog.Describe(id);
        if (row is null || row.Role is not ModuleRole.Instance) return null;
        return _catalog.Find(id) is IAddressed addressed ? new Target(addressed.Address, row.TypeId) : null;
    }


    /// <summary>
    /// A send the Adapter itself refused, carried out of <c>SendAsync</c> as an exception because that path is called
    /// from scripts, which have no <see cref="Result"/> to hand back.
    ///
    /// It exists so the reason survives: <c>speak</c>'s generic catch turns anything unexpected into "speak failed.",
    /// which is right for a null reference and wrong for "这个适配器发不出图片" — the model can act on the second and
    /// cannot act on the first. When an Adapter says why, its own wording is what the model reads.
    /// </summary>
    private sealed class AdapterSendException(Error error) : Exception(error.Message)
    {
        public Error Error { get; } = error;
    }

    private sealed class ScriptApi(IAdapterManager adapters, IChannelManager channels, IAuditSink audit,
        ISignalLog signalLog, IModelManager? models, ISessionManager? sessions, MountedScript mount, RoutedEvent current,
        string? stateScope, IStateStore? state, IControlPlane? plane, CancellationToken ct, IMediaStore? media = null) : IScriptApi
    {
        /// <summary>
        /// The action table, as this script: its own id, the capabilities its manifest declared, and the level its folder
        /// gave it. The plane does the authorizing, so what a script may reach cannot drift from what the model or a
        /// person may reach — one table, one comparison, one audit.
        /// </summary>
        private Actor Self => new(mount.Package.Manifest.Id, ActorKind.Script,
            mount.Package.Manifest.Capabilities, mount.Package.Level);

        public Task<string> ListActionsAsync(CancellationToken cancellationToken)
        {
            var missing = MissingPlane();
            if (missing is not null) return Task.FromResult(missing);
            cancellationToken.ThrowIfCancellationRequested();
            // The same question a listing tool asks, answered for this caller: what exists, what it needs, and what is
            // out of reach is named rather than hidden — a script that cannot see the table cannot ask about it.
            var actions = plane!.Catalog.Select(action => new
            {
                id = action.Id,
                capability = action.Capability,
                level = AccessLevels.Describe(action.RequiredLevel),
                reachable = Self.Allows(action.Capability) && Self.Level >= action.RequiredLevel,
                mutating = action.Mutating,
                summary = action.Summary,
                args = action.ParamsJson
            }).ToArray();
            return Task.FromResult(JsonSerializer.Serialize(new { ok = true, level = AccessLevels.Describe(Self.Level), actions }, JsonOptions));
        }

        public async Task<string> CallActionAsync(string action, string argumentsJson, CancellationToken cancellationToken)
        {
            var missing = MissingPlane();
            if (missing is not null) return missing;
            if (string.IsNullOrWhiteSpace(action))
                return JsonSerializer.Serialize(new { ok = false, error = new { code = "invalid", message = "actions.call needs an id." } }, JsonOptions);
            JsonElement arguments;
            try
            {
                arguments = string.IsNullOrWhiteSpace(argumentsJson)
                    ? JsonSerializer.SerializeToElement(new { })
                    : JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(new { ok = false, error = new { code = "invalid", message = "arguments must be JSON.", detail = ex.Message } }, JsonOptions);
            }

            var called = await plane!.InvokeAsync(Self, action.Trim(), arguments, cancellationToken).ConfigureAwait(false);
            // The refusal reaches the script as data, not as a thrown exception: a script that can read "denied, and why"
            // can decide what to do next, which is the whole reason to give it the door at all. The code is a word, not
            // the number an enum would serialize to — the reader is a script somebody wrote, or a model reading one.
            return called.Ok
                ? JsonSerializer.Serialize(new { ok = true, result = called.Value }, JsonOptions)
                : JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = new
                    {
                        code = called.Error?.Code.ToString().ToLowerInvariant(),
                        message = called.Error?.Message,
                        detail = called.Error?.Detail
                    }
                }, JsonOptions);
        }

        private string? MissingPlane() => plane is null
            ? JsonSerializer.Serialize(new { ok = false, error = new { code = "unavailable", message = "This host has no control plane." } }, JsonOptions)
            : null;

        /// <summary>
        /// Script state: the component owns the meaning, the host owns where it lives. Keys are encoded, not
        /// trusted, and the size is capped — this is memory for a mounted script, not an artifact store.
        /// </summary>
        public async Task<string> ReadStateAsync(string key, CancellationToken cancellationToken)
        {
            var read = await State().ReadAsync(Scope(), StateKey(key), cancellationToken).ConfigureAwait(false);
            // A missing key comes back as Ok with a null value, and Result<string?>.Ok is false for null:
            // "absent" has to be read off Error, or every unwritten key looks like a failure.
            if (read.Error is { } error) throw new InvalidOperationException(error.Message);
            return read.Value ?? "";
        }

        public async Task SetStateAsync(string key, string json, CancellationToken cancellationToken)
        {
            if (json is null) throw new ArgumentException("Script state must be JSON text.");
            if (json.Length > MaxStateBytes) throw new ArgumentException($"Script state value exceeds {MaxStateBytes} characters.");
            ct.ThrowIfCancellationRequested();
            var written = await State().WriteAsync(Scope(), StateKey(key), json, cancellationToken).ConfigureAwait(false);
            if (!written.Ok) throw new InvalidOperationException(written.Error?.Message ?? "Script state write failed.");
        }

        public async Task DeleteStateAsync(string key, CancellationToken cancellationToken)
        {
            var removed = await State().DeleteAsync(Scope(), StateKey(key), cancellationToken).ConfigureAwait(false);
            if (!removed.Ok) throw new InvalidOperationException(removed.Error?.Message ?? "Script state delete failed.");
        }

        private IStateStore State() => state ?? throw new InvalidOperationException("Script state is unavailable: the host has no state store.");

        private string Scope() => stateScope ?? throw new InvalidOperationException("Script state is unavailable: this mount has no state namespace.");

        private static string StateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Script state needs a key.");
            if (key.Length > 512) throw new ArgumentException("Script state key is too long.");
            return StateName.Encode(key);
        }

        private const int MaxStateBytes = 64 * 1024;

        public Task<string> RunSessionAsync(string sessionSpecJson, string userInput, CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.SessionRun, () => RunSessionCoreAsync(sessionSpecJson, userInput, cancellationToken));

        public Task<string> OpenSessionAsync(string optionsJson, CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.SessionOpen, () => OpenSessionCoreAsync(optionsJson, cancellationToken));

        public Task<string> MessageSessionAsync(string optionsJson, CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.SessionMessage, () => MessageSessionCoreAsync(optionsJson, cancellationToken));

        public Task<string> ActivateSessionAsync(string sessionSpecJson, CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.SessionActivate, () => ActivateSessionCoreAsync(sessionSpecJson, cancellationToken));

        /// <summary>
        /// The two-phase activation, first half: wake the Session, carry nothing. Whatever the Channel has
        /// for it is pulled later, when the Session assembles the request (see ChannelDeltaContributor), so
        /// the model sees everything up to "now" instead of only the message that happened to trigger this.
        /// </summary>
        private async Task<string> ActivateSessionCoreAsync(string sessionSpecJson, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.SessionActivate, cancellationToken).ConfigureAwait(false);
            if (sessions is null) throw new InvalidOperationException("Session manager is unavailable.");
            if (current.Name != EventNames.ChannelMessageAccepted || current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.SessionActivate, "Not the source Channel message event.", cancellationToken).ConfigureAwait(false);
            var channelMessage = Parse<ChannelMessage>(current.PayloadJson);
            if (channelMessage.Channel != current.Source || channelMessage.MessageId != current.EventId ||
                !channels.IsBound(current.Source, channelMessage.Binding.Adapter, channelMessage.Binding.PlatformConversationId))
                await DenyAsync(ScriptCapabilities.SessionActivate, "Channel message does not match routed event.", cancellationToken).ConfigureAwait(false);

            var spec = await SessionSpecFromScriptAsync(sessionSpecJson, ScriptCapabilities.SessionActivate, cancellationToken).ConfigureAwait(false);
            // The Channel may name the profile it wants; a spec that names none inherits it. Leftovers fall to
            // the host's own resolution (SessionManager), which says out loud what it picked.
            if (string.IsNullOrWhiteSpace(spec.ModelProfileId) &&
                channels.Get(channelMessage.Channel).Value is { ModelProfile: { Length: > 0 } channelProfile })
                spec = spec with { ModelProfileId = channelProfile };

            // C-01/C-20: the Channel a Session serves is data on the Session, so it is asked for, not hashed into a
            // name. A Channel that no Session serves yet gets a Session named after its type.
            var opened = await sessions.OpenForChannelAsync(channelMessage.Channel.ToString(), spec, cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionActivate, opened.Ok ? "allowed" : "failed",
                opened.Ok && opened.Value is not null ? opened.Value.Id.Value : channelMessage.Channel.ToString()),
                cancellationToken).ConfigureAwait(false);

            // The trigger message is not handed over here on purpose: it is already in the Channel's history,
            // and the delta pull will include it. Passing it twice is how a conversation gets answered twice.
            var input = new SessionTurnInput([], channelMessage.Channel, channelMessage.Binding);
            // The spec is what an absent Session becomes: one that already exists keeps the one it was created with.
            var result = opened.Ok && opened.Value is not null
                ? await opened.Value.RunTurnAsync(current.EventId, input,
                    new TurnToolbox(new SpeakTool(this, current, mount, channels, audit)), cancellationToken).ConfigureAwait(false)
                : Result<SessionTurnResult>.Failure(opened.Error!);
            if (!result.Ok || result.Value is null)
            {
                await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                    mount.Target, ScriptCapabilities.SessionActivate, "failed", result.Error?.Message), cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = false, error = result.Error }, JsonOptions);
            }
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionActivate, "completed", result.Value.SessionId), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = true, result = result.Value }, JsonOptions);
        }

        /// <summary>
        /// The Session serving a Channel, created when there is none.
        ///
        /// Unlike <see cref="ActivateSessionCoreAsync"/> this asks nothing of the current event: a rule coming due is a
        /// perfectly good reason to decide that a conversation should have somebody behind it. What it does <b>not</b>
        /// loosen is the level: the spec still cannot name a level above the folder this script came from, because the
        /// Session it makes outlives the script and every rule behind it.
        /// </summary>
        private async Task<string> OpenSessionCoreAsync(string optionsJson, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.SessionOpen, cancellationToken).ConfigureAwait(false);
            if (sessions is null) throw new InvalidOperationException("Session manager is unavailable.");

            var asked = TryReadOptions(optionsJson, out var options, out var bad);
            if (!asked) return bad!;

            // The Channel: named, or the one this script is mounted on. A Channel that does not exist yet is accepted —
            // the Session is data saying "I serve that conversation", and the conversation may arrive later.
            var channelKey = TextOf(options, "channel") ?? mount.Target.ToString();
            var spec = await SpecFromOptionsAsync(options, ScriptCapabilities.SessionOpen, cancellationToken)
                .ConfigureAwait(false);

            var opened = TextOf(options, "id") is { } id
                ? await sessions.OpenAsync(id, spec, cancellationToken).ConfigureAwait(false)
                : await sessions.OpenForChannelAsync(channelKey, spec, cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionOpen, opened.Ok ? "allowed" : "failed",
                opened.Ok && opened.Value is not null ? opened.Value.Id.Value : channelKey), cancellationToken)
                .ConfigureAwait(false);
            if (!opened.Ok || opened.Value is null)
                return ErrorJson(opened.Error?.Code ?? ErrorCode.Unavailable, opened.Error?.Message ?? "Session could not be opened.");

            var session = opened.Value;
            object? spoke = null;
            if (TextOf(options, "text") is { } text)
            {
                var delivered = await sessions.DeliverAsync(session.Id.Value, text, Sender, mount.Package.Level,
                    WakeOf(options, otherwise: true), cancellationToken).ConfigureAwait(false);
                spoke = delivered.Ok
                    ? new { ok = true, step = delivered.Value!.StepNumber, text = delivered.Value.Text }
                    : new { ok = false, error = delivered.Error?.Message };
            }
            return JsonSerializer.Serialize(new
            {
                ok = true,
                session = new
                {
                    id = session.Id.Value,
                    channel = session.State.Channel,
                    level = AccessLevels.Describe(session.Spec.Level)
                },
                // Said out loud rather than assumed: a Session can exist for a conversation that has no Channel yet, and
                // then nothing it says can leave. A script that reads this can tell "I prepared it" from "I said it".
                channelState = ChannelStateOf(channelKey),
                spoke
            }, JsonOptions);
        }

        /// <summary>
        /// Hands one Session a message from this script.
        ///
        /// The only rule is that the message is honestly labelled. Level is not consulted — a draft may ask a Full
        /// Session for something it cannot do itself, which is the whole reason this door exists — and the receiving
        /// turn is told the sender, the sender's level, and that a shallower request cannot require anything of it.
        /// </summary>
        private async Task<string> MessageSessionCoreAsync(string optionsJson, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.SessionMessage, cancellationToken).ConfigureAwait(false);
            if (sessions is null) throw new InvalidOperationException("Session manager is unavailable.");

            var asked = TryReadOptions(optionsJson, out var options, out var bad);
            if (!asked) return bad!;
            if (TextOf(options, "text") is not { } text)
                return ErrorJson(ErrorCode.Invalid, "session.message needs 'text'.");

            var target = TextOf(options, "id");
            if (target is null)
            {
                if (TextOf(options, "channel") is not { } channelKey)
                    return ErrorJson(ErrorCode.Invalid, "session.message needs 'id' or 'channel'.");
                var listed = await sessions.ListAsync(cancellationToken).ConfigureAwait(false);
                if (!listed.Ok || listed.Value is null)
                    return ErrorJson(listed.Error?.Code ?? ErrorCode.Unavailable, listed.Error?.Message ?? "Sessions could not be listed.");
                var found = listed.Value.FirstOrDefault(summary =>
                    string.Equals(summary.Channel, channelKey, StringComparison.Ordinal));
                if (found is null)
                    return ErrorJson(ErrorCode.Invalid,
                        $"No Session serves {channelKey}. Use ctx.session.open to make one, or name a Session id.");
                target = found.Id;
            }

            var wake = WakeOf(options, otherwise: false);
            var delivered = await sessions.DeliverAsync(target, text, Sender, mount.Package.Level, wake, cancellationToken)
                .ConfigureAwait(false);
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionMessage, delivered.Ok ? "allowed" : "failed", target),
                cancellationToken).ConfigureAwait(false);
            if (!delivered.Ok)
                // A busy Session is retryable by contract; passing the code through keeps "ask me again" apart from
                // "that will never work", which a script can act on.
                return ErrorJson(delivered.Error!.Code, delivered.Error.Message);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                id = target,
                from = Sender,
                fromLevel = AccessLevels.Describe(mount.Package.Level),
                wake,
                step = delivered.Value!.StepNumber,
                text = delivered.Value.Text
            }, JsonOptions);
        }

        /// <summary>How this script is named in a relay: the manifest id and the mount it is acting from.</summary>
        private string Sender => $"script:{mount.Package.Manifest.Id}@{mount.Target}";

        /// <summary>
        /// The spec an options object asks for, or the one a Session made by a script should have by default: answer this
        /// conversation, with whatever profile the host resolves. Run through the level check either way.
        /// </summary>
        private async Task<SessionSpec> SpecFromOptionsAsync(JsonElement options, string capability,
            CancellationToken cancellationToken)
        {
            if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty("spec", out var specJson) &&
                specJson.ValueKind == JsonValueKind.Object)
                return await SessionSpecFromScriptAsync(specJson.GetRawText(), capability, cancellationToken)
                    .ConfigureAwait(false);
            return new SessionSpec("", "回答这个频道里的消息。", "1");
        }

        /// <summary>What a Channel key is right now: known to the host, and running with a binding or not.</summary>
        private object ChannelStateOf(string channelKey)
        {
            ChannelInfo? info = Address.TryParse(channelKey, out var address) &&
                                channels.Get(address) is { Ok: true, Value: { } found }
                ? found
                : null;
            return new
            {
                key = channelKey,
                known = info is not null,
                running = info?.Running == true,
                bound = info?.Bindings.Count > 0
            };
        }

        private static bool TryReadOptions(string optionsJson, out JsonElement options, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(optionsJson))
            {
                options = JsonSerializer.SerializeToElement(new { });
                return true;
            }
            try
            {
                options = JsonSerializer.Deserialize<JsonElement>(optionsJson);
                return true;
            }
            catch (JsonException ex)
            {
                options = default;
                error = ErrorJson(ErrorCode.Invalid, "options must be JSON.", ex.Message);
                return false;
            }
        }

        private static string? TextOf(JsonElement options, string name) =>
            options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString() is { Length: > 0 } text ? text : null
                : null;

        private static bool WakeOf(JsonElement options, bool otherwise) =>
            options.ValueKind == JsonValueKind.Object && options.TryGetProperty("wake", out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : otherwise;

        private static string ErrorJson(ErrorCode code, string message, string? detail = null) =>
            JsonSerializer.Serialize(new
            {
                ok = false,
                error = new { code = JsonNamingPolicy.CamelCase.ConvertName(code.ToString()), message, detail }
            }, JsonOptions);

        /// <summary>
        /// The spec a script hands over, checked against the folder the script came from.
        ///
        /// A spec carries a level, and the level is written onto the Session — which means it is in force for every turn
        /// after this one, long after the script that asked for it is gone. So the question "how deep does this reach"
        /// has to be answered where the script's own depth is already known: a draft the model wrote must not be able to
        /// name the kernel's level for a Session that then does the model's bidding. The folder decides, and it decides
        /// here too — refused rather than quietly lowered, because a script that thinks it made a deep Session and did
        /// not would go on to trust it.
        /// </summary>
        private async Task<SessionSpec> SessionSpecFromScriptAsync(string sessionSpecJson, string capability,
            CancellationToken cancellationToken)
        {
            var spec = Parse<SessionSpec>(sessionSpecJson);
            if (spec.Level <= mount.Package.Level) return spec;
            await DenyAsync(capability,
                $"This script is {AccessLevels.Describe(mount.Package.Level)} and asked for a "
                + $"{AccessLevels.Describe(spec.Level)} Session. A Session is not opened from above the folder the script "
                + "came from: the level would outlive this script and every turn after it.", cancellationToken);
            return spec; // DenyAsync throws; this is here so the compiler sees an answer.
        }

        private async Task<string> RunSessionCoreAsync(string sessionSpecJson, string userInput, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.SessionRun, cancellationToken).ConfigureAwait(false);
            if (sessions is null) throw new InvalidOperationException("Session manager is unavailable.");
            if (current.Name != EventNames.ChannelMessageAccepted || current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.SessionRun, "Not the source Channel message event.", cancellationToken).ConfigureAwait(false);
            var channelMessage = Parse<ChannelMessage>(current.PayloadJson);
            if (channelMessage.Channel != current.Source || channelMessage.MessageId != current.EventId ||
                !channels.IsBound(current.Source, channelMessage.Binding.Adapter, channelMessage.Binding.PlatformConversationId) ||
                !StringComparer.Ordinal.Equals(channelMessage.Text, userInput))
                await DenyAsync(ScriptCapabilities.SessionRun, "Channel message does not match routed event.", cancellationToken).ConfigureAwait(false);
            var spec = await SessionSpecFromScriptAsync(sessionSpecJson, ScriptCapabilities.SessionRun, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(spec.ModelProfileId) &&
                channels.Get(channelMessage.Channel).Value is { ModelProfile: { Length: > 0 } channelProfile })
                spec = spec with { ModelProfileId = channelProfile };
            // Same rule as session.activate: the Session that serves this Channel is asked for, not derived.
            var opened = await sessions.OpenForChannelAsync(channelMessage.Channel.ToString(), spec, cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionRun, opened.Ok ? "allowed" : "failed",
                opened.Ok && opened.Value is not null ? opened.Value.Id.Value : channelMessage.Channel.ToString()),
                cancellationToken).ConfigureAwait(false);
            var input = new SessionTurnInput([new SessionChannelMessage(channelMessage.MessageId.ToString("N"), "user",
                channelMessage.DisplaySender, channelMessage.Text, channelMessage.At, channelMessage.Media)],
                channelMessage.Channel, channelMessage.Binding);
            var result = opened.Ok && opened.Value is not null
                ? await opened.Value.RunTurnAsync(current.EventId, input,
                    new TurnToolbox(new SpeakTool(this, current, mount, channels, audit)), cancellationToken).ConfigureAwait(false)
                : Result<SessionTurnResult>.Failure(opened.Error!);
            if (!result.Ok || result.Value is null)
            {
                await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                    mount.Target, ScriptCapabilities.SessionRun, "failed", result.Error?.Message), cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = false, error = result.Error }, JsonOptions);
            }
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionRun, "completed", result.Value.SessionId), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = true, result = result.Value }, JsonOptions);
        }

        /// <summary>
        /// The tools one turn may use. Built per turn, because "speak" answers a specific Channel message: a tool
        /// bound to that event cannot outlive it, so there is nothing long-lived to register and unregister.
        /// </summary>
        private sealed class TurnToolbox(ISessionTool speak) : ISessionToolbox
        {
            public IReadOnlyList<string> Names { get; } = [SpeakTool.ToolName];

            public Result<ISessionTool> Find(string name) =>
                string.Equals(name, SpeakTool.ToolName, StringComparison.Ordinal)
                    ? Result<ISessionTool>.Success(speak)
                    : Result<ISessionTool>.Failure(new(ErrorCode.Denied, $"No tool named '{name}' in this turn."));
        }

        /// <summary>
        /// Speaking, as a tool. The only thing that reaches the other side: a step that produces text and no tool
        /// call has said nothing, which is exactly why this is a tool and not "whatever the turn returned".
        ///
        /// Every precondition is checked per call rather than once per turn, because a refusal has to reach the
        /// model as something it can read and route around — "you cannot speak here" is recoverable, a turn that
        /// dies is not.
        /// </summary>
        private sealed class SpeakTool(ScriptApi api, RoutedEvent current, MountedScript mount, IChannelManager channels,
            IAuditSink audit) : ISessionTool
        {
            public const string ToolName = "speak";

            public string Name => ToolName;

            public bool EndsTurn => false;

            public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            {
                try { await api.RequireCapabilityAsync(ScriptCapabilities.ChannelReply, cancellationToken).ConfigureAwait(false); }
                catch (UnauthorizedAccessException ex)
                { return Result<SessionToolResult>.Failure(new(ErrorCode.Denied, ex.Message)); }
                if (current.Name != EventNames.ChannelMessageAccepted || current.Source != mount.Target)
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                        "speak requires the source Channel message event."));

                ChannelMessage channelMessage;
                SessionSpeakArguments arguments;
                try
                {
                    channelMessage = Parse<ChannelMessage>(current.PayloadJson);
                    arguments = Parse<SessionSpeakArguments>(call.ArgumentsJson);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                { return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "speak arguments are invalid.", ex.Message)); }

                var binding = channelMessage.Binding;
                if (channelMessage.Channel != current.Source || channelMessage.MessageId != current.EventId ||
                    !channels.IsBound(current.Source, binding.Adapter, binding.PlatformConversationId))
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                        "speak needs the bound Channel's message for this event."));

                // Two shapes, one tool: {text} is the old thing and stays exactly as it was, {blocks} is a layout —
                // words, a mention, a picture, a face, in one message. Both may not be given: a caller that supplies
                // text and blocks has not decided which one is the message.
                var hasBlocks = arguments.Blocks is { Count: > 0 };
                if (hasBlocks && !string.IsNullOrWhiteSpace(arguments.Text))
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                        "speak takes {text} or {blocks}, not both."));
                if (!hasBlocks && string.IsNullOrWhiteSpace(arguments.Text))
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                        "speak needs non-empty text, or blocks to lay out."));

                if (hasBlocks)
                {
                    var built = await api.BuildBlocksAsync(arguments.Blocks!, channelMessage, cancellationToken).ConfigureAwait(false);
                    if (!built.Ok || built.Value is null) return Result<SessionToolResult>.Failure(built.Error!);
                    var layout = built.Value;
                    // No up-front question about what the Adapter can render: it is asked by *sending*, and its answer
                    // is the answer. A sender that checked a capability list first would be a sender that has a model of
                    // every Adapter in it — and a second place for that model to be wrong (IOutboundAdapter.SendAsync).
                    await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                        mount.Target, "session.tool.speak", "allowed", call.Id), cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var sent = await api.DeliverAsync(current.EventId, channelMessage.Channel, binding.Adapter,
                            binding.PlatformConversationId, MessageBlock.Flatten(layout), ChannelReplyModes.Default,
                            channelMessage.PlatformMessageId, cancellationToken, layout).ConfigureAwait(false);
                        // The Session's own view of what it said is the plain-text flattening: the transcript is text,
                        // and what actually appeared as a mention or a picture is the platform's business.
                        return Result<SessionToolResult>.Success(new SessionToolResult(
                            JsonSerializer.Serialize(sent, JsonOptions), MessageBlock.Flatten(layout)));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    // The Adapter's own refusal, in its own words: this is the answer the model needs (see
                    // AdapterSendException). Only genuinely unexpected failures become "speak failed."
                    catch (AdapterSendException ex) { return Result<SessionToolResult>.Failure(ex.Error); }
                    catch (Exception ex)
                    { return Result<SessionToolResult>.Failure(new(ErrorCode.Unavailable, "speak failed.", ex.Message)); }
                }

                var mode = string.IsNullOrWhiteSpace(arguments.Mode) ? ChannelReplyModes.Default : arguments.Mode;
                if (!ChannelReplyModes.IsKnown(mode))
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, $"Unknown reply mode: {mode}"));

                await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                    mount.Target, "session.tool.speak", "allowed", call.Id), cancellationToken).ConfigureAwait(false);
                try
                {
                    // Past the guard above, so this is a sentence: `arguments.Text` may only be null when blocks were
                    // given, and that case returned earlier.
                    var said = arguments.Text!;
                    var parts = await api.DeliverAsync(current.EventId, channelMessage.Channel, binding.Adapter,
                        binding.PlatformConversationId, said, mode, channelMessage.PlatformMessageId, cancellationToken)
                        .ConfigureAwait(false);
                    // SpokenText is how the Session learns what the conversation now has from its own side; without
                    // it a reply sent through a tool would vanish from the model's view of its own words.
                    return Result<SessionToolResult>.Success(new SessionToolResult(
                        JsonSerializer.Serialize(parts, JsonOptions), said));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (AdapterSendException ex) { return Result<SessionToolResult>.Failure(ex.Error); }
                catch (Exception ex)
                { return Result<SessionToolResult>.Failure(new(ErrorCode.Unavailable, "speak failed.", ex.Message)); }
            }
        }

        public Task<string> CompleteModelAsync(string profileId, string requestJson, CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.ModelComplete, () => CompleteModelCoreAsync(profileId, requestJson, cancellationToken));

        private async Task<string> CompleteModelCoreAsync(string profileId, string requestJson, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.ModelComplete, cancellationToken).ConfigureAwait(false);
            if (models is null) throw new InvalidOperationException("Model manager is unavailable.");
            var request = Parse<ModelRequest>(requestJson);
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.ModelComplete, "allowed", profileId), cancellationToken).ConfigureAwait(false);
            var started = await models.CompleteAsync(profileId, request, cancellationToken).ConfigureAwait(false);
            if (!started.Ok || started.Value is null)
            {
                await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                    mount.Target, ScriptCapabilities.ModelComplete, "failed", started.Error?.Message), cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = false, error = started.Error, events = Array.Empty<object>() }, JsonOptions);
            }

            var events = new List<object>();
            var terminalCount = 0;
            var successful = true;
            await foreach (var modelEvent in started.Value.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                switch (modelEvent)
                {
                    case ThinkingDelta thinking: events.Add(new { type = "thinkingDelta", text = thinking.Text }); break;
                    case TextDelta text: events.Add(new { type = "textDelta", text = text.Text }); break;
                    case ToolCallDelta tool: events.Add(new { type = "toolCallDelta", tool.Index, tool.CallId, tool.Name, tool.ArgumentsJsonFragment }); break;
                    case ModelCompleted completed:
                        terminalCount++;
                        events.Add(new { type = "completed", completed.Reason, completed.Usage, completed.ProviderResponseId });
                        break;
                    case ModelFailed failed:
                        terminalCount++;
                        successful = false;
                        events.Add(new { type = "failed", error = failed.Error });
                        break;
                    default: throw new InvalidOperationException("Model provider emitted an unknown event type.");
                }
                if (terminalCount > 1) throw new InvalidOperationException("Model provider emitted more than one terminal event.");
            }
            if (terminalCount != 1) throw new InvalidOperationException("Model provider stream ended without exactly one terminal event.");
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.ModelComplete, successful ? "completed" : "failed", profileId), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = successful, events }, JsonOptions);
        }

        public Task<string> SendSelfAsync(string json, CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.SelfAdapterSend, () => SendSelfCoreAsync(json, cancellationToken));

        private async Task<string> SendSelfCoreAsync(string json, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.SelfAdapterSend, cancellationToken).ConfigureAwait(false);
            if (current.Name != EventNames.AdapterEnvelope || current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.SelfAdapterSend, "Not the source Adapter event.", cancellationToken).ConfigureAwait(false);
            var envelope = Parse<AdapterEnvelope>(current.PayloadJson);
            var message = Parse<OutboundMessage>(json);
            if (envelope.Adapter != current.Source || message.PlatformConversationId != envelope.PlatformConversationId)
                await DenyAsync(ScriptCapabilities.SelfAdapterSend, "Reply destination differs from source conversation.", cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(
                await SendAsync(envelope.EventId, null, current.Source, message, cancellationToken).ConfigureAwait(false), JsonOptions);
        }

        public Task<string> RouteIntoChannelAsync(string json, CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.ChannelRoute, () => RouteIntoChannelCoreAsync(json, cancellationToken));

        /// <summary>
        /// The script owns the policy: if a Channel is already bound to this Adapter conversation,
        /// deliver; otherwise create one from the caller's naming and deliver into it. Creation is
        /// idempotent by binding, so two simultaneous messages cannot mint two Channels.
        /// </summary>
        private async Task<string> RouteIntoChannelCoreAsync(string json, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.ChannelRoute, cancellationToken).ConfigureAwait(false);
            if (current.Name != EventNames.AdapterEnvelope || current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.ChannelRoute, "Not the source Adapter event.", cancellationToken).ConfigureAwait(false);
            var envelope = Parse<AdapterEnvelope>(current.PayloadJson);
            if (envelope.Adapter != current.Source || envelope.EventId != current.EventId)
                await DenyAsync(ScriptCapabilities.ChannelRoute, "Adapter payload does not match routed event.", cancellationToken).ConfigureAwait(false);
            var options = string.IsNullOrWhiteSpace(json) ? new RouteOptions(null, null) : Parse<RouteOptions>(json);
            var channelId = string.IsNullOrWhiteSpace(options.ChannelId)
                ? envelope.PlatformConversationId
                : options.ChannelId!;
            if (channelId.Length is 0 or > 128 || channelId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new InvalidOperationException($"Channel id is not usable: {channelId}");

            var existing = channels.FindBound(envelope.Adapter, envelope.PlatformConversationId);
            var created = false;
            Address channelAddress;
            if (existing.Ok && existing.Value is { } bound)
                channelAddress = bound.Address;
            else
            {
                channelAddress = new Address("channel", "conversation", channelId);
                var aimed = new ChannelBinding(envelope.Adapter, envelope.PlatformConversationId);
                // A Channel under that name may already exist, serving another conversation. Routing says "deliver here"
                // and a conversation belongs to one Channel, so the conversation moves — which is the same operation the
                // channels.connect action performs, not a second implementation of it.
                if (channels.Get(channelAddress).Ok)
                {
                    var moved = await channels.ConnectAsync(channelAddress, aimed, cancellationToken).ConfigureAwait(false);
                    if (!moved.Ok) throw new InvalidOperationException(moved.Error?.Message ?? "Channel could not be re-aimed.");
                    if (!channels.Get(channelAddress).Value!.Running)
                    {
                        var woken = await channels.StartAsync(channelAddress, cancellationToken).ConfigureAwait(false);
                        if (!woken.Ok) throw new InvalidOperationException(woken.Error?.Message ?? "Channel start failed.");
                    }
                }
                else
                {
                    var spec = new ChannelSpec(channelAddress, string.IsNullOrWhiteSpace(options.Name) ? channelId : options.Name!,
                        [aimed], ChannelTypes.Conversation);
                    var minted = await channels.CreateAsync(spec, cancellationToken).ConfigureAwait(false);
                    if (!minted.Ok) throw new InvalidOperationException(minted.Error?.Message ?? "Channel creation failed.");
                    var started = await channels.StartAsync(channelAddress, cancellationToken).ConfigureAwait(false);
                    if (!started.Ok)
                    {
                        await channels.DestroyAsync(channelAddress, CancellationToken.None).ConfigureAwait(false);
                        throw new InvalidOperationException(started.Error?.Message ?? "Channel start failed.");
                    }
                    created = true;
                }
            }

            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id, channelAddress,
                ScriptCapabilities.ChannelRoute, created ? "created" : "allowed"), cancellationToken).ConfigureAwait(false);
            var accepted = await channels.AcceptAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (!accepted.Ok) throw new InvalidOperationException(accepted.Error?.Message ?? "Channel intake failed.");
            if (!channels.IsBound(channelAddress, envelope.Adapter, envelope.PlatformConversationId))
                throw new InvalidOperationException("Channel routing produced no bound Channel.");
            return JsonSerializer.Serialize(new { channel = channelAddress.ToString(), created, acceptedChannels = accepted.Value }, JsonOptions);
        }

        public Task<string> AcceptIntoChannelAsync(CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.ChannelAccept, () => AcceptIntoChannelCoreAsync(cancellationToken));

        private async Task<string> AcceptIntoChannelCoreAsync(CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.ChannelAccept, cancellationToken).ConfigureAwait(false);
            if (current.Name != EventNames.AdapterEnvelope || current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.ChannelAccept, "Not the source Adapter event.", cancellationToken).ConfigureAwait(false);
            var envelope = Parse<AdapterEnvelope>(current.PayloadJson);
            if (envelope.Adapter != current.Source || envelope.EventId != current.EventId)
                await DenyAsync(ScriptCapabilities.ChannelAccept, "Adapter payload does not match routed event.", cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id, mount.Target,
                ScriptCapabilities.ChannelAccept, "allowed"), cancellationToken).ConfigureAwait(false);
            var accepted = await channels.AcceptAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (!accepted.Ok) throw new InvalidOperationException(accepted.Error?.Message ?? "Channel intake failed.");
            return JsonSerializer.Serialize(new { acceptedChannels = accepted.Value }, JsonOptions);
        }

        public Task<string> ReplyThroughChannelAsync(string json, CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.ChannelReply, () => ReplyThroughChannelCoreAsync(json, cancellationToken));

        private async Task<string> ReplyThroughChannelCoreAsync(string json, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.ChannelReply, cancellationToken).ConfigureAwait(false);
            // Two events a Channel reply belongs to, and one rule that covers both: the event came from the Channel this
            // script is mounted on, and the destination is that Channel's own binding.
            //
            // The first is a message the script just received — the original and still the common case. The second is any
            // moment the Channel itself is about: it starts, it stops, or a turn on its Session begins and ends. A script
            // that greets when the conversation comes up, or says something when a turn is done, is doing what a module
            // does in its phases, and it has no message to hang the reply on. Written as a list of names rather than
            // "anything from my target" so that a future event does not silently become a licence to speak.
            if (current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.ChannelReply, "Not the mounted Channel event.", cancellationToken).ConfigureAwait(false);
            if (current.Name is not (EventNames.ChannelMessageAccepted or EventNames.LifecycleStart
                    or EventNames.LifecycleStop or EventNames.LifecycleStarted
                    or EventNames.SessionTurnStarted or EventNames.SessionTurnFinished))
                await DenyAsync(ScriptCapabilities.ChannelReply,
                    $"A Channel reply belongs to a Channel message or to that Channel starting or stopping, not to '{current.Name}'.",
                    cancellationToken).ConfigureAwait(false);

            var request = Parse<ChannelReplyRequest>(json);
            ChannelBinding binding;
            // Where it goes. For a message the script was handed a binding and must name that conversation; for a
            // lifecycle moment there is nothing in the event to name one with, so a Channel that serves one
            // conversation answers for itself — the destination is still checked against its binding below.
            var destination = request.PlatformConversationId;
            if (current.Name == EventNames.ChannelMessageAccepted)
            {
                var routed = Parse<ChannelMessage>(current.PayloadJson);
                binding = routed.Binding;
                if (routed.Channel != current.Source ||
                    !channels.IsBound(current.Source, binding.Adapter, binding.PlatformConversationId) ||
                    request.PlatformConversationId != binding.PlatformConversationId)
                    await DenyAsync(ScriptCapabilities.ChannelReply, "Reply destination differs from Channel binding.", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // No message to read the binding from, so it comes from the Channel itself: what it is aimed at is a
                // fact about the instance, and the graph is allowed to read it because the script is mounted on it.
                var addressed = channels.Get(current.Source);
                if (!addressed.Ok || addressed.Value is null || addressed.Value.Bindings.Count == 0)
                    await DenyAsync(ScriptCapabilities.ChannelReply,
                        "The Channel this script is mounted on is aimed at nothing, so there is nowhere to reply.",
                        cancellationToken).ConfigureAwait(false);
                binding = addressed.Value!.Bindings[0];
                if (string.IsNullOrWhiteSpace(destination)) destination = binding.PlatformConversationId;
                if (!channels.IsBound(current.Source, binding.Adapter, binding.PlatformConversationId) ||
                    destination != binding.PlatformConversationId)
                    await DenyAsync(ScriptCapabilities.ChannelReply, "Reply destination differs from Channel binding.", cancellationToken).ConfigureAwait(false);
            }

            // The mode decides how many messages this one reply becomes. Unknown modes are refused rather than
            // treated as the default: a script asking for "image" today would otherwise send text and look like
            // it worked.
            var mode = string.IsNullOrWhiteSpace(request.Mode) ? ChannelReplyModes.Default : request.Mode;
            if (!ChannelReplyModes.IsKnown(mode))
                await DenyAsync(ScriptCapabilities.ChannelReply, $"Unknown reply mode: {mode}", cancellationToken).ConfigureAwait(false);
            var sent = await DeliverAsync(current.EventId, current.Source, binding.Adapter, destination,
                request.Text, mode, request.ReplyTo, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { mode, sent = sent.TrueForAll(part => part.Sent), parts = sent }, JsonOptions);
        }

        /// <summary>
        /// <c>speak</c>'s <c>{blocks}</c> argument, turned into a layout the Adapter can render — or into the sentence
        /// saying which block could not be built and why.
        ///
        /// Everything is checked here rather than at the platform, because a refusal the model can read is a correction
        /// it can make, and a message that arrives half-rendered is one nobody can fix. In particular: a mention may name
        /// <b>the sender</b> or a platform id, never "@everyone" — a model that could ping a whole group by writing one
        /// word is a model that will, and the cost of being wrong is everybody's phone.
        /// </summary>
        private async ValueTask<Result<IReadOnlyList<MessageBlock>>> BuildBlocksAsync(
            IReadOnlyList<SessionSpeakBlock> blocks, ChannelMessage message, CancellationToken cancellationToken)
        {
            if (blocks.Count > MaxSpeakBlocks)
                return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                    $"一条消息最多 {MaxSpeakBlocks} 个块，这里给了 {blocks.Count} 个。"));
            var layout = new List<MessageBlock>(blocks.Count);
            var text = new System.Text.StringBuilder();
            foreach (var block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = block.Type?.Trim().ToLowerInvariant();
                switch (type)
                {
                    case "text":
                        if (string.IsNullOrEmpty(block.Text))
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                "有一个 text 块没有 text。空块删掉就行。"));
                        text.Append(block.Text);
                        layout.Add(new TextBlock(block.Text));
                        break;
                    case "at" or "mention":
                        var who = block.Who?.Trim();
                        if (string.IsNullOrWhiteSpace(who))
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                "at 块需要 who：\"sender\" 表示回这条消息的人，或者直接写平台号。"));
                        if (who is "all" or "everyone" or "全体" or "全体成员")
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Denied,
                                "不能 @全体成员：那会响每个人的手机，而这不是一条消息该有的分量。"));
                        if (string.Equals(who, "sender", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(message.Sender))
                                return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                    "这条消息没有发送者 id，写不出 at sender；用平台号或者直接称呼对方。"));
                            layout.Add(new MentionBlock(message.Sender));
                            text.Append('@').Append(message.Sender);
                            break;
                        }
                        if (who.Length is 0 or > 20 || !who.All(char.IsAsciiDigit))
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                $"at 只认 \"sender\" 或纯数字的平台号，收到的是：{who}。"));
                        layout.Add(new MentionBlock(who));
                        text.Append('@').Append(who);
                        break;
                    case "image":
                    {
                        if (media is null)
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Unavailable,
                                "这个宿主没有接上媒体存储，发不了图。"));
                        var (scope, path) = SplitMediaPath(block.Scope, block.Path);
                        if (scope is null || string.IsNullOrWhiteSpace(path))
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                "image 块需要 path：\"media/<文件>\" 或 \"workspace/<文件>\"，也可以用 scope + path 分开写。"));
                        var described = await media.DescribeAsync(scope, path, cancellationToken).ConfigureAwait(false);
                        if (described.Error is { } error)
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                $"那个位置没有能发的图片：{scope}/{path}", error.Message));
                        var picture = described.Value!;
                        if (!picture.IsViewable)
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                $"{scope}/{path} 不是图片（{picture.MediaType}），发不出去。"));
                        layout.Add(new ImageBlock(picture));
                        text.Append(MessageBlock.Flatten([new ImageBlock(picture)]));
                        break;
                    }
                    case "file":
                    {
                        if (media is null)
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Unavailable,
                                "这个宿主没有接上媒体存储，发不了文件。"));
                        var (scope, path) = SplitMediaPath(block.Scope, block.Path);
                        if (scope is null || string.IsNullOrWhiteSpace(path))
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                "file 块需要 path：\"media/<文件>\" 或 \"workspace/<文件>\"，也可以用 scope + path 分开写。"));
                        var described = await media.DescribeAsync(scope, path, cancellationToken).ConfigureAwait(false);
                        if (described.Error is { } fileError)
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                $"那个位置没有能发的文件：{scope}/{path}", fileError.Message));
                        var stored = described.Value!;
                        // What it is, asked of the bytes rather than of the name. Nothing here refuses on the answer —
                        // where a file may come from is the byte surface's rule, and it already said yes by producing
                        // this reference (docs/28 §6.6). The description is for the receipt and the adapter's `name`.
                        var name = block.Name?.Trim();
                        if (name is { Length: > 120 })
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                $"file 块的 name 太长（最多 120 字）：{name[..40]}…"));
                        var file = stored;
                        layout.Add(new FileBlock(file, string.IsNullOrWhiteSpace(name) ? null : name));
                        text.Append(MessageBlock.Flatten([new FileBlock(file, name)]));
                        break;
                    }
                    case "face":
                        var id = block.Id?.Trim();
                        if (string.IsNullOrWhiteSpace(id) || id.Length > 10 || !id.All(char.IsAsciiDigit))
                            return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                                "face 块需要平台自己的表情编号（纯数字），例如 {\"type\":\"face\",\"id\":\"1\"}。"));
                        layout.Add(new FaceBlock(id));
                        text.Append("[表情]");
                        break;
                    default:
                        return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                            $"没有这种块：{block.Type}。能用的有 text / at / image / face / file。"));
                }
                if (text.Length > MaxSpeakChars)
                    return Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid,
                        $"这条消息太长（超过 {MaxSpeakChars} 字）。分成几条说，或者先说重要的。"));
            }
            return layout.Count == 0
                ? Result<IReadOnlyList<MessageBlock>>.Failure(new(ErrorCode.Invalid, "blocks 是空的。"))
                : Result<IReadOnlyList<MessageBlock>>.Success(layout);
        }

        /// <summary>
        /// Where a file is: <c>scope</c> + <c>path</c>, or one string whose head is the scope.
        ///
        /// Nothing here lists which scopes exist. The head of <c>media/x</c> is taken as a scope name and the store
        /// answers whether that room exists and whether its bytes may be read at all — one place decides, and the
        /// question "may a model send this" is answered there rather than insisted on twice (docs/24, docs/28 §6.6).
        /// </summary>
        private static (string? Scope, string Path) SplitMediaPath(string? scope, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var trimmed = path.Trim().Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(scope)) return (scope.Trim(), trimmed);
                var slash = trimmed.IndexOf('/');
                return slash > 0 ? (trimmed[..slash], trimmed[(slash + 1)..]) : (null, trimmed);
            }
            return (scope, string.Empty);
        }

        /// <summary>
        /// Turns one reply into the messages it should arrive as, and sends each of them the way an Adapter
        /// expects: one call, one message. Everything above this line may grow more modes (a rendered picture
        /// for a long answer, a file); nothing below it has to know.
        ///
        /// <paramref name="blocks"/> is the layout case, and it is exactly one message: a layout is what one message
        /// looks like, so "split into segments" and "lay it out" cannot both be asked for.
        /// </summary>
        public async Task<List<SendResult>> DeliverAsync(Guid eventId, Address? channel, Address adapter,
            string platformConversationId, string text, string mode, string? replyTo, CancellationToken cancellationToken,
            IReadOnlyList<MessageBlock>? blocks = null)
        {
            var parts = blocks is not null
                ? [text]
                : string.Equals(mode, ChannelReplyModes.Segments, StringComparison.Ordinal)
                    ? SplitOnBlankLines(text)
                    : [text.Trim()];
            var sent = new List<SendResult>(parts.Count);
            for (var index = 0; index < parts.Count; index++)
            {
                // The quoted reply belongs on the first message: the rest are the same answer continuing, and a
                // quote on each of them buries the conversation in arrows.
                var piece = new OutboundMessage(platformConversationId, parts[index], index == 0 ? replyTo : null,
                    index == 0 ? blocks : null);
                sent.Add(await SendAsync(eventId, channel, adapter, piece, cancellationToken).ConfigureAwait(false));
            }
            return sent;
        }

        /// <summary>
        /// Blank line = the next message; a single newline stays a line break inside one message, which is how a
        /// short list survives. No cap: how much a reply should say belongs in the prompt (asking for short
        /// answers), and silently merging parts would make this mode stop meaning what it says.
        /// </summary>
        internal static IReadOnlyList<string> SplitOnBlankLines(string text)
        {
            var parts = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split("\n\n", StringSplitOptions.None)
                .Select(part => part.Trim('\n', ' ', '\t'))
                .Where(part => part.Length > 0)
                .ToArray();
            return parts.Length == 0 ? [text.Trim()] : parts;
        }

        public Task<string> PollSelfAsync(CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.SelfAdapterPoll, () => PollSelfCoreAsync(cancellationToken));

        private async Task<string> PollSelfCoreAsync(CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.SelfAdapterPoll, cancellationToken).ConfigureAwait(false);
            // Polling on demand, not "on a clock pulse": a script that wants its own rhythm decides when to look. That
            // is what makes a relay a choice rather than a requirement — and asking here is a request, not a schedule.
            var found = adapters.Get(mount.Target);
            if (!found.Ok || found.Value is not IPollingAdapter poller)
                throw new InvalidOperationException("Mounted Adapter does not expose a polling endpoint.");
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SelfAdapterPoll, "allowed"), cancellationToken).ConfigureAwait(false);
            var polled = await poller.PollOnceAsync(cancellationToken).ConfigureAwait(false);
            if (!polled.Ok) throw new InvalidOperationException(polled.Error?.Message ?? "Adapter poll failed.");
            return JsonSerializer.Serialize(new { polled = true, accepted = polled.Value }, JsonOptions);
        }

        private async Task<T> InvokeAsync<T>(string capability, Func<Task<T>> body)
        {
            var recordId = Guid.NewGuid();
            var parentId = SignalScope.CurrentId;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                T value;
                using (SignalScope.Enter(recordId, current.EventId))
                    value = await body().ConfigureAwait(false);
                if (value is Result<string> { Ok: false } failed)
                    Record(capability, failed.Error?.Code == ErrorCode.Denied ? "denied" : "failed", started,
                        SignalLevel.Warn, failed.Error?.Message, recordId, parentId);
                else
                    Record(capability, "ok", started, SignalLevel.Info, null, recordId, parentId);
                return value;
            }
            catch (UnauthorizedAccessException)
            {
                // DenyAsync already recorded the denial together with its reason.
                throw;
            }
            catch (OperationCanceledException)
            {
                Record(capability, "timeout", started, SignalLevel.Warn, "Cancelled or timed out", recordId, parentId);
                throw;
            }
            catch (Exception ex)
            {
                Record(capability, "failed", started, SignalLevel.Warn, ex.Message, recordId, parentId);
                throw;
            }
        }

        private void Record(string capability, string outcome, long started, SignalLevel level, string? detail,
            Guid recordId, Guid? parentId)
        {
            signalLog.Write(new()
            {
                Id = recordId,
                ParentId = parentId,
                Group = "capability",
                Name = capability,
                StepId = current.EventId,
                From = $"{mount.Package.Manifest.Id}@{mount.Target}",
                To = CapabilityTarget(capability),
                Mechanism = "call",
                Outcome = outcome,
                DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                // Heartbeat polling fires every second; it is visible at Debug but must not flood the default graph.
                Level = capability == ScriptCapabilities.SelfAdapterPoll
                    ? (level == SignalLevel.Info ? SignalLevel.Debug : level)
                    : level,
                Detail = detail is null ? null : new Dictionary<string, string> { ["message"] = detail }
            });
        }

        private string CapabilityTarget(string capability) => capability switch
        {
            ScriptCapabilities.ModelComplete => "model",
            ScriptCapabilities.SessionRun => "session",
            ScriptCapabilities.ChannelAccept or ScriptCapabilities.ChannelReply or ScriptCapabilities.ChannelRoute
                => current.Source.ToString(),
            _ => mount.Target.ToString()
        };

        /// <summary>
        /// Point log from script code. Correlated with the dispatch that produced it (same StepId),
        /// capped per invocation, and never allowed to throw into the script.
        /// </summary>
        public void Log(string level, string message, string? detailJson)
        {
            try
            {
                var normalized = level?.Trim().ToLowerInvariant() switch
                {
                    "warn" => SignalLevel.Warn,
                    "error" => SignalLevel.Error,
                    _ => SignalLevel.Info
                };
                if (!signalLog.IsEnabled(normalized)) return;
                var count = Interlocked.Increment(ref _logRecords);
                if (count > MaxLogRecords)
                {
                    if (count == MaxLogRecords + 1)
                        signalLog.Write(new()
                        {
                            Group = "module", Name = mount.Package.Manifest.Id, StepId = current.EventId,
                            From = mount.Target.ToString(), Mechanism = "log", Outcome = "truncated",
                            Level = SignalLevel.Warn, Detail = new Dictionary<string, string>
                            { ["message"] = $"Script log cap reached ({MaxLogRecords} records); further entries were dropped." }
                        });
                    return;
                }
                var text = message is { Length: > MaxLogMessageLength } ? message[..MaxLogMessageLength] : message ?? "";
                var detail = new Dictionary<string, string> { ["message"] = text };
                if (detailJson is { Length: > 0 and <= 4_000 })
                {
                    try
                    {
                        using var document = JsonDocument.Parse(detailJson);
                        if (document.RootElement.ValueKind == JsonValueKind.Object)
                            foreach (var property in document.RootElement.EnumerateObject().Take(MaxLogDetailEntries))
                                detail[property.Name.Length > 64 ? property.Name[..64] : property.Name] = property.Value.ValueKind switch
                                {
                                    JsonValueKind.String => Truncate(property.Value.GetString() ?? ""),
                                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                                    _ => Truncate(property.Value.GetRawText())
                                };
                    }
                    catch (JsonException) { /* unparsable detail is dropped; the message still lands */ }
                }
                signalLog.Write(new()
                {
                    Group = "module",
                    Name = mount.Package.Manifest.Id,
                    ParentId = SignalScope.CurrentId,
                    StepId = current.EventId,
                    From = mount.Target.ToString(),
                    Mechanism = "log",
                    Outcome = normalized.ToString().ToLowerInvariant(),
                    Level = normalized,
                    Detail = detail
                });
            }
            catch
            {
                // A script's logging call must never change its own outcome.
            }
        }

        private static string Truncate(string value) =>
            value.Length > MaxLogDetailValueLength ? value[..MaxLogDetailValueLength] : value;

        private async ValueTask RequireCapabilityAsync(string capability, CancellationToken cancellationToken)
        {
            if (mount.Package.Manifest.Capabilities.Contains(capability, StringComparer.Ordinal)) return;
            await DenyAsync(capability, "Capability was not declared.", cancellationToken).ConfigureAwait(false);
        }

        private async Task DenyAsync(string action, string detail, CancellationToken cancellationToken)
        {
            signalLog.Write(new()
            {
                Group = "capability",
                Name = action,
                ParentId = SignalScope.CurrentId,
                StepId = current.EventId,
                From = $"{mount.Package.Manifest.Id}@{mount.Target}",
                To = CapabilityTarget(action),
                Mechanism = "call",
                Outcome = "denied",
                Level = SignalLevel.Warn,
                Detail = new Dictionary<string, string> { ["reason"] = detail }
            });
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, action, "denied", detail), cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedAccessException(detail);
        }

        private async Task<SendResult> SendAsync(Guid eventId, Address? channel, Address adapter, OutboundMessage message, CancellationToken cancellationToken)
        {
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, eventId, mount.Package.Manifest.Id, adapter, "adapter.send", "allowed", message.PlatformConversationId), cancellationToken).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var found = adapters.Get(adapter);
            if (!found.Ok || found.Value is null) throw new InvalidOperationException("Bound Adapter is unavailable.");
            // No capability check here either: this is the one place every block-carrying send passes through, and it
            // passes the message to the Adapter unchanged. If a part cannot go out, the Adapter says so on this call —
            // and if it goes out as its text form instead, SendResult.Detail carries that back to the caller.
            var result = await found.Value.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!result.Ok || result.Value is null)
                throw new AdapterSendException(result.Error ?? new(ErrorCode.Unavailable, "Adapter send failed."));
            if (channel is { } answeredChannel)
            {
                // Written down after the send, never before: the reply is already out, so a failed row costs at
                // most one duplicate on a later replay, while a row without a reply would cost a silence.
                var recorded = await channels.RecordReplyAsync(answeredChannel, eventId, message.PlatformConversationId, message.Text,
                    result.Value.PlatformMessageId, cancellationToken)
                    .ConfigureAwait(false);
                if (!recorded.Ok)
                    signalLog.Write(new()
                    {
                        Group = "graph", Name = "channel.reply.record", StepId = eventId, From = answeredChannel.ToString(),
                        Mechanism = "channel", Outcome = "failed", Level = SignalLevel.Warn,
                        Detail = new Dictionary<string, string>
                        {
                            ["message"] = recorded.Error?.Message ?? string.Empty
                        }
                    });
            }
            return result.Value;
        }

        private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new ArgumentException("Script API received invalid JSON.");

        /// <summary>
        /// <c>speak</c>'s arguments. <see cref="Text"/> and <see cref="Blocks"/> are alternatives: one is a sentence, the
        /// other is a layout, and a caller that sends both has not decided which one is the message.
        /// </summary>
        private sealed record SessionSpeakArguments(string? Text = null, string? Mode = null,
            IReadOnlyList<SessionSpeakBlock>? Blocks = null);

        /// <summary>
        /// One block as the model writes it: <c>{"type":"text","text":…}</c>, <c>{"type":"at","who":"sender"}</c>,
        /// <c>{"type":"image","path":"media/…"}</c>, <c>{"type":"face","id":"1"}</c>.
        ///
        /// Deliberately not the <see cref="MessageBlock"/> hierarchy: the model names where a picture <b>is</b>, and the
        /// core turns that into the reference a picture travels as. Letting a model write a MediaRef would let it invent
        /// one — a scope, a size, a hash — and the first thing it got wrong would be sent.
        /// </summary>
        private sealed record SessionSpeakBlock(string? Type = null, string? Text = null, string? Who = null,
            string? Path = null, string? Scope = null, string? Id = null, string? Name = null);

        /// <summary>
        /// How much one laid-out message may hold. Not a platform limit — it is the point past which "one message" stops
        /// being what was meant, and the refusal says so rather than truncating.
        /// </summary>
        private const int MaxSpeakBlocks = 32;
        private const int MaxSpeakChars = 8000;
        private sealed record RouteOptions(string? ChannelId, string? Name);
        private const int MaxLogRecords = 200;
        private const int MaxLogMessageLength = 2_000;
        private const int MaxLogDetailEntries = 16;
        private const int MaxLogDetailValueLength = 512;
        private int _logRecords;
    }
}

public sealed class JsonScriptCatalog(string programRoot, string dataRoot) : IGraphScriptCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private IReadOnlyList<ScriptIssue> _issues = [];

    /// <inheritdoc />
    public IReadOnlyList<ScriptIssue> LastIssues => _issues;

    /// <summary>
    /// Every script the host can see, in the order that decides who wins when two files share an id.
    ///
    /// The draft root is the last of the writable ones (docs/25): what a model writes is loaded like any other script,
    /// and still has to be mounted by hand — being visible is not being in charge. It sits after the operator's tree so
    /// a draft cannot shadow a script somebody installed deliberately.
    ///
    /// A file that cannot be used is an issue, never a reason to refuse the rest. The host has to be able to start with
    /// a bad draft in the folder, otherwise writing one would be a way to stop the system rather than to extend it.
    /// </summary>
    public async ValueTask<Result<IReadOnlyList<ScriptPackage>>> LoadAsync(CancellationToken cancellationToken)
    {
        var packages = new List<ScriptPackage>();
        var seen = new Dictionary<string, (string Path, TrustDomain Domain)>(StringComparer.Ordinal);
        var issues = new List<ScriptIssue>();
        try
        {
            var roots = new[]
            {
                (Path.Combine(programRoot, "resources", "scripts", "builtin"), TrustDomain.Builtin, AccessLevel.Full, (string?)"bundled"),
                // A person wrote these, or vouched for them by moving them here. That is the whole of the decision.
                (Path.Combine(dataRoot, "scripts", "trusted"), TrustDomain.Trusted, AccessLevel.Full, (string?)"user"),
                // Written by the model and in effect: the mount moved it here, which is why being in this folder and
                // being mounted are the same fact. Still the model's script, so still the model's level.
                (Path.Combine(dataRoot, "scripts", "live"), TrustDomain.Trusted, AccessLevel.Agent, (string?)"live"),
                (Path.Combine(dataRoot, "scripts", "draft"), TrustDomain.Trusted, AccessLevel.Agent, (string?)"draft"),
                (Path.Combine(dataRoot, "scripts", "restricted"), TrustDomain.Restricted, AccessLevel.Full, (string?)"user")
            };
            foreach (var (root, expectedDomain, level, origin) in roots)
                await LoadRootAsync(root, expectedDomain, level, origin, packages, seen, issues, cancellationToken).ConfigureAwait(false);

            // Scripts shipped inside a plugin: dropping the plugin in is the trust decision, so they must declare
            // "trusted" — a shipped script cannot claim to be builtin. They are not written by the operator, so
            // they are read from the plugin folder and never written to.
            //
            // Only the version that is in force is read. A package folder may hold several versions side by side, and
            // scanning all of them would put two copies of one script id in the catalogue — the older one might even
            // win on directory order, which is the shape of a hot replacement that swapped the module and kept
            // running the old script. The layout decides, once, for both this and the loader.
            foreach (var packageRoot in ModulePackageLayout.PackageRoots(Path.Combine(dataRoot, "plugins")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = ModulePackageLayout.Resolve(packageRoot);
                if (!resolved.Ok || resolved.Value is null || !resolved.Value.Resolved)
                {
                    // Named as a package problem, not a script one: the person who put the folder there is the one who
                    // can fix it, and this is where a package's own scripts go missing without any other trace.
                    issues.Add(new(packageRoot, "package layout: " +
                        (resolved.Error?.Message ?? "the package layout could not be resolved.")));
                    continue;
                }
                var scriptsRoot = Path.Combine(resolved.Value.ActiveDirectory, "scripts");
                if (!Directory.Exists(scriptsRoot)) continue;
                // plugin:<id>, and the id is the package's own: a version directory is not a package, so stamping
                // scripts with the folder they happen to sit in would have renamed the library on every replacement.
                await LoadRootAsync(scriptsRoot, TrustDomain.Trusted, AccessLevel.Full, "plugin:" + resolved.Value.Id,
                    packages, seen, issues, cancellationToken).ConfigureAwait(false);
            }
            _issues = issues;
            return Result<IReadOnlyList<ScriptPackage>>.Success(packages);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The walk itself failed — a root that cannot be read. Whatever it got through is still worth reporting.
            _issues = [.. issues, new(dataRoot, ex.Message)];
            return Result<IReadOnlyList<ScriptPackage>>.Failure(new(ErrorCode.Invalid, "Could not load script catalog.", ex.Message));
        }
    }

    /// <summary>One source of script packages. Returns a failure to stop the whole catalog, null when fine.</summary>
    /// <summary>
    /// One source of script packages. A file that cannot be used becomes an issue and the walk continues: a catalogue
    /// that refuses to load costs every other script too, and the caller finds out at the next start — a typo in one
    /// file should not be a host that will not boot (docs/25 §6).
    /// </summary>
    private static async ValueTask LoadRootAsync(string root, TrustDomain expectedDomain, AccessLevel level, string? origin,
        List<ScriptPackage> packages, Dictionary<string, (string Path, TrustDomain Domain)> seen, List<ScriptIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;
        foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                if (bytes.Length > 64_000) { issues.Add(new(manifestPath, "Manifest exceeds 64 KB.")); continue; }
                var manifest = JsonSerializer.Deserialize<ScriptFileManifest>(bytes, Options);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    // The id is checked here, not later: everything below keys on it, and a null would be an exception
                    // rather than an answer.
                    issues.Add(new(manifestPath, "A script manifest needs an \"id\"."));
                    continue;
                }
                if (manifest.TrustDomain != expectedDomain)
                {
                    // Not "shipped": a file claiming builtin from a folder a person can edit. Said plainly, and skipped:
                    // refusing the catalogue over it would let one bad file stop the host.
                    issues.Add(new(manifestPath, $"trustDomain \"{manifest.TrustDomain}\" does not match this folder ({expectedDomain})."));
                    continue;
                }
                if (seen.TryGetValue(manifest.Id, out var first))
                {
                    // The first file wins and the second is reported — never a merge, never a pick with no explanation.
                    //
                    // One exception, and it is the whole reason seeding exists: the operator's copy in scripts/trusted
                    // replaces the shipped one. Without it, the first start after seeding would find every shipped
                    // script twice — "copy it out so it can be edited" would break the host it serves. Only an operator
                    // copy may win; a draft or a plugin does not displace what is already there.
                    if (first.Domain == TrustDomain.Builtin && origin == "user")
                        packages.RemoveAll(package => string.Equals(package.Manifest.Id, manifest.Id, StringComparison.Ordinal));
                    else
                    {
                        issues.Add(new(manifestPath, $"Duplicate script id \"{manifest.Id}\"; already loaded from {first.Path}."));
                        continue;
                    }
                }

                var scriptPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "main.js");
                if (!File.Exists(scriptPath))
                {
                    issues.Add(new(manifestPath, "No main.js next to this manifest."));
                    continue;
                }
                var source = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
                if (source.Length > 256_000) { issues.Add(new(manifestPath, $"Script source exceeds 256 KB: {manifest.Id}")); continue; }

                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
                seen[manifest.Id] = (manifestPath, expectedDomain);
                packages.Add(new(new(manifest.Id, manifest.Version, manifest.TargetType, manifest.Mount, manifest.Events,
                    manifest.EntryPoint, manifest.Capabilities, manifest.TrustDomain, manifest.MaxStatements,
                    manifest.TimeoutMilliseconds, manifest.MemoryLimitBytes, manifest.Call, manifest.Tool), source,
                    fingerprint, origin, level));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                issues.Add(new(manifestPath, ex.Message));
            }
        }
    }
}
