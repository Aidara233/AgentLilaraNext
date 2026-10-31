using System.Text.Json;
using ALN.Adapter.File;
using ALN.Contracts;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;
using ALN.Scripting.Jint;

namespace ALN.Tests;

public sealed class GraphIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-graph-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JsonlAuditSink _audit;
    private readonly ExecutionJournal _journal = new();
    private readonly AdapterTypeCatalog _types = new();
    private readonly EventQueue _events = new(8);
    private readonly GraphBindingProxy _bindings = new();
    private readonly AdapterManager _adapters;
    private readonly GraphManager _graph;
    private readonly FileMailbox _mailbox;
    private readonly FileAdapter _adapter;
    private readonly Address _address = new("adapter", "file", "test-1");

    public GraphIntegrationTests()
    {
        Directory.CreateDirectory(_root);
        _audit = new JsonlAuditSink(Path.Combine(_root, "audit", "test.jsonl"));
        _adapters = new AdapterManager(_types, _bindings);
        _graph = new GraphManager(_adapters, _types, new JintScriptExecutor(_journal), _audit);
        _bindings.Bind(_graph);
        _mailbox = new FileMailbox(Path.Combine(_root, "adapters", "test-1"));
        _adapter = new FileAdapter(new(_address, new("file", "test-account", AdapterDirection.Both)), _mailbox, _events);
    }

    public void Dispose()
    {
        _audit.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Default_script_inherited_from_abstract_adapter_replies_through_same_instance()
    {
        var loaded = await _graph.LoadAsync([PingPong()], default);
        Assert.True(loaded.Ok, loaded.Error?.Detail);
        var created = await _adapters.CreateAsync(new(_address, _adapter.Descriptor), _ => _adapter, default);
        Assert.True(created.Ok, created.Error?.Detail);
        Assert.Contains(_graph.Mounted, m => m.Target == _address && m.Package.Manifest.Id == "ping-pong");
        Assert.True((await _adapters.StartAsync(_address, default)).Ok);

        await Publish("ping");
        var envelope = await ReadOneEventAsync();
        var dispatched = await _graph.DispatchAsync(ScriptEvents.AdapterEnvelope, envelope, default);
        Assert.True(dispatched.Ok, dispatched.Error?.Detail ?? dispatched.Error?.Message);
        Assert.Single(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox"), "*.json"));

        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(OutboxFile()));
        Assert.Equal("pong", output.RootElement.GetProperty("Text").GetString());
        Assert.Equal("room-1", output.RootElement.GetProperty("PlatformConversationId").GetString());
        Assert.Single(_journal.Snapshot(), r => r.EventId != Guid.Empty && r.Succeeded);
        Assert.Single(File.ReadAllLines(Path.Combine(_root, "audit", "test.jsonl")));
    }

    [Fact]
    public async Task Non_matching_message_does_not_reply_and_script_failure_is_recorded()
    {
        Assert.True((await _graph.LoadAsync([PingPong()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_address, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await _adapters.StartAsync(_address, default);
        await Publish("hello");
        var envelope = await ReadOneEventAsync();
        Assert.True((await _graph.DispatchAsync(ScriptEvents.AdapterEnvelope, envelope, default)).Ok);
        Assert.Empty(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox"), "*.json"));
        Assert.Single(_journal.Snapshot());
    }

    [Fact]
    public async Task Unknown_capability_and_restricted_script_are_rejected_without_replacing_current_mounts()
    {
        Assert.True((await _graph.LoadAsync([PingPong()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_address, _adapter.Descriptor), _ => _adapter, default)).Ok);
        var badCapability = PingPong() with { Manifest = PingPong().Manifest with { Capabilities = ["filesystem.root"] } };
        var result = await _graph.LoadAsync([badCapability], default);
        Assert.False(result.Ok);
        Assert.Equal("ping-pong", Assert.Single(_graph.Mounted).Package.Manifest.Id);
        var restricted = PingPong() with { Manifest = PingPong().Manifest with { TrustDomain = TrustDomain.Restricted } };
        Assert.False((await _graph.LoadAsync([restricted], default)).Ok);
    }

    [Fact]
    public async Task Script_cannot_access_clr_and_runtime_failure_is_recorded()
    {
        var package = new ScriptPackage(new("clr-probe", 1, "adapter", MountMode.Default,
            [ScriptEvents.AdapterEnvelope], "onEvent", []),
            "function onEvent(event, ctx) { return System.IO.File.ReadAllText('secret'); }");
        var result = await _graph.LoadAsync([package], default);
        Assert.True(result.Ok, result.Error?.Detail);
        Assert.True((await _adapters.CreateAsync(new(_address, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await _adapters.StartAsync(_address, default);
        await Publish("probe");
        var envelope = await ReadOneEventAsync();
        var execution = await _graph.DispatchAsync(ScriptEvents.AdapterEnvelope, envelope, default);
        Assert.False(execution.Ok);
        Assert.Contains("System", _journal.Snapshot().Single().Error);
        Assert.Empty(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox"), "*.json"));
    }

    [Fact]
    public async Task Script_statement_limit_stops_a_tight_loop_and_records_failure()
    {
        var package = new ScriptPackage(
            new("loop", 1, "adapter", MountMode.Default, [ScriptEvents.AdapterEnvelope], "onEvent", [], TrustDomain.Builtin, 500, 1_000, 8 * 1024 * 1024),
            "function onEvent(event, ctx) { while (true) { } }");
        Assert.True((await _graph.LoadAsync([package], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_address, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await _adapters.StartAsync(_address, default);
        await Publish("loop");
        var envelope = await ReadOneEventAsync();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await _graph.DispatchAsync(ScriptEvents.AdapterEnvelope, envelope, default);
        Assert.False(result.Ok);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
        Assert.False(Assert.Single(_journal.Snapshot()).Succeeded);
    }

    [Fact]
    public async Task Destroy_removes_default_mount_before_instance_is_gone()
    {
        Assert.True((await _graph.LoadAsync([PingPong()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_address, _adapter.Descriptor), _ => _adapter, default)).Ok);
        Assert.Contains(_graph.Mounted, m => m.Target == _address);
        Assert.True((await _adapters.DestroyAsync(_address, default)).Ok);
        Assert.DoesNotContain(_graph.Mounted, m => m.Target == _address);
        Assert.False(_adapters.Get(_address).Ok);
    }

    private ScriptPackage PingPong() => new(
        new ScriptManifest("ping-pong", 1, "adapter", MountMode.Default, [ScriptEvents.AdapterEnvelope],
            "onEvent", [ScriptCapabilities.SelfAdapterSend], TrustDomain.Builtin, 10_000, 1_000, 16 * 1024 * 1024),
        "async function onEvent(event, ctx) { if (event.text !== 'ping') return; await ctx.self.adapter.send({ platformConversationId: event.platformConversationId, text: 'pong', replyTo: event.platformMessageId }); }");

    private async Task Publish(string text)
    {
        var inbox = Path.Combine(_mailbox.Root, "inbox", $"{Guid.NewGuid():N}.ready.json");
        await File.WriteAllTextAsync(inbox, JsonSerializer.Serialize(new { platformConversationId = "room-1", text, messageId = "m-1" }));
        Assert.True((await _adapter.PollOnceAsync()).Ok);
    }

    private async Task<AdapterEnvelope> ReadOneEventAsync()
    {
        await using var enumerator = _events.ReadAllAsync(default).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private string OutboxFile() => Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox"), "*.json").Single();
    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate()) await Task.Delay(10, cancellationToken);
    }

    private sealed class GraphBindingProxy : IInstanceBindings
    {
        private IInstanceBindings? _inner;
        public void Bind(IInstanceBindings inner) => _inner = inner;
        public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) => _inner!.AttachAsync(target, ct);
        public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) => _inner!.DetachAsync(target, ct);
    }
}
