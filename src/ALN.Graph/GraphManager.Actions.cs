using System.Text.Json;
using ALN.Contracts;
using ALN.Graph.Contracts;

namespace ALN.Graph;

/// <summary>
/// The mount verbs, declared by the graph that owns the rules.
///
/// A mount rule is the graph's data and the graph's only writable surface, so "who may mount what and how" has always
/// been its business. It lived in the host because every verb did; the rule about what a scope may name was written
/// here all along.
/// </summary>
public sealed partial class GraphManager
{
    public Result RegisterActions(IControlPlane plane)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        _plane = plane;
        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(GraphActions.EdgeList, "List mounted scripts, durable mount rules, and which rules are still waiting "
                    + "for their target to exist.", Capabilities.ModulesRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""), ListEdgesAsync),
            // One release of forwarding: same handler, same capability, so a client that says graph.mount gets a mount
            // rather than a lesson in naming.
            (new(GraphActions.DeprecatedList, "Deprecated: use modules.edge.list.", Capabilities.ModulesRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""), ListEdgesAsync),


            (new(GraphActions.EdgeUnmount, "Unmount a script from an instance, or remove a rule by id (which takes its "
                    + "mounts with it).", Capabilities.ModulesControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "rule":{"type":"string","description":"Remove this rule and its mounts."},
                  "script":{"type":"string","description":"With 'target': drop that script's mounts on that instance."},
                  "target":{"type":"string","description":"An instance address."}},
                 "additionalProperties":false}
                """, Subject: "rule", MinLevel: AccessLevel.Agent), UnmountAsync),
            (new(GraphActions.DeprecatedUnmount, "Deprecated: use modules.edge.unmount.", Capabilities.ModulesControl, true,
                ParamsJson: GraphActions.UnmountParams, Subject: "rule"), UnmountAsync)
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }

        // The two verbs whose effect depends on who is calling: a mount hands a script the events of an instance, so a
        // caller may only mount a script at or below its own level (docs/07 §4). The body receives the actor because
        // this cannot be read off arguments, which are only what the caller says about itself.
        var guarded = new (ActionDescriptor Descriptor, AuthorizedActionHandler Handler)[]
        {
            (new(GraphActions.EdgeMount, "Mount a script on an instance, a type or a module. A rule is the durable thing: "
                    + "it keeps applying to instances that appear later, and 'budget' limits how many dispatches each "
                    + "resolved target gets.", Capabilities.ModulesControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "script":{"type":"string","description":"The script id."},
                  "target":{"type":"string","description":"An instance address. The simple form: scope defaults to it."},
                  "scope":{"type":"string","enum":["instance","type","module"],"description":"With 'selector': what the rule matches."},
                  "selector":{"type":"string","description":"An address, a type id, or a category."},
                  "budget":{"type":"integer","minimum":1,"description":"Dispatches allowed per target. Omit for unlimited."},
                  "stateScope":{"type":"string","enum":["perTarget","shared"],"description":"Whether the script's memory is per target. Default perTarget."}},
                 "required":["script"],"additionalProperties":false}
                """, Subject: "script", MinLevel: AccessLevel.Agent), MountAsync),
            (new(GraphActions.DeprecatedMount, "Deprecated: use modules.edge.mount.", Capabilities.ModulesControl, true,
                ParamsJson: GraphActions.MountParams, Subject: "script", MinLevel: AccessLevel.Agent), MountAsync)
        };
        foreach (var (descriptor, handler) in guarded)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    private ValueTask<Result<JsonElement>> ListEdgesAsync(JsonElement arguments, CancellationToken ct)
    {
        _ = arguments;
        ct.ThrowIfCancellationRequested();
        var pending = PendingRules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        return ValueTask.FromResult(Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            mounted = Mounted.Select(m => new
            {
                script = m.Package.Manifest.Id,
                version = m.Package.Manifest.Version,
                targetType = m.Package.Manifest.TargetType,
                events = m.Package.Manifest.Events,
                trustDomain = m.Package.Manifest.TrustDomain.ToString(),
                target = m.Target.ToString(),
                rule = m.RuleId,
                remaining = m.Remaining
            }),
            rules = Rules.Select(rule => new
            {
                id = rule.Id,
                script = rule.ScriptId,
                scope = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(rule.Scope.Kind.ToString()),
                selector = rule.Scope.Selector,
                source = rule.Source,
                budget = rule.Budget,
                remaining = rule.Remaining,
                stateScope = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(rule.StateScope.ToString()),
                pending = pending.Contains(rule.Id)
            })
        })));
    }

    /// <summary>
    /// Adds one rule. The simple form names a target instance; the scope form names a selector that keeps matching
    /// instances that do not exist yet — which is the whole reason a rule is a rule and not a list of mounts.
    /// </summary>
    private async ValueTask<Result<JsonElement>> MountAsync(Actor actor, JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryReadText("script", out var scriptId) || scriptId.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "modules.edge.mount requires 'script' (the script id)."));

        int? budget = null;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("budget", out var budgetElement) &&
            budgetElement.ValueKind != JsonValueKind.Null)
        {
            if (!budgetElement.TryGetInt32(out var requested) || requested < 1)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                    "modules.edge.mount 'budget' must be a positive integer (1 = one dispatch only)."));
            budget = requested;
        }

        var stateScope = ScriptStateScope.PerTarget;
        if (arguments.TryReadText("stateScope", out var stateScopeText))
        {
            if (!Enum.TryParse<ScriptStateScope>(stateScopeText.Trim(), ignoreCase: true, out var parsed))
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "modules.edge.mount 'stateScope' must be perTarget or shared."));
            stateScope = parsed;
        }

        MountScope scope;
        if (arguments.TryReadText("scope", out var scopeKind) && scopeKind.Trim().Length > 0)
        {
            if (!arguments.TryReadText("selector", out var selector) || selector.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "modules.edge.mount with 'scope' also needs 'selector'."));
            switch (scopeKind.Trim().ToLowerInvariant())
            {
                case "instance": scope = new(MountScopeKind.Instance, selector.Trim()); break;
                case "type": scope = MountScope.ForType(selector.Trim()); break;
                case "module": scope = MountScope.ForModule(selector.Trim()); break;
                default:
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                        "modules.edge.mount 'scope' must be instance, type or module."));
            }
        }
        else
        {
            // The original shape: one script, one instance.
            var target = arguments.ReadAddress("target");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            scope = MountScope.ForInstance(target.Value);
        }

        var added = await AddRuleAsync(new(scriptId.Trim(), scope, budget, stateScope, actor.Level), ct).ConfigureAwait(false);
        if (!added.Ok || added.Value is null)
            return Result<JsonElement>.Failure(added.Error ?? new(ErrorCode.Internal, "Mount rule was not created."));
        var rule = added.Value;
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            rule = rule.Id,
            script = rule.ScriptId,
            scope = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(rule.Scope.Kind.ToString()),
            selector = rule.Scope.Selector,
            budget = rule.Budget,
            stateScope = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(rule.StateScope.ToString()),
            // Empty means the rule is waiting: the instance it names does not exist yet.
            targets = Mounted.Where(m => m.RuleId == rule.Id).Select(m => m.Target.ToString()).ToArray()
        }));
    }

    private async ValueTask<Result<JsonElement>> UnmountAsync(JsonElement arguments, CancellationToken ct)
    {
        // A rule is the durable thing; removing one takes its mounts with it.
        if (arguments.TryReadText("rule", out var ruleId) && ruleId.Trim().Length > 0)
        {
            var removedRule = await RemoveRuleAsync(ruleId.Trim(), ct).ConfigureAwait(false);
            return removedRule.Ok
                ? Result<JsonElement>.Success(ActionArgs.JsonOf(new { rule = ruleId.Trim(), removed = true }))
                : Result<JsonElement>.Failure(removedRule.Error!);
        }

        var target = arguments.ReadAddress("target");
        if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
        if (!arguments.TryReadText("script", out var scriptId) || scriptId.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                "modules.edge.unmount requires 'rule', or 'script' and 'target'."));

        var unmounted = await UnmountAsync(scriptId.Trim(), target.Value, ct).ConfigureAwait(false);
        return unmounted.Ok
            ? Result<JsonElement>.Success(ActionArgs.JsonOf(new
            {
                script = scriptId.Trim(), target = target.Value.ToString(), mounted = false
            }))
            : Result<JsonElement>.Failure(unmounted.Error!);
    }
}
