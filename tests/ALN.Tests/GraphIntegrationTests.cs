using System.Text.Json;
using ALN.Adapter.File;
using ALN.Channel;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;
using ALN.Scripting.Jint;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

public sealed class GraphIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-channel-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _audit;
    // These tests assert on the whole dispatch graph, down to every queue step, so they ask for Debug explicitly.
    // The default level leaves the noisy half out on purpose.
    private readonly SignalLog _signals = SignalLog.Open(null, new SignalLogOptions(SignalLevel.Debug));
    private readonly AdapterTypeCatalog _adapterTypes = new();
    private readonly IModuleTypeCatalog _types;
    private readonly EventQueue _events = new(32);
    private readonly ALN.Clock.ClockSource _clock;
    private readonly GraphBindingProxy _bindings = new();
    private readonly AdapterManager _adapters;
    private readonly ChannelManager _channels;
    private readonly ModuleCatalog _catalog;
    private readonly GraphManager _graph;
    private readonly FileMailbox _mailbox;
    private readonly FileAdapter _adapter;
    private readonly Address _adapterAddress = new("adapter", "file", "test-1");
    private readonly QueueModel _model = new();
    private readonly SessionManager _sessions;

    public GraphIntegrationTests()
    {
        _events = new(32);
        _clock = new ALN.Clock.ClockSource(_events);
        _bindings = new();
        _audit = new SqliteStore(Path.Combine(_root, "aln.db"));
        Assert.True(_adapterTypes.RegisterPackageTypes("test.adapter.file", [new("adapter.file", "adapter")]).Ok);
        Assert.True(_adapterTypes.RegisterPackageTypes("test.channel", [new("channel.conversation", "channel")]).Ok);
        _types = _adapterTypes;
        _adapters = new AdapterManager(_types, _bindings);
        var catalog = new ModuleCatalog();
        catalog.Attach(_adapters);
        _channels = new ChannelManager(_events, _bindings, catalog);
        _sessions = new SessionManager(_root, _model);
        // The graph finds its managers through the address book, exactly as the host wires it: names in, then the
        // instance owners observed so the graph can enumerate mount targets without knowing what kinds exist.
        _catalog = ModuleWiring.With(_adapters, _channels, _clock, _sessions);
        _graph = new GraphManager(_catalog, _types, new JintScriptExecutor(), _audit, _signals, state: null);
        _bindings.Bind(_graph);
        _mailbox = new FileMailbox(Path.Combine(_root, "adapters", "test-1"));
        _adapter = new FileAdapter(new(_adapterAddress, new("file", "test-account", AdapterDirection.Both), "adapter.file"),
            _mailbox, _events)
        {
            // The adapter keeps its own beat in the host. Here a test polls by hand, so the beat is parked out of the
            // way instead of racing it for the same files.
            PollInterval = TimeSpan.FromHours(1)
        };
        // Nothing to schedule: the adapter asks the clock for no rhythm, and the attach is accepted without holding one.
        Assert.True(_clock.Attach(new ModuleId(_adapterAddress.ToString()), _adapter).Ok);
    }

    public void Dispose()
    {
        _signals.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _audit.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_manual_script_runs_only_on_the_instance_it_was_mounted_on()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply(), ManualTagger("channel.tagger", "tagged")], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        var channelOne = new Address("channel", "conversation", "chat-1");
        var channelTwo = new Address("channel", "conversation", "chat-2");
        await AddChannelAsync(channelOne, "chat-1");
        await AddChannelAsync(channelTwo, "chat-2");

        // A manual script is attached to nothing by loading; only an explicit mount makes it run.
        Assert.DoesNotContain(_graph.Mounted, m => m.Package.Manifest.Id == "channel.tagger");
        Assert.True((await _graph.MountAsync("channel.tagger", channelOne, default)).Ok);
        Assert.False((await _graph.MountAsync("channel.tagger", channelOne, default)).Ok);
        // A default script cannot be mounted by hand: its lifecycle already owns it.
        Assert.False((await _graph.MountAsync("channel.bound-reply", channelOne, default)).Ok);

        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);
        await WriteInputAsync("chat-1", "hello-1");
        await WriteInputAsync("chat-2", "hello-2");
        Assert.Equal(2, (await PulseAdapterAsync()).Value);
        await DrainAsync();

        // chat-1 has two responders (the default one plus the mounted one); chat-2 has one.
        var outOne = Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox", "chat-1"), "*.json")
            .Select(ReadOutbound).Select(m => m.Text).Order().ToArray();
        var outTwo = Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox", "chat-2"), "*.json")
            .Select(ReadOutbound).Select(m => m.Text).ToArray();
        Assert.Equal(["hello-1", "tagged"], outOne);
        Assert.Equal(["hello-2"], outTwo);

        // Unmounting is equally per-instance: chat-1 loses the tagger, chat-2 never had it.
        Assert.True((await _graph.UnmountAsync("channel.tagger", channelOne, default)).Ok);
        Assert.False((await _graph.UnmountAsync("channel.tagger", channelOne, default)).Ok);
        await WriteInputAsync("chat-1", "hello-3");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();
        var afterUnmount = Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox", "chat-1"), "*.json")
            .Select(ReadOutbound).Select(m => m.Text).Order().ToArray();
        Assert.Equal(["hello-1", "hello-3", "tagged"], afterUnmount);
    }

    /// <summary>
    /// A script tree is a file tree, so "reload" is: read it again, hand the whole catalogue back. What makes
    /// that safe is that loading is a replacement, not an addition — the same script id with a new body needs no
    /// unmount, no remount and no restart, and the next event runs the new body.
    /// </summary>
    [Fact]
    public async Task Loading_the_catalogue_again_replaces_what_runs_without_remounting_anything()
    {
        static ScriptPackage Replying(string version) => new(
            new("channel.bound-reply", 1, "channel", MountMode.Default, [EventNames.ChannelMessageAccepted], "onEvent",
                [ScriptCapabilities.ChannelReply], TrustDomain.Builtin),
            $"async function onEvent(event, ctx) {{ await ctx.channel.reply({{ platformConversationId: event.binding.platformConversationId, text: '{version}:' + event.text }}); }}");

        Assert.True((await _graph.LoadAsync([AdapterIntake(), Replying("v1")], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);

        await WriteInputAsync("chat-1", "one");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();
        Assert.Equal(["v1:one"], OutboxTexts("chat-1"));

        // The body changed, the id did not. Nothing was unmounted; the default mount was simply rebuilt from the
        // catalogue that arrived, which is why a reload does not have to remember what was mounted before.
        Assert.True((await _graph.LoadAsync([AdapterIntake(), Replying("v2")], default)).Ok);
        await WriteInputAsync("chat-1", "two");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();
        Assert.Equal(["v1:one", "v2:two"], OutboxTexts("chat-1"));
    }

    /// <summary>
    /// A reload that fails must leave the running one alone. That is the whole reason the catalogue is validated
    /// before the current set is cleared: an edited script with a typo is a reason to keep working, not a reason
    /// to stop answering.
    /// </summary>
    [Fact]
    public async Task A_reload_that_is_refused_leaves_the_previous_scripts_in_place()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);

        await WriteInputAsync("chat-1", "one");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();
        Assert.Equal(["one"], OutboxTexts("chat-1"));

        // Same id, a capability this host does not have: refused at the door.
        var refused = new ScriptPackage(
            new("channel.bound-reply", 2, "channel", MountMode.Default, [EventNames.ChannelMessageAccepted],
                "onEvent", ["channel.teleport"], TrustDomain.Builtin),
            "async function onEvent(event, ctx) { }");
        // The load succeeds and the script does not: refusing an edited body must not take the working one away.
        Assert.True((await _graph.LoadAsync([AdapterIntake(), refused], default)).Ok);
        Assert.Contains(_graph.RejectedScripts, issue => issue.Reason.Contains("capability", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(_graph.Scripts, entry => entry.Package.Manifest.Id == "channel.bound-reply");

        await WriteInputAsync("chat-1", "two");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();
        Assert.Equal(["one", "two"], OutboxTexts("chat-1"));
    }

    /// <summary>
    /// A manual mount is a durable rule naming a script id — not a reference to a package object — so replacing
    /// that script's body keeps the rule, and the instance keeps answering with the new body.
    /// </summary>
    [Fact]
    public async Task A_manual_mount_survives_a_reload_because_a_rule_names_a_script_not_a_package()
    {
        var channel = new Address("channel", "conversation", "chat-1");
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ManualTagger("channel.tagger", "tagged")], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(channel, "chat-1");
        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);
        Assert.True((await _graph.MountAsync("channel.tagger", channel, default)).Ok);

        await WriteInputAsync("chat-1", "one");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();
        Assert.Equal(["tagged"], OutboxTexts("chat-1"));

        Assert.True((await _graph.LoadAsync([AdapterIntake(), ManualTagger("channel.tagger", "tagged-2")], default)).Ok);
        // Still mounted, and still the same rule: a reload does not unmount what a person mounted by hand.
        Assert.Contains(_graph.Mounted, m => m.Package.Manifest.Id == "channel.tagger");
        Assert.Single(_graph.Rules, rule => rule.ScriptId == "channel.tagger");

        await WriteInputAsync("chat-1", "two");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();
        Assert.Equal(["tagged", "tagged-2"], OutboxTexts("chat-1"));
    }

    /// <summary>
    /// The reply mode decides how many messages one answer becomes; the Adapter still sends one message per
    /// call. Segments is the default because a chat window is not a page.
    /// </summary>
    [Fact]
    public async Task One_reply_arrives_as_several_messages_and_a_mode_that_does_not_exist_sends_nothing()
    {
        static ScriptPackage Replying(string id, string body) => new(
            new(id, 1, "channel", MountMode.Default, [EventNames.ChannelMessageAccepted], "onEvent",
                [ScriptCapabilities.ChannelReply], TrustDomain.Builtin),
            $"async function onEvent(event, ctx) {{ {body} }}");

        var defaultMode = Replying("channel.multi-default",
            "await ctx.channel.reply({ platformConversationId: event.binding.platformConversationId, text: '一\\n\\n二\\n\\n三' });");
        var verbatim = Replying("channel.multi-text",
            "await ctx.channel.reply({ platformConversationId: event.binding.platformConversationId, text: '一\\n\\n二', mode: 'text' });");
        Assert.True((await _graph.LoadAsync([AdapterIntake(), defaultMode, verbatim], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);

        await WriteInputAsync("chat-1", "in");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();
        // Two scripts answered one message: the default mode sent three, mode=text sent one with the blank line
        // still inside it — the Adapter is never asked to split anything.
        var texts = Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox", "chat-1"), "*.json")
            .Select(ReadOutbound).Select(m => m.Text).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["一", "一\n\n二", "三", "二"], texts);
    }

    [Fact]
    public async Task A_reply_mode_this_build_does_not_have_is_refused_instead_of_falling_back_to_text()
    {
        var unknown = new ScriptPackage(
            new("channel.multi-unknown", 1, "channel", MountMode.Default, [EventNames.ChannelMessageAccepted],
                "onEvent", [ScriptCapabilities.ChannelReply], TrustDomain.Builtin),
            "async function onEvent(event, ctx) { await ctx.channel.reply({ " +
            "platformConversationId: event.binding.platformConversationId, text: '不会发出去', mode: 'image' }); }");
        Assert.True((await _graph.LoadAsync([AdapterIntake(), unknown], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);

        await WriteInputAsync("chat-1", "in");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        var failures = new List<Result>();
        while (_events.TryRead(out var queued))
        {
            var dispatched = await _graph.DispatchAsync(queued, default);
            if (!dispatched.Ok) { failures.Add(dispatched); break; }
        }
        Assert.Single(failures);
        var outbox = Path.Combine(_mailbox.Root, "outbox");
        Assert.Empty(Directory.Exists(outbox)
            ? Directory.GetFiles(outbox, "*.json", SearchOption.AllDirectories)
            : []);
        Assert.Contains(_audit.ReadAudit(),
            entry => entry.Outcome == "denied" && (entry.Detail ?? "").Contains("image", StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole path with speaking as a tool: script declares it, the model calls it, the reply leaves through
    /// the Channel binding — and the turn ends because the model called `wait`, not because a step went quiet.
    /// </summary>
    [Fact]
    public async Task A_session_speaks_through_its_tool_and_the_turn_ends_on_wait()
    {
        _model.Enqueue(
            new ToolCallDelta(0, "call-1", "speak", """{"text":"在的\n\n有什么事？"}"""),
            new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1)));
        _model.Enqueue(
            new ToolCallDelta(0, "call-2", "wait", "{}"),
            new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1)));

        var chat = new ScriptPackage(
            new("channel.talk", 1, "channel", MountMode.Default, [EventNames.ChannelMessageAccepted], "onEvent",
                [ScriptCapabilities.SessionActivate, ScriptCapabilities.ChannelReply], TrustDomain.Builtin),
            """
            async function onEvent(event, ctx) {
              const spec = {
                modelProfileId: "default", objective: "聊两句", formatVersion: "1",
                tools: [{
                  name: "speak",
                  description: "Say something.",
                  parametersJson: JSON.stringify({
                    type: "object",
                    properties: { text: { type: "string" }, mode: { type: "string" } },
                    required: ["text"], additionalProperties: false
                  })
                }]
              };
              const outcome = JSON.parse(await ctx.session.activate(spec));
              if (!outcome.ok) throw new Error(JSON.stringify(outcome.error));
            }
            """);
        Assert.True((await _graph.LoadAsync([AdapterIntake(), chat], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);

        await WriteInputAsync("chat-1", "在吗");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();

        // segments is the default mode, so the two paragraphs arrived as two messages. The mailbox names files by
        // attempt id, so the *set* is what the transport shows; the order the model wrote them in is what the
        // Session's own record preserves.
        var texts = Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox", "chat-1"), "*.json")
            .Select(ReadOutbound).Select(m => m.Text).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["在的", "有什么事？"], texts);
        var spoken = Directory.GetFiles(Path.Combine(_root, "modules"), "history.jsonl", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadAllLines(path))
            .Select(line => JsonSerializer.Deserialize<SessionHistoryEntry>(line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .SelectMany(entry => entry.ChannelMessages ?? [])
            .ToArray();
        Assert.Contains(spoken, message => message.Source == "assistant" && message.Content == "在的\n\n有什么事？");
        Assert.Equal(2, _model.Requests);
    }

    /// <summary>A model whose steps the test scripts: one array of events per call.</summary>
    private sealed class QueueModel : IModelManager
    {
        private readonly Queue<ModelEvent[]> _steps = new();
        public int Requests { get; private set; }

        public void Enqueue(params ModelEvent[] events) => _steps.Enqueue(events);

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests++;
            if (_steps.Count == 0)
                throw new InvalidOperationException("The test did not script this model call.");
            var step = _steps.Dequeue();
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream(step)));
        }

        private static async IAsyncEnumerable<ModelEvent> Stream(ModelEvent[] events,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var item in events) yield return item;
        }

        public Result RegisterFactory(string packageId, IModelProviderFactory factory) => Result.Success();
        public Result RemovePackageFactories(string packageId) => Result.Success();
        public IReadOnlyList<ModelProviderTypeInfo> ListTypes() => [];
        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<Result<IModelProvider>> CreateAsync(string category, string instance, string configurationJson,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public Result<IModelProvider> Get(Address provider) => Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "No providers."));
        public IReadOnlyList<ModelProviderInfo> List() => [];
        public Result RegisterProfile(ModelProfile profile) => Result.Success();
        public Result RemoveProfile(string profileId) => Result.Success();
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }

    private async Task DrainAsync()
    {
        while (_events.TryRead(out var queued))
        {
            var dispatched = await _graph.DispatchAsync(queued, default);
            Assert.True(dispatched.Ok, dispatched.Error?.Detail ?? dispatched.Error?.Message);
        }
    }

    [Fact]
    public async Task Two_conversation_folders_route_through_their_channels_and_back_to_matching_outboxes()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        var channelOne = new Address("channel", "conversation", "chat-1");
        var channelTwo = new Address("channel", "conversation", "chat-2");
        await AddChannelAsync(channelOne, "chat-1");
        await AddChannelAsync(channelTwo, "chat-2");
        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);

        await WriteInputAsync("chat-1", "one");
        await WriteInputAsync("chat-2", "two");
        Assert.Equal(2, (await PulseAdapterAsync()).Value);
        // One round fewer than before the trigger change: the pulse no longer queues an event of its own.
        await DispatchOneAsync();
        await DispatchOneAsync();
        await DispatchOneAsync();
        await DispatchOneAsync();

        var outOne = Assert.Single(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox", "chat-1"), "*.json"));
        var outTwo = Assert.Single(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox", "chat-2"), "*.json"));
        Assert.Equal("one", ReadOutbound(outOne).Text);
        Assert.Equal("two", ReadOutbound(outTwo).Text);
        Assert.Equal(2, _channels.List().Count);
        // One dispatch fewer than before the trigger change: the pulse polls directly instead of being dispatched.
        Assert.Equal(4, _signals.Snapshot().Count(r => r.Group == "dispatch" && r.Outcome == "ok"));
    }

    [Fact]
    public async Task Unbound_conversation_is_not_claimed_or_replied_to()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        var duplicate = await _channels.CreateAsync(new(new("channel", "conversation", "duplicate"), "duplicate", [new(_adapterAddress, "chat-1")], "channel.conversation"), default);
        Assert.False(duplicate.Ok);
        await _adapters.StartAsync(_adapterAddress, default);
        await WriteInputAsync("chat-3", "nobody owns this room");
        // The trigger is not an event any more: the pulse polls directly, so the queue holds the envelope only.
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DispatchOneAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox"), "*.json", SearchOption.AllDirectories));
        Assert.Equal(1, _signals.Snapshot().Count(r => r.Group == "dispatch"));
    }

    [Fact]
    public async Task Channel_reply_capability_cannot_change_its_bound_destination()
    {
        var maliciousReply = new ScriptPackage(ChannelReply().Manifest,
            "async function onEvent(event, ctx) { await ctx.channel.reply({ platformConversationId: 'chat-2', text: event.text }); }");
        Assert.True((await _graph.LoadAsync([AdapterIntake(), maliciousReply], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        await _adapters.StartAsync(_adapterAddress, default);
        await WriteInputAsync("chat-1", "restricted");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        // The trigger polls directly now, so the queue is envelope → accepted → the refused reply.
        await DispatchOneAsync();
        var replyResult = await DispatchOneResultAsync();
        Assert.False(replyResult.Ok);
        var auditEntries = _audit.ReadAudit();
        Assert.Contains(auditEntries, entry => entry.Outcome == "denied");
        Assert.Empty(Directory.GetFiles(Path.Combine(_mailbox.Root, "outbox"), "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Unknown_capability_restricted_script_and_bad_channel_binding_are_rejected()
    {
        Assert.True((await _graph.LoadAsync([AdapterIntake(), ChannelReply()], default)).Ok);
        var badCapability = AdapterIntake() with { Manifest = AdapterIntake().Manifest with { Capabilities = ["filesystem.root"] } };
        // The load succeeds and that script does not: a refusal costs the script, not the catalogue.
        Assert.True((await _graph.LoadAsync([badCapability], default)).Ok);
        Assert.Contains(_graph.RejectedScripts, issue => issue.Reason.Contains("capability", StringComparison.OrdinalIgnoreCase));
        // The body that was already there stays: a refused replacement never replaced anything.
        Assert.DoesNotContain(_graph.Scripts, entry => entry.Package.Manifest.Capabilities.Contains("filesystem.root"));
        var restricted = AdapterIntake() with { Manifest = AdapterIntake().Manifest with { TrustDomain = TrustDomain.Restricted } };
        Assert.True((await _graph.LoadAsync([restricted], default)).Ok);
        Assert.Contains(_graph.RejectedScripts, issue => issue.Reason.Contains("Restricted", StringComparison.Ordinal));
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        var invalid = await _channels.CreateAsync(new(new("channel", "conversation", "bad"), "bad", [new(_adapterAddress, "../escape")], "channel.conversation"), default);
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
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
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
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
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
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
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
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        await _adapters.StartAsync(_adapterAddress, default);
        await WriteInputAsync("chat-1", "one");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);

        // Only the envelope is queued now: the trigger polled directly instead of being an event of its own.
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
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
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
    public async Task Routing_to_a_name_that_already_serves_another_conversation_moves_it()
    {
        var intake = new ScriptPackage(
            new("adapter.router-move", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent",
                [ScriptCapabilities.ChannelRoute], TrustDomain.Builtin),
            "async function onEvent(event, ctx) { return await ctx.channel.route({ channelId: 'shared' }); }");
        Assert.True((await _graph.LoadAsync([intake], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await _adapters.StartAsync(_adapterAddress, default);

        Assert.True((await DispatchEnvelopeAsync(new(_adapterAddress, "file", "one", "m-1", "hello", DateTimeOffset.UtcNow))).Ok);
        var channel = Assert.Single(_channels.List());
        Assert.Equal("one", Assert.Single(channel.Bindings).PlatformConversationId);

        // Same name, different conversation: one channel serves one conversation, so routing re-aims it through the same
        // operation channels.connect performs — not by minting a second channel under a name that is already taken.
        Assert.True((await DispatchEnvelopeAsync(new(_adapterAddress, "file", "two", "m-2", "hello", DateTimeOffset.UtcNow))).Ok);

        channel = Assert.Single(_channels.List());
        Assert.Equal(new Address("channel", "conversation", "shared"), channel.Address);
        Assert.Equal("two", Assert.Single(channel.Bindings).PlatformConversationId);
    }

    [Fact]
    public async Task Showing_one_instance_answers_what_it_is_aimed_at_rather_than_what_it_is_called()
    {
        var channel = new Address("channel", "conversation", "chat-7");
        // The graph has to be loaded before an instance can be attached to it — an instance exists on the graph.
        Assert.True((await _graph.LoadAsync([], default)).Ok);
        var created = await _channels.CreateAsync(new(channel, "chat-7",
            [new ChannelBinding(_adapterAddress, "room-7")], ChannelTypes.Conversation), default);
        Assert.True(created.Ok, created.Error?.Message + " / " + created.Error?.Detail);
        var tools = new ScriptTools(_graph, new JsonScriptCatalog(Path.Combine(_root, "program"), _root),
            new ScriptLibrary(_root), _catalog);
        var offers = await tools.ToolsAsync(new("s", Guid.NewGuid(), 1), default);
        var show = offers.Value!.Single(offer => offer.Definition.Name == ScriptTools.InstanceToolName).Tool;

        var answer = await show.ExecuteAsync(new("c1", ScriptTools.InstanceToolName,
            """{"address":"channel.conversation#chat-7"}"""), default);

        Assert.True(answer.Ok, answer.Error?.Message);
        // The state is the instance's own answer, so what it is aimed at appears without the host knowing what a
        // channel is: this is the same text channels.list would show, not a second opinion.
        Assert.Contains("room-7", answer.Value!.Content);
        Assert.Contains("\"running\":", answer.Value!.Content);
        // An instance that cannot describe itself must say that rather than be described by a guess.
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"),
            _ => _adapter, default)).Ok);
        Assert.Contains("does not describe itself", await NothingToDescribe(offers.Value!));

        var invented = await show.ExecuteAsync(new("c2", ScriptTools.InstanceToolName,
            """{"address":"channel.conversation#nope"}"""), default);
        Assert.True(invented.Ok);
        Assert.Contains("no such instance", invented.Value!.Content);
    }

    /// <summary>An instance that cannot describe itself must say that, not be described by a guess.</summary>
    private async Task<string> NothingToDescribe(IReadOnlyList<SessionToolOffer> offers)
    {
        var show = offers.Single(offer => offer.Definition.Name == ScriptTools.InstanceToolName).Tool;
        var answer = await show.ExecuteAsync(new("c3", ScriptTools.InstanceToolName,
            """{"address":"adapter.file#test-1"}"""), default);
        return answer.Value!.Content;
    }

    [Fact]
    public async Task Routing_without_the_route_capability_is_denied()
    {
        var intake = new ScriptPackage(
            new("adapter.router-denied", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent",
                [ScriptCapabilities.SelfAdapterPoll], TrustDomain.Builtin),
            "async function onEvent(event, ctx) { await ctx.channel.route({ channelId: event.platformConversationId }); }");
        Assert.True((await _graph.LoadAsync([intake], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await _adapters.StartAsync(_adapterAddress, default);

        var dispatched = await DispatchEnvelopeAsync(new(_adapterAddress, "file", "chat-9", "m-1", "hello", DateTimeOffset.UtcNow));
        Assert.False(dispatched.Ok);
        Assert.Empty(_channels.List());
        Assert.Contains(_signals.Snapshot(), r => r.Outcome == "denied");
    }

    /// <summary>
    /// A mount target is whatever the address book says belongs to a type — not whatever falls into one of the two
    /// categories the old dispatcher knew by name. The gadget here is a kind the graph has never heard of.
    /// </summary>
    [Fact]
    public async Task A_kind_the_graph_has_never_heard_of_becomes_a_mount_target_by_being_in_the_catalog()
    {
        Assert.True(_adapterTypes.RegisterPackageTypes("test.gadget", [new("gadget", null, IsAbstract: true)]).Ok);
        Assert.True(_adapterTypes.RegisterPackageTypes("test.gadget.thing", [new("gadget.thing", "gadget")]).Ok);
        var gadget = new Address("gadget", "thing", "g-1");
        var owner = new GadgetOwner(gadget);
        var catalog = ModuleWiring.With(_adapters, _channels, _clock, owner);
        var graph = new GraphManager(catalog, _types, new JintScriptExecutor(), _audit, _signals);
        // A script that says it wants "gadget" targets. Nothing in the graph knows that word.
        var watcher = new ScriptPackage(
            new("gadget.watcher", 1, "gadget", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent",
                [ScriptCapabilities.ChannelAccept], TrustDomain.Builtin),
            "async function onEvent(event, ctx) { }");

        Assert.True((await graph.LoadAsync([watcher], default)).Ok);

        // It found one without being told what a gadget is: the address book said what belongs to the type.
        Assert.Contains(graph.Mounted, mount => mount.Target == gadget);
    }

    /// <summary>An owner of one instance of a kind that exists only in this test.</summary>
    private sealed class GadgetOwner(Address address) : IModuleIndex
    {
        public string Category => "gadget";
        public IModule Module { get; } = new Gadget(address);
        public LocatedModule? Locate(ModuleId id) => id == Module.Id
            ? new LocatedModule(Module, new CatalogEntry(Module.Id, "gadget.thing", Category, ModuleRole.Instance))
            : null;
        public IReadOnlyList<CatalogEntry> Entries() =>
            [new CatalogEntry(Module.Id, "gadget.thing", Category, ModuleRole.Instance)];
    }

    private sealed class Gadget(Address address) : IAddressed, IModule
    {
        public Address Address { get; } = address;
        public ModuleId Id => new(Address.ToString());
    }

    private async Task<Result> DispatchEnvelopeAsync(AdapterEnvelope envelope) => await _graph.DispatchAsync(
        new(EventNames.AdapterEnvelope, _adapterAddress, envelope.EventId,
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default);

    private async Task AddChannelAsync(Address address, string conversation)
    {
        var result = await _channels.CreateAsync(new(address, conversation, [new(_adapterAddress, conversation)], "channel.conversation"), default);
        Assert.True(result.Ok, result.Error?.Detail ?? result.Error?.Message);
        Assert.True((await _channels.StartAsync(address, default)).Ok);
    }

    /// <summary>
    /// Nothing in the host answers a message. The only callers of <c>RunTurnAsync</c> are scripts, and so are the
    /// only callers of <c>ChannelManager.AcceptAsync</c>: C# supplies the parts and the bus, never the wiring.
    /// With no catalogue the host still starts, still claims its mail, and still settles the round — which is the
    /// part worth knowing: the input is acknowledged and then dropped, not held for a script that is not there.
    /// </summary>
    [Fact]
    public async Task With_no_scripts_a_message_is_claimed_settled_and_answered_by_nobody()
    {
        Assert.True((await _graph.LoadAsync([], default)).Ok);
        Assert.Empty(_graph.Mounted);

        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);

        await WriteInputAsync("chat-1", "nobody-is-listening");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();

        // Claimed out of the inbox, so nothing is stuck; acknowledged into processed, so nothing will retry it.
        Assert.Empty(Directory.GetFiles(Path.Combine(_mailbox.Root, "inbox", "chat-1")));
        Assert.Single(Directory.GetFiles(Path.Combine(_mailbox.Root, "processed", "chat-1")));

        // Nothing was said, no Channel message was accepted, no Session was opened: all three are script work.
        Assert.False(Directory.Exists(Path.Combine(_mailbox.Root, "outbox", "chat-1")));
        Assert.DoesNotContain(_signals.Snapshot(), record => record.Group == "script");
    }

    /// <summary>
    /// Half the catalogue is still nothing: without the script that turns Adapter envelopes into Channel
    /// messages, a responder has nothing to respond to. The two scripts are two halves of one conversation.
    /// </summary>
    [Fact]
    public async Task Removing_only_the_intake_script_is_enough_to_silence_the_responder()
    {
        Assert.True((await _graph.LoadAsync([ChannelReply()], default)).Ok);
        Assert.True((await _adapters.CreateAsync(new(_adapterAddress, _adapter.Descriptor, "adapter.file"), _ => _adapter, default)).Ok);
        await AddChannelAsync(new("channel", "conversation", "chat-1"), "chat-1");
        Assert.True((await _adapters.StartAsync(_adapterAddress, default)).Ok);

        await WriteInputAsync("chat-1", "in");
        Assert.Equal(1, (await PulseAdapterAsync()).Value);
        await DrainAsync();

        Assert.False(Directory.Exists(Path.Combine(_mailbox.Root, "outbox", "chat-1")));
        // The responder is mounted and healthy — it simply never hears an event.
        Assert.Contains(_graph.Mounted, m => m.Package.Manifest.Id == "channel.bound-reply");
    }

    /// <summary>
    /// A draft may be written by anything that can reach the draft folder, so it may not attach itself: what a model
    /// writes is something a person still has to mount. The same body mounted by hand is fine — it is the attaching,
    /// not the script, that a draft may not do.
    /// </summary>
    [Fact]
    public async Task A_draft_cannot_attach_itself_to_a_type()
    {
        var attaching = ChannelReply() with
        {
            Origin = "draft",
            Manifest = ChannelReply().Manifest with { Mount = MountMode.Default }
        };
        Assert.True((await _graph.LoadAsync([AdapterIntake(), attaching], default)).Ok);
        Assert.Contains(_graph.RejectedScripts,
            issue => issue.Reason.Contains("draft must be mounted by hand", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_graph.Scripts, entry => entry.Package.Manifest.Id == "channel.bound-reply");

        var manual = ChannelReply() with
        {
            Origin = "draft",
            Manifest = ChannelReply().Manifest with { Mount = MountMode.Manual }
        };
        Assert.True((await _graph.LoadAsync([AdapterIntake(), manual], default)).Ok);
        Assert.Contains(_graph.Scripts, entry => entry.Package.Manifest.Id == "channel.bound-reply");
        Assert.Empty(_graph.RejectedScripts);
    }

    /// <summary>One tool out of a source's offers, by name: the test calls what the model would be offered.</summary>
    private static ISessionTool ToolOf(Result<IReadOnlyList<SessionToolOffer>> offers, string name) =>
        offers.Value!.Single(offer => offer.Definition.Name == name).Tool;

    /// <summary>
    /// The loop the model gets: mount, and the folder follows the mount. A draft that is mounted is in effect, so it is
    /// in scripts/live; unmount it and it goes back to scripts/draft. The folder is worth trusting only if it cannot
    /// disagree with the graph.
    /// </summary>
    [Fact]
    public async Task Mounting_a_draft_moves_it_to_live_and_unmounting_moves_it_back()
    {
        var channel = new Address("channel", "conversation", "chat-1");
        var draft = Path.Combine(_root, "scripts", "draft", "say-hello");
        var live = Path.Combine(_root, "scripts", "live", "say-hello");
        Directory.CreateDirectory(draft);
        await File.WriteAllTextAsync(Path.Combine(draft, "manifest.json"), ScriptSeed.ExampleManifest);
        await File.WriteAllTextAsync(Path.Combine(draft, "main.js"), ScriptSeed.ExampleSource);

        var tools = new ScriptTools(_graph, new JsonScriptCatalog(Path.Combine(_root, "program"), _root),
            new ScriptLibrary(_root));
        var offers = await tools.ToolsAsync(new("s", Guid.NewGuid(), 1), default);
        await ToolOf(offers, ScriptTools.ReloadToolName).ExecuteAsync(new("c1", ScriptTools.ReloadToolName, "{}"), default);
        await AddChannelAsync(channel, "chat-1");

        var mounted = await ToolOf(offers, ScriptTools.MountToolName).ExecuteAsync(
            new("c2", ScriptTools.MountToolName, $$"""{"script":"channel.say-hello","target":"{{channel}}"}"""), default);
        Assert.True(mounted.Ok, mounted.Error?.Message);

        // The mount happened, and the folder followed it.
        Assert.Contains(_graph.Rules, rule => rule.ScriptId == "channel.say-hello");
        Assert.True(Directory.Exists(live));
        Assert.False(Directory.Exists(draft));
        Assert.Contains(_graph.Scripts, entry => entry.Package.Manifest.Id == "channel.say-hello"
                                                 && ScriptReload.LibraryOf(entry.Package) == "live");

        var unmounted = await ToolOf(offers, ScriptTools.UnmountToolName).ExecuteAsync(
            new("c3", ScriptTools.UnmountToolName, $$"""{"script":"channel.say-hello","target":"{{channel}}"}"""), default);
        Assert.True(unmounted.Ok, unmounted.Error?.Message);

        // No mount names it any more, so it is a draft again.
        Assert.DoesNotContain(_graph.Rules, rule => rule.ScriptId == "channel.say-hello");
        Assert.True(Directory.Exists(draft));
        Assert.False(Directory.Exists(live));
        Assert.Contains(_graph.Scripts, entry => entry.Package.Manifest.Id == "channel.say-hello"
                                                 && ScriptReload.LibraryOf(entry.Package) == "draft");
    }

    /// <summary>One script can answer more than one instance: only the last unmount sends it back to the draft folder.</summary>
    [Fact]
    public async Task A_script_mounted_twice_stays_live_until_the_last_mount_goes()
    {
        var first = new Address("channel", "conversation", "chat-1");
        var second = new Address("channel", "conversation", "chat-2");
        var draft = Path.Combine(_root, "scripts", "draft", "say-hello");
        Directory.CreateDirectory(draft);
        await File.WriteAllTextAsync(Path.Combine(draft, "manifest.json"), ScriptSeed.ExampleManifest);
        await File.WriteAllTextAsync(Path.Combine(draft, "main.js"), ScriptSeed.ExampleSource);

        var tools = new ScriptTools(_graph, new JsonScriptCatalog(Path.Combine(_root, "program"), _root),
            new ScriptLibrary(_root));
        var offers = await tools.ToolsAsync(new("s", Guid.NewGuid(), 1), default);
        await ToolOf(offers, ScriptTools.ReloadToolName).ExecuteAsync(new("c1", ScriptTools.ReloadToolName, "{}"), default);
        await AddChannelAsync(first, "chat-1");
        await AddChannelAsync(second, "chat-2");

        foreach (var (target, index) in new[] { (first, 2), (second, 3) })
        {
            var mounted = await ToolOf(offers, ScriptTools.MountToolName).ExecuteAsync(
                new("c" + index, ScriptTools.MountToolName, $$"""{"script":"channel.say-hello","target":"{{target}}"}"""),
                default);
            Assert.True(mounted.Ok, mounted.Error?.Message);
        }

        var oneLeft = await ToolOf(offers, ScriptTools.UnmountToolName).ExecuteAsync(
            new("c4", ScriptTools.UnmountToolName, $$"""{"script":"channel.say-hello","target":"{{first}}"}"""), default);
        Assert.True(oneLeft.Ok, oneLeft.Error?.Message);
        // Still answers chat-2, so it is still live.
        Assert.True(Directory.Exists(Path.Combine(_root, "scripts", "live", "say-hello")));
        Assert.False(Directory.Exists(draft));
    }

    /// <summary>
    /// The addresses a mount takes are not guessable, so the model gets a way to look. Filtering is part of it: a host
    /// with a hundred channels and no search is a list nobody reads.
    /// </summary>
    [Fact]
    public async Task The_model_can_find_an_instance_address_and_narrow_the_list()
    {
        var tools = new ScriptTools(_graph, new JsonScriptCatalog(Path.Combine(_root, "program"), _root));
        var offers = await tools.ToolsAsync(new("s", Guid.NewGuid(), 1), default);
        // The graph has to have been loaded before instances can be attached to it.
        Assert.True((await _graph.LoadAsync([AdapterIntake()], default)).Ok);
        await AddChannelAsync(new Address("channel", "conversation", "chat-1"), "chat-1");
        await AddChannelAsync(new Address("channel", "conversation", "chat-2"), "chat-2");

        var all = await ToolOf(offers, ScriptTools.InstancesToolName)
            .ExecuteAsync(new("c1", ScriptTools.InstancesToolName, "{}"), default);
        Assert.True(all.Ok, all.Error?.Message);
        Assert.Contains("channel.conversation#chat-1", all.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("channel.conversation#chat-2", all.Value.Content, StringComparison.Ordinal);

        // By any part of the address.
        var byQuery = await ToolOf(offers, ScriptTools.InstancesToolName)
            .ExecuteAsync(new("c2", ScriptTools.InstancesToolName, """{"query":"chat-2"}"""), default);
        Assert.True(byQuery.Ok);
        Assert.Contains("chat-2", byQuery.Value!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-1", byQuery.Value.Content, StringComparison.Ordinal);

        // By type, and the type filter is a prefix so "channel" keeps "channel.conversation".
        var byType = await ToolOf(offers, ScriptTools.InstancesToolName)
            .ExecuteAsync(new("c3", ScriptTools.InstancesToolName, """{"type":"channel"}"""), default);
        Assert.True(byType.Ok);
        Assert.Contains("chat-1", byType.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("chat-2", byType.Value.Content, StringComparison.Ordinal);

        var nothing = await ToolOf(offers, ScriptTools.InstancesToolName)
            .ExecuteAsync(new("c4", ScriptTools.InstancesToolName, """{"type":"adapter"}"""), default);
        Assert.True(nothing.Ok);
        Assert.Contains("No instance matches", nothing.Value!.Content, StringComparison.Ordinal);
    }

    /// <summary>One line answers both questions: where it is, and what already answers there.</summary>
    [Fact]
    public async Task An_instance_line_says_which_scripts_are_mounted_on_it()
    {
        var channel = new Address("channel", "conversation", "chat-1");
        var draft = Path.Combine(_root, "scripts", "draft", "say-hello");
        Directory.CreateDirectory(draft);
        await File.WriteAllTextAsync(Path.Combine(draft, "manifest.json"), ScriptSeed.ExampleManifest);
        await File.WriteAllTextAsync(Path.Combine(draft, "main.js"), ScriptSeed.ExampleSource);

        var tools = new ScriptTools(_graph, new JsonScriptCatalog(Path.Combine(_root, "program"), _root),
            new ScriptLibrary(_root));
        var offers = await tools.ToolsAsync(new("s", Guid.NewGuid(), 1), default);
        await ToolOf(offers, ScriptTools.ReloadToolName).ExecuteAsync(new("c1", ScriptTools.ReloadToolName, "{}"), default);
        await AddChannelAsync(channel, "chat-1");
        var mounted = await ToolOf(offers, ScriptTools.MountToolName).ExecuteAsync(
            new("c2", ScriptTools.MountToolName, $$"""{"script":"channel.say-hello","target":"{{channel}}"}"""), default);
        Assert.True(mounted.Ok, mounted.Error?.Message);

        var listed = await ToolOf(offers, ScriptTools.InstancesToolName)
            .ExecuteAsync(new("c3", ScriptTools.InstancesToolName, """{"query":"chat-1"}"""), default);

        Assert.True(listed.Ok);
        Assert.Contains("channel.conversation#chat-1", listed.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("← channel.say-hello", listed.Value.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The model's side of the draft folder: it can ask what is loaded and ask for a re-read, and neither of those starts
    /// anything. A draft becomes visible after a reload and still runs for nobody until a person mounts it.
    /// </summary>
    [Fact]
    public async Task The_model_can_see_a_draft_and_reload_it_and_still_cannot_start_it()
    {
        var folder = Path.Combine(_root, "scripts", "draft", "say-hello");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), ScriptSeed.ExampleManifest);
        await File.WriteAllTextAsync(Path.Combine(folder, "main.js"), ScriptSeed.ExampleSource);

        var tools = new ScriptTools(_graph, new JsonScriptCatalog(Path.Combine(_root, "program"), _root));
        var offers = await tools.ToolsAsync(new("s", Guid.NewGuid(), 1), default);
        Assert.True(offers.Ok, offers.Error?.Message);

        // Nothing was read yet: the folders are read when somebody asks, which is what makes reload a decision.
        Assert.Empty(_graph.Scripts);

        var reloaded = await ToolOf(offers, ScriptTools.ReloadToolName)
            .ExecuteAsync(new("c1", ScriptTools.ReloadToolName, "{}"), default);
        Assert.True(reloaded.Ok, reloaded.Error?.Message);
        Assert.Contains("channel.say-hello", reloaded.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("runs only after somebody mounts it", reloaded.Value.Content, StringComparison.Ordinal);

        // Loaded, visible, mounted on nothing.
        Assert.Contains(_graph.Scripts, entry => entry.Package.Manifest.Id == "channel.say-hello");
        Assert.DoesNotContain(_graph.Mounted, mount => mount.Package.Manifest.Id == "channel.say-hello");

        var listed = await ToolOf(offers, ScriptTools.ListToolName)
            .ExecuteAsync(new("c2", ScriptTools.ListToolName, "{}"), default);
        Assert.True(listed.Ok);
        Assert.Contains("channel.say-hello", listed.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("[draft]", listed.Value.Content, StringComparison.Ordinal);
        Assert.Contains("runs only once somebody mounts it", listed.Value.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The half that makes the tools worth having: when a draft is refused, the model is told which rule it broke, in the
    /// same answer — on the model's side of the wire, not only in a signal a person has to go looking for.
    /// </summary>
    [Fact]
    public async Task The_model_is_told_why_its_draft_was_refused()
    {
        var folder = Path.Combine(_root, "scripts", "draft", "too-eager");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"),
            ScriptSeed.ExampleManifest.Replace("\"mount\": \"manual\"", "\"mount\": \"default\""));
        await File.WriteAllTextAsync(Path.Combine(folder, "main.js"), ScriptSeed.ExampleSource);

        var tools = new ScriptTools(_graph, new JsonScriptCatalog(Path.Combine(_root, "program"), _root));
        var offers = await tools.ToolsAsync(new("s", Guid.NewGuid(), 1), default);
        await ToolOf(offers, ScriptTools.ReloadToolName).ExecuteAsync(new("c1", ScriptTools.ReloadToolName, "{}"), default);

        var listed = await ToolOf(offers, ScriptTools.ListToolName)
            .ExecuteAsync(new("c2", ScriptTools.ListToolName, "{}"), default);
        Assert.True(listed.Ok);
        Assert.Contains("refused by the graph", listed.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("mounted by hand", listed.Value.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An empty graph is said out loud: "no scripts" and "silently answering nobody" are different problems.</summary>
    [Fact]
    public async Task With_no_scripts_the_list_says_nothing_would_answer()
    {
        var tools = new ScriptTools(_graph, new JsonScriptCatalog(Path.Combine(_root, "program"), _root));
        var offers = await tools.ToolsAsync(new("s", Guid.NewGuid(), 1), default);

        var listed = await ToolOf(offers, ScriptTools.ListToolName)
            .ExecuteAsync(new("c", ScriptTools.ListToolName, "{}"), default);

        Assert.True(listed.Ok);
        Assert.Contains("nothing would answer", listed.Value!.Content, StringComparison.Ordinal);
    }

    /// <summary>They are a module's tools, so they arrive and leave with that module: nothing keeps feeding rounds.</summary>
    [Fact]
    public async Task The_script_control_module_offers_its_tools_and_takes_them_back()
    {
        var extensions = new SessionExtensionRegistry();
        var manifest = new ModuleManifest(new ModuleId(ScriptToolsModule.TypeId), ScriptToolsModule.TypeId, Enabled: true);
        var built = ScriptToolsModule.FromManifest(manifest, _graph,
            new JsonScriptCatalog(Path.Combine(_root, "program"), _root), extensions);
        Assert.True(built.Ok, built.Error?.Message);

        Assert.True((await built.Value!.InitializeAsync(default)).Ok);
        Assert.Contains(extensions.Tools, source => source.Id == ScriptTools.SourceId);

        Assert.True((await built.Value.StopAsync(default)).Ok);
        Assert.DoesNotContain(extensions.Tools, source => source.Id == ScriptTools.SourceId);
    }

    /// <summary>The file is the message: its text, nothing else. The id and the sender are the mailbox's business.</summary>
    private async Task WriteInputAsync(string conversation, string text)
    {
        var directory = Path.Combine(_mailbox.Root, "inbox", conversation);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{Guid.NewGuid():N}.txt"), text);
    }

    private async Task<Result<int>> PulseAdapterAsync()
    {
        // Ask the adapter to look now, which is what the clock used to do for it. A test that leaned on a schedule
        // would be testing the scheduler instead of the mailbox.
        var polled = await _adapter.PollOnceAsync(default);
        Assert.True(polled.Ok, polled.Error?.Message);
        return polled;
    }

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
        new("adapter.channel-intake", 1, "adapter", MountMode.Default, [EventNames.AdapterEnvelope], "onEvent",
            [ScriptCapabilities.ChannelAccept, ScriptCapabilities.SelfAdapterPoll], TrustDomain.Builtin),
        "async function onEvent(event, ctx) { await ctx.channel.accept(); }");

    /// <summary>A manual script: nothing attaches it, so it only runs once somebody mounts it by instance.</summary>
    private static ScriptPackage ManualTagger(string id, string text) => new(
        new(id, 1, "channel", MountMode.Manual, [EventNames.ChannelMessageAccepted], "onEvent",
            [ScriptCapabilities.ChannelReply], TrustDomain.Trusted),
        $"async function onEvent(event, ctx) {{ await ctx.channel.reply({{ platformConversationId: event.binding.platformConversationId, text: '{text}' }}); }}");

    private ScriptPackage ChannelReply() => new(
        new("channel.bound-reply", 1, "channel", MountMode.Default, [EventNames.ChannelMessageAccepted], "onEvent",
            [ScriptCapabilities.ChannelReply], TrustDomain.Builtin),
        "async function onEvent(event, ctx) { await ctx.channel.reply({ platformConversationId: event.binding.platformConversationId, text: event.text, replyTo: event.platformMessageId }); }");

    private static OutboundMessage ReadOutbound(string path) => JsonSerializer.Deserialize<OutboundMessage>(File.ReadAllText(path))!;

    /// <summary>Everything one conversation has been sent, sorted: the order files land in is not a promise.</summary>
    private string[] OutboxTexts(string conversation) => Directory
        .GetFiles(Path.Combine(_mailbox.Root, "outbox", conversation), "*.json")
        .Select(ReadOutbound).Select(message => message.Text)
        .Order(StringComparer.Ordinal).ToArray();

    private sealed class GraphBindingProxy : IInstanceBindings
    {
        private IInstanceBindings? _inner;
        public void Bind(IInstanceBindings inner) => _inner = inner;
        public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) => _inner!.AttachAsync(target, ct);
        public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) => _inner!.DetachAsync(target, ct);
    }
}
