using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Archive;
using ALN.Contracts.Models;
using ALN.Contracts.Prompts;
using ALN.Contracts.Sessions;
using ALN.Graph.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The actions the core exposes. Handlers return purpose-built DTOs, never internal objects: a
/// provider spec carries an api key, a session history is unbounded, and an Address is transport
/// detail. Redaction lives here, next to the data, not in each transport.
/// </summary>
public static class CoreActions
{
    /// <summary>Who this process is and how it is doing. The host's own verbs stay here: the kernel's table is not
    /// anybody else's to describe.</summary>
    public const string SystemInfo = "system.info";
    // The channel and adapter verbs live with their modules now (ALN.Channel / ALN.Runtime AdapterManager.Actions):
    // the ids are in ALN.Contracts.ChannelActions / AdapterActions, and each manager registers them itself.
    // The three edge actions were graph.* until the graph grew modules (docs/15). They live under modules.edge now, and
    // the old names stay registered for one release, pointing at the same handlers: a client that says graph.mount
    // should get a mount, not a lesson in naming.
    public const string EdgesList = "modules.edge.list";
    public const string EdgesMount = "modules.edge.mount";
    public const string EdgesUnmount = "modules.edge.unmount";
    public const string GraphList = "graph.list";
    public const string GraphMount = "graph.mount";
    public const string GraphUnmount = "graph.unmount";
    public const string ParamsList = "params.list";
    public const string ParamsSet = "params.set";
    public const string ModelsList = "models.list";
    public const string ModulesList = "modules.list";
    public const string ModulesLoad = "modules.load";
    public const string ModulesUnload = "modules.unload";
    /// <summary>The module <b>packages</b> loaded into this process. Formerly modules.list, which now means the graph.</summary>
    public const string PackagesList = "packages.list";
    public const string ModuleTypes = "system.types";
    public const string ScriptsList = "scripts.list";
    public const string ScriptsSet = "scripts.set";
    public const string ArchiveList = "archive.list";
    public const string ArchiveMove = "archive.move";
    public const string ArchiveRestore = "archive.restore";
    public const string PromptsList = "prompts.list";
    public const string PromptsGet = "prompts.get";
    public const string PromptsSet = "prompts.set";
    public const string ScriptsGet = "scripts.get";
    /// <summary>Reads the script folders again and replaces what the graph is running (docs/25 §7).</summary>
    public const string ScriptsReload = "scripts.reload";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    public static Result RegisterAll(ControlPlane plane, IAdapterManager adapters, IChannelManager channels,
        IModelManager models, ISignalLog signals, IGraphRuntime graph, IParameterRegistry parameters,
        IModuleCatalog? modules = null, ISessionManager? sessions = null, IArchiveRegistry? archive = null,
        IPromptLibrary? prompts = null, IModuleTypeCatalog? moduleTypes = null, IModelInstanceStore? modelInstances = null,
        Func<ModuleGraphStatus>? graphStatus = null, Kernel? kernel = null, IGraphScriptCatalog? scriptCatalog = null)
    {
        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(SystemInfo, "Core version, uptime and counts.", Capabilities.SystemRead, false),
                (_, _) =>
                {
                    // The live graph, when there is one to ask. Null fields say "nobody wired this process to a
                    // kernel", which is a different answer from zero and has to stay distinguishable.
                    var status = graphStatus?.Invoke();
                    return ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                    {
                        core = "ALN",
                        running = true,
                        adapters = adapters.List().Count,
                        channels = channels.List().Count,
                        profiles = models.ListProfiles().Count,
                        instances = status?.Instances,
                        ready = status?.Ready,
                        // The one number a health check can look at (docs/22 §5.2): a system that came up minus two
                        // edges behaves exactly like a whole one, and only this count says so.
                        degraded = status?.Degraded,
                        // Who is being pushed on a schedule, in the same words the startup receipt prints: a client
                        // that re-formats "200ms" would be a second opinion about a number the schedule decided.
                        rhythm = (status?.Rhythms ?? []).Select(beat => beat.Line).ToArray(),
                        badEdges = (status?.BadEdges ?? []).Select(EdgeOf).ToArray()
                    })));
                }),
            (new(EdgesList, "List mounted scripts, durable mount rules, and which rules are still waiting.", Capabilities.ModulesRead, false), ListEdges),
            // One release of forwarding: same handler, same capability, so an old client keeps working and gets the
            // new body. The description says where it went, because that is the only place a caller will look.
            (new(GraphList, "Deprecated: use modules.edge.list.", Capabilities.ModulesRead, false), ListEdges),

            // Manual scripts are the per-instance policy switch: a script declared "mount": "manual" is
            // not attached to anything by its lifecycle, so this is the only way it ever runs.
            (new(EdgesMount, "Mount a manual script onto one instance.", Capabilities.ModulesControl, true), MountScript),
            (new(GraphMount, "Deprecated: use modules.edge.mount.", Capabilities.ModulesControl, true), MountScript),
            (new(EdgesUnmount, "Unmount a script from one instance.", Capabilities.ModulesControl, true), UnmountScript),
            (new(GraphUnmount, "Deprecated: use modules.edge.unmount.", Capabilities.ModulesControl, true), UnmountScript),

            (new(ParamsList, "List every tunable the running components expose, with its current value.", Capabilities.ParamsRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                {
                    // Enums as words: a client that has to know "2 means Reconnect" is a client that will get it
                    // wrong the first time a third value is added.
                    parameters = parameters.List().Select(parameter => new
                    {
                        scope = parameter.Scope,
                        name = parameter.Name,
                        kind = parameter.Kind.ToString().ToLowerInvariant(),
                        value = parameter.Value,
                        defaultValue = parameter.DefaultValue,
                        apply = parameter.Apply.ToString().ToLowerInvariant(),
                        secret = parameter.Secret,
                        minimum = parameter.Minimum,
                        maximum = parameter.Maximum,
                        description = parameter.Description
                    })
                })))),

            (new(ParamsSet, "Change a tunable: validated by its owner, applied now, persisted for restarts.", Capabilities.ParamsWrite, true),
                async (arguments, ct) =>
                {
                    if (!TryReadText(arguments, "scope", out var scope) || scope.Trim().Length == 0)
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "params.set requires 'scope'."));
                    if (!TryReadText(arguments, "name", out var name) || name.Trim().Length == 0)
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "params.set requires 'name'."));
                    if (!TryReadText(arguments, "value", out var value))
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "params.set requires 'value' (send \"\" to clear)."));

                    var changed = await parameters.SetAsync(scope.Trim(), name.Trim(), value, ct).ConfigureAwait(false);
                    if (!changed.Ok) return Result<JsonElement>.Failure(changed.Error!);
                    var current = parameters.List()
                        .FirstOrDefault(p => p.Scope == scope.Trim() && p.Name == name.Trim());
                    return Result<JsonElement>.Success(ToJson(new
                    {
                        scope = scope.Trim(),
                        name = name.Trim(),
                        value = current?.Value,
                        apply = current?.Apply.ToString().ToLowerInvariant(),
                        secret = current?.Secret ?? false
                    }));
                }),

            (new(ScriptsList, "Scripts in the catalog, with their library and whether they are switched on.", Capabilities.GraphRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                {
                    libraries = graph.Scripts.GroupBy(entry => string.IsNullOrWhiteSpace(entry.Package.Origin) ? "user" : entry.Package.Origin)
                        .Select(group => new { library = group.Key, scripts = group.Count(), enabled = group.Count(entry => entry.Enabled) }),
                    scripts = graph.Scripts.Select(entry => new
                    {
                        id = entry.Package.Manifest.Id,
                        library = string.IsNullOrWhiteSpace(entry.Package.Origin) ? "user" : entry.Package.Origin,
                        mount = JsonNamingPolicy.CamelCase.ConvertName(entry.Package.Manifest.Mount.ToString()),
                        trust = JsonNamingPolicy.CamelCase.ConvertName(entry.Package.Manifest.TrustDomain.ToString()),
                        enabled = entry.Enabled,
                        mounted = graph.Mounted.Count(mounted => mounted.Package.Manifest.Id == entry.Package.Manifest.Id)
                    })
                })))),
            (new(ScriptsSet, "Switch one script or a whole library on or off. target: a script id, or bundled / user / plugin:<id>.", Capabilities.GraphControl, false),
                async (arguments, ct) =>
                {
                    if (!TryReadText(arguments, "target", out var target) || target.Trim().Length == 0)
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "scripts.set requires 'target'."));
                    var enabled = !arguments.TryGetProperty("enabled", out var flag) || flag.ValueKind != JsonValueKind.False;
                    var switched = await graph.SetScriptEnabledAsync(target.Trim(), enabled, ct).ConfigureAwait(false);
                    return switched.Ok
                        ? Result<JsonElement>.Success(ToJson(new { target = target.Trim(), enabled, mounted = graph.Mounted.Count }))
                        : Result<JsonElement>.Failure(switched.Error!);
                }),
            (new(ModuleTypes, "Every module type the process knows: what a spec may name, and its abstract family.", Capabilities.SystemRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                {
                    types = (moduleTypes?.ListTypes() ?? []).Select(type => new
                    {
                        typeId = type.TypeId,
                        parent = type.ParentTypeId,
                        @abstract = type.IsAbstract
                    })
                })))),
            (new(PackagesList, "Module packages loaded into this process, with what each provides.", Capabilities.SystemRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                {
                    modules = modules?.Loaded.Select(manifest => new
                    {
                        id = manifest.Id,
                        version = manifest.Version,
                        kind = JsonNamingPolicy.CamelCase.ConvertName(manifest.PackageKind.ToString()),
                        reload = JsonNamingPolicy.CamelCase.ConvertName(manifest.ReloadPolicy.ToString()),
                        provides = manifest.Provides.Select(type => type.TypeId),
                        dependencies = manifest.Dependencies.Select(dependency => dependency.Id)
                    }) ?? [],
                    contracts = ModuleContracts.Version
                })))),
            // The graph, not the packages: which instances exist right now, where each one got to, and what was added or
            // removed since startup. A module is a instance here (docs/22 §3), and the package it came from is a different
            // question with a different answer (packages.list).
            (new(ModulesList, "Modules in the graph right now: id, type, phase, and what is not working.", Capabilities.ModulesRead, false),
                (_, _) =>
                {
                    if (kernel is null) return ValueTask.FromResult(Unavailable("There is no kernel to ask."));
                    var status = kernel.Status;
                    return ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                    {
                        instances = kernel.Instances.Select(instance => new
                        {
                            id = instance.Id.ToString(),
                            type = instance.Type,
                            phase = JsonNamingPolicy.CamelCase.ConvertName(instance.Phase.ToString()),
                            failure = instance.Failure
                        }),
                        ready = status.Ready,
                        degraded = status.Degraded,
                        rhythm = status.Rhythms.Select(beat => beat.Line),
                        badEdges = status.BadEdges.Select(EdgeOf)
                    })));
                }),
            // Loading is reading a module's own file and adding it to the running graph; it is not "scan the disk again".
            // A module that is already there is a conflict, not a reload: replacing one while it works is a different
            // operation with different questions (what happens to what it holds), and it has not been answered yet.
            (new(ModulesLoad, "Load one module from its own module.json into the running graph: {\"id\"}.", Capabilities.ModulesControl, true),
                async (arguments, ct) =>
                {
                    if (kernel is null) return Unavailable("There is no kernel to load anything into.");
                    if (!TryReadText(arguments, "id", out var id) || id.Trim().Length == 0)
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "modules.load requires 'id'."));
                    var loaded = await kernel.LoadAsync(new ModuleId(id.Trim()), ct).ConfigureAwait(false);
                    if (!loaded.Ok) return Result<JsonElement>.Failure(loaded.Error!);
                    var addition = loaded.Value!;
                    return Result<JsonElement>.Success(ToJson(new
                    {
                        id = addition.Instance.Id.ToString(),
                        type = addition.Instance.Type,
                        phase = JsonNamingPolicy.CamelCase.ConvertName(addition.Instance.Phase.ToString()),
                        failure = addition.Instance.Failure,
                        wiredEdges = addition.WiredEdges,
                        badEdges = addition.BadEdges.Select(EdgeOf)
                    }));
                }),
            // Unloading takes the instance out and puts the directory in the bin, because that is what "gone" has meant
            // everywhere else here; keeping it is the explicit choice (archive: false), not the default.
            (new(ModulesUnload, "Take one module out of the graph. archive defaults to true: its directory goes to the bin.", Capabilities.ModulesControl, true),
                async (arguments, ct) =>
                {
                    if (kernel is null) return Unavailable("There is no kernel to unload anything from.");
                    if (!TryReadText(arguments, "id", out var id) || id.Trim().Length == 0)
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "modules.unload requires 'id'."));
                    var archive = !(arguments.ValueKind == JsonValueKind.Object &&
                                    arguments.TryGetProperty("archive", out var flag) && flag.ValueKind == JsonValueKind.False);
                    var unloaded = await kernel.UnloadAsync(new ModuleId(id.Trim()), archive, ct).ConfigureAwait(false);
                    if (!unloaded.Ok) return Result<JsonElement>.Failure(unloaded.Error!);
                    return Result<JsonElement>.Success(ToJson(new
                    {
                        id = id.Trim(),
                        unloaded = true,
                        archived = archive,
                        // Edges that still pointed at it: reported, not repaired. Their files say something the graph
                        // no longer has, and rewriting somebody else's file is not this action's business.
                        droppedEdges = unloaded.Value!.Select(EdgeOf)
                    }));
                }),
            // The bin: what could go in, what is in it, and putting things back. Same two verbs for every kind,
            // so a script or an operator learns it once — an action that only ever did Sessions would have to be
            // relearned for model-call artifacts next.
            (new(ArchiveList, "What could be put away, and what is already in the bin. Read-only.", Capabilities.ArchiveRead, false),
                ListArchive),
            (new(ArchiveMove, "Put candidates in the bin: one kind, all of it or the named ids. dryRun defaults to true.", Capabilities.ArchiveControl, true),
                MoveToArchive),
            (new(ArchiveRestore, "Take items back out of the bin, by id. dryRun defaults to true.", Capabilities.ArchiveControl, true),
                RestoreFromArchive),

            // Prompts are files; this is the "what do I have and where do I edit it" action. There is no write
            // side on purpose: editing a prompt is editing a file, not calling an API.
            (new(PromptsGet, "Read one prompt file's text so it can be edited.", Capabilities.PromptsRead, false), ReadPrompt),
            (new(PromptsSet, "Write one prompt file into the data root (the copy that wins).", Capabilities.PromptsWrite, true), WritePrompt),
            (new(ScriptsGet, "One script package as loaded: manifest and source.", Capabilities.GraphRead, false), ReadScript),
            (new(ScriptsReload, "Read the script folders again and switch to what they now say. Failing scripts keep the body that already works.", Capabilities.GraphControl, false),
                (_, ct) => ReloadScripts(ct)),
            (new(PromptsList, "Prompt groups and the files in them, with their origin and fingerprint.", Capabilities.PromptsRead, false),
                (_, _) =>
                {
                    if (prompts is null) return ValueTask.FromResult(Result<JsonElement>.Failure(
                        new(ErrorCode.Unavailable, "The prompt library is unavailable.")));
                    var catalog = prompts.List();
                    return ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                    {
                        roots = prompts.Roots,
                        groups = catalog.Groups.Select(group => new
                        {
                            group = group.Group,
                            prompts = group.Entries.Select(entry => new
                            {
                                name = entry.Name,
                                bytes = entry.Bytes,
                                fingerprint = entry.Fingerprint,
                                path = entry.Path
                            })
                        }),
                        // Reported rather than swallowed: a prompt that cannot be read is a Session that would
                        // answer as somebody else if nothing said so.
                        warnings = catalog.Warnings
                    })));
                })
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();

        ValueTask<Result<JsonElement>> ListEdges(JsonElement arguments, CancellationToken ct)
        {
            var pending = graph.PendingRules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
            return ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
            {
                mounted = graph.Mounted.Select(m => new
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
                rules = graph.Rules.Select(rule => new
                {
                    id = rule.Id,
                    script = rule.ScriptId,
                    scope = JsonNamingPolicy.CamelCase.ConvertName(rule.Scope.Kind.ToString()),
                    selector = rule.Scope.Selector,
                    source = rule.Source,
                    budget = rule.Budget,
                    remaining = rule.Remaining,
                    stateScope = JsonNamingPolicy.CamelCase.ConvertName(rule.StateScope.ToString()),
                    pending = pending.Contains(rule.Id)
                })
            })));
        }

        /// <summary>One bad edge as a client reads it: what, on what, and which kind of wrong.</summary>
        static object EdgeOf(BadEdge edge) => new
        {
            master = edge.Master.ToString(),
            slave = edge.Slave.ToString(),
            reason = JsonNamingPolicy.CamelCase.ConvertName(edge.Reason.ToString()),
            detail = edge.Detail
        };
        async ValueTask<Result<JsonElement>> ReadPrompt(JsonElement arguments, CancellationToken ct)
        {
            if (prompts is null) return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "The prompt library is unavailable."));
            if (!TryReadText(arguments, "group", out var group) || group.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "prompts.get requires 'group'."));
            if (!TryReadText(arguments, "name", out var name) || name.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "prompts.get requires 'name'."));
            group = group.Trim();
            name = name.Trim();

            // The catalog is what knows where the winning copy lives; resolving alone would answer with the text
            // but not with the path the editor needs to show.
            var catalog = prompts.List();
            var known = catalog.Groups.FirstOrDefault(candidate => string.Equals(candidate.Group, group, StringComparison.Ordinal));
            var entry = known?.Entries.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (entry is null)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                    $"No prompt '{name}' in group '{group}'. Available: {(known is null ? catalog.Groups.Count == 0 ? "(no groups)" : string.Join(", ", catalog.Groups.Select(g => g.Group)) : string.Join(", ", known.Entries.Select(e => e.Name)))}."));

            var selection = new PromptSelection(new Dictionary<string, string>(StringComparer.Ordinal) { [group] = name });
            var resolved = await prompts.ResolveAsync(selection, ct).ConfigureAwait(false);
            if (!resolved.Ok || resolved.Value is null) return Result<JsonElement>.Failure(resolved.Error!);
            return Result<JsonElement>.Success(ToJson(new
            {
                group,
                name,
                path = entry.Path,
                bytes = entry.Bytes,
                fingerprint = entry.Fingerprint,
                text = resolved.Value.Text
            }));
        }

        async ValueTask<Result<JsonElement>> WritePrompt(JsonElement arguments, CancellationToken ct)
        {
            if (prompts is null) return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "The prompt library is unavailable."));
            if (!TryReadText(arguments, "group", out var group) || group.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "prompts.set requires 'group'."));
            if (!TryReadText(arguments, "name", out var name) || name.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "prompts.set requires 'name'."));
            // Required even when empty-looking, so that clearing a prompt is a decision somebody made rather than a
            // field the client forgot to send.
            if (!arguments.TryGetProperty("text", out var textItem) || textItem.ValueKind != JsonValueKind.String)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "prompts.set requires 'text' as a string."));

            var saved = await prompts.SaveAsync(group.Trim(), name.Trim(), textItem.GetString() ?? "", ct).ConfigureAwait(false);
            if (!saved.Ok || saved.Value is null) return Result<JsonElement>.Failure(saved.Error!);
            return Result<JsonElement>.Success(ToJson(new
            {
                group = group.Trim(),
                name = name.Trim(),
                path = saved.Value,
                bytes = System.Text.Encoding.UTF8.GetByteCount(textItem.GetString() ?? "")
            }));
        }

        /// <summary>
        /// Re-reads every script folder and hands the result to the graph. The reading and the rules live in
        /// <see cref="ScriptReload"/> because the same question has two doors — this action and the model's tool — and an
        /// answer that depends on the door is a second implementation waiting to disagree.
        /// </summary>
        ValueTask<Result<JsonElement>> ReloadScripts(CancellationToken ct)
        {
            if (scriptCatalog is null)
                return ValueTask.FromResult(Result<JsonElement>.Failure(
                    new(ErrorCode.Unavailable, "No script catalogue is wired to this process.")));
            return ReloadScriptsCore();

            async ValueTask<Result<JsonElement>> ReloadScriptsCore()
            {
                var reloaded = await ScriptReload.RunAsync(graph, scriptCatalog, ct).ConfigureAwait(false);
                if (!reloaded.Ok || reloaded.Value is null)
                    return Result<JsonElement>.Failure(reloaded.Error ?? new(ErrorCode.Internal, "Reload failed."));
                return Result<JsonElement>.Success(ToJson(new
                {
                    loaded = true,
                    scripts = reloaded.Value.Scripts.Select(status => new
                    {
                        id = status.Id,
                        library = status.Library,
                        enabled = status.Enabled
                    }).ToArray(),
                    rejected = reloaded.Value.Rejected.Select(issue => new { path = issue.Path, reason = issue.Reason }).ToArray()
                }));
            }
        }

        ValueTask<Result<JsonElement>> ReadScript(JsonElement arguments, CancellationToken ct)
        {
            if (!TryReadText(arguments, "id", out var id) || id.Trim().Length == 0)
                return ValueTask.FromResult(Result<JsonElement>.Failure(new(ErrorCode.Invalid, "scripts.get requires 'id'.")));
            var found = graph.Scripts.FirstOrDefault(entry => string.Equals(entry.Package.Manifest.Id, id.Trim(), StringComparison.Ordinal));
            if (found.Package is null)
                return ValueTask.FromResult(Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"No script named '{id.Trim()}'.")));
            var manifest = found.Package.Manifest;
            return ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
            {
                id = manifest.Id,
                version = manifest.Version,
                library = string.IsNullOrWhiteSpace(found.Package.Origin) ? "user" : found.Package.Origin,
                targetType = manifest.TargetType,
                mount = JsonNamingPolicy.CamelCase.ConvertName(manifest.Mount.ToString()),
                trust = JsonNamingPolicy.CamelCase.ConvertName(manifest.TrustDomain.ToString()),
                entryPoint = manifest.EntryPoint,
                events = manifest.Events,
                capabilities = manifest.Capabilities,
                fingerprint = found.Package.Fingerprint,
                enabled = found.Enabled,
                source = found.Package.Source
            })));
        }

        /// <summary>"This process has no graph to talk about" — a different answer from "the graph says zero".</summary>
        static Result<JsonElement> Unavailable(string message) => Result<JsonElement>.Failure(new(ErrorCode.Unavailable, message));

        async ValueTask<Result<JsonElement>> MountScript(JsonElement arguments, CancellationToken ct)
        {
            if (!TryReadText(arguments, "script", out var scriptId) || scriptId.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "graph.mount requires 'script' (the script id)."));

            int? budget = null;
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("budget", out var budgetElement) &&
                budgetElement.ValueKind != JsonValueKind.Null)
            {
                if (!budgetElement.TryGetInt32(out var requested) || requested < 1)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "graph.mount 'budget' must be a positive integer (1 = one dispatch only)."));
                budget = requested;
            }

            var stateScope = ScriptStateScope.PerTarget;
            if (TryReadText(arguments, "stateScope", out var stateScopeText))
            {
                if (!Enum.TryParse<ScriptStateScope>(stateScopeText.Trim(), ignoreCase: true, out var parsed))
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "graph.mount 'stateScope' must be perTarget or shared."));
                stateScope = parsed;
            }

            MountScope scope;
            if (TryReadText(arguments, "scope", out var scopeKind) && scopeKind.Trim().Length > 0)
            {
                if (!TryReadText(arguments, "selector", out var selector) || selector.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "graph.mount with 'scope' also needs 'selector'."));
                switch (scopeKind.Trim().ToLowerInvariant())
                {
                    case "instance": scope = new(MountScopeKind.Instance, selector.Trim()); break;
                    case "type": scope = MountScope.ForType(selector.Trim()); break;
                    case "module": scope = MountScope.ForModule(selector.Trim()); break;
                    default:
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "graph.mount 'scope' must be instance, type or module."));
                }
            }
            else
            {
                // The original shape: one script, one instance.
                var target = ReadAddress(arguments, "target");
                if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
                scope = MountScope.ForInstance(target.Value);
            }

            var added = await graph.AddRuleAsync(new(scriptId.Trim(), scope, budget, stateScope), ct).ConfigureAwait(false);
            if (!added.Ok || added.Value is null) return Result<JsonElement>.Failure(added.Error ?? new(ErrorCode.Internal, "Mount rule was not created."));
            var rule = added.Value;
            return Result<JsonElement>.Success(ToJson(new
            {
                rule = rule.Id,
                script = rule.ScriptId,
                scope = JsonNamingPolicy.CamelCase.ConvertName(rule.Scope.Kind.ToString()),
                selector = rule.Scope.Selector,
                budget = rule.Budget,
                stateScope = JsonNamingPolicy.CamelCase.ConvertName(rule.StateScope.ToString()),
                // Empty means the rule is waiting: the instance it names does not exist yet.
                targets = graph.Mounted.Where(m => m.RuleId == rule.Id).Select(m => m.Target.ToString()).ToArray()
            }));
        }

        async ValueTask<Result<JsonElement>> UnmountScript(JsonElement arguments, CancellationToken ct)
        {
            // A rule is the durable thing; removing one takes its mounts with it.
            if (TryReadText(arguments, "rule", out var ruleId) && ruleId.Trim().Length > 0)
            {
                var removedRule = await graph.RemoveRuleAsync(ruleId.Trim(), ct).ConfigureAwait(false);
                return removedRule.Ok
                    ? Result<JsonElement>.Success(ToJson(new { rule = ruleId.Trim(), removed = true }))
                    : Result<JsonElement>.Failure(removedRule.Error!);
            }

            var target = ReadAddress(arguments, "target");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            if (!TryReadText(arguments, "script", out var scriptId) || scriptId.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "graph.unmount requires 'rule', or 'script' and 'target'."));

            var unmounted = await graph.UnmountAsync(scriptId.Trim(), target.Value, ct).ConfigureAwait(false);
            return unmounted.Ok
                ? Result<JsonElement>.Success(ToJson(new { script = scriptId.Trim(), target = target.Value.ToString(), mounted = false }))
                : Result<JsonElement>.Failure(unmounted.Error!);
        }

        async ValueTask<Result<JsonElement>> ListArchive(JsonElement arguments, CancellationToken ct)
        {
            if (archive is null) return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "The archive module is not loaded."));
            var kind = TryReadText(arguments, "kind", out var requestedKind) ? requestedKind.Trim() : null;
            var candidates = await archive.CandidatesAsync(kind, ct).ConfigureAwait(false);
            if (!candidates.Ok || candidates.Value is null) return Result<JsonElement>.Failure(candidates.Error!);
            var archived = await archive.ArchivedAsync(kind, ct).ConfigureAwait(false);
            if (!archived.Ok || archived.Value is null) return Result<JsonElement>.Failure(archived.Error!);
            return Result<JsonElement>.Success(ToJson(new
            {
                kinds = archive.Kinds,
                candidates = ToJson(CandidatesOf(candidates.Value)),
                archived = ToJson(archived.Value.Select(ItemOf).ToArray()),
                bin = new { items = archived.Value.Count, bytes = archived.Value.Sum(item => item.Bytes) }
            }));
        }

        async ValueTask<Result<JsonElement>> MoveToArchive(JsonElement arguments, CancellationToken ct)
        {
            if (archive is null) return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "The archive module is not loaded."));
            if (!TryReadText(arguments, "kind", out var kind) || kind.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                    $"archive.move requires 'kind' ({string.Join(", ", archive.Kinds)})."));
            var ids = ReadIds(arguments);
            // Dry run unless the caller says otherwise: a missing flag must never be what took a conversation out
            // of service.
            var moved = await archive.ArchiveAsync(kind.Trim(), ids, IsDryRun(arguments), ct).ConfigureAwait(false);
            return moved.Ok && moved.Value is not null
                ? Result<JsonElement>.Success(ToJson(ReportOf(moved.Value)))
                : Result<JsonElement>.Failure(moved.Error!);
        }

        async ValueTask<Result<JsonElement>> RestoreFromArchive(JsonElement arguments, CancellationToken ct)
        {
            if (archive is null) return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "The archive module is not loaded."));
            if (!TryReadText(arguments, "kind", out var kind) || kind.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                    $"archive.restore requires 'kind' ({string.Join(", ", archive.Kinds)})."));
            var ids = ReadIds(arguments);
            if (ids is null)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                    "archive.restore requires 'ids': putting everything back at once is a decision, not a default."));
            var restored = await archive.RestoreAsync(kind.Trim(), ids, IsDryRun(arguments), ct).ConfigureAwait(false);
            return restored.Ok && restored.Value is not null
                ? Result<JsonElement>.Success(ToJson(ReportOf(restored.Value)))
                : Result<JsonElement>.Failure(restored.Error!);
        }

        static object[] CandidatesOf(IReadOnlyList<ArchiveCandidate> candidates) => candidates.Select(candidate => (object)new
        {
            kind = candidate.Kind,
            id = candidate.Id,
            bytes = candidate.Bytes,
            lastActiveAt = candidate.LastUsedAt,
            owner = candidate.Owner,
            reason = candidate.Reason
        }).ToArray();

        static object ItemOf(ArchivedItem item) => new
        {
            kind = item.Kind,
            id = item.Id,
            bytes = item.Bytes,
            archivedAt = item.ArchivedAt,
            owner = item.Owner,
            reason = item.Reason,
            note = item.Note,
            path = item.Path
        };

        static object ReportOf(ArchiveReport report) => new
        {
            dryRun = report.DryRun,
            count = report.Count,
            done = report.Done,
            bytes = report.Bytes,
            items = report.Items.Select(item => new
            {
                kind = item.Kind,
                id = item.Id,
                action = item.Action,
                done = item.Done,
                bytes = item.Bytes,
                message = item.Message
            })
        };

        static bool IsDryRun(JsonElement arguments) =>
            !(arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("dryRun", out var flag) &&
              flag.ValueKind == JsonValueKind.False);

        static IReadOnlyList<string>? ReadIds(JsonElement arguments)
        {
            if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("ids", out var ids) ||
                ids.ValueKind != JsonValueKind.Array) return null;
            var values = ids.EnumerateArray()
                .Where(id => id.ValueKind == JsonValueKind.String)
                .Select(id => id.GetString() ?? "")
                .Where(id => id.Length > 0)
                .ToArray();
            return values.Length == 0 ? null : values;
        }

        ValueTask<Result<JsonElement>> RecentSignals(JsonElement arguments, CancellationToken ct)
        {
            var limit = 200;
            if (arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var requested))
                limit = Math.Clamp(requested, 1, 1_000);
            var group = arguments.ValueKind == JsonValueKind.Object &&
                        arguments.TryGetProperty("group", out var groupElement) && groupElement.ValueKind == JsonValueKind.String
                ? groupElement.GetString()
                : null;
            var records = signals.Snapshot()
                .Where(r => group is null || string.Equals(r.Group, group, StringComparison.Ordinal))
                .TakeLast(limit)
                .Select(r => new
                {
                    at = r.At,
                    level = r.Level.ToString(),
                    group = r.Group,
                    name = r.Name,
                    step = r.StepId,
                    outcome = r.Outcome,
                    durationMs = Math.Round(r.DurationMs, 1),
                    detail = r.Detail
                });
            return ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new { records })));
        }
    }

    /// <summary>
    /// "adapter.file" + "file-1" → adapter.file#file-1, through the same parser that reads them back, so a name
    /// the server would later refuse cannot be produced here.
    /// </summary>
    private static Result<Address> BuildAddress(string typeId, string instance)
    {
        if (typeId.Contains('#') || instance.Contains('#') || instance.Any(char.IsWhiteSpace))
            return Result<Address>.Failure(new(ErrorCode.Invalid, $"Not a usable instance name: {instance}"));
        if (!Address.TryParse($"{typeId}#{instance}", out var address) || !address.IsInstance)
            return Result<Address>.Failure(new(ErrorCode.Invalid, $"Not an instance address: {typeId}#{instance}"));
        return Result<Address>.Success(address);
    }

    private static bool TryReadText(JsonElement arguments, string property, out string value)
    {
        value = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(property, out var found) ||
            found.ValueKind != JsonValueKind.String) return false;
        value = found.GetString() ?? string.Empty;
        return true;
    }

    private static Result<Address> ReadAddress(JsonElement arguments, string property)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String || !Address.TryParse(value.GetString(), out var address) ||
            !address.IsInstance)
            return Result<Address>.Failure(new(ErrorCode.Invalid, $"Argument '{property}' must be an instance address."));
        return Result<Address>.Success(address);
    }
}
