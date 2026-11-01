using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;

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
await using var auditStream = new AsyncDisposeAdapter(new JsonlAuditSink(Path.Combine(data.Root, "audit", "capabilities.jsonl")));
await using var signalLog = SignalLog.Open(Path.Combine(data.Root, "logs"));
var moduleLoggers = new SignalModuleLoggerFactory(signalLog);
var queue = new EventQueue(capacity: 256);
var messageStore = new FileMessageStore(Path.Combine(data.Root, "messages"));
var adapterSpecs = new FileAdapterSpecStore(Path.Combine(data.Root, "adapters"));
var channelSpecs = new FileChannelSpecStore(Path.Combine(data.Root, "channels"));
var adapterTypes = new AdapterTypeCatalog();
var bindings = new InstanceBindingsProxy();
var adapterFactories = new AdapterFactoryRegistry();
var adapters = new AdapterManager(adapterTypes, bindings, adapterFactories, adapterSpecs);
var artifacts = new FileArtifactStore(Path.Combine(data.Root, "artifacts"));
var modelManager = new ModelManager(artifacts, signalLog);
var roundContextContributors = new RoundContextContributorRegistry();
var timeContributor = roundContextContributors.Register(new TimeContextContributor());
if (!timeContributor.Ok) { Console.Error.WriteLine(timeContributor.Error?.Message); return 1; }
var channelContributor = roundContextContributors.Register(new ChannelInfoContributor());
if (!channelContributor.Ok) { Console.Error.WriteLine(channelContributor.Error?.Message); return 1; }
// The second half of activation: the Session pulls whatever the Channel has not handed over yet at the
// moment it assembles the request, so activation itself can stay a signal with no payload.
var channelDelta = new FileChannelDeltaSource(messageStore, Path.Combine(data.Root, "messages"));
var deltaContributor = roundContextContributors.Register(new ChannelDeltaContributor(channelDelta));
if (!deltaContributor.Ok) { Console.Error.WriteLine(deltaContributor.Error?.Message); return 1; }
var sessions = new SessionManager(Path.Combine(data.Root, "sessions"), modelManager, roundContextContributors, signalLog);
var reloadState = new ModuleReloadStateStore();
var hostServices = new HostServiceProvider(
    (typeof(IAdapterManager), adapters),
    (typeof(IAdapterFactoryRegistry), adapterFactories),
    (typeof(IModelManager), modelManager),
    (typeof(ISessionManager), sessions),
    (typeof(IRoundContextContributorRegistry), roundContextContributors),
    (typeof(IFileMailbox), mailbox),
    (typeof(IEventSink), new RecordingEventSink(queue, messageStore)),
    (typeof(IMessageStore), messageStore),
    (typeof(IChannelSpecStore), channelSpecs),
    (typeof(IAdapterSpecStore), adapterSpecs),
    (typeof(IModuleTypeCatalog), adapterTypes),
    (typeof(IModuleTypeRegistry), adapterTypes),
    (typeof(IInstanceBindings), bindings),
    (typeof(ISignalLog), signalLog),
    (typeof(IModuleLoggerFactory), moduleLoggers),
    (typeof(IAuditSink), auditStream.Sink),
    (typeof(IModuleReloadStateStore), reloadState),
    (typeof(ModuleHostPaths), new ModuleHostPaths(AppContext.BaseDirectory, data.Root)));
var moduleServices = new ModuleServiceRegistry(hostServices);
var moduleUnloads = new ModuleUnloadCoordinator();
await using var modulePackages = new ModulePackageLoader(moduleUnloads, moduleServices, adapterTypes, reloadState,
    [adapterFactories, modelManager], adapters, bindings);
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
var modelConfiguration = await LoadModelConfigurationAsync(Path.Combine(data.Root, "models"));
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
// The manager owns adapter startup; it only needs the heartbeat wired before the signal arrives.
adapters.UseHeartbeat(new RecordingEventSink(queue, messageStore), clock, TimeSpan.FromSeconds(1));
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
var adapterSpec = storedAdapters.Value!.FirstOrDefault(s => s.Address.TypeId == "adapter.file")
    ?? new AdapterSpec(new Address("adapter", "file", "file-1"), new AdapterDescriptor("file", "file-1", AdapterDirection.Both));
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
var pump = new EventPump(queue, graph.DispatchAsync, signalLog);
var pumpTask = pump.RunAsync(pumpStop.Token);
var clockTask = clock.RunAsync(shutdown.Token);

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
foreach (var outcome in lifecycleReport.Outcomes.Where(o => o.Outcome != "ok"))
    Console.Error.WriteLine($"startup {outcome.Outcome}: {outcome.Name} — {outcome.Message}");
if (!lifecycleReport.Outcomes.Any(o => o.Outcome == "ok"))
{
    // Nothing came up at all: that is the only startup failure that justifies refusing to run.
    await lifecycle.StopAsync(CancellationToken.None);
    shutdown.Cancel();
    queue.Complete();
    try { await pumpTask; } catch (OperationCanceledException) { }
    try { await clockTask; } catch (OperationCanceledException) { }
    return 1;
}

// The network surface is opt-in: no api/server.json, no listener. The core knows nothing about the
// WebUI itself — it exposes actions, and a separate project consumes them.
var controlPlane = new ControlPlane(auditStream.Sink, signalLog);
var actionsRegistered = CoreActions.RegisterAll(controlPlane, adapters, channels, modelManager, signalLog, graph);
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

async Task<Result> LoadModelConfigurationAsync(string modelsRoot)
{
    if (!Directory.Exists(modelsRoot)) return Result.Success();
    try
    {
        foreach (var categoryDirectory in Directory.EnumerateDirectories(modelsRoot).Order(StringComparer.Ordinal))
        foreach (var configPath in Directory.EnumerateFiles(categoryDirectory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(configPath, CancellationToken.None);
            if (bytes.Length > 256_000)
                return Result.Failure(new(ErrorCode.Invalid, $"Model configuration exceeds 256 KB: {configPath}"));
            var config = JsonSerializer.Deserialize<ModelInstanceConfiguration>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (config is null)
                return Result.Failure(new(ErrorCode.Invalid, $"Model configuration is incomplete: {configPath}"));
            if (!config.Enabled) continue;
            if (config.Configuration.ValueKind != JsonValueKind.Object)
                return Result.Failure(new(ErrorCode.Invalid, $"Model provider configuration must be a JSON object: {configPath}"));

            var category = Path.GetFileName(categoryDirectory);
            var instance = Path.GetFileNameWithoutExtension(configPath);
            var createdProvider = await modelManager.CreateAsync(category, instance, config.Configuration.GetRawText(), CancellationToken.None);
            if (!createdProvider.Ok || createdProvider.Value is null) return Result.Failure(createdProvider.Error!);
            var providerAddress = createdProvider.Value.Address;
            foreach (var profile in config.Profiles ?? [])
            {
                var registered = modelManager.RegisterProfile(new(profile.Id, providerAddress, profile.ModelId, profile.DefaultSampling));
                if (!registered.Ok) return registered;
            }
        }
        return Result.Success();
    }
    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
    { return Result.Failure(new(ErrorCode.Invalid, "Model configuration could not be loaded.", ex.Message)); }
}

// Channels are created by scripts and persisted; startup only revives what already exists.
// A conversation folder with no channel is not a channel — the next message routes into a new one.
// Channels are revived by their manager when the lifecycle signal arrives; the host keeps no list.

sealed class HostServiceProvider(params (Type Type, object Service)[] services) : IServiceProvider
{
    private readonly IReadOnlyDictionary<Type, object> _services = services.ToDictionary(x => x.Type, x => x.Service);
    public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
}

sealed class AsyncDisposeAdapter(JsonlAuditSink sink) : IAsyncDisposable
{
    public IAuditSink Sink => sink;
    public ValueTask DisposeAsync() { sink.Dispose(); return ValueTask.CompletedTask; }
}

sealed record ModelInstanceConfiguration(
    JsonElement Configuration,
    IReadOnlyList<ModelProfileConfiguration>? Profiles,
    bool Enabled = true);
sealed record ModelProfileConfiguration(string Id, string ModelId, SamplingSettings? DefaultSampling = null);
