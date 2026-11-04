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
// Transport state, not configuration: the mailbox is IO and lives under io/, one directory per
// type and instance, mirroring the spec tree under adapters/.
var mailboxRoot = Path.Combine(data.Root, "io", "file", "file-1");
var mailbox = new FileMailbox(mailboxRoot);
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
await using var signalLog = SignalLog.Open(Path.Combine(data.Root, "logs"));
var moduleLoggers = new SignalModuleLoggerFactory(signalLog);
var queue = new EventQueue(capacity: 256);
// The address book. Not a module itself: if it were, finding a module would require a module.
var modules = new ModuleCatalog();
var adapterSpecs = new FileAdapterSpecStore(Path.Combine(data.Root, "adapters"));
var channelSpecs = new FileChannelSpecStore(Path.Combine(data.Root, "channels"));
var adapterTypes = new AdapterTypeCatalog();
var bindings = new InstanceBindingsProxy();
var adapterFactories = new AdapterFactoryRegistry();
// One registry for every tunable: components register themselves, the registry validates, applies, persists.
var parameters = new ParameterRegistry(stateStore);
var adapters = new AdapterManager(adapterTypes, bindings, adapterFactories, adapterSpecs, parameters: parameters);
var artifacts = new FileArtifactStore(Path.Combine(data.Root, "artifacts"));
var modelManager = new ModelManager(artifacts, signalLog);
var roundContextContributors = new RoundContextContributorRegistry();
var timeContributor = roundContextContributors.Register(new TimeContextContributor());
if (!timeContributor.Ok) { Console.Error.WriteLine(timeContributor.Error?.Message); return 1; }
var channelContributor = roundContextContributors.Register(new ChannelInfoContributor());
if (!channelContributor.Ok) { Console.Error.WriteLine(channelContributor.Error?.Message); return 1; }
var memoryContributor = roundContextContributors.Register(new MemoryContextContributor(storage));
if (!memoryContributor.Ok) { Console.Error.WriteLine(memoryContributor.Error?.Message); return 1; }
// The second half of activation: the Session pulls whatever the Channel has not handed over yet at the
// moment it assembles the request, so activation itself can stay a signal with no payload. Its cursor is
// component state, so it goes through the state store rather than a path the host invents.
// The startup receipt: whoever restores something says what it restored, and the host prints it once.
var restore = new RestoreReport();
restore.Note("storage", $"sqlite {Path.GetFileName(storage.Path)}: {storage.CountMessages()} message(s), " +
                        $"{storage.CountStateKeys()} state key(s), {storage.CountAuditRecords()} audit record(s), " +
                        $"memory {storage.CountMemoryEntries()} entry(ies)");
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
var sessions = new SessionManager(Path.Combine(data.Root, "sessions"), modelManager, roundContextContributors, signalLog, prompts,
    storage);
var reloadState = new ModuleReloadStateStore();
var hostServices = new HostServiceProvider(
    (typeof(IAdapterManager), adapters),
    (typeof(IAdapterFactoryRegistry), adapterFactories),
    (typeof(IModelManager), modelManager),
    (typeof(ISessionManager), sessions),
    (typeof(IPromptLibrary), prompts),
    (typeof(IRoundContextContributorRegistry), roundContextContributors),
    (typeof(IFileMailbox), mailbox),
    (typeof(IEventSink), new RecordingEventSink(queue, messageStore)),
    (typeof(IMessageStore), messageStore),
    (typeof(IStateStore), stateStore),
    (typeof(IAtomicWork), storage),
    (typeof(IQueuedMessageStore), storage),
    (typeof(IRestoreReporter), restore),
    (typeof(IInboundAcks), acks),
    (typeof(IParameterRegistry), parameters),
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
var modelInstances = new FileModelInstanceStore(Path.Combine(data.Root, "models"));
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
var registrationFailures = new List<string>();
void Register(IModule module)
{
    var attached = modules.Attach(module);
    if (!attached.Ok) registrationFailures.Add(attached.Error?.Message ?? $"Could not register {module.Id}.");
}
Register(adapters);
Register(modelManager);
Register(sessions);
Register((IModule)channels!);
Register((IModule)graph!);
Register((IModule)clock!);
// The instances those managers hold are findable through them, not copied in: the owner's dictionary is the
// truth, so a destroyed instance is gone from the catalog by being gone from its owner.
foreach (var index in new[] { (IModuleIndex)adapters, modelManager, (IModuleIndex)channels!, (IModuleIndex)sessions })
{
    var observed = modules.Observe(index);
    if (!observed.Ok) registrationFailures.Add(observed.Error?.Message ?? $"Could not observe {index.Category}.");
}
if (registrationFailures.Count > 0)
{
    foreach (var failure in registrationFailures) Console.Error.WriteLine(failure);
    return 1;
}
// Which modules exist is not something anybody can discover from a log, and a catalogue that silently failed to
// register would look exactly like one with nothing in it.
restore.Note("modules", $"{modules.Count} registered: {string.Join(", ", modules.List().Where(row => row.Role is not ModuleRole.Instance).Select(row => row.Id.Value))}");
var channelDelta = new FileChannelDeltaSource(messageStore, stateStore);
var deltaContributor = roundContextContributors.Register(new ChannelDeltaContributor(channelDelta, channels));
if (!deltaContributor.Ok) { Console.Error.WriteLine(deltaContributor.Error?.Message); return 1; }
// The manager owns adapter startup and the stop sweep; the clock interval belongs to the Adapter, which is the
// only thing that knows how its transport wants to be polled.
adapters.UseHeartbeat(new RecordingEventSink(queue, messageStore), clock);
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
// lifecycle coordinator's job, not the host's.
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
if (storedAdapters.Value!.Count > 1)
    Console.Error.WriteLine("Warning: only the first adapter.file spec is started; additional Adapter instances need their own mailbox.");

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

// The host's only startup act: hand one signal to the managers, in dependency order.
var lifecycle = new LifecycleCoordinator(new RecordingEventSink(queue, messageStore), signalLog);
var registered = lifecycle.Register(adapters);
// No dependency between adapters and channels: availability is checked when a message is delivered,
// so neither manager has to wait for the other.
if (registered.Ok) registered = lifecycle.Register((ILifecycleParticipant)channels);
if (!registered.Ok)
{
    Console.Error.WriteLine(registered.Error?.Message ?? "Lifecycle wiring failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
var lifecycleReport = await lifecycle.StartAsync(shutdown.Token);
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
foreach (var outcome in lifecycleReport.Outcomes.Where(o => o.Outcome != "ok"))
    Console.Error.WriteLine($"startup {outcome.Outcome}: {outcome.Name} — {outcome.Message}");
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
if (!lifecycleReport.Outcomes.Any(o => o.Outcome == "ok"))
{
    // Nothing came up at all: that is the only startup failure that justifies refusing to run.
    await lifecycle.StopAsync(CancellationToken.None);
    shutdown.Cancel();
    queue.Complete();
    try { await pumpTask; } catch (OperationCanceledException) { }
    try { await clockTask; } catch (OperationCanceledException) { }
    try { await deadlineSweepTask; } catch (OperationCanceledException) { }
    return 1;
}

// The network surface is opt-in: no api/server.json, no listener. The core knows nothing about the
// WebUI itself — it exposes actions, and a separate project consumes them.
var controlPlane = new ControlPlane(auditSink, signalLog);
var actionsRegistered = CoreActions.RegisterAll(controlPlane, adapters, channels, modelManager, signalLog, graph, parameters, modulePackages, sessions, archive, prompts, adapterTypes, modelInstances);
if (!actionsRegistered.Ok)
{
    Console.Error.WriteLine(actionsRegistered.Error?.Message ?? "Action registration failed.");
    await lifecycle.StopAsync(CancellationToken.None);
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

Console.WriteLine($"ALN is running. Data: {data.Root}; inbox: {Path.Combine(mailboxRoot, "inbox")}; create conversation subdirectories before starting. Ctrl+C to stop.");
try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
finally
{
    if (api is not null) await api.DisposeAsync();
    await lifecycle.StopAsync(CancellationToken.None);
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
        var createdProvider = await modelManager.CreateAsync(entry.Category, entry.Instance,
            entry.Spec.Configuration.GetRawText(), CancellationToken.None);
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

sealed class HostServiceProvider(params (Type Type, object Service)[] services) : IServiceProvider
{
    private readonly IReadOnlyDictionary<Type, object> _services = services.ToDictionary(x => x.Type, x => x.Service);
    public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
}
