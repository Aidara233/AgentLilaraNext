using System.Runtime.CompilerServices;
using System.Text.Json;
using ALN.Channel;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Scripting.Jint;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// A message laid out as blocks — words, a mention, a picture — from the model's tool call to the segments a bridge
/// receives, and the refusal when the adapter cannot render it.
///
/// Three things are pinned. <b>Blocks arrive in order inside one message</b> (the whole point of layout), <b>a mention can
/// only name somebody the core resolved</b> (never "@everyone"), and <b>a part the Adapter cannot put on the wire is
/// refused by the Adapter itself</b> — a model that thinks it showed a picture and did not is worse than a model that was
/// told no.
///
/// That last one is asked of nobody in advance: no sender in this file consults a capability list, because there is none.
/// The Adapter is called once with the layout and answers (<c>IOutboundAdapter.SendAsync</c>), which is also why the
/// recording adapter below can refuse from inside <see cref="RecordingAdapter.SendAsync"/> rather than being configured
/// to look incapable from outside.
/// </summary>
public sealed class OutboundBlocksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-outbound-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _audit;
    private readonly SignalLog _signals = SignalLog.Open(null, new SignalLogOptions(SignalLevel.Warn));
    private readonly AdapterTypeCatalog _types = new();
    private readonly EventQueue _events = new(32);
    private readonly AdapterManager _adapters;
    private readonly ChannelManager _channels;
    private readonly ModuleCatalog _catalog;
    private readonly QueueModel _model = new();
    private readonly SessionManager _sessions;
    private readonly GraphManager _graph;
    private readonly GraphBindings _bindings;
    private readonly RecordingAdapter _adapter;
    private static readonly Address AdapterAddress = new("adapter", "recording", "one");

    public OutboundBlocksTests()
    {
        Directory.CreateDirectory(_root);
        _audit = new SqliteStore(Path.Combine(_root, "aln.db"));
        Assert.True(_types.RegisterPackageTypes("test.adapter", [new("adapter.recording", "adapter")]).Ok);
        Assert.True(_types.RegisterPackageTypes("test.channel", [new("channel.conversation", "channel")]).Ok);
        var bindings = new QuietBindings();
        _adapters = new AdapterManager(_types, bindings);
        // The Channel finds its Adapter manager by name, so it is handed a catalog that already has one — the same order
        // the host uses. The graph then gets the address book with everything attached.
        var inner = new ModuleCatalog();
        inner.Attach(_adapters);
        _channels = new ChannelManager(_events, bindings, inner);
        _sessions = new SessionManager(_root, _model);
        _catalog = ModuleWiring.With(_adapters, _channels, _sessions);        var media = new FileMediaStore(new LocalFileSystemAccess(_root,
            [FileScopes.Media, FileScopes.Workspace], binaryScopes: [FileScopes.Media, FileScopes.Workspace],
            readOnlyScopes: [FileScopes.Media]));
        _graph = new GraphManager(_catalog, _types, new JintScriptExecutor(), _audit, _signals, state: null, media: media);
        _bindings = new GraphBindings();
        _bindings.Bind(_graph);
        _adapter = new RecordingAdapter();
    }

    public void Dispose()
    {
        _signals.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _audit.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- the pieces, without a graph ---------------------------------------------------------

    [Fact]
    public void A_layout_is_one_text_view_with_the_parts_in_the_order_they_were_written()
    {
        var picture = new MediaRef(FileScopes.Media, "image/ab/x.png", MediaKind.Image, "image/png", 33, 4, 3, "ab");
        var layout = new MessageBlock[]
        {
            new TextBlock("回 "), new MentionBlock("20000"), new TextBlock(" 看"), new ImageBlock(picture), new FaceBlock("1")
        };

        // The text is the fallback every reader uses: the transcript, the log, and a platform that can only say words.
        // No separator is invented between blocks — the producer that wants one writes it into a text block.
        Assert.Equal("回 @20000 看[图片：media/image/ab/x.png][表情]", MessageBlock.Flatten(layout));
    }

    // ---- the model's tool call, all the way to the wire ---------------------------------------

    [Fact]
    public async Task A_laid_out_reply_arrives_as_one_message_with_its_parts_in_order()
    {
        await SendOneAsync(blocks: """
            [{"type":"text","text":"看这个 "},
             {"type":"at","who":"sender"},
             {"type":"text","text":"："},
             {"type":"face","id":"1"},
             {"type":"image","path":"media/image/ab/x.png"}]
            """, picture: true);

        var message = Assert.Single(_adapter.Sent);
        Assert.Equal("chat-1", message.PlatformConversationId);
        var blocks = message.Blocks!;
        Assert.Equal(5, blocks.Count);
        Assert.Equal("看这个 ", Assert.IsType<TextBlock>(blocks[0]).Text);
        // "sender" was resolved by the core from the message being answered — the model never typed an id.
        Assert.Equal("20000", Assert.IsType<MentionBlock>(blocks[1]).Id);
        Assert.Equal("1", Assert.IsType<FaceBlock>(blocks[3]).Id);
        // A picture travels as a reference — scope plus the path inside it — never as its bytes.
        var picture = Assert.IsType<ImageBlock>(blocks[4]).Picture;
        Assert.Equal(FileScopes.Media, picture.Scope);
        Assert.Equal("image/ab/x.png", picture.Path);
        Assert.True(picture.IsViewable);
        // The plain-text view is what the Session's own transcript records.
        Assert.Equal(message.Text, MessageBlock.Flatten(blocks));
        Assert.Contains("@20000", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_the_adapter_cannot_send_is_refused_by_the_adapter_and_the_model_is_told()
    {
        await SendOneAsync(blocks: """[{"type":"text","text":"看"},{"type":"image","path":"media/image/ab/x.png"}]""",
            picture: true, refuseImages: true);

        // The Adapter was called — nobody asked it what it could do first, and nobody refused on its behalf.
        Assert.Equal(1, _adapter.Attempts);
        // Nothing was sent: a placeholder reaching a group would be a message the model believes it sent.
        Assert.Empty(_adapter.Sent);
        // And what the model reads next is the Adapter's own reason, naming the part it could not put on the wire.
        var answered = _model.Requests[^1].Messages.Where(message => message.Role == "tool").ToArray();
        Assert.Contains(answered, message => message.Content?.Contains("发不出", StringComparison.Ordinal) == true);
        Assert.Contains(answered, message => message.Content?.Contains("图片", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_file_block_reaches_the_adapter_as_a_reference_and_never_as_bytes()
    {
        await SendOneAsync(blocks: """
            [{"type":"text","text":"日志在"},{"type":"file","path":"workspace/report.log"},
             {"type":"text","text":"里"}]
            """, workspaceFile: ("report.log", "boom"u8.ToArray()));

        var message = Assert.Single(_adapter.Sent);
        var blocks = message.Blocks!;
        Assert.Equal(3, blocks.Count);
        // A file travels as a reference plus the name the receiver should see — the same "no bytes in a record" rule a
        // picture follows (docs/28 §3), and the reason a send of a hundred megabytes costs this process nothing.
        var file = Assert.IsType<FileBlock>(blocks[1]);
        Assert.Equal(FileScopes.Workspace, file.File.Scope);
        Assert.Equal("report.log", file.File.Path);
        Assert.Equal("report.log", file.SuggestedName);
        Assert.Equal(4, file.File.Bytes);
        // The text view is what the transcript keeps, and it says what the thing was.
        Assert.Contains("[文件：workspace/report.log]", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_block_that_is_not_there_is_refused_by_name()
    {
        await SendOneAsync(blocks: """[{"type":"file","path":"workspace/missing.bin"}]""");

        // Refused before anything leaves, and the refusal names the place rather than saying "speak failed".
        Assert.Empty(_adapter.Sent);
        var answered = _model.Requests[^1].Messages.Where(message => message.Role == "tool").ToArray();
        Assert.Contains(answered, message => message.Content?.Contains("没有能发的文件", StringComparison.Ordinal) == true);
        Assert.Contains(answered, message => message.Content?.Contains("workspace/missing.bin", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("""[{"type":"at","who":"all"},{"type":"text","text":"开会了"}]""", "全体")]
    [InlineData("""[{"type":"at","who":"某人"}]""", "平台号")]
    [InlineData("""[{"type":"image","path":"media/image/ab/missing.png"}]""", "图片")]
    [InlineData("""[{"type":"video","path":"x"}]""", "没有这种块")]
    [InlineData("""[{"type":"face","id":"smile"}]""", "表情编号")]
    public async Task A_layout_that_would_be_wrong_is_refused_before_anything_leaves(string blocks, string reason)
    {
        await SendOneAsync(blocks, picture: true);

        Assert.Empty(_adapter.Sent);
        var answered = _model.Requests[^1].Messages.Where(message => message.Role == "tool").ToArray();
        Assert.Contains(answered, message => message.Content?.Contains(reason, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Text_and_blocks_together_are_refused_because_nobody_decided_which_is_the_message()
    {
        await SendOneAsync(blocks: """[{"type":"text","text":"块"}]""", text: "还有一段话");

        Assert.Empty(_adapter.Sent);
        var answered = _model.Requests[^1].Messages.Where(message => message.Role == "tool").ToArray();
        Assert.Contains(answered, message => message.Content?.Contains("not both", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// One inbound message → the script opens a session → the model calls <c>speak</c> → the adapter (or its refusal).
    /// <paramref name="refuseImages"/> makes the recording adapter decline pictures <b>from inside its own SendAsync</b>:
    /// the only way an adapter now says "not this part", since nothing outside it can know.
    /// </summary>
    private async Task SendOneAsync(string blocks, bool picture = false, string? text = null, bool refuseImages = false,
        (string Name, byte[] Content)? workspaceFile = null)
    {
        var loaded = await _graph.LoadAsync([ReplyWithBlocks()], default);
        Assert.True(loaded.Ok, "load: " + loaded.Error?.Message + " / " + loaded.Error?.Detail);
        _adapter.RefuseImages = refuseImages;
        if (workspaceFile is { } file)
        {
            Directory.CreateDirectory(Path.Combine(_root, FileScopes.Workspace));
            await File.WriteAllBytesAsync(Path.Combine(_root, FileScopes.Workspace, file.Name), file.Content);
        }

        var adapterCreated = await _adapters.CreateAsync(
            new AdapterSpec(AdapterAddress, new("recording", "one", AdapterDirection.Both), "adapter.recording"),
            _ => _adapter, default);
        Assert.True(adapterCreated.Ok, "adapter: " + adapterCreated.Error?.Message + " / " + adapterCreated.Error?.Detail);
        var channel = new Address("channel", "conversation", "chat-1");
        var created = await _channels.CreateAsync(new(channel, "chat-1", [new(AdapterAddress, "chat-1")],
            "channel.conversation"), default);
        Assert.True(created.Ok, "create: " + created.Error?.Message + " / " + created.Error?.Detail);
        var started = await _channels.StartAsync(channel, default);
        Assert.True(started.Ok, "start: " + started.Error?.Message + " / " + started.Error?.Detail);
        // Mounted by hand: a default script is attached by the instance lifecycle, and this test drives the lifecycle by
        // hand — so it says what it wants instead of pretending to be a Channel coming up.
        var mounted = await _graph.MountAsync("channel.blocks", channel, default);
        Assert.True(mounted.Ok, "mount: " + mounted.Error?.Message + " / " + mounted.Error?.Detail);
        if (picture)
        {
            Directory.CreateDirectory(Path.Combine(_root, FileScopes.Media, "image", "ab"));
            await File.WriteAllBytesAsync(Path.Combine(_root, FileScopes.Media, "image", "ab", "x.png"), Png());
        }

        // The model: a speak call carrying the layout, then a wait (which is what ends a turn).
        var arguments = text is null
            ? $$"""{"blocks":{{blocks}}}"""
            : $$"""{"blocks":{{blocks}},"text":{{JsonSerializer.Serialize(text)}}}""";
        _model.Enqueue(
            new ToolCallDelta(0, "call-1", "speak", arguments),
            new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1)));
        _model.Enqueue(
            new ToolCallDelta(0, "call-2", "wait", "{}"),
            new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1)));

        // The Channel message itself, not an Adapter envelope: turning one into the other is a script of its own, and the
        // subject here is what happens after a message is in the conversation.
        var binding = new ChannelBinding(AdapterAddress, "chat-1");
        var eventId = Guid.NewGuid();
        var message = new ChannelMessage(eventId, channel, binding, "m-1", "看图", DateTimeOffset.UtcNow,
            Sender: "20000", MentionsSelf: true, SenderRemark: null, SenderName: "小明");
        var dispatched = await _graph.DispatchAsync(new(EventNames.ChannelMessageAccepted, channel, eventId,
            JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default);
        Assert.True(dispatched.Ok, dispatched.Error?.Detail ?? dispatched.Error?.Message);
        await DrainAsync();
    }

    private async Task DrainAsync()
    {
        var guard = 0;
        while (_events.TryRead(out var queued))
        {
            Assert.True(++guard < 50, "the event queue did not settle");
            var dispatched = await _graph.DispatchAsync(queued, default);
            Assert.True(dispatched.Ok, dispatched.Error?.Detail ?? dispatched.Error?.Message);
        }
    }

    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x03,
        0x08, 0x06, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    /// <summary>The script a channel is served by: hand the message to a session that may answer with blocks.</summary>
    private static ScriptPackage ReplyWithBlocks() => new(
        new("channel.blocks", 1, "channel", MountMode.Manual, [EventNames.ChannelMessageAccepted], "onEvent",
            [ScriptCapabilities.ChannelReply, ScriptCapabilities.SessionRun], TrustDomain.Builtin),
        """
        async function onEvent(event, ctx) {
          const spec = {
            modelProfileId: "default",
            objective: "Answer this conversation. Speak with the speak tool; a layout is allowed.",
            formatVersion: "1",
            tools: [{
              name: "speak",
              description: "Send one message, either as text or as blocks.",
              parametersJson: JSON.stringify({
                type: "object",
                properties: {
                  text: { type: "string" },
                  blocks: { type: "array" }
                },
                additionalProperties: true
              })
            }]
          };
          await ctx.session.run(spec, event.text);
        }
        """);

    private sealed class RecordingAdapter : AdapterBase, IAdapterHealth
    {
        public RecordingAdapter() : base(AdapterAddress,
            new AdapterDescriptor("recording", "one", AdapterDirection.Both)) { }

        /// <summary>
        /// What this Adapter declines, decided inside <see cref="SendAsync"/> — the way every adapter now answers, since
        /// there is no capability list for a sender to consult. A mailbox that cannot carry bytes says so here; one that
        /// writes whole messages to disk never gets the chance to be wrong about itself.
        /// </summary>
        public bool RefuseImages { get; set; }

        /// <summary>How many times a message reached this adapter. One means the caller went straight to the send.</summary>
        public int Attempts { get; private set; }

        public List<OutboundMessage> Sent { get; } = [];
        public bool Connected => true;
        public string? HealthDetail => null;

        public override ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
        {
            Attempts++;
            if (RefuseImages && message.Blocks?.Any(block => block is ImageBlock) == true)
                return ValueTask.FromResult(Result<SendResult>.Failure(new(ErrorCode.Invalid,
                    "这个适配器发不出图片。把这一块换成文字，或者别发它。")));
            Sent.Add(message);
            return ValueTask.FromResult(Result<SendResult>.Success(new(true, "attempt-1", PlatformMessageId: "9001")));
        }
    }

    private sealed class QuietBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }

    /// <summary>
    /// Where a module lands when it comes up. The host hands this to the graph; a test that only pretends would miss the
    /// half that matters here — the script that answers a message is mounted by the Channel announcing itself.
    /// </summary>
    private sealed class GraphBindings : IInstanceBindings
    {
        private IInstanceBindings? _inner;
        public void Bind(IInstanceBindings inner) => _inner = inner;
        public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) => _inner!.AttachAsync(target, ct);
        public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) => _inner!.DetachAsync(target, ct);
    }

    /// <summary>A model that answers with one canned step per call, so the turn's decisions are the only variable.</summary>
    private sealed class QueueModel : IModelManager
    {
        private readonly Queue<ModelEvent[]> _steps = new();
        public List<ModelRequest> Requests { get; } = [];

        public void Enqueue(params ModelEvent[] events) => _steps.Enqueue(events);

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_steps.Count == 0) throw new InvalidOperationException("The test did not script this model call.");
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream(_steps.Dequeue())));
        }

        private static async IAsyncEnumerable<ModelEvent> Stream(ModelEvent[] events,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        public IReadOnlyList<ModelProfile> ListProfiles() =>
        [
            new("default", new Address("model.openai-chat#one"), "scripted", Capabilities: ModelCapabilities.ToolCalls)
        ];
    }
}
