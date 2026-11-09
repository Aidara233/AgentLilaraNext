using System.Text.Json;
using ALN.Adapter.File;
using ALN.Channel;
using ALN.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// A picture arriving from outside: the mailbox takes it in, the Adapter hands it on as bytes, the Channel stores it
/// once and keeps the pointer.
///
/// The interesting part is what each layer is allowed to decide. The mailbox decides only "text or not text". The
/// Adapter decides nothing and passes bytes. The Channel stores and answers with references — and a store that cannot
/// be written is a note, never a message that never arrives (docs/28 §1, §2).
/// </summary>
public sealed class InboundMediaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-inbound-media-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A 33-byte PNG header at a size nobody would confuse with a thumbnail.</summary>
    private static byte[] Png(int width, int height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        0x08, 0x06, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    private LocalFileSystemAccess Access() =>
        new(_root, [FileScopes.Media, FileScopes.Workspace], binaryScopes: [FileScopes.Media, FileScopes.Workspace],
            readOnlyScopes: [FileScopes.Media]);

    private SqliteStore OpenStore()
    {
        var store = new SqliteStore(Path.Combine(_root, "aln.db"));
        _stores.Add(store);
        return store;
    }

    // ---- the mailbox: "the file's text is the message" keeps its meaning -------------------------------------------

    [Fact]
    public async Task A_picture_dropped_in_the_inbox_is_input_with_no_words()
    {
        var mailbox = new FileMailbox(Path.Combine(_root, "mailbox"));
        var inbox = Path.Combine(mailbox.Root, "inbox", "room");
        Directory.CreateDirectory(inbox);
        // The name lies on purpose: what the file is comes from its bytes, which is the rule this whole path rests on.
        await File.WriteAllBytesAsync(Path.Combine(inbox, "notes.txt"), Png(7, 5));

        var inputs = await mailbox.ReadPendingAsync(default);

        var input = Assert.Single(inputs);
        Assert.Equal("", input.Content);
        var attachment = Assert.Single(input.Attachments!);
        Assert.Equal(MediaKind.Image, attachment.Kind);
        Assert.Equal("image/png", attachment.MediaType);
        Assert.Equal(Png(7, 5), attachment.Bytes);
    }

    [Fact]
    public async Task A_text_file_is_still_just_a_message()
    {
        var mailbox = new FileMailbox(Path.Combine(_root, "mailbox-text"));
        var inbox = Path.Combine(mailbox.Root, "inbox", "room");
        Directory.CreateDirectory(inbox);
        await File.WriteAllTextAsync(Path.Combine(inbox, "hello.txt"), "在的");

        var input = Assert.Single(await mailbox.ReadPendingAsync(default));

        Assert.Equal("在的", input.Content);
        Assert.Null(input.Attachments);
    }

    [Fact]
    public async Task Bytes_nobody_can_identify_are_still_refused_with_a_reason()
    {
        // The door did not open for "any binary": an unidentifiable file is refused exactly as before, because a
        // half-written binary read as text is worse than a rejected file (docs/28 §5).
        var mailbox = new FileMailbox(Path.Combine(_root, "mailbox-junk"));
        var inbox = Path.Combine(mailbox.Root, "inbox", "room");
        Directory.CreateDirectory(inbox);
        await File.WriteAllBytesAsync(Path.Combine(inbox, "thing.bin"), [0x80, 0x80, 0x80]);

        Assert.Empty(await mailbox.ReadPendingAsync(default));
        var rejected = Path.Combine(mailbox.Root, "rejected", "room");
        Assert.Contains("UTF-8", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(rejected, "*.error.txt"))),
            StringComparison.Ordinal);
    }

    // ---- the adapter: bytes out, nothing decided ----------------------------------------------------------------

    [Fact]
    public async Task The_file_adapter_publishes_the_picture_as_bytes_with_no_text()
    {
        var mailbox = new FileMailbox(Path.Combine(_root, "adapter"));
        var inbox = Path.Combine(mailbox.Root, "inbox", "chat-1");
        Directory.CreateDirectory(inbox);
        await File.WriteAllBytesAsync(Path.Combine(inbox, "photo.png"), Png(2, 2));
        var sink = new RecordingSink();
        var adapter = new FileAdapter(
            new AdapterSpec(Address, new("file", "account", AdapterDirection.Both), "adapter.file"), mailbox, sink);
        Assert.True((await adapter.StartAsync(default)).Ok);

        var polled = await adapter.PollOnceAsync();

        Assert.True(polled.Ok, polled.Error?.Message);
        Assert.Equal(1, polled.Value);
        var envelope = JsonSerializer.Deserialize<AdapterEnvelope>(sink.Events.Single().PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("", envelope.Text);
        var attachment = Assert.Single(envelope.Attachments!);
        Assert.Equal("image/png", attachment.MediaType);
        Assert.Equal(Png(2, 2), attachment.Bytes);
    }

    private static readonly Address Address = new("adapter", "file", "media-1");

    private sealed class RecordingSink : IEventSink
    {
        public List<RoutedEvent> Events { get; } = [];

        public ValueTask<Result> PublishAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
        {
            Events.Add(routedEvent);
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> SubscribeAsync(string name, Func<RoutedEvent, CancellationToken, ValueTask<Result>> handler,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> SubscribeAllAsync(Func<RoutedEvent, CancellationToken, ValueTask<Result>> handler,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }

    // ---- the channel: store once, keep the pointer ---------------------------------------------------------------

    [Fact]
    public async Task A_message_with_a_picture_is_stored_once_and_the_rows_keep_the_pointer()
    {
        var store = OpenStore();
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapter", [new("adapter.file", "adapter")]).Ok);
        var bindings = new QuietBindings();
        var events = new EventQueue(8);
        var catalog = new ModuleCatalog();
        var adapters = new AdapterManager(types, bindings);
        catalog.Attach(adapters);
        var mediaLocal = new FileMediaStore(Access());
        var channels = new ChannelManager(events, bindings, catalog, messages: store, media: mediaLocal);
        var adapter = new Address("adapter", "file", "media-1");
        var channel = new Address("channel", "conversation", "media-1");
        var created = await adapters.CreateAsync(
            new AdapterSpec(adapter, new("file", "media-1", AdapterDirection.Both), "adapter.file"),
            _ => new StubAdapter(adapter, new("file", adapter.Instance!, AdapterDirection.Both)), default);
        Assert.True(created.Ok, created.Error?.Message + " / " + created.Error?.Detail);
        Assert.True((await channels.CreateAsync(new(channel, "media-1", [new(adapter, "chat-1")], "channel.conversation"),
            default)).Ok);
        Assert.True((await channels.StartAsync(channel, default)).Ok);

        var envelope = new AdapterEnvelope(adapter, "file", "chat-1", "m-1", "", DateTimeOffset.UtcNow,
            Attachments: [new InboundAttachment(MediaKind.Image, "image/png", Png(3, 3))]);
        var delivered = await channels.AcceptAsync(envelope, default);

        Assert.True(delivered.Ok, delivered.Error?.Message);
        // The accepted event carries references, not bytes: the picture is on disk once, named by its own hash.
        RoutedEvent? accepted = null;
        while (events.TryRead(out var queued))
            if (string.Equals(queued.Name, "channel.message.accepted", StringComparison.Ordinal)) accepted = queued;
        var message = JsonSerializer.Deserialize<ChannelMessage>(accepted!.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var reference = Assert.Single(message.Media!);
        Assert.Equal(MediaKind.Image, reference.Kind);
        Assert.Equal(3, reference.Width);
        Assert.True(File.Exists(Path.Combine(_root, FileScopes.Media, reference.Path.Replace('/', Path.DirectorySeparatorChar))));

        // And the Channel's own history row holds the same pointer — that row is what a Session is handed later.
        var stored = Assert.Single((await store.ReadAsync(channel, default)).Value!);
        Assert.Equal("", stored.Text);
        Assert.Equal(reference.Path, Assert.Single(stored.Media!).Path);
        // The bytes are not in the row: a picture inlined here would be copied once per message, for as long as it exists.
        Assert.DoesNotContain(Convert.ToBase64String(Png(3, 3)), JsonSerializer.Serialize(stored), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_that_cannot_be_stored_does_not_take_the_message_down_with_it()
    {
        // No media store on this host. The words still arrive — dropping what somebody said because an attachment could
        // not be kept is the wrong trade, and a Channel that fails the whole message has no way to say why.
        var store = OpenStore();
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapter", [new("adapter.file", "adapter")]).Ok);
        var bindings = new QuietBindings();
        var events = new EventQueue(8);
        var catalog = new ModuleCatalog();
        var adapters = new AdapterManager(types, bindings);
        catalog.Attach(adapters);
        var report = new CollectingReporter();
        var channels = new ChannelManager(events, bindings, catalog, messages: store, report: report);
        var adapter = new Address("adapter", "file", "no-media");
        var channel = new Address("channel", "conversation", "no-media");
        var created = await adapters.CreateAsync(
            new AdapterSpec(adapter, new("file", "no-media", AdapterDirection.Both), "adapter.file"),
            _ => new StubAdapter(adapter, new("file", adapter.Instance!, AdapterDirection.Both)), default);
        Assert.True(created.Ok, created.Error?.Message + " / " + created.Error?.Detail);
        Assert.True((await channels.CreateAsync(new(channel, "no-media", [new(adapter, "chat-1")], "channel.conversation"),
            default)).Ok);
        Assert.True((await channels.StartAsync(channel, default)).Ok);

        var delivered = await channels.AcceptAsync(new AdapterEnvelope(adapter, "file", "chat-1", "m-2", "看这个",
            DateTimeOffset.UtcNow, Attachments: [new InboundAttachment(MediaKind.Image, "image/png", Png(1, 1))]), default);

        Assert.True(delivered.Ok, delivered.Error?.Message);
        var stored = Assert.Single((await store.ReadAsync(channel, default)).Value!);
        Assert.Equal("看这个", stored.Text);
        Assert.Empty(stored.Media ?? []);
        Assert.Contains(report.Notes, note => note.Contains("no media store", StringComparison.Ordinal));
    }

    // ---- the message store: the column round-trips, and an old row reads as "none" --------------------------------

    [Fact]
    public async Task The_media_column_round_trips_and_a_row_without_one_reads_as_none()
    {
        var store = OpenStore();
        var owner = new Address("channel", "conversation", "m");
        var withMedia = new StoredMessage("m-1", owner, "chat-1", null, "alice", "看", DateTimeOffset.UtcNow,
            Media: [new MediaRef(FileScopes.Media, "image/ab/abcdef0123456789.png", MediaKind.Image, "image/png", 33, 4, 3, "abcdef")]);
        Assert.True((await store.AppendAsync(withMedia, default)).Ok);
        Assert.True((await store.AppendAsync(new StoredMessage("m-2", owner, "chat-1", null, "alice", "没有图",
            DateTimeOffset.UtcNow), default)).Ok);

        var read = (await store.ReadAsync(owner, default)).Value!;

        var picture = Assert.Single(read[0].Media!);
        Assert.Equal("image/png", picture.MediaType);
        Assert.Equal(4, picture.Width);
        Assert.Equal(MediaKind.Image, picture.Kind);
        Assert.Null(read[1].Media);
        // The newest-first page reads the same column, and must not lose it.
        var recent = (await store.ReadRecentAsync(owner, 10, default)).Value!;
        Assert.Equal(picture.Path, Assert.Single(recent[0].Media!).Path);
    }

    private sealed class StubAdapter(Address address, AdapterDescriptor descriptor) : IChatAdapter, IAdapterHealth
    {
        public Address Address { get; } = address;
        public ModuleId Id => new(Address.ToString());
        public AdapterDescriptor Descriptor { get; } = descriptor;
        public AdapterState State => AdapterState.Running;
        public bool Connected => true;
        public string? HealthDetail => null;
        public ValueTask<Result> StartAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SendResult>.Success(new(true, "attempt")));
    }

    private sealed class QuietBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }

    private sealed class CollectingReporter : IRestoreReporter
    {
        public List<string> Notes { get; } = [];
        public void Note(string section, string message) => Notes.Add(section + ": " + message);
        public IReadOnlyList<RestoreNote> Snapshot() => [.. Notes.Select(note => new RestoreNote("test", note))];
    }
}
