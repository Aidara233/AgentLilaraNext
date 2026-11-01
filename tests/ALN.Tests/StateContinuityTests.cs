using System.Text.Json;
using ALN.Adapter.File;
using ALN.Channel;
using ALN.Clock;
using ALN.Contracts;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;
using ALN.Scripting.Jint;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// What a restart has to look like from the outside: the same mounts, the same wishes, the same script
/// memory. Every test here builds a whole component graph over one data root, throws the object graph away,
/// and builds another one — which is exactly what the host does between two runs.
///
/// A forced exit is the same test with the disposal skipped: everything that matters is written when it
/// changes, not on the way out.
/// </summary>
public sealed class StateContinuityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-continuity-" + Guid.NewGuid().ToString("N"));
    private static readonly Address ChatOne = new("channel", "conversation", "chat-1");
    private static readonly Address ChatTwo = new("channel", "conversation", "chat-2");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Mount_rules_survive_a_restart_and_reach_a_channel_that_only_exists_afterwards()
    {
        string ruleId;
        using (var first = new Node(_root))
        {
            await first.StartAsync([Intake(), Echo("channel.tagger", "tagged")]);
            await first.AddChannelAsync("chat-1");

            // "Every Channel", not "this Channel": one rule, applied to what matches now and later.
            var added = await first.Graph.AddRuleAsync(new("channel.tagger", MountScope.ForType("channel.conversation")), default);
            Assert.True(added.Ok && added.Value is not null, added.Error?.Message);
            ruleId = added.Value!.Id;
            Assert.Contains(first.Graph.Mounted, m => m.RuleId == ruleId && m.Target == ChatOne);
        }

        using var second = new Node(_root);
        // The graph comes up before any Channel does — the same order the host uses.
        await second.LoadAsync([Intake(), Echo("channel.tagger", "tagged")]);
        var pending = Assert.Single(second.Graph.PendingRules);
        Assert.Equal(ruleId, pending.Id);
        Assert.DoesNotContain(second.Graph.Mounted, m => m.RuleId is not null);

        await second.ReviveAsync();
        // The Channel reappearing is what resolves the rule; nothing re-mounted it by hand.
        Assert.Contains(second.Graph.Mounted, m => m.RuleId == ruleId && m.Target == ChatOne);
        Assert.Empty(second.Graph.PendingRules);

        await second.AddChannelAsync("chat-2");
        Assert.Contains(second.Graph.Mounted, m => m.RuleId == ruleId && m.Target == ChatTwo);

        // ... and the rule is a row a person can find, keyed by scope rather than scattered over files.
        var stored = await second.Storage.ReadAsync(GraphManager.StateScope, GraphManager.StateKey, default);
        Assert.Contains("channel.tagger", stored.Value ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_one_shot_rule_is_spent_before_it_runs_and_never_returns()
    {
        using var node = new Node(_root);
        await node.StartAsync([Intake(), Echo("channel.greeter", "hi once")]);
        await node.AddChannelAsync("chat-1");

        var added = await node.Graph.AddRuleAsync(
            new("channel.greeter", MountScope.ForInstance(ChatOne), Budget: 1), default);
        Assert.True(added.Ok && added.Value is not null, added.Error?.Message);
        var ruleId = added.Value!.Id;

        await node.SayAsync("chat-1", "hello");
        await node.DrainAsync();
        Assert.Equal(["hi once"], node.OutboxTexts("chat-1"));

        // Spent means gone: no mount, no rule waiting, nothing to resurrect.
        Assert.DoesNotContain(node.Graph.Mounted, m => m.Package.Manifest.Id == "channel.greeter");
        Assert.DoesNotContain(node.Graph.Rules, rule => rule.Id == ruleId);

        await node.SayAsync("chat-1", "again");
        await node.DrainAsync();
        Assert.Equal(["hi once"], node.OutboxTexts("chat-1"));
    }

    [Fact]
    public async Task Script_state_is_per_target_and_outlives_the_process()
    {
        using (var first = new Node(_root))
        {
            await first.StartAsync([Intake(), Counter()]);
            await first.AddChannelAsync("chat-1");
            await first.AddChannelAsync("chat-2");
            var added = await first.Graph.AddRuleAsync(new("channel.counter", MountScope.ForType("channel.conversation")), default);
            Assert.True(added.Ok, added.Error?.Message);

            await first.SayAsync("chat-1", "one");
            await first.DrainAsync();
            await first.SayAsync("chat-2", "two");
            await first.DrainAsync();

            // Two channels are two conversations, so two counters.
            Assert.Equal(["#1"], first.OutboxTexts("chat-1"));
            Assert.Equal(["#1"], first.OutboxTexts("chat-2"));
        }

        using var second = new Node(_root);
        await second.StartAsync([Intake(), Counter()]);
        // The outbox is on disk too, so only what is new tells us what the counter did.
        var beforeSecond = second.OutboxTexts("chat-1").Length;
        await second.SayAsync("chat-1", "three");
        await second.DrainAsync();
        var afterSecond = second.OutboxTexts("chat-1");
        // chat-1 continued from #1 while chat-2 stayed where it was.
        Assert.Equal(beforeSecond + 1, afterSecond.Length);
        Assert.Contains("#2", afterSecond);

        var beforeChatTwo = second.OutboxTexts("chat-2").Length;
        await second.SayAsync("chat-2", "four");
        await second.DrainAsync();
        var afterChatTwo = second.OutboxTexts("chat-2");
        Assert.Equal(beforeChatTwo + 1, afterChatTwo.Length);
        Assert.Contains("#2", afterChatTwo);
    }

    [Fact]
    public async Task A_shared_state_scope_is_one_namespace_for_every_target_the_rule_reaches()
    {
        using var node = new Node(_root);
        await node.StartAsync([Intake(), Counter()]);
        await node.AddChannelAsync("chat-1");
        await node.AddChannelAsync("chat-2");
        var added = await node.Graph.AddRuleAsync(
            new("channel.counter", MountScope.ForType("channel.conversation"), StateScope: ScriptStateScope.Shared), default);
        Assert.True(added.Ok, added.Error?.Message);

        await node.SayAsync("chat-1", "one");
        await node.DrainAsync();
        await node.SayAsync("chat-2", "two");
        await node.DrainAsync();

        Assert.Equal(["#1"], node.OutboxTexts("chat-1"));
        Assert.Equal(["#2"], node.OutboxTexts("chat-2"));
    }

    [Fact]
    public async Task A_channel_somebody_stopped_stays_stopped_after_a_restart()
    {
        using (var first = new Node(_root))
        {
            await first.StartAsync([Intake()]);
            await first.AddChannelAsync("chat-1");
            await first.AddChannelAsync("chat-2");
            Assert.True((await first.Channels.StopAsync(ChatOne, default)).Ok);
        }

        using var second = new Node(_root);
        await second.StartAsync([Intake()]);

        Assert.False(second.Channels.Get(ChatOne).Value!.Running);
        Assert.True(second.Channels.Get(ChatTwo).Value!.Running);
        // A revival that quietly undid a decision is the failure this test exists for.
        Assert.Contains(second.Report.Snapshot(), note => note.Component == "channels" && note.Summary.Contains("kept stopped"));
    }

    [Fact]
    public async Task A_window_and_a_delta_cursor_are_still_what_they_were_before_the_restart()
    {
        using (var first = new Node(_root))
        {
            await first.StartAsync([Intake()]);
            await first.AddChannelAsync("chat-1");
            var window = await first.Parameters.SetAsync(ChatOne.ToString(), "maxMessages", "1", default);
            Assert.True(window.Ok, window.Error?.Message);

            // One delta taken: the cursor now points at the end of the conversation.
            var delta = new FileChannelDeltaSource(first.Messages, first.State);
            Assert.True((await first.Messages.AppendAsync(new("m1", ChatOne, "chat-1", "p1", "alice", "first",
                DateTimeOffset.UtcNow), default)).Ok);
            Assert.Single((await delta.TakeDeltaAsync(ChatOne, null, default)).Value!.Messages);
        }

        using var second = new Node(_root);
        await second.StartAsync([Intake()]);

        Assert.Equal("1", second.Parameters.List().Single(p => p.Scope == ChatOne.ToString() && p.Name == "maxMessages").Value);
        Assert.True(second.Channels.Get(ChatOne).Value!.MaxMessages == 1);
        // The cursor survived, so the next turn does not re-read what was already delivered.
        var afterRestart = await new FileChannelDeltaSource(second.Messages, second.State).TakeDeltaAsync(ChatOne, null, default);
        Assert.True(afterRestart.Value!.IsEmpty);
    }

    [Fact]
    public async Task The_greeting_sample_greets_once_even_across_a_restart()
    {
        var sample = SamplePackage("channel.greeting");
        using (var first = new Node(_root))
        {
            await first.StartAsync([Intake(), sample]);
            await first.AddChannelAsync("chat-1");
            Assert.True((await first.Graph.AddRuleAsync(
                new("channel.greeting", MountScope.ForType("channel.conversation")), default)).Ok);

            await first.SayAsync("chat-1", "在吗");
            await first.DrainAsync();
            await first.SayAsync("chat-1", "还没回我");
            await first.DrainAsync();
            // Two messages, one greeting: the flag is in ctx.state, not in a variable.
            Assert.Single(first.OutboxTexts("chat-1"));
        }

        using var second = new Node(_root);
        await second.StartAsync([Intake(), sample]);
        await second.SayAsync("chat-1", "重启了");
        await second.DrainAsync();
        Assert.Single(second.OutboxTexts("chat-1"));
    }

    [Fact]
    public async Task A_message_that_was_accepted_but_never_answered_is_replayed_once_after_a_restart()
    {
        using (var first = new Node(_root))
        {
            await first.StartAsync([Intake(), Echo("channel.responder", "answered")]);
            await first.AddChannelAsync("chat-1");
            Assert.True((await first.Graph.AddRuleAsync(new("channel.responder", MountScope.ForType("channel.conversation")), default)).Ok);

            await first.SayAsync("chat-1", "你在吗");
            Assert.True(await first.StepAsync());   // clock.pulse → the Adapter polls and publishes the envelope
            Assert.True(await first.StepAsync());   // adapter.envelope → the Channel accepts, the answer is queued
            // Accepted, unanswered, and the process goes away before the round that would answer it.
            Assert.Empty(first.OutboxTexts("chat-1"));
            var pending = await first.Storage.ListAsync(ChannelManager.PendingScope, ChatOne, default);
            Assert.NotEmpty(pending.Value!);
        }

        using var second = new Node(_root);
        await second.StartAsync([Intake(), Echo("channel.responder", "answered")]);
        Assert.Contains(second.Report.Snapshot(), note => note.Component == "channels" && note.Summary.Contains("replayed"));
        await second.DrainAsync();
        Assert.Equal(["answered"], second.OutboxTexts("chat-1"));

        // The round finished, so the marker is gone: a third boot has nothing left to replay.
        using var third = new Node(_root);
        await third.StartAsync([Intake(), Echo("channel.responder", "answered")]);
        Assert.DoesNotContain(third.Report.Snapshot(), note => note.Component == "channels" && note.Summary.Contains("replayed"));
        await third.DrainAsync();
        Assert.Equal(["answered"], third.OutboxTexts("chat-1"));
    }

    [Fact]
    public async Task The_same_platform_message_delivered_twice_is_answered_once()
    {
        using var node = new Node(_root);
        await node.StartAsync([Intake(), Echo("channel.responder", "once")]);
        await node.AddChannelAsync("chat-1");
        Assert.True((await node.Graph.AddRuleAsync(new("channel.responder", MountScope.ForType("channel.conversation")), default)).Ok);

        await node.SayAsync("chat-1", "hi", messageId: "platform-1");
        await node.DrainAsync();
        // The transport hands the very same message over again — a redelivery, not a new message.
        await node.SayAsync("chat-1", "hi", messageId: "platform-1");
        await node.DrainAsync();

        Assert.Single(node.OutboxTexts("chat-1"));
        var history = (await node.Messages.ReadAsync(ChatOne, default)).Value!;
        var inbound = Assert.Single(history, message => message.Direction == MessageDirection.Inbound);
        Assert.Equal("platform-1", inbound.PlatformMessageId);
        // ... and exactly one reply, pointing at what it answers. That row is what makes "already answered"
        // a question with an answer.
        var outbound = Assert.Single(history, message => message.Direction == MessageDirection.Outbound);
        Assert.Equal(inbound.Id, outbound.ReplyTo);
    }

    [Fact]
    public async Task A_message_that_was_answered_is_not_answered_again_when_its_marker_survives()
    {
        string answeredMessageId;
        using (var first = new Node(_root))
        {
            await first.StartAsync([Intake(), Echo("channel.responder", "only once")]);
            await first.AddChannelAsync("chat-1");
            Assert.True((await first.Graph.AddRuleAsync(new("channel.responder", MountScope.ForType("channel.conversation")), default)).Ok);

            await first.SayAsync("chat-1", "你在吗", messageId: "platform-2");
            await first.DrainAsync();
            Assert.Equal(["only once"], first.OutboxTexts("chat-1"));

            var history = (await first.Messages.ReadAsync(ChatOne, default)).Value!;
            answeredMessageId = Assert.Single(history, message => message.Direction == MessageDirection.Outbound).ReplyTo!;
            // The reply went out, then the process died before the marker was cleared: exactly the window this
            // whole mechanism exists for.
            var marker = new
            {
                messageId = answeredMessageId,
                platformMessageId = "platform-2",
                adapter = "adapter.file#file-1",
                conversation = "chat-1",
                text = "你在吗",
                at = DateTimeOffset.UtcNow,
                sender = (string?)null
            };
            var seeded = await first.Storage.PutAsync(ChannelManager.PendingScope, ChatOne, answeredMessageId,
                System.Text.Json.JsonSerializer.Serialize(marker, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                null, default);
            Assert.True(seeded.Ok, seeded.Error?.Message);
        }

        using var second = new Node(_root);
        await second.StartAsync([Intake(), Echo("channel.responder", "only once")]);
        Assert.Contains(second.Report.Snapshot(), note => note.Component == "channels" && note.Summary.Contains("already answered"));
        await second.DrainAsync();

        // No second reply, and the stale marker is gone.
        Assert.Single(second.OutboxTexts("chat-1"));
        Assert.Empty((await second.Storage.ListAsync(ChannelManager.PendingScope, ChatOne, default)).Value!);
    }

    [Fact]
    public async Task A_claimed_input_that_was_never_acknowledged_is_offered_again()
    {
        using (var first = new Node(_root))
        {
            await first.StartAsync([Intake(), Echo("channel.responder", "recovered")]);
            await first.AddChannelAsync("chat-1");
            Assert.True((await first.Graph.AddRuleAsync(new("channel.responder", MountScope.ForType("channel.conversation")), default)).Ok);

            await first.SayAsync("chat-1", "你好");
            Assert.True(await first.StepAsync());   // the Adapter claims the file and publishes the envelope
            // Claimed, published, never dispatched: the mailbox must not believe this is done.
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "io", "file", "file-1", "inflight", "chat-1")));
            Assert.Empty(first.OutboxTexts("chat-1"));
        }

        using var second = new Node(_root);
        await second.LoadAsync([Intake(), Echo("channel.responder", "recovered")]);
        // Starting the Adapter is what reclaims: the claim outlived the process that made it.
        await second.ReviveAsync();
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "io", "file", "file-1", "inbox", "chat-1")));

        Assert.True((await second.Clock.PulseDueAsync(DateTimeOffset.UtcNow.AddSeconds(2), default)).Ok);
        await second.DrainAsync();
        Assert.Equal(["recovered"], second.OutboxTexts("chat-1"));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "io", "file", "file-1", "processed", "chat-1")));
    }


    private static ScriptPackage SamplePackage(string id)
    {
        var directory = Path.Combine(RepositoryRoot(), "samples", "scripts", "trusted", id);
        var manifest = JsonSerializer.Deserialize<ScriptFileManifest>(
            File.ReadAllText(Path.Combine(directory, "manifest.json")),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                        })
                    ?? throw new InvalidOperationException($"Sample manifest is unreadable: {id}");
        return new(new(manifest.Id, manifest.Version, manifest.TargetType, manifest.Mount, manifest.Events,
                manifest.EntryPoint, manifest.Capabilities, manifest.TrustDomain, manifest.MaxStatements,
                manifest.TimeoutMilliseconds, manifest.MemoryLimitBytes),
            File.ReadAllText(Path.Combine(directory, "main.js")));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ALN.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    /// <summary>One process worth of components over a data root; dispose it and build another to "restart".</summary>
    private sealed class Node : IDisposable
    {
        private readonly Address _adapterAddress = new("adapter", "file", "file-1");
        private readonly AdapterTypeCatalog _types = new();
        private readonly BindingProxy _bindings = new();

        public Node(string root)
        {
            Root = root;
            Signals = SignalLog.Open(null);
            // Each Node is one "process": its own connection to the same file. Disposing it and building
            // another is what a restart looks like to the storage layer.
            Storage = new SqliteStore(Path.Combine(root, "aln.db"));
            Report = new RestoreReport();
            Parameters = new ParameterRegistry(Storage);
            Events = new EventQueue(64);
            Clock = new ClockSource(Events);
            Assert.True(_types.RegisterPackageTypes("test.adapter.file", [new("adapter.file", "adapter")]).Ok);
            Assert.True(_types.RegisterPackageTypes("test.channel", [new("channel.conversation", "channel")]).Ok);
            Adapters = new AdapterManager(_types, _bindings);
            Channels = new ChannelManager(Adapters, Events, _bindings,
                new FileChannelSpecStore(Path.Combine(root, "channels")), Storage, Parameters, Storage, Report, Storage, Storage);
            Graph = new GraphManager(Adapters, Channels, Clock, _types, new JintScriptExecutor(), Storage, Signals,
                state: Storage, report: Report);
            _bindings.Bind(Graph);
            Settlers = new EventSettlers(new ChannelAcceptSettler(Channels), new InboundAckSettler(Acks));
            Mailbox = new FileMailbox(Path.Combine(root, "io", "file", "file-1"));
            Adapter = new FileAdapter(new(_adapterAddress, new("file", "acc", AdapterDirection.Both)), Mailbox, Events, Clock, Acks);
        }

        public string Root { get; }
        public SignalLog Signals { get; }
        public SqliteStore Storage { get; }
        public IStateStore State => Storage;
        public RestoreReport Report { get; }
        public ParameterRegistry Parameters { get; }
        public IMessageStore Messages => Storage;
        public EventQueue Events { get; }
        public ClockSource Clock { get; }
        public AdapterManager Adapters { get; }
        public ChannelManager Channels { get; }
        public GraphManager Graph { get; }
        public FileMailbox Mailbox { get; }
        public FileAdapter Adapter { get; }
        public InboundAckRegistry Acks { get; } = new();
        public EventSettlers Settlers { get; }

        public async Task LoadAsync(IEnumerable<ScriptPackage> scripts)
        {
            var loaded = await Graph.LoadAsync(scripts, default);
            Assert.True(loaded.Ok, loaded.Error?.Message);
        }

        /// <summary>The part of startup that follows script loading: adapter, then Channels, then parameters.</summary>
        public async Task ReviveAsync()
        {
            var created = await Adapters.CreateAsync(new(_adapterAddress, Adapter.Descriptor), _ => Adapter, default);
            Assert.True(created.Ok, created.Error?.Message);
            Assert.True((await Adapters.StartAsync(_adapterAddress, default)).Ok);
            var started = await Channels.StartAsync(new LifecycleSignal(LifecyclePhase.Start, DateTimeOffset.UtcNow), default);
            Assert.True(started.Ok, started.Error?.Message);
            // Channels register their tunables while they are revived, so this is the earliest honest restore.
            var restored = await Parameters.RestoreAsync(default);
            Assert.True(restored.Ok, restored.Error?.Message);
            // Accepted-but-unfinished messages get their wake-up back, exactly as the host does after startup.
            var replayed = await Channels.ReplayPendingAsync(default);
            Assert.True(replayed.Ok, replayed.Error?.Message);
        }

        public async Task StartAsync(IEnumerable<ScriptPackage> scripts)
        {
            await LoadAsync(scripts);
            await ReviveAsync();
        }

        public async Task AddChannelAsync(string conversation)
        {
            var address = new Address("channel", "conversation", conversation);
            var created = await Channels.CreateAsync(new(address, conversation, [new(_adapterAddress, conversation)]), default);
            // After a restart the Channel is already there, revived by the manager: that is not a failure.
            if (!created.Ok && created.Error?.Code != ErrorCode.Conflict) Assert.Fail(created.Error?.Message ?? "channel creation failed");
            if (Channels.Get(address).Value is { Running: false })
                Assert.True((await Channels.StartAsync(address, default)).Ok);
        }

        public async Task SayAsync(string conversation, string text, string? messageId = null)
        {
            var directory = Path.Combine(Mailbox.Root, "inbox", conversation);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{Guid.NewGuid():N}.ready.json"),
                JsonSerializer.Serialize(new { text, messageId = messageId ?? Guid.NewGuid().ToString("N") }));
            var pulsed = await Clock.PulseDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), default);
            Assert.True(pulsed.Ok, pulsed.Error?.Message);
        }

        /// <summary>Dispatches exactly one queued event and settles it, the way the pump does one at a time.</summary>
        public async Task<bool> StepAsync()
        {
            if (!Events.TryRead(out var queued)) return false;
            var dispatched = await Graph.DispatchAsync(queued, default);
            Assert.True(dispatched.Ok, dispatched.Error?.Detail ?? dispatched.Error?.Message);
            var settled = await Settlers.SettleAsync(queued, default);
            Assert.True(settled.Ok, settled.Error?.Detail ?? settled.Error?.Message);
            return true;
        }

        public async Task DrainAsync()
        {
            var guard = 0;
            while (await StepAsync())
                Assert.True(++guard < 64, "event queue did not settle");
        }

        public string[] OutboxTexts(string conversation)
        {
            var directory = Path.Combine(Mailbox.Root, "outbox", conversation);
            if (!Directory.Exists(directory)) return [];
            return Directory.GetFiles(directory, "*.json")
                .Select(path => JsonSerializer.Deserialize<OutboundMessage>(File.ReadAllText(path))!.Text)
                .Order(StringComparer.Ordinal).ToArray();
        }

        public void Dispose()
        {
            Signals.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Storage.Dispose();
        }
    }

    private sealed class BindingProxy : IInstanceBindings
    {
        private IInstanceBindings? _inner;
        public void Bind(IInstanceBindings inner) => _inner = inner;
        public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) => _inner!.AttachAsync(target, ct);
        public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) => _inner!.DetachAsync(target, ct);
    }

    private static ScriptPackage Intake() => new(
        new("adapter.channel-intake", 1, "adapter", MountMode.Default,
            [EventNames.AdapterEnvelope, EventNames.ClockPulse], "onEvent",
            [ScriptCapabilities.ChannelAccept, ScriptCapabilities.SelfAdapterPoll], TrustDomain.Builtin),
        "async function onEvent(event, ctx) { if (ctx.eventName === 'clock.pulse') { await ctx.self.adapter.poll(); return; } await ctx.channel.accept(); }");

    /// <summary>A manual script with one fixed answer: what a mount rule is for.</summary>
    private static ScriptPackage Echo(string id, string text) => new(
        new(id, 1, "channel", MountMode.Manual, [EventNames.ChannelMessageAccepted], "onEvent",
            [ScriptCapabilities.ChannelReply], TrustDomain.Trusted),
        $"async function onEvent(event, ctx) {{ await ctx.channel.reply({{ platformConversationId: event.binding.platformConversationId, text: '{text}' }}); }}");

    /// <summary>Remembers how many times it has run, per its state namespace.</summary>
    private static ScriptPackage Counter() => new(
        new("channel.counter", 1, "channel", MountMode.Manual, [EventNames.ChannelMessageAccepted], "onEvent",
            [ScriptCapabilities.ChannelReply], TrustDomain.Trusted),
        """
        async function onEvent(event, ctx) {
          const seen = (await ctx.state.get('count')) ?? 0;
          await ctx.state.set('count', seen + 1);
          await ctx.channel.reply({ platformConversationId: event.binding.platformConversationId, text: '#' + (seen + 1) });
        }
        """);
}
