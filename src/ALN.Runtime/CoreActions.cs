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
    public const string SystemInfo = "system.info";
    public const string AdaptersList = "adapters.list";
    public const string AdaptersStart = "adapters.start";
    public const string AdaptersStop = "adapters.stop";
    public const string AdaptersCreate = "adapters.create";
    public const string AdaptersDestroy = "adapters.destroy";
    public const string ChannelsList = "channels.list";
    public const string ChannelsStart = "channels.start";
    public const string ChannelsStop = "channels.stop";
    public const string ChannelsCreate = "channels.create";
    public const string ChannelsDestroy = "channels.destroy";
    public const string ChannelsSend = "channels.send";
    public const string GraphList = "graph.list";
    public const string GraphMount = "graph.mount";
    public const string ParamsList = "params.list";
    public const string ParamsSet = "params.set";
    public const string GraphUnmount = "graph.unmount";
    public const string ModelsList = "models.list";
    public const string ModulesList = "modules.list";
    public const string ModuleTypes = "system.types";
    public const string ScriptsList = "scripts.list";
    public const string ScriptsSet = "scripts.set";
    public const string SignalsRecent = "signals.recent";
    public const string SessionsList = "sessions.list";
    public const string ArchiveList = "archive.list";
    public const string ArchiveMove = "archive.move";
    public const string ArchiveRestore = "archive.restore";
    public const string PromptsList = "prompts.list";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    public static Result RegisterAll(ControlPlane plane, IAdapterManager adapters, IChannelManager channels,
        IModelManager models, ISignalLog signals, IGraphRuntime graph, IParameterRegistry parameters,
        IModuleCatalog? modules = null, ISessionManager? sessions = null, IArchiveRegistry? archive = null,
        IPromptLibrary? prompts = null, IModuleTypeCatalog? moduleTypes = null)
    {
        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(SystemInfo, "Core version, uptime and counts.", Capabilities.SystemRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                {
                    core = "ALN",
                    running = true,
                    adapters = adapters.List().Count,
                    channels = channels.List().Count,
                    profiles = models.ListProfiles().Count
                })))),

            (new(AdaptersList, "List adapters without credentials.", Capabilities.AdaptersRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(
                    adapters.List().Select(a => new
                    {
                        address = a.Address.ToString(),
                        platform = a.Descriptor.PlatformId,
                        // The declared label, and — when the adapter can learn it — who it actually is.
                        account = Identity(a.Address)?.AccountId ?? a.Descriptor.AccountId,
                        declaredAccount = a.Descriptor.AccountId,
                        displayName = Identity(a.Address)?.DisplayName,
                        direction = a.Descriptor.Direction.ToString(),
                        state = a.State.ToString(),
                        // Whether the connection behind the state is actually up. Null when the adapter
                        // type has no opinion, so "false" never means "unknown".
                        connected = Health(a.Address)?.Connected,
                        detail = Health(a.Address)?.HealthDetail
                    }))))),

            (new(AdaptersStart, "Start one adapter.", Capabilities.AdaptersControl, true), StartAdapter),
            (new(AdaptersStop, "Stop one adapter.", Capabilities.AdaptersControl, true), StopAdapter),
            (new(AdaptersCreate, "Create one adapter instance: {\"type\", \"instance\", \"platform\", \"account\", \"direction\", \"settings\"}.", Capabilities.AdaptersControl, true), CreateAdapter),
            (new(AdaptersDestroy, "Destroy one adapter instance and forget its spec.", Capabilities.AdaptersControl, true), DestroyAdapter),

            (new(ChannelsList, "List channels and their bindings.", Capabilities.ChannelsRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(
                    channels.List().Select(c => new
                    {
                        address = c.Address.ToString(),
                        name = c.Name,
                        running = c.Running,
                        maxMessages = c.MaxMessages,
                        bindings = c.Bindings.Select(b => new
                        {
                            adapter = b.Adapter.ToString(),
                            conversation = b.PlatformConversationId
                        })
                    }))))),

            (new(ChannelsStart, "Start one channel.", Capabilities.ChannelsControl, true), StartChannel),
            (new(ChannelsStop, "Stop one channel.", Capabilities.ChannelsControl, true), StopChannel),
            (new(ChannelsSend, "Send one text message through a channel's binding.", Capabilities.MessagesWrite, true), SendChannel),
            (new(ChannelsCreate, "Create one channel instance: {\"type\", \"instance\", \"name\", \"bindings\": [{\"adapter\", \"conversation\"}], \"maxMessages\"}.", Capabilities.ChannelsControl, true), CreateChannel),
            (new(ChannelsDestroy, "Destroy one channel instance and forget its spec.", Capabilities.ChannelsControl, true), DestroyChannel),

            (new(GraphList, "List mounted scripts, durable mount rules, and which rules are still waiting.", Capabilities.GraphRead, false),
                (_, _) =>
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
                }),

            // Manual scripts are the per-instance policy switch: a script declared "mount": "manual" is
            // not attached to anything by its lifecycle, so this is the only way it ever runs.
            (new(GraphMount, "Mount a manual script onto one instance.", Capabilities.GraphControl, true), MountScript),
            (new(GraphUnmount, "Unmount a script from one instance.", Capabilities.GraphControl, true), UnmountScript),

            (new(ParamsList, "List every tunable the running components expose, with its current value.", Capabilities.ParamsRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new { parameters = parameters.List() })))),

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
                        apply = current?.Apply.ToString(),
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
            (new(ModulesList, "Module packages loaded into this process, with what each provides.", Capabilities.SystemRead, false),
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
            (new(ModelsList, "List provider instances and profiles without configuration or keys.", Capabilities.ModelsRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                {
                    // Only addresses and type ids: ModelProviderInfo carries configuration, which can
                    // hold an api key, so it is never serialized here.
                    providers = models.List().Select(p => new { address = p.Address.ToString() }),
                    profiles = models.ListProfiles().Select(p => new
                    {
                        id = p.Id,
                        provider = p.Provider.ToString(),
                        model = p.ModelId
                    })
                })))),

            (new(SignalsRecent, "Recent signal records, newest last.", Capabilities.SignalsRead, false), RecentSignals),

            // Sessions are the only thing in the data root that is created without a spec the operator ever
            // wrote (a Channel's script mints them), so they are also the only thing that needs collecting.
            (new(SessionsList, "Sessions on disk: whose they are, when they last spoke, how much room they take.", Capabilities.SessionsRead, false),
                async (_, ct) =>
                {
                    if (sessions is null) return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "Session manager is unavailable."));
                    var listed = await sessions.ListAsync(ct).ConfigureAwait(false);
                    if (!listed.Ok || listed.Value is null) return Result<JsonElement>.Failure(listed.Error!);
                    return Result<JsonElement>.Success(ToJson(new
                    {
                        sessions = listed.Value.Select(session => new
                        {
                            id = session.Id,
                            channel = session.Channel,
                            lastActiveAt = session.LastActiveAt,
                            steps = session.Steps,
                            bytes = session.Bytes
                        })
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

        async ValueTask<Result<JsonElement>> DestroyAdapter(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            var destroyed = await adapters.DestroyAsync(target.Value, ct).ConfigureAwait(false);
            return destroyed.Ok
                ? Result<JsonElement>.Success(ToJson(new { address = target.Value.ToString(), destroyed = true }))
                : Result<JsonElement>.Failure(destroyed.Error!);
        }

        /// <summary>
        /// Creates one adapter from a form's worth of arguments.
        ///
        /// The address is built from the type and the instance name, never from arguments that spell it out: an
        /// address is a name, and a caller that can choose "adapter.file#x" while naming type "adapter.qq" is a
        /// caller that can make the two disagree before the spec is even stored.
        /// </summary>
        async ValueTask<Result<JsonElement>> CreateAdapter(JsonElement arguments, CancellationToken ct)
        {
            if (!TryReadText(arguments, "type", out var typeId) || typeId.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "adapters.create requires 'type'."));
            var type = typeId.Trim();
            if (!type.StartsWith("adapter.", StringComparison.Ordinal))
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"Not an adapter type: {type}"));
            if (moduleTypes is { } catalog && !catalog.IsConcrete(type))
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"Adapter type is not registered in this process: {type}"));
            if (!TryReadText(arguments, "instance", out var instance) || instance.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "adapters.create requires 'instance'."));
            var address = BuildAddress(type, instance.Trim());
            if (!address.Ok) return Result<JsonElement>.Failure(address.Error!);

            var platform = TryReadText(arguments, "platform", out var platformText) && platformText.Trim().Length > 0
                ? platformText.Trim()
                : address.Value.Kind!;
            var account = TryReadText(arguments, "account", out var accountText) && accountText.Trim().Length > 0
                ? accountText.Trim()
                : instance.Trim();
            // Direction decides which half of IChatAdapter anyone may call, so a typo must be refused rather than
            // defaulted: "inboud" quietly becoming Both turns an inbound-only account into a sender.
            var direction = AdapterDirection.Both;
            if (TryReadText(arguments, "direction", out var directionText) && directionText.Trim().Length > 0)
            {
                if (!Enum.TryParse<AdapterDirection>(directionText.Trim(), ignoreCase: true, out direction) || !Enum.IsDefined(direction))
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                        $"adapters.create 'direction' must be inbound, outbound or both, not '{directionText}'."));
            }

            var settings = ReadSettings(arguments, out var settingsError);
            if (settingsError is not null) return Result<JsonElement>.Failure(settingsError);

            var spec = new AdapterSpec(address.Value, new AdapterDescriptor(platform, account, direction), type, settings);
            var created = await adapters.CreateAsync(spec, ct).ConfigureAwait(false);
            if (!created.Ok || created.Value is null) return Result<JsonElement>.Failure(created.Error ?? new(ErrorCode.Unavailable, "Adapter could not be created."));
            return Result<JsonElement>.Success(ToJson(new
            {
                address = address.Value.ToString(),
                type,
                platform,
                account,
                direction = direction.ToString().ToLowerInvariant()
            }));
        }

        async ValueTask<Result<JsonElement>> DestroyChannel(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            var destroyed = await channels.DestroyAsync(target.Value, ct).ConfigureAwait(false);
            return destroyed.Ok
                ? Result<JsonElement>.Success(ToJson(new { address = target.Value.ToString(), destroyed = true }))
                : Result<JsonElement>.Failure(destroyed.Error!);
        }

        async ValueTask<Result<JsonElement>> CreateChannel(JsonElement arguments, CancellationToken ct)
        {
            if (!TryReadText(arguments, "type", out var typeId) || typeId.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "channels.create requires 'type'."));
            var type = typeId.Trim();
            if (!type.StartsWith("channel.", StringComparison.Ordinal))
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"Not a channel type: {type}"));
            if (moduleTypes is { } catalog && !catalog.IsConcrete(type))
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"Channel type is not registered in this process: {type}"));
            if (!TryReadText(arguments, "instance", out var instance) || instance.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "channels.create requires 'instance'."));
            var address = BuildAddress(type, instance.Trim());
            if (!address.Ok) return Result<JsonElement>.Failure(address.Error!);
            var name = TryReadText(arguments, "name", out var nameText) && nameText.Trim().Length > 0 ? nameText.Trim() : instance.Trim();

            var bindings = new List<ChannelBinding>();
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("bindings", out var bindingItems))
            {
                if (bindingItems.ValueKind != JsonValueKind.Array)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "channels.create 'bindings' must be an array."));
                foreach (var item in bindingItems.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("adapter", out var adapterText) || adapterText.ValueKind != JsonValueKind.String ||
                        !Address.TryParse(adapterText.GetString(), out var adapterAddress) || !adapterAddress.IsInstance)
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "Each binding needs 'adapter' as an instance address."));
                    if (!item.TryGetProperty("conversation", out var conversationText) || conversationText.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(conversationText.GetString()))
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "Each binding needs a non-empty 'conversation'."));
                    bindings.Add(new ChannelBinding(adapterAddress, conversationText.GetString()!.Trim()));
                }
            }

            int? maxMessages = null;
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("maxMessages", out var window) &&
                window.ValueKind != JsonValueKind.Null)
            {
                if (window.ValueKind != JsonValueKind.Number || !window.TryGetInt32(out var parsed) || parsed <= 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "channels.create 'maxMessages' must be a positive integer."));
                maxMessages = parsed;
            }

            var spec = new ChannelSpec(address.Value, name, bindings, type, maxMessages);
            var created = await channels.CreateAsync(spec, ct).ConfigureAwait(false);
            if (!created.Ok || created.Value is null) return Result<JsonElement>.Failure(created.Error ?? new(ErrorCode.Unavailable, "Channel could not be created."));
            return Result<JsonElement>.Success(ToJson(new
            {
                address = address.Value.ToString(),
                type,
                name,
                bindings = bindings.Select(binding => new { adapter = binding.Adapter.ToString(), conversation = binding.PlatformConversationId }),
                maxMessages
            }));
        }

        async ValueTask<Result<JsonElement>> StartAdapter(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            var started = await adapters.StartAsync(target.Value, ct).ConfigureAwait(false);
            return started.Ok
                ? Result<JsonElement>.Success(ToJson(new { address = target.Value.ToString(), state = "running" }))
                : Result<JsonElement>.Failure(started.Error!);
        }

        async ValueTask<Result<JsonElement>> StopAdapter(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            var stopped = await adapters.StopAsync(target.Value, ct).ConfigureAwait(false);
            return stopped.Ok
                ? Result<JsonElement>.Success(ToJson(new { address = target.Value.ToString(), state = "stopped" }))
                : Result<JsonElement>.Failure(stopped.Error!);
        }

        async ValueTask<Result<JsonElement>> StartChannel(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            var started = await channels.StartAsync(target.Value, ct).ConfigureAwait(false);
            return started.Ok
                ? Result<JsonElement>.Success(ToJson(new { address = target.Value.ToString(), state = "running" }))
                : Result<JsonElement>.Failure(started.Error!);
        }

        async ValueTask<Result<JsonElement>> StopChannel(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            var stopped = await channels.StopAsync(target.Value, ct).ConfigureAwait(false);
            return stopped.Ok
                ? Result<JsonElement>.Success(ToJson(new { address = target.Value.ToString(), state = "stopped" }))
                : Result<JsonElement>.Failure(stopped.Error!);
        }

        // Optional per-adapter health: the state says whether somebody asked it to run, this says
        // whether it is actually connected right now.
        IAdapterHealth? Health(Address address)
        {
            var found = adapters.Get(address);
            return found.Ok ? found.Value as IAdapterHealth : null;
        }

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

        IAdapterIdentity? Identity(Address address)
        {
            var found = adapters.Get(address);
            return found.Ok ? found.Value as IAdapterIdentity : null;
        }

        /// <summary>
        /// One text, one binding, no policy: this is the action a human or a test uses to prove that the
        /// Channel → Adapter → platform path works without waiting for somebody to talk to the bot.
        ///
        /// It refuses a channel with zero or several bindings rather than picking one: guessing which
        /// conversation a message was meant for is exactly the kind of guess that ends up in the wrong
        /// group.
        /// </summary>
        async ValueTask<Result<JsonElement>> SendChannel(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            if (!TryReadText(arguments, "text", out var text) || text.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "channels.send requires a non-empty 'text'."));
            var replyTo = TryReadText(arguments, "replyTo", out var reply) ? reply : null;

            var channel = channels.Get(target.Value);
            if (!channel.Ok || channel.Value is null) return Result<JsonElement>.Failure(channel.Error!);
            if (!channel.Value.Running)
                return Result<JsonElement>.Failure(new(ErrorCode.Conflict, $"Channel is not running: {target.Value}"));
            if (channel.Value.Bindings.Count != 1)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                    $"Channel has {channel.Value.Bindings.Count} bindings; channels.send needs exactly one. " +
                    "Sending to a chosen binding among several is not implemented."));

            var binding = channel.Value.Bindings[0];
            var adapter = adapters.Get(binding.Adapter);
            if (!adapter.Ok || adapter.Value is null) return Result<JsonElement>.Failure(adapter.Error!);

            var sent = await adapter.Value.SendAsync(new OutboundMessage(binding.PlatformConversationId, text, replyTo), ct)
                .ConfigureAwait(false);
            if (!sent.Ok || sent.Value is null) return Result<JsonElement>.Failure(sent.Error!);
            return Result<JsonElement>.Success(ToJson(new
            {
                channel = target.Value.ToString(),
                adapter = binding.Adapter.ToString(),
                conversation = binding.PlatformConversationId,
                sent = sent.Value.Sent,
                messageId = sent.Value.AttemptId,
                detail = sent.Value.Detail
            }));
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

    /// <summary>
    /// Adapter settings as a flat string map. Strict on purpose: an object value here would be stored as
    /// "[object Object]" by whoever reads it next, and a number that silently became "1.0" is a config bug
    /// nobody can see in the file.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ReadSettings(JsonElement arguments, out Error? error)
    {
        error = null;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("settings", out var settings) ||
            settings.ValueKind == JsonValueKind.Null) return null;
        if (settings.ValueKind != JsonValueKind.Object)
        {
            error = new(ErrorCode.Invalid, "adapters.create 'settings' must be an object of string values.");
            return null;
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in settings.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                error = new(ErrorCode.Invalid, $"Setting '{property.Name}' must be a string.");
                return null;
            }
            map[property.Name] = property.Value.GetString() ?? "";
        }
        return map;
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
