using System.Text.Json;
using ALN.Adapter.File;
using ALN.Channel;
using ALN.Contracts;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;
using ALN.Scripting.Jint;

namespace ALN.Tests;

public sealed class GraphIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-channel-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JsonlAuditSink _audit;
    private readonly SignalLog _signals = SignalLog.Open(null);
    private readonly AdapterTypeCatalog _adapterTypes = new();
    private readonly IModuleTypeCatalog _types;
    private readonly EventQueue _events = new(32);
    private readonly ALN.Clock.ClockSource _clock;
    private readonly GraphBindingProxy _bindings = new();
    private readonly AdapterManager _adapters;
    private readonly ChannelManager _channels;
    private readonly GraphManager _graph;
    private readonly FileMailbox _mailbox;
    private readonly FileAdapter _adapter;
    private readonly Address _adapterAddress = new("adapter", "file", "test-1");

    public GraphIntegrationTests()
    {
        _events = new(32);
        _clock = new ALN.Clock.ClockSource(_events);
        _bindings = new();
        _audit = new JsonlAuditSink(Path.Combine(_root, "audit", "test.jsonl"));
        Assert.True(_adapterTypes.RegisterPackageTypes("test.adapter.file", [new("adapter.file", "adapter")]).Ok);
        Assert.True(_adapterTypes.RegisterPackageTypes("test.channel", [new("channel.conversation", "channel")]).Ok);
        _types = _adapterTypes;
        _adapters = new AdapterManager(_types, _bindings);
        _channels = new ChannelManager(_adapters, _events, _bindings);
        _graph = new GraphManager(_adapters, _channels, _clock, _types, new JintScriptExecutor(), _audit, _signals);
        _bindings.Bind(_graph);
        _mailbox = new FileMailbox(Path.Combine(_root, "adapters", "test-1"));
        _adapter = new FileAdapter(new(_adapterAddress, new("file", "test-account", AdapterDirection.Both)), _mailbox, _events, _clock);
    }

    public void Dispose()
    {
        _signals.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _audit.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Two_conversation_folders_route_through_their_channels_and_back_to_matching_outboxes()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        var channelOne = new Address("channel", "conversation", "chat-1");
        var channelTwo = new Address("channel", "conversation", "chat-2");
        await AddChannelAsync(channelOne, "chat-1");
        await AddChannelAsync(channelTwo, "chat-2");
        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);

        await WriteInputAsync("chat-1", "one");
        await WriteInputAsync("chat-2", "two");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DispatchOneAsync();
        await DispatchOneAsync();
        await DispatchOneAsync();
        await DispatchOneAsync();
        await DispatchOneAsync();

        var outOne = Assert.Single(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox", "chat-1"), "*.json"));
        var outTwo = Assert.Single(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox", "chat-2"), "*.json"));
        Assert.Equal("one", ReadOutbound(outOne).Text);
        Assert.Equal("two", ReadOutbound(outTwo).Text);
        Assert.Equal(2, _channels.List().Count);
        Assert.Equal(5, _signals.Snapshot().Count(r => r.Group == "dispatch" && r.Outcome == "ok"));
    }

    [Fact]
    public async Task Unbound_conversation_is_not_claimed_or_replied_to()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        var duplicate = await _channels.CreateAsync(new(new("channel", "conversation", "duplicate"), "duplicate", [new(_adapterAddress, "chat-1")]), default);
        Assert.False(duplicate.Ok);
        await _adapters.StartAsync(_adapterAddress, default);
        await WriteInputAsync("chat-3", "nobody owns this room");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DispatchOneAsync();
        await DispatchOneAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox"), "*.json", SearchOption.AllDirectories));
        Assert.Equal(2, _signals.Snapshot().Count(r => r.Group == "dispatch"));
    }

    [Fact]
    public async Task Channel_reply_capability_cannot_change_its_bound_destination()
    {
        var maliciousReply = new ScriptPackage(ChannelReply().Manifest,
            "async function onEvent(event, ctx) { await ctx.channel.reply({ platformConversationId: 'chat-2', text: event.text }); }");
        Assert.True((await _graph.LoadAsync([AdapterIntake(), maliciousReply], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        await _adapters.StartAsync(_adapterAddress, default);
        await WriteInputAsync("chat-1", "restricted");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DispatchOneAsync();
        await DispatchOneAsync();
        var replyResult = await DispatchOneResultAsync();
        Assert.False(replyResult.Ok);
        var auditEntries = File.ReadAllLines(Path.Combine(_root, "audit", "test.jsonl"));
        Assert.Contains(auditEntries, line => line.Contains("denied", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox"), "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Unknown_capability_restricted_script_and_bad_channel_binding_are_rejected()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply()], default)).Ok);
        var badCapability = AdapterIntake() with { Manifest = AdapterIntake().Manifest with { Capabilities = ["filesystem.root"] } };
        Assert.False((await _graph.LoadAsync([badCapability], default)).Ok);
        var restricted = AdapterIntake() with { Manifest = AdapterIntake().Manifest with { TrustDomain = TrustDomain.Restricted } };
        Assert.False((await _graph.LoadAsync([restricted], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        var invalid = await _channels.CreateAsync(new(new("channel", "conversation", "bad"), "bad", [new(_adapterAddress, "../escape")]), default);
        Assert.False(invalid.Ok);
    }

    [Fact]
    public async Task Point_logs_land_on_the_same_tree_as_the_edge_that_produced_them()
    {
        var loggingIntake = new ScriptPackage(
            new("adapter.log-intake", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent",
                [ScriptCapabilities.ChannelAccept], TrustDomain.Builtin),
            "async function onEvent(event, ctx) { ctx.log.info('saw message', { conversation: event.platformConversationId }); await ctx.channel.accept(); }");
        Assert.True((await _graph.LoadAsync([loggingIntake], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        var channel = new Address("channel", "conversation", "chat-1");
        await AddChannelAsync(channel, "chat-1");
        await _adapters.StartAsync(_adapterAddress, default);

        var envelope = new AdapterEnvelope(_adapterAddress, "file", "chat-1", "m-1", "hello", DateTimeOffset.UtcNow);
        var dispatched = await _graph.DispatchAsync(new(EventNames.AdapterEnvelope, _adapterAddress, envelope.EventId,
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default);
        Assert.True(dispatched.Ok, dispatched.Error?.Detail ?? dispatched.Error?.Message);

        var records = _signals.Snapshot();
        var root = Assert.Single(SignalTree.Build(records));
        Assert.Equal("event", root.Record.Group);
        var edge = Assert.Single(root.Children);
        Assert.Equal("dispatch", edge.Record.Group);
        Assert.Equal(envelope.EventId, edge.Record.StepId);
        Assert.Contains(edge.Children, c => c.Record.Group == "module");
        Assert.Contains(edge.Children, c => c.Record.Group == "capability");
        // One tree: every non-root record points at a record that is also in the log.
        var ids = records.Select(r => r.Id).ToHashSet();
        Assert.All(records.Where(r => r.ParentId is not null), r => Assert.Contains(r.ParentId!.Value, ids));
        Assert.All(records, r => Assert.Equal(envelope.EventId, r.StepId));
    }

    [Fact]
    public async Task Script_log_records_are_capped_and_never_break_the_handler()
    {
        var chatty = new ScriptPackage(
            new("adapter.chatty", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent", [],
                TrustDomain.Builtin),
            "async function onEvent(event, ctx) { for (let i = 0; i < 500; i++) ctx.log.error('spam ' + i); }");
        Assert.True((await _graph.LoadAsync([chatty], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await _adapters.StartAsync(_adapterAddress, default);

        var envelope = new AdapterEnvelope(_adapterAddress, "file", "chat-1", "m-1", "hello", DateTimeOffset.UtcNow);
        var dispatched = await _graph.DispatchAsync(new(EventNames.AdapterEnvelope, _adapterAddress, envelope.EventId,
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default);

        Assert.True(dispatched.Ok, dispatched.Error?.Detail ?? dispatched.Error?.Message);
        var moduleRecords = _signals.Snapshot().Where(r => r.Group == "module").ToArray();
        Assert.Equal(201, moduleRecords.Length);
        Assert.Single(moduleRecords, r => r.Outcome == "truncated");
        Assert.Equal(200, moduleRecords.Count(r => r.Outcome == "error"));
    }

    [Fact]
    public async Task Fan_out_to_several_handlers_is_a_branch_not_several_roots()
    {
        // Two scripts mounted on the same adapter and event: one event, two lanes.
        var first = new ScriptPackage(
            new("adapter.fan-a", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent", [],
                TrustDomain.Builtin),
            "async function onEvent(event, ctx) { ctx.log.info('a'); }");
        var second = new ScriptPackage(
            new("adapter.fan-b", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent", [],
                TrustDomain.Builtin),
            "async function onEvent(event, ctx) { ctx.log.warn('b'); }");
        Assert.True((await _graph.LoadAsync([first, second], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await _adapters.StartAsync(_adapterAddress, default);

        var envelope = new AdapterEnvelope(_adapterAddress, "file", "chat-1", "m-1", "hello", DateTimeOffset.UtcNow);
        var dispatched = await _graph.DispatchAsync(new(EventNames.AdapterEnvelope, _adapterAddress, envelope.EventId,
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default);
        Assert.True(dispatched.Ok, dispatched.Error?.Detail ?? dispatched.Error?.Message);

        var roots = SignalTree.Build(_signals.Snapshot());
        var fanOut = Assert.Single(roots);
        Assert.True(fanOut.Forks);
        Assert.Equal(2, fanOut.Children.Count);
        // Each lane keeps its own note: the fork is not flattened into one line.
        Assert.Equal(["a"], fanOut.Children[0].Children.Select(c => c.Record.Detail!["message"]));
        Assert.Equal(["b"], fanOut.Children[1].Children.Select(c => c.Record.Detail!["message"]));
    }

    [Fact]
    public async Task Event_emitted_inside_a_handler_appears_as_a_branch_of_that_handler()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        await _adapters.StartAsync(_adapterAddress, default);
        await WriteInputAsync("chat-1", "one");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);

        // Drain both queued events: the clock pulse (poll) and the adapter envelope (accept).
        await DispatchOneAsync();
        await DispatchOneAsync();

        var intake = Assert.Single(SignalTree.Build(_signals.Snapshot()), n => n.Record.Mechanism == "event" &&
            n.Record.Name == EventNames.AdapterEnvelope);
        var intakeHandler = Assert.Single(intake.Children, c => c.Record.Mechanism == "emit");
        Assert.Contains(intakeHandler.Children, c => c.Record.Mechanism == "call" &&
            c.Record.Name == ScriptCapabilities.ChannelAccept);
    }

    [Fact]
    public async Task Routing_creates_a_channel_when_missing_and_reuses_it_afterwards()
    {
        var intake = new ScriptPackage(
            new("adapter.router", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent",
                [ScriptCapabilities.ChannelRoute], TrustDomain.Builtin),
            "async function onEvent(event, ctx) { return await ctx.channel.route({ channelId: event.platformConversationId }); }");
        var loaded = await _graph.LoadAsync([intake], default);
        Assert.True(loaded.Ok, loaded.Error?.Detail ?? loaded.Error?.Message);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await _adapters.StartAsync(_adapterAddress, default);

        var first = new AdapterEnvelope(_adapterAddress, "file", "chat-9", "m-1", "hello", DateTimeOffset.UtcNow);
        var firstDispatch = await DispatchEnvelopeAsync(first);
        Assert.True(firstDispatch.Ok, firstDispatch.Error?.Detail ?? firstDispatch.Error?.Message);
        var created = Assert.Single(_channels.List());
        Assert.Equal(new Address("channel", "conversation", "chat-9"), created.Address);
        Assert.True(created.Running);

        var second = new AdapterEnvelope(_adapterAddress, "file", "chat-9", "m-2", "again", DateTimeOffset.UtcNow);
        Assert.True((await DispatchEnvelopeAsync(second)).Ok);

        // Reuse: still one channel, and the script was told it was not created the second time.
        Assert.Single(_channels.List());
        Assert.Contains(_signals.Snapshot(), r => r.Name == ScriptCapabilities.ChannelRoute && r.Outcome == "ok");
    }

    [Fact]
    public async Task Routing_without_the_route_capability_is_denied()
    {
        var intake = new ScriptPackage(
            new("adapter.router-denied", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent",
                [ScriptCapabilities.SelfAdapterPoll], TrustDomain.Builtin),
            "async function onEvent(event, ctx) { await ctx.channel.route({ channelId: event.platformConversationId }); }");
        Assert.True((await _graph.LoadAsync([intake], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await _adapters.StartAsync(_adapterAddress, default);

        var dispatched = await DispatchEnvelopeAsync(new(_adapterAddress, "file", "chat-9", "m-1", "hello", DateTimeOffset.UtcNow));
        Assert.False(dispatched.Ok);
        Assert.Empty(_channels.List());
        Assert.Contains(_signals.Snapshot(), r => r.Outcome == "denied");
    }

    private async Task<Result> DispatchEnvelopeAsync(AdapterEnvelope envelope) => await _graph.DispatchAsync(
        new(EventNames.AdapterEnvelope, _adapterAddress, envelope.EventId,
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default);

    private async Task AddChannelAsync(Address address, string conversation)
    {
        var result = await _channels.CreateAsync(new(address, conversation, [new(_adapterAddress, conversation)]), default);
        Assert.True(result.Ok, result.Error?.Detail ?? result.Error?.Message);
        Assert.True((await _channels.StartAsync(address, default)).Ok);
    }

    private async Task WriteInputAsync(string conversation, string text)
    {
        var directory = Path.Combine(_mailbox.Root, "inbox", conversation);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{Guid.NewGuid():N}.ready.json"),
            JsonSerializer.Serialize(new { text, messageId = Guid.NewGuid().ToString("N") }));
    }

    private async Task<Result<int>> PulseAdapterAsync() =>
        await _clock.PulseDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), default);

    private async Task DispatchOneAsync()
    {
        var result = await DispatchOneResultAsync();
        Assert.True(result.Ok, result.Error?.Detail ?? result.Error?.Message);
    }

    private async Task<Result> DispatchOneResultAsync()
    {
        await using var enumerator = _events.ReadAllAsync(default).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        return await _graph.DispatchAsync(enumerator.Current, default);
    }

    private ScriptPackage AdapterIntake() => new(
        new("adapter.channel-intake", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope, EventNames.ClockPulse], "onEvent",
            [ScriptCapabilities.ChannelAccept, ScriptCapabilities.SelfAdapterPoll], TrustDomain.Builtin),
        "async function onEvent(event, ctx) { if (ctx.eventName === 'clock.pulse') { await ctx.self.adapter.poll(); return; } await ctx.channel.accept(); }");

    private ScriptPackage ChannelReply() => new(
        new("channel.bound-reply", 1, "channel", MountMode.Default, [EventNames.ChannelMessageAccepted], "onEvent",
            [ScriptCapabilities.ChannelReply], TrustDomain.Builtin),
        "async function onEvent(event, ctx) { await ctx.channel.reply({ platformConversationId: event.binding.platformConversationId, text: event.text, replyTo: event.platformMessageId }); }");

    private static OutboundMessage ReadOutbound(string path) => JsonSerializer.Deserialize<OutboundMessage>(File.ReadAllText(path))!;

    private sealed class GraphBindingProxy : IInstanceBindings
    {
        private IInstanceBindings? _inner;
        public void Bind(IInstanceBindings inner) => _inner = inner;
        public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) => _inner!.AttachAsync(target, ct);
        public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) => _inner!.DetachAsync(target, ct);
    }
}
