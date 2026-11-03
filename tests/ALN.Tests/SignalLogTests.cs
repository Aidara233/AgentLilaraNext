using System.Text.Json;
using ALN.Adapter.File;
using ALN.Contracts;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;
using ALN.Scripting.Jint;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

public sealed class SignalLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-signal-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task MinLevel_filters_records_from_memory_and_file()
    {
        var directory = Path.Combine(_root, "logs");
        var log = SignalLog.Open(directory, new SignalLogOptions(SignalLevel.Warn));
        log.Write(new() { Group = "dispatch", Name = "debug-edge", Level = SignalLevel.Debug });
        log.Write(new() { Group = "dispatch", Name = "info-edge", Level = SignalLevel.Info });
        log.Write(new() { Group = "dispatch", Name = "warn-edge", Level = SignalLevel.Warn, Outcome = "failed" });
        await log.DisposeAsync();

        var record = Assert.Single(log.Snapshot());
        Assert.Equal("warn-edge", record.Name);
        var lines = File.ReadAllLines(Path.Combine(directory, $"signals-{DateOnly.FromDateTime(DateTime.UtcNow):yyyyMMdd}.jsonl"));
        var written = Assert.Single(lines);
        Assert.Contains("warn-edge", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_never_block_or_throw_and_memory_stays_bounded()
    {
        var log = SignalLog.Open(null, new SignalLogOptions(SignalLevel.Debug, Capacity: 64, MemoryWindow: 8));
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < 20_000; i++)
            log.Write(new() { Group = "dispatch", Name = $"edge-{i}", Level = SignalLevel.Debug });
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        Assert.Equal(0, log.FaultCount);
        Assert.True(log.Snapshot().Count <= 8);
        // A bounded queue with a live writer absorbs a burst; recording must never become back-pressure.
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"20k writes took {elapsed}.");

        await log.DisposeAsync();
        // Writing after shutdown is a no-op, not an exception thrown into the caller's dispatch.
        log.Write(new() { Group = "dispatch", Name = "after-dispose", Level = SignalLevel.Debug });
        Assert.Equal(0, log.FaultCount);
    }

    [Fact]
    public async Task Maximum_level_never_suppresses_an_audit_entry()
    {
        var signals = SignalLog.Open(null, new SignalLogOptions(SignalLevel.Error));
        using var audit = new SqliteStore(Path.Combine(_root, "aln.db"));
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapter.file", [new("adapter.file", "adapter")]).Ok);
        var bindings = new GraphBindingProxy();
        var adapters = new AdapterManager(types, bindings);
        var events = new EventQueue(8);
        var clock = new ALN.Clock.ClockSource(events);
        // Channel manager is not reachable on this path; denial happens before any resource is touched.
        var channels = new ALN.Channel.ChannelManager(adapters, events, bindings);
        var graph = new GraphManager(ModuleWiring.With(adapters, channels, clock), types, new JintScriptExecutor(), audit, signals);
        bindings.Bind(graph);

        // Declares no capabilities but calls one: RequireCapabilityAsync must deny.
        var undeclared = new ScriptPackage(
            new("adapter.denied-intake", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent", [],
                TrustDomain.Builtin),
            "async function onEvent(event, ctx) { await ctx.self.adapter.send({ platformConversationId: event.platformConversationId, text: 'x' }); }");
        Assert.True((await graph.LoadAsync([undeclared], default)).Ok);

        var adapterAddress = new Address("adapter", "file", "denied-1");
        var mailbox = new FileMailbox(Path.Combine(_root, "adapters", "denied-1"));
        var adapter = new FileAdapter(new(adapterAddress, new("file", "denied-account", AdapterDirection.Both), "adapter.file"), mailbox, events, clock);
        Assert.True((await adapters.CreateAsync(new(adapterAddress, adapter.Descriptor, "adapter.file"), _ => adapter, default)).Ok);

        var envelope = new AdapterEnvelope(adapterAddress, "file", "chat-1", "m-1", "hello", DateTimeOffset.UtcNow);
        var dispatched = await graph.DispatchAsync(new(EventNames.AdapterEnvelope, adapterAddress,
            envelope.EventId, JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default);

        Assert.False(dispatched.Ok);
        // Signal log is filtered to Error, so no denial record survives there...
        Assert.DoesNotContain(signals.Snapshot(), r => r.Outcome == "denied");
        // ...while the audit trail keeps the accountability record.
        var auditEntries = audit.ReadAudit();
        Assert.Contains(auditEntries, entry => entry.Outcome == "denied");
        await signals.DisposeAsync();
    }

    private sealed class GraphBindingProxy : IInstanceBindings
    {
        private IInstanceBindings? _inner;
        public void Bind(IInstanceBindings inner) => _inner = inner;
        public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) => _inner!.AttachAsync(target, ct);
        public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) => _inner!.DetachAsync(target, ct);
    }
}
