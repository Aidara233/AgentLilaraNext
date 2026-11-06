using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Archive;
using ALN.Contracts.Models;
using ALN.Contracts.Prompts;
using ALN.Contracts.Sessions;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;
using ALN.Storage.Sqlite;

var dataArg = Array.FindIndex(args, a => a == "--data-dir");
if (dataArg < 0)
{
    Console.Error.WriteLine("An explicit storage directory is required. Pass --data-dir <path>.");
    return 2;
}
if (dataArg + 1 >= args.Length || string.IsNullOrWhiteSpace(args[dataArg + 1]))
{
    Console.Error.WriteLine("--data-dir requires a non-empty path.");
    return 2;
}
var data = DataDirectory.Open(args[dataArg + 1]);
// Transport state, not configuration: a file Adapter's mailbox is IO and lives under io/file/<account>/, which the
// adapter package derives from the spec it is handed. The host no longer hands out one mailbox for everybody — that
// made the second file Adapter read the first one's inbox.
var defaultInbox = Path.Combine(data.Root, "io", "file", "file-1", "inbox");
// One file for everything a program writes down: component state, message history, audit records. The
// spec tree stays files — those a person writes and reads.
using var storage = new SqliteStore(Path.Combine(data.Root, "aln.db"));
IStateStore stateStore = storage;
IMessageStore messageStore = storage;
IAuditSink auditSink = storage;
// Nothing reads the old file layout any more; say so instead of silently ignoring what is there.
foreach (var legacy in new[] { "state", "messages", "audit" })
    if (Directory.Exists(Path.Combine(data.Root, legacy)))
        Console.Error.WriteLine($"note: {legacy}/ is left over from file storage and is ignored; aln.db is the source of truth now.");
// The spec trees moved too: an Adapter, Channel or Session spec is a module file under modules/<id>/, and nothing
// reads <DataRoot>/adapters, /channels or /sessions. A spec written there is a no-op, and a no-op looks exactly like
// a spec that was read and rejected on purpose — which is how a person ends up checking the code for a mistake the
// disk made. So the directory gets a line, the same way the old file layout does.
foreach (var stale in new[] { "adapters", "channels", "sessions" })
    if (Directory.Exists(Path.Combine(data.Root, stale)))
        Console.Error.WriteLine($"note: {stale}/ is not read; instance specs live in " +
            $"{ModuleManifestStore.DirectoryName}/<id>/{ModuleManifestStore.FileName}.");
await using var signalLog = SignalLog.Open(Path.Combine(data.Root, "logs"));
var moduleLoggers = new SignalModuleLoggerFactory(signalLog);
var queue = new EventQueue(capacity: 256);
// The address book. Not a module itself: if it were, finding a module would require a module.
var modules = new ModuleCatalog();
var adapterTypes = new AdapterTypeCatalog();
// The spec stores are given the type tree: "is this record mine" is a question about types, not about how a type id
// happens to be spelled (C-04/C-06).
var adapterSpecs = new FileAdapterSpecStore(data.Root, adapterTypes);
var channelSpecs = new FileChannelSpecStore(data.Root, adapterTypes);
var bindings = new InstanceBindingsProxy();
var adapterFactories = new AdapterFactoryRegistry();
// One registry for every tunable: components register themselves, the registry validates, applies, persists.
var parameters = new ParameterRegistry(stateStore);
var adapters = new AdapterManager(adapterTypes, bindings, adapterFactories, adapterSpecs, parameters: parameters);
var artifacts = new FileArtifactStore(Path.Combine(data.Root, "artifacts"));
var modelManager = new ModelManager(artifacts, signalLog);
var sessionExtensions = new SessionExtensionRegistry();
var timeSource = sessionExtensions.Register(new TimeContextSource());
if (!timeSource.Ok) { Console.Error.WriteLine(timeSource.Error?.Message); return 1; }
var channelInfoSource = sessionExtensions.Register(new ChannelInfoSource());
if (!channelInfoSource.Ok) { Console.Error.WriteLine(channelInfoSource.Error?.Message); return 1; }
// Everything a model may reach on disk goes through this one port, and the scopes it knows are registered here and
// nowhere else. The memory library is the first; another is added by registering it here and naming it in that
// module's own module.json — never by a module inventing a path.
var fileAccess = new LocalFileSystemAccess(data.Root, [FileScopes.Memory, FileScopes.ScriptsDraft]);
// Memory is a module of its own: it registers its own line and its own tools, and nothing else knows it exists. It
// reads and writes through the port above, so "the model may tidy memory with file tools" needs no special case:
// they reach the same files by the same rules.
var memoryLibrary = new FileMemoryLibrary(fileAccess);
var memoryModule = new MemoryModule(memoryLibrary, sessionExtensions);
// The second half of activation: the Session pulls whatever the Channel has not handed over yet at the
// moment it assembles the request, so activation itself can stay a signal with no payload. Its cursor is
// component state, so it goes through the state store rather than a path the host invents.
// The startup receipt: whoever restores something says what it restored, and the host prints it once.
var restore = new RestoreReport();
// Memory is not in the database any more, so its number is a directory listing rather than a count of rows. A
// library that cannot be read at startup is not worth stopping for: the receipt says so and the host carries on.
var memoryGroups = await memoryLibrary.GroupsAsync(CancellationToken.None);
restore.Note("storage", $"sqlite {Path.GetFileName(storage.Path)}: {storage.CountMessages()} message(s), " +
                        $"{storage.CountStateKeys()} state key(s), {storage.CountAuditRecords()} audit record(s)");
restore.Note("memory", memoryGroups.Error is { } memoryError
    ? $"unreadable: {memoryError.Message}"
    : $"{memoryGroups.Value?.Count ?? 0} group(s) under {FileScopes.Memory}/");
// Acknowledgements owed to Adapters: published is not handled, so the mailbox file is only settled after
// the event round it produced is over.
var acks = new InboundAckRegistry();
// Prompts are files a person edits: <dataRoot>/prompts/<group>/<name>.md — shipped defaults live next to the
// binary and the data root overrides them file by file, so an edit needed today is a file, not a release.
var prompts = new PromptLibrary([
    Path.Combine(AppContext.BaseDirectory, "resources", "prompts"),
    Path.Combine(data.Root, "prompts")]);
var catalog = prompts.List();
if (catalog.Groups.Count > 0 || catalog.Warnings.Count > 0)
    restore.Note("prompts", $"{catalog.Groups.Count} group(s), {catalog.Groups.Sum(group => group.Entries.Count)} prompt(s)" +
        (catalog.Warnings.Count == 0 ? "" : $"; skipped {catalog.Warnings.Count}: {string.Join("; ", catalog.Warnings)}"));
var sessions = new SessionManager(data.Root, extensions: sessionExtensions, signalLog: signalLog, prompts: prompts, catalog: modules);
var reloadState = new ModuleReloadStateStore();
// The action table exists before anything runs, because the modules that implement actions are the ones that
// register them: a manager is created (and readied) inside a package load, long before startup finishes, and it
// used to find no control plane to register with. Whoever implements a verb announces it; the host only hands out
// the table. This is also why it is in host services: a module reaches it the same way it reaches the signal log.
var controlPlane = new ControlPlane(auditSink, signalLog);
// The adapter manager is built here rather than inside a package, so it registers here. A manager a package builds
// registers from that package's start — the rule is the same either way: the implementer announces, and a failure to
// register is a startup failure, because a verb that silently does not exist is only discovered by whoever tries it.
var adapterActions = adapters.RegisterActions(controlPlane);
if (!adapterActions.Ok)
{
    Console.Error.WriteLine(adapterActions.Error?.Message ?? "Adapter action registration failed.");
    return 1;
}
var hostServices = new HostServiceProvider(
    (typeof(IControlPlane), controlPlane),
    (typeof(IAdapterManager), adapters),
    (typeof(IAdapterFactoryRegistry), adapterFactories),
    (typeof(IModelManager), modelManager),
    (typeof(ISessionManager), sessions),
    (typeof(IPromptLibrary), prompts),
    (typeof(ISessionExtensionRegistry), sessionExtensions),
    (typeof(IEventSink), new RecordingEventSink(queue, messageStore)),
    (typeof(IMessageStore), messageStore),
    (typeof(IStateStore), stateStore),
    (typeof(IAtomicWork), storage),
    (typeof(IQueuedMessageStore), storage),
    (typeof(IRestoreReporter), restore),
    (typeof(IInboundAcks), acks),
    (typeof(IParameterRegistry), parameters),
    (typeof(IFileMailboxFactory), new FileMailboxFactory(data.Root)),
    (typeof(IChannelSpecStore), channelSpecs),
    (typeof(IAdapterSpecStore), adapterSpecs),
    (typeof(IModuleTypeCatalog), adapterTypes),
    (typeof(IModuleTypeRegistry), adapterTypes),
    (typeof(IInstanceBindings), bindings),
    (typeof(ISignalLog), signalLog),
    (typeof(IModuleLoggerFactory), moduleLoggers),
    (typeof(IAuditSink), auditSink),
    (typeof(ICatalog), modules),
    (typeof(IModuleReloadStateStore), reloadState),
    (typeof(ModuleHostPaths), new ModuleHostPaths(AppContext.BaseDirectory, data.Root)));
var moduleServices = new ModuleServiceRegistry(hostServices);
var moduleUnloads = new ModuleUnloadCoordinator();
await using var modulePackages = new ModulePackageLoader(moduleUnloads, moduleServices, adapterTypes, reloadState,
    [adapterFactories, modelManager], adapters, bindings);
// The bin comes up first: the bundled runtime registers what it has to offer into it, and anything else that
// piles up (model-call artifacts next, most likely) registers the same way, without knowing this package.
var archivePackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "archive"), ModulePackageKind.Bundled, CancellationToken.None);
if (!archivePackage.Ok)
{
    Console.Error.WriteLine(archivePackage.Error?.Message ?? "Archive module failed to load.");
    return 1;
}
var archiveService = moduleServices.GetService<IArchiveRegistry>(ArchiveService.Registry);
if (!archiveService.Ok || archiveService.Value is null)
{
    Console.Error.WriteLine("The archive module did not provide its registry.");
    return 1;
}
var archive = archiveService.Value;
var deadlines = new SessionDeadlineHandler(archive, sessions);
var bundledPackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "bundled.runtime"), ModulePackageKind.Bundled, CancellationToken.None);
if (!bundledPackage.Ok)
{
    Console.Error.WriteLine(bundledPackage.Error?.Message ?? "Bundled modules failed to load.");
    return 1;
}
var modelPackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "model.openai-chat"), ModulePackageKind.Plugin, CancellationToken.None);
if (!modelPackage.Ok)
{
    Console.Error.WriteLine(modelPackage.Error?.Message ?? "OpenAI Chat provider module failed to load.");
    return 1;
}
// The store's root is the data root, not <root>/models: a provider is a module directory like everything else.
var modelInstances = new FileModelInstanceStore(data.Root, adapterTypes);
var modelConfiguration = await LoadModelConfigurationAsync(modelInstances);
if (!modelConfiguration.Ok)
{
    Console.Error.WriteLine(modelConfiguration.Error?.Message ?? "Model configuration failed to load.");
    return 1;
}
var graphResult = moduleServices.GetService<IGraphRuntime>("graph.runtime");
var channelResult = moduleServices.GetService<IChannelManager>("channel.manager");
var clockResult = moduleServices.GetService<IClockManager>("clock.manager");
if (!graphResult.Ok || graphResult.Value is null || !channelResult.Ok || channelResult.Value is null || !clockResult.Ok || clockResult.Value is null)
{
    Console.Error.WriteLine("Bundled module package did not provide the required Graph, Channel and Clock services.");
    return 1;
}
var graph = graphResult.Value;
var channels = channelResult.Value;
var clock = clockResult.Value;
// The address book comes up before anything else and is filled in as modules appear. Every registration here
// is a *singleton*: the type is the manager class itself — not the category it owns, which is why an adapter
// lookup by type never returns the manager. The category is the namespace it manages.
// One table per concern: what the catalog lists, and which of those own instances the catalog should not copy.
var registered = HostComposition.Register(
    modules,
    [adapters, modelManager, sessions, (IModule)channels!, (IModule)graph!, (IModule)clock!],
    // The instances those managers hold are findable through them, not copied in: the owner's dictionary is the
    // truth, so a destroyed instance is gone from the catalog by being gone from its owner.
    [adapters, modelManager, (IModuleIndex)channels!, sessions]);
if (!registered.Ok)
{
    Console.Error.WriteLine(registered.Error?.Message);
    return 1;
}
// Which modules exist is not something anybody can discover from a log, and a catalogue that silently failed to
// register would look exactly like one with nothing in it.
restore.Note("modules", $"{modules.Count} registered: {string.Join(", ", modules.List().Where(row => row.Role is not ModuleRole.Instance).Select(row => row.Id.Value))}");
var channelDelta = new FileChannelDeltaSource(messageStore, stateStore);
var deltaSource = sessionExtensions.Register(new ChannelDeltaSource(channelDelta, channels));
if (!deltaSource.Ok) { Console.Error.WriteLine(deltaSource.Error?.Message); return 1; }
// The manager owns adapter startup and the stop sweep; the clock interval belongs to the Adapter, which is the
// only thing that knows how its transport wants to be polled.
adapters.UseEvents(new RecordingEventSink(queue, messageStore));
var adapterPackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "adapter.file"), ModulePackageKind.Plugin, CancellationToken.None);
if (!adapterPackage.Ok || adapterPackage.Value is null)
{
    Console.Error.WriteLine(adapterPackage.Error?.Message ?? "FileAdapter module load failed.");
    return 1;
}
// The QQ adapter is optional in the sense that no spec may use it; a load failure is still fatal,
// because a package that fails to load but leaves specs referring to it would start up half-blind.
var qqPackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "adapter.qq"), ModulePackageKind.Plugin, CancellationToken.None);
if (!qqPackage.Ok || qqPackage.Value is null)
{
    Console.Error.WriteLine(qqPackage.Error?.Message ?? "QQ adapter module load failed.");
    return 1;
}

// Modules the operator dropped in live under the data root, not next to the binary: an upgrade replaces the
// binary directory, and a plugin is not part of the upgrade. A broken one is reported and skipped — a
// third-party package must not be able to stop the host from starting.
foreach (var dropped in await DroppedModules.LoadAsync(modulePackages, Path.Combine(data.Root, "plugins"), CancellationToken.None))
{
    if (dropped.Loaded) restore.Note("modules", $"plugin {dropped.Manifest!.Id} {dropped.Manifest.Version} from {Path.GetFileName(dropped.Directory)}");
    else Console.Error.WriteLine($"plugin skipped: {Path.GetFileName(dropped.Directory)} — {dropped.Failure?.Message} {dropped.Failure?.Detail}".TrimEnd());
}

// Before the catalogue is read, and only once per script: the shipped scripts get a copy in the operator's tree,
// where they can be edited and reloaded. Never rewritten afterwards (C-03) — see ScriptSeed for why the copy is
// not byte-identical.
var seed = new ScriptSeed(AppContext.BaseDirectory, data.Root);
var seededScripts = await seed.CopyMissingAsync(CancellationToken.None);
if (seededScripts.AddedAnything)
    restore.Note("scripts", $"copied to scripts/trusted: {string.Join(", ", seededScripts.Copied)}");
foreach (var (seedId, seedReason) in seededScripts.Failed)
    restore.Note("scripts", $"could not copy {seedId}: {seedReason}");
if (await seed.EnsureDraftGuideAsync(CancellationToken.None))
    restore.Note("scripts", $"wrote scripts/draft/{ScriptSeed.GuideFileName}");

var scriptCatalog = moduleServices.GetService<IGraphScriptCatalog>("graph.script-catalog");
if (!scriptCatalog.Ok || scriptCatalog.Value is null)
{
    Console.Error.WriteLine(scriptCatalog.Error?.Message ?? "Graph script catalog is unavailable.");
    return 1;
}
var scripts = await scriptCatalog.Value.LoadAsync(CancellationToken.None);
if (!scripts.Ok || scripts.Value is null)
{
    Console.Error.WriteLine(scripts.Error?.Message ?? "Script loading failed.");
    return 1;
}
var loaded = await graph.LoadAsync(scripts.Value, CancellationToken.None);
if (!loaded.Ok)
{
    Console.Error.WriteLine($"Graph loading failed: {loaded.Error?.Message} {loaded.Error?.Detail}");
    return 1;
}

// Configuration, not startup: make sure the file Adapter has a spec to revive. Starting it is the
// kernel's job, not the host's.
var storedAdapters = await adapterSpecs.LoadAllAsync(CancellationToken.None);
if (!storedAdapters.Ok)
{
    Console.Error.WriteLine(storedAdapters.Error?.Message ?? "Stored Adapter specs could not be read.");
    return 1;
}
var adapterSpec = storedAdapters.Value!.FirstOrDefault(s => s.TypeId == "adapter.file")
    ?? new AdapterSpec(new Address("adapter", "file", "file-1"), new AdapterDescriptor("file", "file-1", AdapterDirection.Both), "adapter.file");
if (storedAdapters.Value!.Count == 0)
{
    var seeded = await adapterSpecs.SaveAsync(adapterSpec, CancellationToken.None);
    if (!seeded.Ok)
    {
        Console.Error.WriteLine(seeded.Error?.Message ?? "Adapter spec could not be seeded.");
        return 1;
    }
}

using var shutdown = new CancellationTokenSource();
using var pumpStop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var pump = new EventPump(queue, graph.DispatchAsync, signalLog,
    // Two things are owed after a round: the mailbox file stays claimed until the event it carries is done,
    // and a Channel message stops being "accepted but unfinished" so a restart has nothing to replay.
    new EventSettlers(new ChannelAcceptSettler(channels), new InboundAckSettler(acks)));
var pumpTask = pump.RunAsync(pumpStop.Token);
var clockTask = clock.RunAsync(shutdown.Token);
// Deadlines a Session declared with wait. Started here, next to the other long-running loops, so that stopping
// the host stops it too — a sweep that outlived the host would act on state nothing else is maintaining.
var deadlineSweepTask = sessions.RunDeadlineSweepAsync(deadlines, TimeProvider.System,
    SessionDeadlineHandler.Interval, shutdown.Token);

// The host's only startup act: build the graph and hand it to the kernel, which decides the phases, the order
// and what to do about a module that fails. The host does not know what a wave is.
// The kernel is told which types this process builds itself: every instance has a module.json too (docs/22 §2), and
// its type belongs to a manager, so the loader must not treat the directory as a module it failed to understand.
// The kernel is told which types this process builds itself. That used to be a predicate written here, next to the
// kernel; now it is a fact registered on the factory table: a type whose instances a manager builds is "owned", so
// the loader reads its file as that instance's configuration instead of reporting a module it failed to understand.
var factories = new ModuleFactoryRegistry();
// The file system module is built from its own file, like every other module that a person configures: the factory
// gets the manifest and decides what its config means. The whitelist is therefore an edit, not a recompile.
factories.Register(FileSystemModule.TypeId,
    manifest => FileSystemModule.FromManifest(manifest, fileAccess, sessionExtensions));
// The script control tools are a module too: "who may reload scripts" is answerable by looking at the graph instead of
// at the host's source, and the two tools it offers read whatever the graph holds at the moment they are called.
factories.Register(ScriptToolsModule.TypeId,
    manifest => ScriptToolsModule.FromManifest(manifest, graph, scriptCatalog.Value!, sessionExtensions,
        new ScriptLibrary(data.Root), modules));
// The action table reaches the model through a module too, and the plane it registers into came up long before this:
// a module that offers tools is a module like any other, and the tools read the table as it is when they are called.
factories.Register(ActionToolsModule.TypeId,
    manifest => ActionToolsModule.FromManifest(manifest, controlPlane, sessionExtensions));
foreach (var type in adapterTypes.ListTypes().Where(type => adapterTypes.IsConcrete(type.TypeId)))
    factories.RegisterOwned(type.TypeId);
foreach (var type in modelManager.ListTypes())
    factories.RegisterOwned(type.TypeId);
factories.RegisterOwned(SessionManager.SessionTypeId);

var kernel = new Kernel(data.Root, factories, signalLog, hostOwnsTypes: factories.IsOwned);
// The kernel's table is the truth for what exists. The catalog looks there last — after its own entries and the
// declared owners — so nothing that answers today changes answer, and a module on the graph is findable without being
// registered twice.
modules.UseSource(id => kernel.Describe(id) is { } row && kernel.Find(id) is { } found && found is IModule module
    ? new LocatedModule(module, row)
    : null);
modules.UseRows(() => kernel.Instances
    .Select(info => new CatalogEntry(info.Id, info.Type, info.Category, info.Role))
    .ToArray());
// One table, one loop: these are the modules the host builds itself, so nothing on disk could produce them. Every
// one of them is a master that holds instances (or, for memory, a store) — none of them has phases worth ordering.
var declaredModules = new DeclaredModule[]
{
    new(new LifecycleModule(adapters, new ModuleId(ModuleNames.AdapterManager)), ModuleNames.AdapterManager,
        "Adapter manager"),
    // No dependency between adapters and channels: availability is checked when a message is delivered, so neither
    // manager has to wait for the other.
    new(new LifecycleModule((ILifecycleParticipant)channels!, new ModuleId(ModuleNames.ChannelManager)), ModuleNames.ChannelManager,
        "Channel manager"),
    new(memoryModule, MemoryModule.TypeId, "Memory"),
    // The model manager holds provider instances, so it is a master and has to be an instance. It has no phases of its
    // own — providers are built from their files — so it is a carrier.
    new(new PassiveInstance(new ModuleId(ModuleNames.ModelManager), modelManager), ModuleNames.ModelManager,
        "Model manager"),
    // Sessions, for the same reason as models: the manager holds them, so a Session is an instance the moment it is
    // loaded. No phase of its own either — a Session is loaded on demand, never by a wave.
    new(new PassiveInstance(new ModuleId(ModuleNames.SessionManager), sessions), ModuleNames.SessionManager,
        "Session manager")
};
var declaredCore = HostComposition.Declare(kernel, declaredModules);
if (!declaredCore.Ok)
{
    Console.Error.WriteLine(declaredCore.Error?.Message ?? "Module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// Instances are announced to the graph by the manager that holds them, so "what is mounted on this Channel" stops
// being a question only the manager can answer (docs/23 §4 step 1). Installed before startup, because reviving a
// stored Channel happens *during* startup — wiring it afterwards would announce only the ones created later.
if (channels is IInstanceSink sink)
    sink.UseInstanceHost(new InstanceHost(kernel, new ModuleId(ModuleNames.ChannelManager)));
// The same for Adapters - and it has to be here, not after startup: reviving the stored ones happens inside the
// kernel's Ready phase, and an Adapter that is not a instance never gets attached to the clock.
if (adapters is IInstanceSink adapterNodes)
    adapterNodes.UseInstanceHost(new InstanceHost(kernel, new ModuleId(ModuleNames.AdapterManager)));
// Model providers were built from their files long before the graph existed, so nothing announced them at the time.
// Announcing them now — before startup — makes them ordinary declarations: startup initializes, wires and starts
// them, and the receipt counts their edges like everybody else's.
var modelNodeHost = new InstanceHost(kernel, new ModuleId(ModuleNames.ModelManager));
if (modelManager is IInstanceSink modelNodes)
    modelNodes.UseInstanceHost(modelNodeHost);
foreach (var provider in modelManager.Entries())
{
    var announced = await modelNodeHost.AttachAsync(new ModuleId(ModuleNames.ModelManager), provider.Id,
        provider.TypeId, null, CancellationToken.None);
    if (announced.Ok) continue;
    Console.Error.WriteLine(announced.Error?.Message ?? $"Model provider {provider.Id} could not be announced.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// Sessions are announced by their manager when it loads one, so the sink is installed before startup — a revived
// Session is loaded inside a Ready phase, not after it.
if (sessions is IInstanceSink sessionNodes)
    sessionNodes.UseInstanceHost(new InstanceHost(kernel, new ModuleId(ModuleNames.SessionManager)));
// The clock is a instance too, so the graph can answer "who is on a schedule". It has no phases of its own - the host
// runs its loop - so it is a passive carrier rather than a participant.
var clockDeclared = kernel.Declare(new PassiveInstance(new ModuleId(ModuleNames.Clock), clock), typeId: ModuleNames.Clock);
if (!clockDeclared.Ok)
{
    Console.Error.WriteLine(clockDeclared.Error?.Message ?? "Clock module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// The file system module reads its whitelist from its own file, so the file has to exist before the scan. Written
// once and never rewritten (C-03): from then on the whitelist is whatever a person left there.
var fileSystemFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(FileSystemModule.TypeId), FileSystemModule.TypeId, Enabled: true,
        ConfigJson: FileSystemModule.DefaultConfig), CancellationToken.None);
if (!fileSystemFile.Ok)
{
    Console.Error.WriteLine(fileSystemFile.Error?.Message ?? "File system module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// The script control tools have no configuration, but they still get a file: a module that is on the graph is findable
// there, and one the host invents silently cannot be removed by editing a file.
var scriptToolsFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(ScriptToolsModule.TypeId), ScriptToolsModule.TypeId, Enabled: true),
    CancellationToken.None);
if (!scriptToolsFile.Ok)
{
    Console.Error.WriteLine(scriptToolsFile.Error?.Message ?? "Script control module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// The action table, offered to the model the same way: a module with a file, so "may the model reach the actions"
// is a question with an answer on disk rather than a switch in the source.
var actionToolsFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(ActionToolsModule.TypeId), ActionToolsModule.TypeId, Enabled: true),
    CancellationToken.None);
if (!actionToolsFile.Ok)
{
    Console.Error.WriteLine(actionToolsFile.Error?.Message ?? "Action tool module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// What the file system module will actually offer, said out loud. The scopes this host registers and the scopes that
// module's own file declares are two different lists, and only their intersection is reachable — and the file is never
// rewritten (C-03), so a module.json written before a scope existed keeps the old list forever. Without this line,
// "the tools do not offer it" is indistinguishable from "the code forgot it", which is exactly the confusion this
// project already paid for once.
var declaredScopes = ReadDeclaredScopes(Path.Combine(data.Root, ModuleManifestStore.DirectoryName,
    FileSystemModule.TypeId, ModuleManifestStore.FileName));
var offeredScopes = declaredScopes.Length == 0
    ? fileAccess.Scopes
    : fileAccess.Scopes.Where(scope => declaredScopes.Contains(scope, StringComparer.Ordinal)).ToArray();
restore.Note("filesystem", $"offers {string.Join(", ", offeredScopes)}");
var undeclaredScopes = declaredScopes.Length == 0
    ? []
    : fileAccess.Scopes.Where(scope => !declaredScopes.Contains(scope, StringComparer.Ordinal)).ToArray();
if (undeclaredScopes.Length > 0)
{
    Console.Error.WriteLine(
        $"filesystem.local's {ModuleManifestStore.FileName} does not declare {string.Join(", ", undeclaredScopes)}: the file "
        + "tools will not offer it. Add it to that file's config.scopes to grant it — the host will not rewrite the file.");
}

var started = await kernel.StartAsync(shutdown.Token);
if (!started.Ok || started.Value is null)
{
    Console.Error.WriteLine(started.Error?.Message ?? "Module startup failed.");
    await kernel.StopAsync(CancellationToken.None);
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
var receipt = started.Value;
// Channels register their tunables when they are revived, so persisted overrides are applied after startup,
// before anything reads a value.
var restored = await parameters.RestoreAsync(CancellationToken.None);
if (!restored.Ok)
    Console.Error.WriteLine($"Warning: a persisted parameter could not be restored: {restored.Error?.Message} {restored.Error?.Detail}");
// Messages that were accepted but whose round never finished: replay them now that Channels are alive.
var replayed = await channels.ReplayPendingAsync(CancellationToken.None);
if (!replayed.Ok)
    Console.Error.WriteLine($"Warning: unfinished messages could not be replayed: {replayed.Error?.Message} {replayed.Error?.Detail}");
// A deadline nobody can see is a deadline nobody can trust, and "the sweep is alive and found nothing" would
// otherwise look exactly like "the sweep never ran". This line is the difference, for a person reading startup.
var waiting = await sessions.ListAsync(CancellationToken.None);
if (waiting.Ok && waiting.Value is not null)
{
    var declared = waiting.Value.Where(session => session.WaitUntil is not null).Select(session => session.WaitUntil!.Value).ToArray();
    restore.Note("deferred", declared.Length == 0
        ? "no Session is waiting on a deadline"
        : $"{declared.Length} Session(s) waiting, earliest {declared.Min():O}");
}
foreach (var outcome in receipt.Instances.Where(instance => instance.Phase != ModulePhase.Ready))
    Console.Error.WriteLine($"startup {outcome.Phase}: {outcome.Id} — {outcome.Failure}");
foreach (var edge in receipt.BadEdges)
    Console.Error.WriteLine($"startup bad edge: {edge.Master} -> {edge.Slave} ({edge.Reason}): {edge.Detail}");
foreach (var cycle in receipt.Cycles)
    Console.Error.WriteLine($"startup cycle (ready together): {string.Join(" <-> ", cycle.Members)}");
foreach (var issue in receipt.Issues)
    Console.Error.WriteLine($"startup issue: {issue.Path} — {issue.Message}");
restore.Note("graph", $"{receipt.Ready}/{receipt.Instances.Count} ready, {receipt.WiredEdges} edge(s) wired" +
    (receipt.Degraded == 0 ? "" : $", degraded {receipt.Degraded}"));
// A trigger is not an edge, so it does not show up in the line above; printing it is what keeps "who pushes this
// adapter" from being a fact only the clock knows (docs/22 §1.4).
foreach (var rhythm in receipt.Rhythms)
    restore.Note("rhythm", rhythm.Line);
// What the address book can answer for once everything is running, instances included. A catalog whose instances
// are only in somebody else's dictionary would look identical from the outside — until something asks.
var liveInstances = modules.List()
    .Where(row => row.Role is ModuleRole.Instance)
    .GroupBy(row => row.Category, StringComparer.Ordinal)
    .Select(group => $"{group.Key} {group.Count()}")
    .Order(StringComparer.Ordinal)
    .ToArray();
restore.Note("instances", liveInstances.Length == 0 ? "none" : string.Join(", ", liveInstances));
foreach (var note in restore.Snapshot())
    Console.WriteLine($"restored {note.Component}: {note.Summary}");
if (receipt.Ready == 0)
{
    // Nothing came up at all: that is the only startup failure that justifies refusing to run.
    await kernel.StopAsync(CancellationToken.None);
    shutdown.Cancel();
    queue.Complete();
    try { await pumpTask; } catch (OperationCanceledException) { }
    try { await clockTask; } catch (OperationCanceledException) { }
    try { await deadlineSweepTask; } catch (OperationCanceledException) { }
    return 1;
}

// The network surface is opt-in: no api/server.json, no listener. The core knows nothing about the
// WebUI itself — it exposes actions, and a separate project consumes them. The table itself came up long before
// startup (see host services); this is where the host adds the verbs it owns itself.
var modelActions = modelManager.RegisterActions(controlPlane, modelInstances);
if (!modelActions.Ok)
{
    Console.Error.WriteLine(modelActions.Error?.Message ?? "Model action registration failed.");
    return 1;
}
var sessionActions = sessions.RegisterActions(controlPlane);
if (!sessionActions.Ok)
{
    Console.Error.WriteLine(sessionActions.Error?.Message ?? "Session action registration failed.");
    return 1;
}
var scriptActions = ScriptActions.Register(controlPlane, graph, scriptCatalog.Value, new ScriptLibrary(data.Root));
if (!scriptActions.Ok)
{
    Console.Error.WriteLine(scriptActions.Error?.Message ?? "Script action registration failed.");
    return 1;
}
var promptActions = PromptActions.Register(controlPlane, prompts);
if (!promptActions.Ok)
{
    Console.Error.WriteLine(promptActions.Error?.Message ?? "Prompt action registration failed.");
    return 1;
}
var actionsRegistered = CoreActions.RegisterAll(controlPlane, adapters, channels, modelManager, signalLog, graph, parameters, modulePackages, sessions, archive, prompts, adapterTypes, graphStatus: () => kernel.Status, kernel: kernel, scriptCatalog: scriptCatalog.Value);
if (!actionsRegistered.Ok)
{
    Console.Error.WriteLine(actionsRegistered.Error?.Message ?? "Action registration failed.");
    await kernel.StopAsync(CancellationToken.None);
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
ALN.Api.ApiInstance? api = null;
var apiSettings = ALN.Api.ApiSettings.Load(data.Root);
if (apiSettings is not null)
{
    foreach (var rejected in apiSettings.RejectedOrigins)
        Console.Error.WriteLine($"Warning: cors entry '{rejected}' is not an origin (scheme://host[:port]); ignored.");
    api = await ALN.Api.ApiHost.StartAsync(apiSettings, controlPlane, shutdown.Token);
    Console.WriteLine($"API listening on {string.Join(", ", api.Urls)}; {controlPlane.Catalog.Count} actions; authentication required." +
                      (api.Origins.Count == 0 ? " No cross-origin callers allowed." : $" CORS: {string.Join(", ", api.Origins)}."));
}
else
{
    Console.WriteLine("API disabled: no api/server.json.");
}

Console.WriteLine($"ALN is running. Data: {data.Root}; inbox: {defaultInbox}; create conversation subdirectories before starting. Ctrl+C to stop.");
try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
finally
{
    if (api is not null) await api.DisposeAsync();
    await kernel.StopAsync(CancellationToken.None);
    shutdown.Cancel();
    try { await clockTask; } catch (OperationCanceledException) { }
    try { await deadlineSweepTask; } catch (OperationCanceledException) { }
    queue.Complete();
    try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (TimeoutException) { pumpStop.Cancel(); try { await pumpTask; } catch (OperationCanceledException) { } }
    catch (OperationCanceledException) { }
    foreach (var profile in modelManager.ListProfiles()) modelManager.RemoveProfile(profile.Id);
    foreach (var provider in modelManager.List()) await modelManager.DestroyAsync(provider.Address, CancellationToken.None);
    await modulePackages.UnloadAsync(modelPackage.Value!.Id, TimeSpan.FromSeconds(10), force: true, CancellationToken.None);
    await modulePackages.UnloadAsync(adapterPackage.Value.Id, TimeSpan.FromSeconds(10), force: true, CancellationToken.None);
}
return 0;

async Task<Result> LoadModelConfigurationAsync(IModelInstanceStore store)
{
    var loaded = await store.LoadAllAsync(CancellationToken.None);
    if (!loaded.Ok || loaded.Value is null) return Result.Failure(loaded.Error!);
    foreach (var entry in loaded.Value)
    {
        if (!entry.Spec.Enabled) continue;
        // The type comes from the file, not from a category lookup: the file is what somebody wrote, and a loader
        // that re-derives the type here would be a second opinion about which provider this directory configures.
        var createdProvider = await modelManager.CreateAsync(
            new ModelProviderSpec(entry.Provider, entry.Spec.Configuration.GetRawText(), entry.TypeId),
            CancellationToken.None);
        if (!createdProvider.Ok || createdProvider.Value is null) return Result.Failure(createdProvider.Error!);
        var providerAddress = createdProvider.Value.Address;
        foreach (var profile in entry.Spec.Profiles ?? [])
        {
            var registered = modelManager.RegisterProfile(new(profile.Id, providerAddress, profile.ModelId, profile.DefaultSampling));
            if (!registered.Ok) return registered;
        }
    }
    return Result.Success();
}

// Channels are created by scripts and persisted; startup only revives what already exists.
// A conversation folder with no channel is not a channel — the next message routes into a new one.
// Channels are revived by their manager when the lifecycle signal arrives; the host keeps no list.

/// <summary>
/// The scopes a module's own file declares, in either shape that file has ever had: the object written now, or the
/// escaped string an older version wrote. Read-only and best-effort — this answers "what will be offered", so a file
/// that cannot be read is reported by the caller as "nothing declared" rather than taken as an empty whitelist.
/// </summary>
static string[] ReadDeclaredScopes(string manifestPath)
{
    if (!File.Exists(manifestPath)) return [];
    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (root.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
            return ScopesOf(config);
        if (root.TryGetProperty("configJson", out var legacy) && legacy.ValueKind == JsonValueKind.String)
        {
            using var inner = JsonDocument.Parse(legacy.GetString()!);
            return inner.RootElement.ValueKind == JsonValueKind.Object ? ScopesOf(inner.RootElement) : [];
        }
        return [];
    }
    catch (Exception ex) when (ex is IOException or JsonException)
    {
        return [];
    }
}

static string[] ScopesOf(JsonElement config) =>
    config.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
        ? scopes.EnumerateArray().Where(scope => scope.ValueKind == JsonValueKind.String)
            .Select(scope => scope.GetString()!).ToArray()
        : [];

sealed class HostServiceProvider(params (Type Type, object Service)[] services) : IServiceProvider
{
    private readonly IReadOnlyDictionary<Type, object> _services = services.ToDictionary(x => x.Type, x => x.Service);
    public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
}
