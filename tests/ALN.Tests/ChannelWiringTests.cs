using ALN.Channel;
using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// Wiring as state: one Channel per conversation, one conversation per Channel, and a move that cannot leave either
/// half behind.
///
/// What is worth pinning here is the part that is invisible from the outside: a conversation taken by somebody else
/// does not come back on the next boot because a spec file still mentions it, and a Channel that lost its conversation
/// does not either.
/// </summary>
public sealed class ChannelWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-wiring-" + Guid.NewGuid().ToString("N"));
    private readonly AdapterTypeCatalog _types = new();
    private readonly SqliteStore _state;
    private readonly FileChannelSpecStore _specs;

    public ChannelWiringTests()
    {
        Directory.CreateDirectory(_root);
        Assert.True(_types.RegisterPackageTypes("test.pack", [new("channel.conversation", "channel")]).Ok);
        _state = new SqliteStore(Path.Combine(_root, "aln.db"));
        _specs = new FileChannelSpecStore(_root, _types);
    }

    public void Dispose()
    {
        _state.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ChannelManager NewManager() => new(new EventQueue(8), new QuietBindings(), new ModuleCatalog(),
        _specs, state: _state, types: _types);

    private static readonly Address AdapterOne = new("adapter", "file", "one");

    private static ChannelSpec Spec(string name, string conversation) => new(
        new Address("channel", "conversation", name), name,
        [new ChannelBinding(AdapterOne, conversation)], "channel.conversation");

    [Fact]
    public async Task Taking_a_conversation_away_leaves_the_other_channel_with_nothing_and_says_who()
    {
        var channels = NewManager();
        Assert.True((await channels.CreateAsync(Spec("a", "room-1"), default)).Ok);
        Assert.True((await channels.CreateAsync(Spec("b", "room-2"), default)).Ok);
        var b = new Address("channel", "conversation", "b");

        var moved = await channels.ConnectAsync(b, new ChannelBinding(AdapterOne, "room-1"), default);

        Assert.True(moved.Ok, moved.Error?.Message);
        // Three things a caller has to be able to see: where it went, what B used to serve, and who just went silent.
        Assert.Equal(b, moved.Value!.Channel);
        Assert.Equal("room-1", moved.Value.Binding!.PlatformConversationId);
        Assert.Equal("room-2", moved.Value.Dropped!.PlatformConversationId);
        Assert.Equal(new Address("channel", "conversation", "a"), moved.Value.TakenFrom);
        Assert.Empty(channels.Get(new Address("channel", "conversation", "a")).Value!.Bindings);
        Assert.Equal("room-1", Assert.Single(channels.Get(b).Value!.Bindings).PlatformConversationId);

        // And the same move again changes nothing: a retry must not produce a second row or a complaint.
        var again = await channels.ConnectAsync(b, new ChannelBinding(AdapterOne, "room-1"), default);
        Assert.True(again.Ok, again.Error?.Message);
        Assert.Null(again.Value!.TakenFrom);
    }

    [Fact]
    public async Task A_conversation_taken_away_does_not_come_back_from_its_old_spec_file()
    {
        var first = NewManager();
        Assert.True((await first.CreateAsync(Spec("a", "room-1"), default)).Ok);
        Assert.True((await first.CreateAsync(Spec("b", "room-2"), default)).Ok);
        Assert.True((await first.ConnectAsync(new Address("channel", "conversation", "b"),
            new ChannelBinding(AdapterOne, "room-1"), default)).Ok);
        Assert.True((await first.DisconnectAsync(new Address("channel", "conversation", "b"), default)).Ok);

        // A fresh manager over the same files: the spec of "a" still says room-1, and that is exactly what must not win.
        var second = NewManager();
        var started = await second.StartAsync(new LifecycleSignal(LifecyclePhase.Start, DateTimeOffset.UtcNow), default);
        Assert.True(started.Ok, started.Error?.Message);

        Assert.Empty(second.Get(new Address("channel", "conversation", "a")).Value!.Bindings);
        Assert.Empty(second.Get(new Address("channel", "conversation", "b")).Value!.Bindings);
        // The file was read, not rewritten (C-03): what changed is the state, not the record of what it was made as.
        var onDisk = await _specs.LoadAllAsync(default);
        Assert.Contains(onDisk.Value!, spec => spec.Address.Instance == "a"
                                              && spec.Bindings[0].PlatformConversationId == "room-1");
    }

    [Fact]
    public async Task A_disconnected_channel_keeps_its_window_and_hears_nothing()
    {
        var channels = NewManager();
        var address = new Address("channel", "conversation", "a");
        Assert.True((await channels.CreateAsync(Spec("a", "room-1"), default)).Ok);
        Assert.True((await channels.StartAsync(address, default)).Ok);

        var disconnected = await channels.DisconnectAsync(address, default);
        Assert.True(disconnected.Ok, disconnected.Error?.Message);
        Assert.Equal("room-1", disconnected.Value!.Dropped!.PlatformConversationId);
        Assert.False(channels.IsBound(address, AdapterOne, "room-1"));
        Assert.True(channels.Get(address).Value!.Running);   // it still exists, and it is still switched on

        var idle = await channels.DisconnectAsync(address, default);
        Assert.True(idle.Ok, idle.Error?.Message);
        Assert.Null(idle.Value!.Dropped);
    }

    [Fact]
    public async Task A_destroyed_channel_does_not_leave_its_aim_behind()
    {
        var channels = NewManager();
        var address = new Address("channel", "conversation", "a");
        Assert.True((await channels.CreateAsync(Spec("a", "room-1"), default)).Ok);
        Assert.True((await channels.DestroyAsync(address, default)).Ok);

        // Re-created at the same address with a different conversation: an inherited aim would have answered for both.
        Assert.True((await channels.CreateAsync(Spec("a", "room-9"), default)).Ok);

        Assert.Equal("room-9", Assert.Single(channels.Get(address).Value!.Bindings).PlatformConversationId);
    }

    private sealed class QuietBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }
}
