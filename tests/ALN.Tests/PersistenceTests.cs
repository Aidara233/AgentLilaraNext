using System.Text.Json;
using ALN.Contracts;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-persist-" + Guid.NewGuid().ToString("N"));
    private static readonly Address Adapter = new("adapter", "file", "file-1");
    private static readonly Address Channel = new("channel", "conversation", "chat-1");
    private readonly List<SqliteStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SqliteStore OpenStore()
    {
        var store = new SqliteStore(Path.Combine(_root, "aln.db"));
        _stores.Add(store);
        return store;
    }

    [Fact]
    public async Task Message_history_is_kept_per_owner_and_ordered()
    {
        IMessageStore store = OpenStore();
        Assert.True((await store.AppendAsync(new("a1", Adapter, "chat-1", "p1", "alice", "first", new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)), default)).Ok);
        Assert.True((await store.AppendAsync(new("c1", Channel, "chat-1", "p1", "alice", "first", new(2026, 1, 2, 3, 4, 6, TimeSpan.Zero)), default)).Ok);
        Assert.True((await store.AppendAsync(new("a2", Adapter, "chat-1", "p2", "bob", "second", new(2026, 1, 2, 3, 4, 7, TimeSpan.Zero)), default)).Ok);

        // Each owner sees only its own traffic — Adapter history is not Channel history.
        var adapterMessages = await store.ReadAsync(Adapter, default);
        var channelMessages = await store.ReadAsync(Channel, default);
        Assert.True(adapterMessages.Ok);
        Assert.Equal(["first", "second"], adapterMessages.Value!.Select(m => m.Text));
        Assert.Equal(["first"], channelMessages.Value!.Select(m => m.Text));
        // One row set per owner, both in the same place: no per-day, per-conversation files to count.
        Assert.Equal(2, (await store.ReadAsync(Adapter, default)).Value!.Count);
        Assert.Single((await store.ReadAsync(Channel, default)).Value!);
    }

    [Fact]
    public async Task Adapter_specs_survive_a_round_trip()
    {
        var store = new FileAdapterSpecStore(_root);
        var spec = new AdapterSpec(Adapter, new AdapterDescriptor("file", "file-1", AdapterDirection.Both), "adapter.file");
        Assert.True((await store.SaveAsync(spec, default)).Ok);

        var loaded = Assert.Single((await store.LoadAllAsync(default)).Value!);
        Assert.Equal(spec.Address, loaded.Address);
        Assert.Equal(spec.Descriptor, loaded.Descriptor);
        // The type makes the round trip too: it is what everything downstream classifies this by.
        Assert.Equal("adapter.file", loaded.TypeId);
        // The spec is meant to be hand-editable, so it is stored as indented JSON, not a blob — in the module
        // directory the node is named after, which is the same directory everything else about it would live in.
        Assert.Contains("\"platformId\": \"file\"", await File.ReadAllTextAsync(ModulePath("adapter.file#file-1")));

        Assert.True((await store.DeleteAsync(Adapter, default)).Ok);
        Assert.Empty((await store.LoadAllAsync(default)).Value!);
        // Deleting the spec takes the now-empty instance directory with it; an empty tree that comes
        // back on every start is how a storage layout rots.
        Assert.False(Directory.Exists(Path.Combine(_root, "modules", "adapter.file#file-1")));
    }

    // The path carries the name and nothing else: the type lives inside the file.
    private string ModulePath(string id) => Path.Combine(_root, "modules", id, "module.json");

    [Fact]
    public async Task Adapter_settings_round_trip_and_stay_out_of_the_plain_spec_file()
    {
        var store = new FileAdapterSpecStore(_root);
        var settings = new Dictionary<string, string>
        {
            ["endpoint"] = "ws://127.0.0.1:3001",
            ["accessToken"] = "onebot-token"
        };
        var spec = new AdapterSpec(new Address("adapter", "qq", "qq-1"),
            new AdapterDescriptor("qq", "123456", AdapterDirection.Both), "adapter.qq", settings);
        Assert.True((await store.SaveAsync(spec, default)).Ok);

        var loaded = Assert.Single((await store.LoadAllAsync(default)).Value!);
        Assert.Equal("ws://127.0.0.1:3001", loaded.Settings!["endpoint"]);
        Assert.Equal("onebot-token", loaded.Settings["accessToken"]);
        // Hand-editable, like every other spec: the whole point is that a human can write this file.
        var text = await File.ReadAllTextAsync(ModulePath("adapter.qq#qq-1"));
        Assert.Contains("\"endpoint\": \"ws://127.0.0.1:3001\"", text);
    }

    [Fact]
    public async Task A_spec_that_does_not_say_what_it_is_fails_loudly_instead_of_being_skipped()
    {
        // Without a type the store cannot even tell whether the file belongs to it. Skipping would read as
        // "nothing configured" and the instance would silently not exist; guessing from the directory name is
        // the thing this layout exists to avoid. So: an error that names the file.
        Directory.CreateDirectory(Path.Combine(_root, "modules", "adapter.file#file-1"));
        await File.WriteAllTextAsync(ModulePath("adapter.file#file-1"),
            """
            { "id": "adapter.file#file-1", "config": { "platformId": "file", "accountId": "file-1", "direction": "Both" } }
            """);

        var store = new FileAdapterSpecStore(_root);
        var loaded = await store.LoadAllAsync(default);

        Assert.False(loaded.Ok);
        Assert.Contains("no type", loaded.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("module.json", loaded.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_type_counts_as_no_type_at_all()
    {
        Directory.CreateDirectory(Path.Combine(_root, "modules", "adapter.file#file-1"));
        await File.WriteAllTextAsync(ModulePath("adapter.file#file-1"),
            """
            { "id": "adapter.file#file-1", "type": "  ", "config": { "platformId": "file", "accountId": "file-1", "direction": "Both" } }
            """);

        var store = new FileAdapterSpecStore(_root);
        Assert.False((await store.LoadAllAsync(default)).Ok);
    }

    [Fact]
    public async Task A_spec_written_before_settings_existed_still_loads()
    {
        Directory.CreateDirectory(Path.Combine(_root, "modules", "adapter.file#file-1"));
        // No settings key at all: the adapter simply has none, and the file does not grow an empty object for it.
        await File.WriteAllTextAsync(ModulePath("adapter.file#file-1"),
            """
            {
              "id": "adapter.file#file-1",
              "type": "adapter.file",
              "config": { "platformId": "file", "accountId": "file-1", "direction": "Both" }
            }
            """);

        var store = new FileAdapterSpecStore(_root);
        var loaded = Assert.Single((await store.LoadAllAsync(default)).Value!);
        Assert.Equal("adapter.file#file-1", loaded.Address.ToString());
        Assert.Null(loaded.Settings);
    }

    [Fact]
    public async Task A_spec_file_loose_in_the_module_tree_is_an_error_that_names_the_right_place()
    {
        var modules = Path.Combine(_root, "modules");
        Directory.CreateDirectory(modules);
        await File.WriteAllTextAsync(Path.Combine(modules, "file-1.json"),
            """
            { "id": "adapter.file#file-1", "type": "adapter.file" }
            """);

        var store = new FileAdapterSpecStore(_root);
        var loaded = await store.LoadAllAsync(default);

        // No compatibility path: the layout is the contract, so a stray file is reported rather than
        // half-interpreted, and the message says where it belongs.
        Assert.False(loaded.Ok);
        Assert.Contains("<id>/module.json", loaded.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_that_disagrees_with_the_id_inside_it_is_refused()
    {
        // The id is the directory name (docs/22 §2). A file that claims another node's id would be a node the
        // graph answers to by one name while it lives under another — the next person to open the folder would be
        // looking at a different module.
        Directory.CreateDirectory(Path.Combine(_root, "modules", "somewhere-else"));
        await File.WriteAllTextAsync(Path.Combine(_root, "modules", "somewhere-else", "module.json"),
            """
            { "id": "adapter.file#file-1", "type": "adapter.file", "config": { "platformId": "file", "accountId": "file-1", "direction": "Both" } }
            """);

        var store = new FileAdapterSpecStore(_root);
        var loaded = await store.LoadAllAsync(default);

        Assert.False(loaded.Ok);
        Assert.Contains("does not match its directory", loaded.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_adapter_without_settings_does_not_grow_an_empty_settings_object()
    {
        var store = new FileAdapterSpecStore(_root);
        Assert.True((await store.SaveAsync(new AdapterSpec(Adapter,
            new AdapterDescriptor("file", "file-1", AdapterDirection.Both), "adapter.file"), default)).Ok);

        var text = await File.ReadAllTextAsync(ModulePath("adapter.file#file-1"));
        Assert.DoesNotContain("settings", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Channel_specs_survive_a_round_trip_including_bindings()
    {
        var store = new FileChannelSpecStore(_root);
        var spec = new ChannelSpec(Channel, "chat-1", [new ChannelBinding(Adapter, "chat-1")], "channel.conversation");
        Assert.True((await store.SaveAsync(spec, default)).Ok);

        var loaded = Assert.Single((await store.LoadAllAsync(default)).Value!);
        Assert.Equal(Channel, loaded.Address);
        Assert.Equal("chat-1", loaded.Name);
        Assert.Equal(Adapter, Assert.Single(loaded.Bindings).Adapter);
    }

    [Fact]
    public async Task Channel_can_be_created_before_its_adapter_and_delivery_reports_the_missing_one()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapter", [new("adapter.file", "adapter")]).Ok);
        var bindings = new StubBindings();
        var events = new EventQueue(8);
        var channels = new ALN.Channel.ChannelManager(new AdapterManager(types, bindings), events, bindings);
        var adapter = new Address("adapter", "file", "later-1");
        var spec = new ChannelSpec(new("channel", "conversation", "chat-1"), "chat-1", [new ChannelBinding(adapter, "chat-1")],
            "channel.conversation");

        // Creation must not depend on startup order: the Adapter is simply not there yet.
        var created = await channels.CreateAsync(spec, default);
        Assert.True(created.Ok, created.Error?.Message);
        Assert.True((await channels.StartAsync(spec.Address, default)).Ok);

        var envelope = new AdapterEnvelope(adapter, "file", "chat-1", "m-1", "hello", DateTimeOffset.UtcNow);
        var delivered = await channels.AcceptAsync(envelope, default);

        Assert.False(delivered.Ok);
        Assert.Equal(ErrorCode.Unavailable, delivered.Error?.Code);
        Assert.Contains("later-1", delivered.Error?.Message, StringComparison.Ordinal);
        // Nothing was queued for a delivery that never happened.
        Assert.False(events.TryRead(out _));
    }

    [Fact]
    public void Address_round_trips_through_its_text_form()
    {
        Assert.True(Address.TryParse("adapter.file#file-1", out var parsed));
        Assert.Equal(Adapter, parsed);
        Assert.True(Address.TryParse("channel.conversation#chat-1", out var channel));
        Assert.Equal(Channel, channel);
        Assert.False(Address.TryParse("adapter.#x", out _));
        Assert.False(Address.TryParse("adapter.file#", out _));
    }

    private sealed class StubBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }

    [Fact]
    public async Task Adapter_messages_are_recorded_once_at_the_publish_boundary()
    {
        var messages = OpenStore();
        var inner = new EventQueue(capacity: 8);
        var sink = new RecordingEventSink(inner, messages);
        var envelope = new AdapterEnvelope(Adapter, "file", "chat-1", "p1", "hello", DateTimeOffset.UtcNow, "alice");

        Assert.True((await sink.PublishAsync(new(EventNames.AdapterEnvelope, Adapter, envelope.EventId,
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default)).Ok);

        var stored = Assert.Single((await messages.ReadAsync(Adapter, default)).Value!);
        Assert.Equal("hello", stored.Text);
        Assert.Equal("alice", stored.Sender);
        // The event still reaches the queue: recording is a side effect, not a diversion.
        Assert.True(inner.TryRead(out var queued));
        Assert.Equal(EventNames.AdapterEnvelope, queued.Name);
    }
}
