using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The memory module: one line per round (the group names) and two tools, both registered or neither.
///
/// The shape under test is the split, not the storage: the module owns "what a round is told" and "what a round can
/// call", and the library owns the files. A module that registered half of itself would tell the model that memory
/// exists while leaving it no way to open any — which is worse than memory being absent.
/// </summary>
public sealed class MemoryModuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-memory-module-" + Guid.NewGuid().ToString("N"));
    private readonly SessionExtensionRegistry _extensions = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private FileMemoryLibrary Library()
    {
        var access = new LocalFileSystemAccess(_root, [FileScopes.Memory]);
        return new FileMemoryLibrary(access);
    }

    [Fact]
    public async Task The_module_registers_both_ways_into_a_round_and_takes_them_back_when_it_stops()
    {
        var module = new MemoryModule(Library(), _extensions);
        Assert.Empty(_extensions.Floating);
        Assert.Empty(_extensions.Tools);

        Assert.True((await module.InitializeAsync(default)).Ok);

        // One module, two surfaces: a line for every round, and the tools that make the line useful.
        Assert.Single(_extensions.Floating);
        Assert.Single(_extensions.Tools);
        Assert.True((await module.StopAsync(default)).Ok);
        Assert.Empty(_extensions.Floating);
        Assert.Empty(_extensions.Tools);
    }

    [Fact]
    public async Task A_module_that_cannot_register_all_of_itself_registers_none_of_itself()
    {
        // Something else already holds the tools' id: half a module would offer a line naming a tool that is not
        // there, which is a promise the model cannot keep.
        Assert.True(_extensions.Register(new StubTools(MemoryTools.SourceId)).Ok);
        var module = new MemoryModule(Library(), _extensions);

        var initialized = await module.InitializeAsync(default);

        Assert.False(initialized.Ok);
        Assert.Empty(_extensions.Floating);
    }

    [Fact]
    public async Task The_line_names_every_group_and_none_of_their_contents()
    {
        var library = Library();
        Assert.True((await library.AppendLineAsync("关系-小林", "他说部署改到周五", "小林", null, default)).Ok);
        Assert.True((await library.AppendLineAsync("项目-ALN", "合并记忆与文档两层", null, null, default)).Ok);
        var module = new MemoryModule(library, _extensions);
        Assert.True((await module.InitializeAsync(default)).Ok);

        var line = await _extensions.Floating.Single().CollectAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default);
        Assert.True(line.Ok, line.Error?.Message);
        var section = Assert.Single(line.Value!);

        Assert.Contains("关系-小林", section.Content, StringComparison.Ordinal);
        Assert.Contains("项目-ALN", section.Content, StringComparison.Ordinal);
        Assert.Contains("2 组", section.Content, StringComparison.Ordinal);
        // The names are the point: the bodies stay out until somebody opens a group.
        Assert.DoesNotContain("部署改到周五", section.Content, StringComparison.Ordinal);

        var tools = await _extensions.Tools.Single().ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default);
        Assert.Equal(["remember", "read_memory", "search_memory", "forget"],
            tools.Value!.Select(offer => offer.Tool.Name).ToArray());
        // Writing memory down is not speaking to anyone: the loop must not treat a turn that only did this as a reply.
        Assert.All(tools.Value!, offer => Assert.False(offer.ActsExternally));
    }

    [Fact]
    public async Task An_empty_library_contributes_no_line_at_all()
    {
        var module = new MemoryModule(Library(), _extensions);
        Assert.True((await module.InitializeAsync(default)).Ok);

        var line = await _extensions.Floating.Single().CollectAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default);
        Assert.True(line.Ok);
        Assert.Empty(line.Value!);
    }

    [Fact]
    public async Task A_group_that_has_piled_up_is_named_in_the_line_so_someone_can_act_on_it()
    {
        var library = Library();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < MemoryLimits.LineWarning; index++)
            Assert.True((await library.AppendLineAsync("运维", $"第 {index} 条", null, null, default)).Ok);
        _ = now;

        var module = new MemoryModule(library, _extensions);
        Assert.True((await module.InitializeAsync(default)).Ok);
        var line = await _extensions.Floating.Single().CollectAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default);

        var content = Assert.Single(line.Value!).Content;
        Assert.Contains("该折叠", content, StringComparison.Ordinal);
        Assert.Contains("运维", content, StringComparison.Ordinal);
    }

    private sealed class StubTools(string id) : ISessionToolSource
    {
        public string Id { get; } = id;
        public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(
            RoundContextRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success([]));
    }
}
