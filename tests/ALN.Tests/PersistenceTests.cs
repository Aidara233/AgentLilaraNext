using System.Text.Json;
using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-persist-" + Guid.NewGuid().ToString("N"));
    private static readonly Address Adapter = new("adapter", "file", "file-1");
    private static readonly Address Channel = new("channel", "logical", "chat-1");

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
        Assert.True(File.Exists(Path.Combine(_root, "messages", "logical", "chat-1", "20260102.jsonl")));
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
        // The spec is meant to be hand-editable, so it is stored as indented JSON, not a blob.
        Assert.Contains("\"platformId\": \"file\"", await File.ReadAllTextAsync(Path.Combine(_root, "adapters", "file-1.json")));

        Assert.True((await store.DeleteAsync(Adapter, default)).Ok);
        Assert.Empty((await store.LoadAllAsync(default)).Value!);
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
    public void Address_round_trips_through_its_text_form()
    {
        Assert.True(Address.TryParse("adapter.file#file-1", out var parsed));
        Assert.Equal(Adapter, parsed);
        Assert.True(Address.TryParse("channel.logical#chat-1", out var channel));
        Assert.Equal(Channel, channel);
        Assert.False(Address.TryParse("adapter.#x", out _));
        Assert.False(Address.TryParse("adapter.file#", out _));
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
