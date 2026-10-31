using ALN.Adapter.File;
using ALN.Channel;
using ALN.Contracts;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;
using ALN.Scripting.Jint;

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
var typeCatalog = new ChannelTypeCatalog(adapterTypes);
var bindings = new GraphBindingProxy();
var adapters = new AdapterManager(typeCatalog, bindings);
var channels = new ChannelManager(adapters, queue, bindings);
var executor = new JintScriptExecutor(journal);
var graph = new GraphManager(adapters, channels, typeCatalog, executor, auditStream.Sink);
bindings.Bind(graph);

var catalog = new JsonScriptCatalog(AppContext.BaseDirectory, data.Root);
var scripts = await catalog.LoadAsync(CancellationToken.None);
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
var created = await adapters.CreateAsync(adapterSpec, spec => new FileAdapter(spec, mailbox, queue), CancellationToken.None);
if (!created.Ok || created.Value is not FileAdapter fileAdapter)
{
    Console.Error.WriteLine(created.Error?.Message ?? "Adapter creation failed.");
    return 1;
}

var channelAddresses = new HashSet<Address>();
var startupChannels = await ReconcileChannelsAsync(CancellationToken.None);
if (!startupChannels.Ok)
{
    Console.Error.WriteLine(startupChannels.Error?.Message);
    await adapters.DestroyAsync(adapterSpec.Address, CancellationToken.None);
    return 1;
}
var started = await adapters.StartAsync(adapterSpec.Address, CancellationToken.None);
if (!started.Ok)
{
    Console.Error.WriteLine(started.Error?.Message ?? "Adapter start failed.");
    return 1;
}

using var shutdown = new CancellationTokenSource();
using var pumpStop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var pump = new EventPump(queue, graph.DispatchAsync);
var pumpTask = pump.RunAsync(pumpStop.Token);
Console.WriteLine($"ALN is running. Data: {data.Root}; inbox: {Path.Combine(mailboxRoot, "inbox")}; create a subdirectory per conversation. Ctrl+C to stop.");
try
{
    while (!shutdown.IsCancellationRequested)
    {
        var reconciled = await ReconcileChannelsAsync(shutdown.Token);
        if (!reconciled.Ok) Console.Error.WriteLine(reconciled.Error?.Message);
        var polled = await fileAdapter.PollOnceAsync(shutdown.Token);
        if (!polled.Ok) Console.Error.WriteLine(polled.Error?.Message);
        await Task.Delay(TimeSpan.FromMilliseconds(200), shutdown.Token);
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
finally
{
    await adapters.StopAsync(adapterSpec.Address, CancellationToken.None);
    queue.Complete();
    try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (TimeoutException) { pumpStop.Cancel(); try { await pumpTask; } catch (OperationCanceledException) { } }
    catch (OperationCanceledException) { }
    foreach (var address in channelAddresses.ToArray()) await channels.DestroyAsync(address, CancellationToken.None);
    await adapters.DestroyAsync(adapterSpec.Address, CancellationToken.None);
}
return 0;

async Task<Result> ReconcileChannelsAsync(CancellationToken cancellationToken)
{
    IReadOnlyList<string> conversationIds;
    try { conversationIds = await mailbox.GetConversationIdsAsync(cancellationToken); }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex) { return Result.Failure(new(ErrorCode.Unavailable, "Could not enumerate FileAdapter conversations.", ex.Message)); }
    foreach (var conversationId in conversationIds)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = new Address("channel", "logical", conversationId);
        if (channelAddresses.Contains(address)) continue;
        var spec = new ChannelSpec(address, conversationId, [new ChannelBinding(adapterSpec.Address, conversationId)]);
        var createdChannel = await channels.CreateAsync(spec, cancellationToken);
        if (!createdChannel.Ok) return Result.Failure(createdChannel.Error!);
        var channelStart = await channels.StartAsync(address, cancellationToken);
        if (!channelStart.Ok)
        {
            await channels.DestroyAsync(address, CancellationToken.None);
            return channelStart;
        }
        channelAddresses.Add(address);
    }
    return Result.Success();
}

sealed class GraphBindingProxy : IInstanceBindings
{
    private IInstanceBindings? _inner;
    public void Bind(IInstanceBindings inner) => _inner = inner;
    public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) =>
        (_inner ?? throw new InvalidOperationException("Graph bindings not initialized.")).AttachAsync(target, ct);
    public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) =>
        (_inner ?? throw new InvalidOperationException("Graph bindings not initialized.")).DetachAsync(target, ct);
}

sealed class AsyncDisposeAdapter(JsonlAuditSink sink) : IAsyncDisposable
{
    public IAuditSink Sink => sink;
    public ValueTask DisposeAsync() { sink.Dispose(); return ValueTask.CompletedTask; }
}
