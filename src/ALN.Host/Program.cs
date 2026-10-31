using ALN.Contracts;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;

var dataArg = Array.FindIndex(args, a => a == "--data-dir");
if (dataArg >= 0 && dataArg + 1 >= args.Length)
{
    Console.Error.WriteLine("--data-dir requires a path.");
    return 2;
}
var data = DataDirectory.Open(dataArg >= 0 ? args[dataArg + 1] : null);
var mailboxRoot = Path.Combine(data.Root, "adapters", "file-1");
var mailbox = new FileMailbox(mailboxRoot);
await using var auditStream = new AsyncDisposeAdapter(new JsonlAuditSink(Path.Combine(data.Root, "audit", "capabilities.jsonl")));
var journal = new ExecutionJournal();
var queue = new EventQueue(capacity: 256);
var adapterTypes = new AdapterTypeCatalog();
var bindings = new InstanceBindingsProxy();
var adapterFactories = new AdapterFactoryRegistry();
var adapters = new AdapterManager(adapterTypes, bindings, adapterFactories);
var reloadState = new ModuleReloadStateStore();
var hostServices = new HostServiceProvider(
    (typeof(IAdapterManager), adapters),
    (typeof(IAdapterFactoryRegistry), adapterFactories),
    (typeof(IFileMailbox), mailbox),
    (typeof(IEventSink), queue),
    (typeof(IModuleTypeCatalog), adapterTypes),
    (typeof(IModuleTypeRegistry), adapterTypes),
    (typeof(IInstanceBindings), bindings),
    (typeof(IExecutionJournal), journal),
    (typeof(IAuditSink), auditStream.Sink),
    (typeof(IModuleReloadStateStore), reloadState),
    (typeof(ModuleHostPaths), new ModuleHostPaths(AppContext.BaseDirectory, data.Root)));
var moduleServices = new ModuleServiceRegistry(hostServices);
var moduleUnloads = new ModuleUnloadCoordinator();
await using var modulePackages = new ModulePackageLoader(moduleUnloads, moduleServices, adapterTypes, reloadState,
    [adapterFactories], adapters, bindings);
var bundledPackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "bundled.runtime"), ModulePackageKind.Bundled, CancellationToken.None);
if (!bundledPackage.Ok)
{
    Console.Error.WriteLine(bundledPackage.Error?.Message ?? "Bundled modules failed to load.");
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
var adapterPackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "adapter.file"), ModulePackageKind.Plugin, CancellationToken.None);
if (!adapterPackage.Ok || adapterPackage.Value is null)
{
    Console.Error.WriteLine(adapterPackage.Error?.Message ?? "FileAdapter module load failed.");
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

var adapterSpec = new AdapterSpec(new Address("adapter", "file", "file-1"), new AdapterDescriptor("file", "file-1", AdapterDirection.Both));
var created = await adapters.CreateAsync(adapterSpec, CancellationToken.None);
if (!created.Ok || created.Value is null)
{
    Console.Error.WriteLine(created.Error?.Message ?? "Adapter creation failed.");
    return 1;
}
created = default;

var channelAddresses = new HashSet<Address>();
var startupChannels = await ReconcileChannelsAsync();
if (!startupChannels.Ok)
{
    Console.Error.WriteLine(startupChannels.Error?.Message);
    await adapters.DestroyAsync(adapterSpec.Address, CancellationToken.None);
    return 1;
}

using var shutdown = new CancellationTokenSource();
using var pumpStop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var pump = new EventPump(queue, graph.DispatchAsync);
var pumpTask = pump.RunAsync(pumpStop.Token);
var clockTask = clock.RunAsync(shutdown.Token);
var started = await adapters.StartAsync(adapterSpec.Address, CancellationToken.None);
if (!started.Ok)
{
    Console.Error.WriteLine(started.Error?.Message ?? "Adapter start failed.");
    shutdown.Cancel();
    queue.Complete();
    try { await pumpTask; } catch (OperationCanceledException) { }
    try { await clockTask; } catch (OperationCanceledException) { }
    return 1;
}

Console.WriteLine($"ALN is running. Data: {data.Root}; inbox: {Path.Combine(mailboxRoot, "inbox")}; create conversation subdirectories before starting. Ctrl+C to stop.");
try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
finally
{
    await adapters.StopAsync(adapterSpec.Address, CancellationToken.None);
    shutdown.Cancel();
    try { await clockTask; } catch (OperationCanceledException) { }
    queue.Complete();
    try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (TimeoutException) { pumpStop.Cancel(); try { await pumpTask; } catch (OperationCanceledException) { } }
    catch (OperationCanceledException) { }
    foreach (var address in channelAddresses.ToArray()) await channels.DestroyAsync(address, CancellationToken.None);
    await modulePackages.UnloadAsync(adapterPackage.Value.Id, TimeSpan.FromSeconds(10), force: true, CancellationToken.None);
}
return 0;

async Task<Result> ReconcileChannelsAsync()
{
    IReadOnlyList<string> conversationIds;
    try { conversationIds = await mailbox.GetConversationIdsAsync(CancellationToken.None); }
    catch (Exception ex) { return Result.Failure(new(ErrorCode.Unavailable, "Could not enumerate FileAdapter conversations.", ex.Message)); }
    foreach (var conversationId in conversationIds)
    {
        var address = new Address("channel", "logical", conversationId);
        if (channelAddresses.Contains(address)) continue;
        var spec = new ChannelSpec(address, conversationId, [new ChannelBinding(adapterSpec.Address, conversationId)]);
        var createdChannel = await channels.CreateAsync(spec, CancellationToken.None);
        if (!createdChannel.Ok) return Result.Failure(createdChannel.Error!);
        var channelStart = await channels.StartAsync(address, CancellationToken.None);
        if (!channelStart.Ok)
        {
            await channels.DestroyAsync(address, CancellationToken.None);
            return channelStart;
        }
        channelAddresses.Add(address);
    }
    return Result.Success();
}

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
