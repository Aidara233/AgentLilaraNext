using System.Text.Json;
using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-persist-" + Guid.NewGuid().ToString("N"));
    private static readonly Address Adapter = new("adapter", "file", "file-1");
    private static readonly Address Channel = new("channel", "conversation", "chat-1");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Message_history_is_kept_per_owner_and_ordered()
    {
        var store = new FileMessageStore(Path.Combine(_root, "messages"));
        Assert.True((await store.AppendAsync(new("a1", Adapter, "chat-1", "p1", "alice", "first", new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)), default)).Ok);
        Assert.True((await store.AppendAsync(new("c1", Channel, "chat-1", "p1", "alice", "first", new(2026, 1, 2, 3, 4, 6, TimeSpan.Zero)), default)).Ok);
        Assert.True((await store.AppendAsync(new("a2", Adapter, "chat-1", "p2", "bob", "second", new(2026, 1, 2, 3, 4, 7, TimeSpan.Zero)), default)).Ok);

        // Each owner sees only its own traffic — Adapter history is not Channel history.
        var adapterMessages = await store.ReadAsync(Adapter, default);
        var channelMessages = await store.ReadAsync(Channel, default);
        Assert.True(adapterMessages.Ok);
        Assert.Equal(["first", "second"], adapterMessages.Value!.Select(m => m.Text));
        Assert.Equal(["first"], channelMessages.Value!.Select(m => m.Text));
        Assert.True(File.Exists(Path.Combine(_root, "messages", "file", "file-1", "20260102.jsonl")));
        Assert.True(File.Exists(Path.Combine(_root, "messages", "conversation", "chat-1", "20260102.jsonl")));
    }

    [Fact]
    public async Task Adapter_specs_survive_a_round_trip()
    {
        var store = new FileAdapterSpecStore(Path.Combine(_root, "adapters"));
        var spec = new AdapterSpec(Adapter, new AdapterDescriptor("file", "file-1", AdapterDirection.Both));
        Assert.True((await store.SaveAsync(spec, default)).Ok);

        var loaded = Assert.Single((await store.LoadAllAsync(default)).Value!);
        Assert.Equal(spec.Address, loaded.Address);
        Assert.Equal(spec.Descriptor, loaded.Descriptor);
        // The spec is meant to be hand-editable, so it is stored as indented JSON, not a blob — under
        // adapters/<type>/<instance>/, mirroring the code's own nesting.
        Assert.Contains("\"platformId\": \"file\"", await File.ReadAllTextAsync(SpecPath("file", "file-1")));

        Assert.True((await store.DeleteAsync(Adapter, default)).Ok);
        Assert.Empty((await store.LoadAllAsync(default)).Value!);
        // Deleting the spec takes the now-empty instance directory with it; an empty tree that comes
        // back on every start is how a storage layout rots.
        Assert.False(Directory.Exists(Path.Combine(_root, "adapters", "file")));
    }

    private string SpecPath(string type, string instance) =>
        Path.Combine(_root, "adapters", type, instance, FileAdapterSpecStore.FileName);

    [Fact]
    public async Task Adapter_settings_round_trip_and_stay_out_of_the_plain_spec_file()
    {
        var store = new FileAdapterSpecStore(Path.Combine(_root, "adapters"));
        var settings = new Dictionary<string, string>
        {
            ["endpoint"] = "ws://127.0.0.1:3001",
            ["accessToken"] = "onebot-token"
        };
        var spec = new AdapterSpec(new Address("adapter", "qq", "qq-1"),
            new AdapterDescriptor("qq", "123456", AdapterDirection.Both), settings);
        Assert.True((await store.SaveAsync(spec, default)).Ok);

        var loaded = Assert.Single((await store.LoadAllAsync(default)).Value!);
        Assert.Equal("ws://127.0.0.1:3001", loaded.Settings!["endpoint"]);
        Assert.Equal("onebot-token", loaded.Settings["accessToken"]);
        // Hand-editable, like every other spec: the whole point is that a human can write this file.
        var text = await File.ReadAllTextAsync(SpecPath("qq", "qq-1"));
        Assert.Contains("\"endpoint\": \"ws://127.0.0.1:3001\"", text);
    }

    [Fact]
    public async Task A_spec_written_before_settings_existed_still_loads()
    {
        Directory.CreateDirectory(Path.Combine(_root, "adapters", "file", "file-1"));
        // Exactly what the previous version wrote, in the nested place: no settings key at all.
        await File.WriteAllTextAsync(SpecPath("file", "file-1"),
            """
            {
              "address": "adapter.file#file-1",
              "platformId": "file",
              "accountId": "file-1",
              "direction": "Both"
            }
            """);

        var store = new FileAdapterSpecStore(Path.Combine(_root, "adapters"));
        var loaded = Assert.Single((await store.LoadAllAsync(default)).Value!);
        Assert.Equal("adapter.file#file-1", loaded.Address.ToString());
        Assert.Null(loaded.Settings);
    }

    [Fact]
    public async Task A_spec_left_in_a_flat_layout_is_an_error_that_names_the_right_place()
    {
        var root = Path.Combine(_root, "adapters");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "file-1.json"),
            """
            { "address": "adapter.file#file-1", "platformId": "file", "accountId": "file-1", "direction": "Both" }
            """);

        var store = new FileAdapterSpecStore(root);
        var loaded = await store.LoadAllAsync(default);

        // No compatibility path: the layout is the contract, so a stray file is reported rather than
        // half-interpreted, and the message says where it belongs.
        Assert.False(loaded.Ok);
        Assert.Contains("<type>/<instance>/adapter.json", loaded.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_spec_in_the_wrong_directory_is_an_error_that_names_both_places()
    {
        Directory.CreateDirectory(Path.Combine(_root, "adapters", "qq", "qq-1"));
        await File.WriteAllTextAsync(SpecPath("qq", "qq-1"),
            """
            { "address": "adapter.file#file-1", "platformId": "file", "accountId": "file-1", "direction": "Both" }
            """);

        var store = new FileAdapterSpecStore(Path.Combine(_root, "adapters"));
        var loaded = await store.LoadAllAsync(default);

        Assert.False(loaded.Ok);
        Assert.Contains("expected file/file-1", loaded.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_adapter_without_settings_does_not_grow_an_empty_settings_object()
    {
        var store = new FileAdapterSpecStore(Path.Combine(_root, "adapters"));
        Assert.True((await store.SaveAsync(new AdapterSpec(Adapter,
            new AdapterDescriptor("file", "file-1", AdapterDirection.Both)), default)).Ok);

        var text = await File.ReadAllTextAsync(SpecPath("file", "file-1"));
        Assert.DoesNotContain("settings", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Channel_specs_survive_a_round_trip_including_bindings()
    {
        var store = new FileChannelSpecStore(Path.Combine(_root, "channels"));
        var spec = new ChannelSpec(Channel, "chat-1", [new ChannelBinding(Adapter, "chat-1")]);
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
        var spec = new ChannelSpec(new("channel", "conversation", "chat-1"), "chat-1", [new ChannelBinding(adapter, "chat-1")]);

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
        var messages = new FileMessageStore(Path.Combine(_root, "messages"));
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
