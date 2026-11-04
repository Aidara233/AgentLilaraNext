using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

public sealed class MemoryModuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-memory-module-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;
    private readonly SessionExtensionRegistry _extensions = new();

    public MemoryModuleTests()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteStore(Path.Combine(_root, "aln.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task The_module_registers_both_ways_into_a_round_and_takes_them_back_when_it_stops()
    {
        var module = new MemoryModule(_store, _extensions);
        Assert.Empty(_extensions.Floating);
        Assert.Empty(_extensions.Tools);

        Assert.True((await module.InitializeAsync(default)).Ok);

        // One module, two surfaces: a line for every round, and the tools that keep it honest.
        Assert.Single(_extensions.Floating);
        Assert.Single(_extensions.Tools);
        Assert.True((await module.StopAsync(default)).Ok);
        Assert.Empty(_extensions.Floating);
        Assert.Empty(_extensions.Tools);
    }

    [Fact]
    public async Task A_module_that_cannot_register_all_of_itself_registers_none_of_itself()
    {
        // Something else already holds the tools' id: half a module would offer a floating line that names recall
        // while recall is missing, which is a promise the model cannot keep.
        Assert.True(_extensions.Register(new StubTools(MemoryToolSource.SourceId)).Ok);
        var module = new MemoryModule(_store, _extensions);

        var initialized = await module.InitializeAsync(default);

        Assert.False(initialized.Ok);
        Assert.Empty(_extensions.Floating);
    }

    [Fact]
    public async Task The_registered_sources_answer_with_this_conversations_memory()
    {
        // The scope is the conversation's address, not a name a test made up: memory outlives the session, and the
        // Channel address is what a later session will still be able to say.
        var channel = new Address("channel", "conversation", "chat-1");
        await _store.AddAsync(new MemoryDraft(channel.ToString(), MemoryKind.Fragment, "部署改到周五了", At: DateTimeOffset.UtcNow), default);
        var module = new MemoryModule(_store, _extensions);
        Assert.True((await module.InitializeAsync(default)).Ok);
        var request = new RoundContextRequest("s-1", Guid.NewGuid(), 1, channel);

        var line = await _extensions.Floating.Single().CollectAsync(request, default);
        var tools = await _extensions.Tools.Single().ToolsAsync(request, default);

        Assert.True(line.Ok, line.Error?.Message);
        var section = Assert.Single(line.Value!);
        Assert.Contains("1 条碎片", section.Content, StringComparison.Ordinal);
        // The floating line says there is something to recall; it does not paste the store into the request.
        Assert.DoesNotContain("部署改到周五了", section.Content, StringComparison.Ordinal);
        Assert.True(tools.Ok, tools.Error?.Message);
        Assert.Equal(["remember", "recall", "forget"], tools.Value!.Select(offer => offer.Tool.Name).ToArray());
        // Writing memory down is not speaking to anyone: the loop must not treat a turn that only did this as a reply.
        Assert.All(tools.Value!, offer => Assert.False(offer.ActsExternally));
    }

    private sealed class StubTools(string id) : ISessionToolSource
    {
        public string Id { get; } = id;
        public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(
            RoundContextRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success([]));
    }
}
