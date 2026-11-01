using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Graph.Contracts;

namespace ALN.Graph;

public sealed class GraphManager : IGraphRuntime
{
    private static readonly HashSet<string> AllowedCapabilities = new(StringComparer.Ordinal)
    { ScriptCapabilities.SelfAdapterSend, ScriptCapabilities.SelfAdapterPoll, ScriptCapabilities.ChannelAccept, ScriptCapabilities.ChannelRoute, ScriptCapabilities.ChannelReply, ScriptCapabilities.ModelComplete, ScriptCapabilities.SessionRun, ScriptCapabilities.SessionActivate };
    private readonly IAdapterManager _adapters;
    private readonly IChannelManager _channels;
    private readonly IClockManager _clock;
    private readonly IModuleTypeCatalog _types;
    private readonly IScriptExecutor _executor;
    private readonly IAuditSink _audit;
    private readonly ISignalLog _signalLog;
    private readonly IModelManager? _models;
    private readonly ISessionManager? _sessions;
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

    public GraphManager(IAdapterManager adapters, IChannelManager channels, IClockManager clock, IModuleTypeCatalog types,
        IScriptExecutor executor, IAuditSink audit, ISignalLog signalLog, IModelManager? models = null, ISessionManager? sessions = null,
        IStateStore? state = null, IRestoreReporter? report = null)
    { _adapters = adapters; _channels = channels; _clock = clock; _types = types; _executor = executor; _audit = audit; _signalLog = signalLog; _models = models; _sessions = sessions; _state = state; _report = report ?? NullRestoreReporter.Instance; }

    public IReadOnlyList<MountedScript> Mounted
    { get { _gate.Wait(); try { return _mounted.ToArray(); } finally { _gate.Release(); } } }

    public async ValueTask<Result> LoadAsync(IEnumerable<ScriptPackage> packages, CancellationToken cancellationToken)
    {
        var incoming = packages.ToArray();
        var candidate = new Dictionary<string, ScriptPackage>(StringComparer.Ordinal);
        foreach (var package in incoming)
        {
            var m = package.Manifest;
            if (string.IsNullOrWhiteSpace(m.Id) || !candidate.TryAdd(m.Id, package) || m.Version < 1 ||
                string.IsNullOrWhiteSpace(m.TargetType) || !_types.IsKnownAbstract(m.TargetType) || m.Events is null || m.Events.Length == 0 ||
                m.Events.Any(e => e is not (EventNames.AdapterEnvelope or EventNames.ChannelMessageAccepted or EventNames.ClockPulse)) || string.IsNullOrWhiteSpace(m.EntryPoint) ||
                m.MaxStatements is < 1 or > 1_000_000 || m.TimeoutMilliseconds is < 1 or > 60_000 ||
                m.MemoryLimitBytes is < 1_048_576 or > 268_435_456)
                return Result.Failure(new(ErrorCode.Invalid, $"Invalid script manifest: {m.Id}"));
            if (m.TrustDomain == TrustDomain.Restricted)
                return Result.Failure(new(ErrorCode.Denied, "Restricted scripts require the process-isolated runner, which is not part of this initial host."));
            if (m.Capabilities is null || m.Capabilities.Any(c => !AllowedCapabilities.Contains(c)))
                return Result.Failure(new(ErrorCode.Denied, $"Script {m.Id} requests an unsupported capability."));
            if (m.Mount is not (MountMode.Default or MountMode.Manual))
                return Result.Failure(new(ErrorCode.Invalid, $"Invalid mount mode: {m.Id}"));
            if (string.IsNullOrWhiteSpace(package.Source) || package.Source.Length > 256_000)
                return Result.Failure(new(ErrorCode.Invalid, $"Empty or oversized source: {m.Id}"));
            var validated = await _executor.ValidateAsync(package, cancellationToken).ConfigureAwait(false);
            if (!validated.Ok) return validated;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var restored = await RestoreRulesAsync(cancellationToken).ConfigureAwait(false);
            if (!restored.Ok) return restored;
            await RestoreDisabledAsync(cancellationToken).ConfigureAwait(false);
            _packages.Clear();
            foreach (var pair in candidate) _packages.Add(pair.Key, pair.Value);
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
                if (!TargetExists(named) || !IsCompatible(package.Manifest.TargetType, named))
                    return Result<MountRule>.Failure(new(ErrorCode.Invalid, "Manual mount target is absent or incompatible."));
            }
            else if (targets.Any(target => Matches(request.Scope, target) && IsCompatible(package.Manifest.TargetType, target) &&
                                           _mounted.Any(m => m.Package.Manifest.Id == request.ScriptId && m.Target == target)))
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

    private Address[] Targets() =>
        _adapters.List().Select(a => a.Address).Concat(_channels.List().Select(c => c.Address)).ToArray();

    private bool Matches(MountScope scope, Address target) => scope.Kind switch
    {
        MountScopeKind.Instance => Address.TryParse(scope.Selector, out var named) && named == target,
        // "channel" (a module) and "channel.conversation" (a type) are the same walk up the type tree;
        // keeping them separate kinds is about what a person means, and about validating it.
        MountScopeKind.Type or MountScopeKind.Module => _types.IsAssignableTo(target.TypeId, scope.Selector),
        _ => false
    };

    /// <summary>True when this rule actually puts a script on this target right now.</summary>
    private bool Resolves(MountRule rule, Address target) =>
        !rule.IsSpent(target) &&
        Matches(rule.Scope, target) &&
        _packages.TryGetValue(rule.ScriptId, out var package) &&
        IsEnabled(package) &&
        IsCompatible(package.Manifest.TargetType, target);

    /// <summary>Call with the gate held: brings one target in line with every rule that names it.</summary>
    private void ApplyRulesTo(Address target)
    {
        foreach (var rule in _rules)
        {
            if (!Resolves(rule, target)) continue;
            if (_mounted.Any(m => m.Package.Manifest.Id == rule.ScriptId && m.Target == target)) continue;
            _mounted.Add(new(_packages[rule.ScriptId], target, rule.Id, rule.RemainingFor(target)));
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

    /// <summary>Named after the package a script arrived in; the operator's own tree is simply "user".</summary>
    private static string LibraryOf(ScriptPackage package) =>
        string.IsNullOrWhiteSpace(package.Origin) ? "user" : package.Origin;

    /// <summary>One namespace for both switches: <c>library:&lt;id&gt;</c> and <c>script:&lt;id&gt;</c>.</summary>
    private static string SwitchKey(string target) =>
        target.StartsWith("library:", StringComparison.Ordinal) || target.StartsWith("script:", StringComparison.Ordinal)
            ? target
            : target.Contains(':') || target is "user" or "bundled"
                ? "library:" + target
                : "script:" + target;

    private bool IsEnabled(ScriptPackage package) =>
        !_disabled.Contains("script:" + package.Manifest.Id) && !_disabled.Contains("library:" + LibraryOf(package));

    public async ValueTask<Result> SetScriptEnabledAsync(string target, bool enabled, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target)) return Result.Failure(new(ErrorCode.Invalid, "Which script or library?"));
        var key = SwitchKey(target.Trim());
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var known = key.StartsWith("library:", StringComparison.Ordinal)
                ? _packages.Values.Select(LibraryOf).Distinct(StringComparer.Ordinal).Contains(key["library:".Length..])
                : _packages.ContainsKey(key["script:".Length..]);
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
            if (IsCompatible(package.Manifest.TargetType, target)) _mounted.Add(new(package, target));
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
            if (!_types.IsConcrete(target.TypeId)) return Result.Failure(new(ErrorCode.Invalid, "Mount target must be a concrete module instance."));
            if (!TargetExists(target)) return Result.Failure(new(ErrorCode.Invalid, $"Mount target not found: {target}"));
            _drainingTargets.Remove(target);
            foreach (var package in _packages.Values.Where(p => p.Manifest.Mount == MountMode.Default && IsEnabled(p) && IsCompatible(p.Manifest.TargetType, target)))
                if (!_mounted.Any(m => m.Package.Manifest.Id == package.Manifest.Id && m.Target == target)) _mounted.Add(new(package, target));
            // A target that appears now is also a target that some rule was already waiting for.
            var beforeRules = _mounted.Count(m => m.RuleId is not null);
            ApplyRulesTo(target);
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

    public async ValueTask<Result> MountAsync(string scriptId, Address target, CancellationToken cancellationToken)
    {
        var added = await AddRuleAsync(new MountRequest(scriptId, MountScope.ForInstance(target)), cancellationToken).ConfigureAwait(false);
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
        ClockPulse? clockPulse = null;
        if (routedEvent.Name == EventNames.ClockPulse && routedEvent.Source == _clock.Address && routedEvent.Target is not null)
        {
            try { clockPulse = JsonSerializer.Deserialize<ClockPulse>(routedEvent.PayloadJson, JsonOptions); }
            catch (JsonException) { }
        }
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
                    var api = new ScriptApi(_adapters, _channels, _clock, _audit, _signalLog, _models, _sessions, mount, routedEvent,
                        stateScopes[handlerIndex], _state, deadline.Token);
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
                    // A successful clock pulse is a wake-up, not a fact: at Debug, so a polling adapter cannot
                    // flood the default graph with one record per tick per mounted script. A failed one stays
                    // at Warn because that is the interesting half.
                    Level = !result.Ok ? SignalLevel.Warn
                        : routedEvent.Name == EventNames.ClockPulse ? SignalLevel.Debug
                        : SignalLevel.Info,
                    Detail = result.Ok ? null : new Dictionary<string, string>
                    {
                        ["event"] = routedEvent.Name,
                        ["script"] = mount.Package.Manifest.Id,
                        ["code"] = result.Error?.Code.ToString() ?? "unknown",
                        ["message"] = result.Error?.Message ?? ""
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
            if (clockPulse is not null && routedEvent.Target is { } clockTarget)
                await _clock.AcknowledgeAsync(clockPulse.SubscriptionId, clockTarget, routedEvent.EventId, CancellationToken.None).ConfigureAwait(false);
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

    private bool IsCompatible(string targetType, Address target) => _types.IsAssignableTo(target.TypeId, targetType);
    /// <summary>Stable edge identity derived from assembly-time data, not runtime names.</summary>
    internal static string EdgeId(MountedScript mount, string eventName) =>
        $"{mount.Package.Manifest.Id}@{mount.Target}#{eventName}";    private bool TargetExists(Address target) => target.Module switch
    { "adapter" => _adapters.Get(target).Ok, "channel" => _channels.Get(target).Ok, _ => false };

    private sealed class ScriptApi(IAdapterManager adapters, IChannelManager channels, IClockManager clock, IAuditSink audit,
        ISignalLog signalLog, IModelManager? models, ISessionManager? sessions, MountedScript mount, RoutedEvent current,
        string? stateScope, IStateStore? state, CancellationToken ct) : IScriptApi
    {
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

            var spec = Parse<SessionSpec>(sessionSpecJson);
            // The Channel may name the profile it wants; a spec that names none inherits it. Leftovers fall to
            // the host's own resolution (SessionManager), which says out loud what it picked.
            if (string.IsNullOrWhiteSpace(spec.ModelProfileId) &&
                channels.Get(channelMessage.Channel).Value is { ModelProfile: { Length: > 0 } channelProfile })
                spec = spec with { ModelProfileId = channelProfile };
            if (spec.Tools?.Any(tool => tool.Name != "send_message") == true)
                return JsonSerializer.Serialize(new { ok = false, error = new { message = "Unknown Session tool binding." } }, JsonOptions);
            if (spec.Tools?.Any(tool => tool.Name == "send_message") == true &&
                !mount.Package.Manifest.Capabilities.Contains(ScriptCapabilities.ChannelReply, StringComparer.Ordinal))
                await DenyAsync(ScriptCapabilities.ChannelReply, "send_message requires channel.bound-send.", cancellationToken).ConfigureAwait(false);

            var identity = $"{current.Source}\n{channelMessage.Channel}";
            var sessionId = "s-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionActivate, "allowed", sessionId), cancellationToken).ConfigureAwait(false);

            // The trigger message is not handed over here on purpose: it is already in the Channel's history,
            // and the delta pull will include it. Passing it twice is how a conversation gets answered twice.
            var input = new SessionTurnInput([], channelMessage.Channel, channelMessage.Binding);
            var result = await sessions.RunTurnAsync(sessionId, spec, current.EventId, input, this, cancellationToken).ConfigureAwait(false);
            if (!result.Ok || result.Value is null)
            {
                await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                    mount.Target, ScriptCapabilities.SessionActivate, "failed", result.Error?.Message), cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = false, error = result.Error }, JsonOptions);
            }
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionActivate, "completed", sessionId), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = true, result = result.Value }, JsonOptions);
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
            var spec = Parse<SessionSpec>(sessionSpecJson);
            if (string.IsNullOrWhiteSpace(spec.ModelProfileId) &&
                channels.Get(channelMessage.Channel).Value is { ModelProfile: { Length: > 0 } channelProfile })
                spec = spec with { ModelProfileId = channelProfile };
            if (spec.Tools?.Any(tool => tool.Name != "send_message") == true)
                return JsonSerializer.Serialize(new { ok = false, error = new { message = "Unknown Session tool binding." } }, JsonOptions);
            if (spec.Tools?.Any(tool => tool.Name == "send_message") == true &&
                !mount.Package.Manifest.Capabilities.Contains(ScriptCapabilities.ChannelReply, StringComparer.Ordinal))
                await DenyAsync(ScriptCapabilities.ChannelReply, "send_message requires channel.bound-send.", cancellationToken).ConfigureAwait(false);
            var identity = $"{current.Source}\n{channelMessage.Channel}";
            var sessionId = "s-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionRun, "allowed", sessionId), cancellationToken).ConfigureAwait(false);
            var input = new SessionTurnInput([new SessionChannelMessage(channelMessage.MessageId.ToString("N"), "user",
                channelMessage.Sender, channelMessage.Text, channelMessage.At)], channelMessage.Channel, channelMessage.Binding);
            var result = await sessions.RunTurnAsync(sessionId, spec, current.EventId, input, this, cancellationToken).ConfigureAwait(false);
            if (!result.Ok || result.Value is null)
            {
                await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                    mount.Target, ScriptCapabilities.SessionRun, "failed", result.Error?.Message), cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = false, error = result.Error }, JsonOptions);
            }
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionRun, "completed", sessionId), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = true, result = result.Value }, JsonOptions);
        }

        public ValueTask<Result<string>> ExecuteAsync(string sessionId, Guid activationId, ModelToolCall call,
            CancellationToken cancellationToken) =>
            new(InvokeAsync("session.send_message", () => ExecuteCoreAsync(sessionId, activationId, call, cancellationToken)));

        private async Task<Result<string>> ExecuteCoreAsync(string sessionId, Guid activationId, ModelToolCall call,
            CancellationToken cancellationToken)
        {
            if (call.Name != "send_message")
                return Result<string>.Failure(new(ErrorCode.Denied, $"Session tool binding is not available: {call.Name}"));
            try { await RequireCapabilityAsync(ScriptCapabilities.ChannelReply, cancellationToken).ConfigureAwait(false); }
            catch (UnauthorizedAccessException ex)
            { return Result<string>.Failure(new(ErrorCode.Denied, ex.Message)); }
            if (current.Name != EventNames.ChannelMessageAccepted || current.Source != mount.Target)
                return Result<string>.Failure(new(ErrorCode.Denied, "Session send_message requires the source Channel message event."));

            ChannelMessage channelMessage;
            SessionSendArguments arguments;
            try
            {
                channelMessage = Parse<ChannelMessage>(current.PayloadJson);
                arguments = Parse<SessionSendArguments>(call.ArgumentsJson);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            { return Result<string>.Failure(new(ErrorCode.Invalid, "Session tool arguments are invalid.", ex.Message)); }
            var binding = channelMessage.Binding;
            if (channelMessage.Channel != current.Source || channelMessage.MessageId != current.EventId ||
                !channels.IsBound(current.Source, binding.Adapter, binding.PlatformConversationId) || string.IsNullOrWhiteSpace(arguments.Text))
                return Result<string>.Failure(new(ErrorCode.Invalid, "Session send_message requires non-empty text for the bound Channel."));

            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, "session.tool.send_message", "allowed", call.Id), cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await SendAsync(current.EventId, channelMessage.Channel, binding.Adapter,
                    new OutboundMessage(binding.PlatformConversationId, arguments.Text, channelMessage.PlatformMessageId), cancellationToken)
                    .ConfigureAwait(false);
                return Result<string>.Success(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            { return Result<string>.Failure(new(ErrorCode.Unavailable, "Session send_message failed.", ex.Message)); }
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
            return await SendAsync(envelope.EventId, null, current.Source, message, cancellationToken).ConfigureAwait(false);
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
                var spec = new ChannelSpec(channelAddress, string.IsNullOrWhiteSpace(options.Name) ? channelId : options.Name!,
                    [new ChannelBinding(envelope.Adapter, envelope.PlatformConversationId)]);
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
            if (current.Name != EventNames.ChannelMessageAccepted || current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.ChannelReply, "Not the mounted Channel event.", cancellationToken).ConfigureAwait(false);
            var routed = Parse<ChannelMessage>(current.PayloadJson);
            var message = Parse<OutboundMessage>(json);
            var binding = routed.Binding;
            if (routed.Channel != current.Source || !channels.IsBound(current.Source, binding.Adapter, binding.PlatformConversationId) ||
                message.PlatformConversationId != binding.PlatformConversationId)
                await DenyAsync(ScriptCapabilities.ChannelReply, "Reply destination differs from Channel binding.", cancellationToken).ConfigureAwait(false);
            return await SendAsync(current.EventId, routed.Channel, binding.Adapter, message, cancellationToken).ConfigureAwait(false);
        }

        public Task<string> PollSelfAsync(CancellationToken cancellationToken) =>
            InvokeAsync(ScriptCapabilities.SelfAdapterPoll, () => PollSelfCoreAsync(cancellationToken));

        private async Task<string> PollSelfCoreAsync(CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.SelfAdapterPoll, cancellationToken).ConfigureAwait(false);
            if (current.Name != EventNames.ClockPulse || current.Source != clock.Address || current.Target != mount.Target)
                await DenyAsync(ScriptCapabilities.SelfAdapterPoll, "Clock pulse is not addressed to this Adapter.", cancellationToken).ConfigureAwait(false);
            var pulse = Parse<ClockPulse>(current.PayloadJson);
            if (!clock.IsRegistered(pulse.SubscriptionId, mount.Target))
                return JsonSerializer.Serialize(new { polled = false, stale = true }, JsonOptions);
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

        private async Task<string> SendAsync(Guid eventId, Address? channel, Address adapter, OutboundMessage message, CancellationToken cancellationToken)
        {
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, eventId, mount.Package.Manifest.Id, adapter, "adapter.send", "allowed", message.PlatformConversationId), cancellationToken).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var found = adapters.Get(adapter);
            if (!found.Ok || found.Value is null) throw new InvalidOperationException("Bound Adapter is unavailable.");
            var result = await found.Value.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) throw new InvalidOperationException(result.Error?.Message ?? "Adapter send failed.");
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
            return JsonSerializer.Serialize(result.Value, JsonOptions);
        }

        private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new ArgumentException("Script API received invalid JSON.");
        private sealed record SessionSendArguments(string Text);
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
    public async ValueTask<Result<IReadOnlyList<ScriptPackage>>> LoadAsync(CancellationToken cancellationToken)
    {
        var packages = new List<ScriptPackage>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var roots = new[]
            {
                (Path.Combine(programRoot, "resources", "scripts", "builtin"), TrustDomain.Builtin, (string?)"bundled"),
                (Path.Combine(dataRoot, "scripts", "trusted"), TrustDomain.Trusted, (string?)"user"),
                (Path.Combine(dataRoot, "scripts", "restricted"), TrustDomain.Restricted, (string?)"user")
            };
            foreach (var (root, expectedDomain, origin) in roots)
            {
                var failure = await LoadRootAsync(root, expectedDomain, origin, packages, seen, cancellationToken).ConfigureAwait(false);
                if (failure is not null) return Result<IReadOnlyList<ScriptPackage>>.Failure(failure);
            }

            // Scripts shipped inside a plugin: dropping the plugin in is the trust decision, so they must declare
            // "trusted" — a shipped script cannot claim to be builtin. They are not written by the operator, so
            // they are read from the plugin folder and never written to.
            var pluginsRoot = Path.Combine(dataRoot, "plugins");
            if (Directory.Exists(pluginsRoot))
            {
                foreach (var plugin in Directory.GetDirectories(pluginsRoot).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scriptsRoot = Path.Combine(plugin, "scripts");
                    if (!Directory.Exists(scriptsRoot)) continue;
                    if (await LoadRootAsync(scriptsRoot, TrustDomain.Trusted, "plugin:" + Path.GetFileName(plugin), packages, seen, cancellationToken)
                        .ConfigureAwait(false) is { } failure) return Result<IReadOnlyList<ScriptPackage>>.Failure(failure);
                }
            }
            return Result<IReadOnlyList<ScriptPackage>>.Success(packages);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<IReadOnlyList<ScriptPackage>>.Failure(new(ErrorCode.Invalid, "Could not load script catalog.", ex.Message)); }
    }

    /// <summary>One source of script packages. Returns a failure to stop the whole catalog, null when fine.</summary>
    private static async ValueTask<Error?> LoadRootAsync(string root, TrustDomain expectedDomain, string? origin,
        List<ScriptPackage> packages, Dictionary<string, string> seen, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return null;
        foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > 64_000) return new(ErrorCode.Invalid, "Script manifest exceeds 64 KB.");
            var manifest = JsonSerializer.Deserialize<ScriptFileManifest>(bytes, Options);
            if (manifest is null || manifest.TrustDomain != expectedDomain)
                return new(ErrorCode.Denied, $"Invalid manifest or trust domain mismatch: {manifestPath}");
            // Two scripts with one id would make graph.mount ambiguous; say which two, do not pick one.
            if (seen.TryGetValue(manifest.Id, out var first))
                return new(ErrorCode.Conflict, $"Duplicate script id {manifest.Id}: {first} and {manifestPath}");
            var scriptPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "main.js");
            var source = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
            if (source.Length > 256_000) return new(ErrorCode.Invalid, $"Script source exceeds 256 KB: {manifest.Id}");
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
            seen[manifest.Id] = manifestPath;
            packages.Add(new(new(manifest.Id, manifest.Version, manifest.TargetType, manifest.Mount, manifest.Events,
                manifest.EntryPoint, manifest.Capabilities, manifest.TrustDomain, manifest.MaxStatements,
                manifest.TimeoutMilliseconds, manifest.MemoryLimitBytes), source, fingerprint,
                origin));
        }
        return null;
    }
}
