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
    private readonly ExecutionJournal _journal = new();
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
        Assert.True(_adapterTypes.RegisterPackageTypes("test.channel", [new("channel.logical", "channel")]).Ok);
        _types = _adapterTypes;
        _adapters = new AdapterManager(_types, _bindings);
        _channels = new ChannelManager(_adapters, _events, _bindings);
        _graph = new GraphManager(_adapters, _channels, _clock, _types, new JintScriptExecutor(_journal), _audit);
        _bindings.Bind(_graph);
        _mailbox = new FileMailbox(Path.Combine(_root, "adapters", "test-1"));
        _adapter = new FileAdapter(new(_adapterAddress, new("file", "test-account", AdapterDirection.Both)), _mailbox, _events, _clock);
    }

    public void Dispose()
    {
        _audit.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Two_conversation_folders_route_through_their_channels_and_back_to_matching_outboxes()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        var channelOne = new Address("channel", "logical", "chat-1");
        var channelTwo = new Address("channel", "logical", "chat-2");
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
        Assert.Equal(5, _journal.Snapshot().Count(r => r.Succeeded));
    }

    [Fact]
    public async Task Unbound_conversation_is_not_claimed_or_replied_to()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "logical", "chat-1"), "chat-1");
        var duplicate = await _channels.CreateAsync(new(new("channel", "logical", "duplicate"), "duplicate", [new(_adapterAddress, "chat-1")]), default);
        Assert.False(duplicate.Ok);
        await _adapters.StartAsync(_adapterAddress, default);
        await WriteInputAsync("chat-3", "nobody owns this room");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DispatchOneAsync();
        await DispatchOneAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox"), "*.json", SearchOption.AllDirectories));
        Assert.Equal(2, _journal.Snapshot().Count);
    }

    [Fact]
    public async Task Channel_reply_capability_cannot_change_its_bound_destination()
    {
        var maliciousReply = new ScriptPackage(ChannelReply().Manifest,
            "async function onEvent(event, ctx) { await ctx.channel.reply({ platformConversationId: 'chat-2', text: event.text }); }");
        Assert.True((await _graph.LoadAsync([AdapterIntake(), maliciousReply], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "logical", "chat-1"), "chat-1");
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
        var invalid = await _channels.CreateAsync(new(new("channel", "logical", "bad"), "bad", [new(_adapterAddress, "../escape")]), default);
        Assert.False(invalid.Ok);
    }

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
