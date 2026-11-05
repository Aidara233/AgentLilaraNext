using ALN.Contracts;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

public sealed class ChannelParameterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-channel-params-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _state;
    private static readonly Address ChannelAddress = new("channel", "conversation", "chat-1");
    private static readonly Address AdapterAddress = new("adapter", "file", "file-1");

    public ChannelParameterTests()
    {
        Directory.CreateDirectory(_root);
        _state = new SqliteStore(Path.Combine(_root, "aln.db"));
    }

    public void Dispose()
    {
        _state.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_destroyed_channel_releases_its_parameter_scope_so_the_same_address_can_be_created_again()
    {
        var registry = new ParameterRegistry(_state);
        var channels = NewChannels(registry);
        var spec = new ChannelSpec(ChannelAddress, "chat-1", [new ChannelBinding(AdapterAddress, "chat-1")], "channel.conversation");

        Assert.True((await channels.CreateAsync(spec, default)).Ok);
        Assert.Contains(registry.List(), view => view.Scope == ChannelAddress.ToString());

        Assert.True((await channels.DestroyAsync(ChannelAddress, default)).Ok);
        // The scope is keyed by the address, so a registration left behind would answer for a Channel that is gone.
        Assert.DoesNotContain(registry.List(), view => view.Scope == ChannelAddress.ToString());

        // The same address again: it gets its tunables back instead of being created with none of them.
        var recreated = await channels.CreateAsync(spec, default);
        Assert.True(recreated.Ok, recreated.Error?.Detail ?? recreated.Error?.Message);
        Assert.Contains(registry.List(), view => view.Scope == ChannelAddress.ToString() && view.Name == "maxMessages");
        Assert.True((await registry.SetAsync(ChannelAddress.ToString(), "maxMessages", "5", default)).Ok);
        Assert.Equal(5, channels.Get(ChannelAddress).Value?.MaxMessages);
    }

    [Fact]
    public async Task A_scope_that_is_still_taken_fails_the_creation_instead_of_hiding_it()
    {
        var registry = new ParameterRegistry(_state);
        var channels = NewChannels(registry);
        var spec = new ChannelSpec(ChannelAddress, "chat-1", [new ChannelBinding(AdapterAddress, "chat-1")], "channel.conversation");
        // Something else still answers to this scope: a Channel that was never torn down properly.
        Assert.True(registry.Register(new FixedTarget(ChannelAddress.ToString())).Ok);

        var created = await channels.CreateAsync(spec, default);

        Assert.False(created.Ok);
        Assert.Equal(ErrorCode.Conflict, created.Error!.Code);
        // And it is not half-created: a Channel the caller was told about must exist.
        Assert.False(channels.Get(ChannelAddress).Ok);
    }

    private ALN.Channel.ChannelManager NewChannels(IParameterRegistry parameters)
    {
        var bindings = new StubBindings();
        var catalog = new ModuleCatalog();
        catalog.Attach(new AdapterManager(new AdapterTypeCatalog(), bindings));
        return new(new EventQueue(8), bindings, catalog, parameters: parameters);
    }

    private sealed record FixedTarget(string ParameterScope) : IParameterTarget
    {
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];
        public string? ReadParameter(string name) => null;
        public ValueTask<Result> ApplyParameterAsync(string name, string value, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
    }

    private sealed class StubBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(ALN.Contracts.Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(ALN.Contracts.Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }
}
