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
    public const string ParamsList = "params.list";
    public const string ParamsSet = "params.set";
    public const string ModelsList = "models.list";
    public const string ModulesList = "modules.list";
    public const string ModulesLoad = "modules.load";
    public const string ModulesUnload = "modules.unload";
    /// <summary>The module <b>packages</b> loaded into this process. Formerly modules.list, which now means the graph.</summary>
    public const string PackagesList = "packages.list";
    public const string ModuleTypes = "system.types";
    /// <summary>Recent signal records, newest last. The WebUI's Signals view reads nothing else (it was dropped
    /// while the other verbs moved out of the host, and a page that asks for an id nobody registered just breaks).</summary>
    public const string SignalsRecent = "signals.recent";
    public const string ScriptsList = "scripts.list";
    public const string ScriptsSet = "scripts.set";
    /// <summary>Reads the script folders again and replaces what the graph is running (docs/25 §7).</summary>
    public const string ScriptsReload = "scripts.reload";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    public static Result RegisterAll(ControlPlane plane, IAdapterManager adapters, IChannelManager channels,
        IModelManager models, ISignalLog signals, IGraphRuntime graph, IParameterRegistry parameters,
        IModuleCatalog? modules = null, ISessionManager? sessions = null, IArchiveRegistry? archive = null,
        IPromptLibrary? prompts = null, IModuleTypeCatalog? moduleTypes = null, IModelInstanceStore? modelInstances = null,
        Func<ModuleGraphStatus>? graphStatus = null, Kernel? kernel = null, IGraphScriptCatalog? scriptCatalog = null,
        IModulePackageMaintenance? packages = null)
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

            (new(SignalsRecent, "Recent signal records, newest last.", Capabilities.SignalsRead, false), RecentSignals),

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
            (new(PackagesList, "Module packages loaded into this process: what each provides, the version, and the "
                    + "directory its code was read from. Also the package folders on disk and which of their versions "
                    + "is in force, so a folder that did not load is visible rather than merely absent.",
                Capabilities.SystemRead, false),
                (_, _) =>
                {
                    var loaded = packages?.LoadedPackages;
                    return ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                    {
                        modules = loaded is not null
                            ? loaded.Select(info => new
                            {
                                id = info.Id,
                                version = info.Version,
                                kind = JsonNamingPolicy.CamelCase.ConvertName(info.PackageKind.ToString()),
                                reload = JsonNamingPolicy.CamelCase.ConvertName(info.ReloadPolicy.ToString()),
                                provides = info.Provides.Select(type => type.TypeId),
                                dependencies = info.Dependencies.Select(dependency => dependency.Id),
                                directory = (string?)info.Directory
                            })
                            : modules?.Loaded.Select(manifest => new
                            {
                                id = manifest.Id,
                                version = manifest.Version,
                                kind = JsonNamingPolicy.CamelCase.ConvertName(manifest.PackageKind.ToString()),
                                reload = JsonNamingPolicy.CamelCase.ConvertName(manifest.ReloadPolicy.ToString()),
                                provides = manifest.Provides.Select(type => type.TypeId),
                                dependencies = manifest.Dependencies.Select(dependency => dependency.Id),
                                directory = (string?)null
                            }) ?? [],
                        // What is on disk, as opposed to what is running: a version folder nobody selected, a pointer
                        // naming a version that is not there, two layouts in one folder. All of those look identical
                        // from the "modules" list above — they are simply absent from it.
                        plugins = PluginFolders(packages),
                        contracts = ModuleContracts.Version
                    })));
                }),
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
            //
            // 2026-11-12：从 full 降到 operator（原来写的是"内核的"）。它不是代码，也不是内核的表，它是图的手术；
            // 而给它 operator 而不是 agent 的理由在下面 unload 那一段——装载会往图里加边、卸载会删目录并让别人的边
            // 掉下来。造适配器/频道/会话各有自己的动词，都已经在 agent，用不着借这条。
            (new(ModulesLoad, "Load one module from its own module.json into the running graph: {\"id\"}.", Capabilities.ModulesControl, true,
                MinLevel: AccessLevel.Operator),
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
            // 2026-11-12：full → operator，与 modules.load 同一个理由。落后半句仍成立：掉下来的边会出现在答复里，
            // 而修它不是这条动词的事。
            (new(ModulesUnload, "Take one module out of the graph. archive defaults to true: its directory goes to the bin.", Capabilities.ModulesControl, true,
                MinLevel: AccessLevel.Operator),
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
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();

        /// <summary>One bad edge as a client reads it: what, on what, and which kind of wrong.</summary>
        static object EdgeOf(BadEdge edge) => new
        {
            master = edge.Master.ToString(),
            slave = edge.Slave.ToString(),
            reason = JsonNamingPolicy.CamelCase.ConvertName(edge.Reason.ToString()),
            detail = edge.Detail
        };
        static Result<JsonElement> Unavailable(string message) => Result<JsonElement>.Failure(new(ErrorCode.Unavailable, message));
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

    /// <summary>
    /// The package folders on disk, whether or not they loaded. A folder that could not be resolved is the one thing a
    /// log line says once and nobody reads again: it is listed here with the reason, next to the folders that worked.
    /// </summary>
    private static object[] PluginFolders(IModulePackageMaintenance? packages)
    {
        if (packages?.PluginsRoot is not { } root) return [];
        var running = packages.LoadedPackages.ToDictionary(info => info.Id, StringComparer.Ordinal);
        return ModulePackageLayout.PackageRoots(root).Select(folder =>
        {
            var name = System.IO.Path.GetFileName(folder);
            var resolved = ModulePackageLayout.Resolve(folder);
            var value = resolved.Ok ? resolved.Value : null;
            running.TryGetValue(name, out var loaded);
            return (object)new
            {
                folder = name,
                layout = value is null ? (string?)null : (value.Legacy ? "flat" : "versions"),
                active = value?.Version,
                versions = value?.Versions ?? ModulePackageLayout.ListVersions(folder),
                running = loaded?.Version,
                problem = value is null ? resolved.Error?.Message : null
            };
        }).ToArray();
    }
}
