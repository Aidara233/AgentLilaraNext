using ALN.Adapter.File;
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
var typeCatalog = new AdapterTypeCatalog();
var queue = new EventQueue(capacity: 256);
var bindings = new GraphBindingProxy();
var adapters = new AdapterManager(typeCatalog, bindings);
var executor = new JintScriptExecutor(journal);
var graph = new GraphManager(adapters, typeCatalog, executor, auditStream.Sink);
bindings.Bind(graph);

var programRoot = AppContext.BaseDirectory;
var catalog = new JsonScriptCatalog(programRoot, data.Root);
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
if (!created.Ok || created.Value is null)
{
    Console.Error.WriteLine(created.Error?.Message ?? "Adapter creation failed.");
    return 1;
}
var start = await adapters.StartAsync(adapterSpec.Address, CancellationToken.None);
if (!start.Ok)
{
    Console.Error.WriteLine(start.Error?.Message ?? "Adapter start failed.");
    return 1;
}

using var shutdown = new CancellationTokenSource();
using var pumpStop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var pump = new EventPump(queue, (envelope, ct) => graph.DispatchAsync(ScriptEvents.AdapterEnvelope, envelope, ct));
var pumpTask = pump.RunAsync(pumpStop.Token);
var fileAdapter = (FileAdapter)created.Value;
Console.WriteLine($"ALN is running. Data: {data.Root}; inbox: {Path.Combine(mailboxRoot, "inbox")}; Ctrl+C to stop.");
try
{
    while (!shutdown.IsCancellationRequested)
    {
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
    await adapters.DestroyAsync(adapterSpec.Address, CancellationToken.None);
}
return 0;

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
