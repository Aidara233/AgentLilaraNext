using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The four memory tools as a model meets them: write a fragment, open a group, find one by what it says, drop one.
///
/// The interesting cases are the two that used to be missing. Finding a fragment is not "read the whole file": the
/// ranking is the point, and a query that matches nothing must say so rather than return the newest lines. Dropping one
/// is not "delete the group": it removes exactly one line from whichever file holds it, and leaves a block's quote
/// visibly unanswered instead of quietly letting a conclusion keep its backing.
/// </summary>
public sealed class MemoryToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-memtools-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (FileMemoryLibrary Library, MemoryTools Tools) Open()
    {
        var access = new LocalFileSystemAccess(_root, [FileScopes.Memory]);
        var library = new FileMemoryLibrary(access);
        return (library, new MemoryTools(library));
    }

    private static async Task<Result<SessionToolResult>> CallAsync(MemoryTools tools, string tool, string arguments)
    {
        var offers = (await tools.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default)).Value!;
        var offer = offers.Single(candidate => candidate.Definition.Name == tool);
        return await offer.Tool.ExecuteAsync(new ModelToolCall("call-1", tool, arguments), default);
    }

    [Fact]
    public async Task The_four_tools_are_offered_and_none_of_them_speaks_to_anyone()
    {
        var (_, tools) = Open();
        var offers = (await tools.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default)).Value!;
        Assert.Equal(["remember", "read_memory", "search_memory", "forget"],
            offers.Select(offer => offer.Definition.Name));
        Assert.All(offers, offer => Assert.False(offer.ActsExternally));
    }

    [Fact]
    public async Task A_search_finds_by_what_was_said_and_not_by_being_recent()
    {
        var (_, tools) = Open();
        Assert.True((await CallAsync(tools, "remember",
            """{"group":"关系-小林","text":"部署改到周五，因为周四客户冻结","who":"小林(同事)"}""")).Ok);
        Assert.True((await CallAsync(tools, "remember",
            """{"group":"关系-小林","text":"他问要不要一起吃晚饭"}""")).Ok);

        var found = await CallAsync(tools, "search_memory", """{"group":"关系-小林","query":"部署"}""");
        Assert.True(found.Ok, found.Error?.Message);
        Assert.Contains("部署改到周五", found.Value!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("晚饭", found.Value.Content, StringComparison.Ordinal);
        Assert.Contains("小林(同事)", found.Value.Content, StringComparison.Ordinal);
        Assert.Contains("共 2 条碎片", found.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_that_matches_nothing_says_so_instead_of_listing_the_recent_ones()
    {
        var (_, tools) = Open();
        Assert.True((await CallAsync(tools, "remember", """{"group":"组","text":"部署改到周五"}""")).Ok);

        var found = await CallAsync(tools, "search_memory", """{"group":"组","query":"火箭发射"}""");
        Assert.True(found.Ok, found.Error?.Message);
        Assert.Contains("没有和「火箭发射」对得上", found.Value!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("部署改到周五", found.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_query_returns_the_recent_fragments()
    {
        var (_, tools) = Open();
        Assert.True((await CallAsync(tools, "remember", """{"group":"组","text":"第一条"}""")).Ok);
        Assert.True((await CallAsync(tools, "remember", """{"group":"组","text":"第二条"}""")).Ok);

        var found = await CallAsync(tools, "search_memory", """{"group":"组"}""");
        Assert.Contains("最近的 2 条碎片", found.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("第二条", found.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dropping_one_fragment_leaves_the_rest_and_tells_the_truth_about_a_quote()
    {
        var (library, tools) = Open();
        var kept = (await library.AppendLineAsync("组", "这条是对的", null, null, default)).Value!.Line;
        var wrong = (await library.AppendLineAsync("组", "这条记错了", null, null, default)).Value!.Line;
        Assert.True((await library.WriteBlockAsync("组",
            $"部署在周五。见 {MemoryQuotes.Spell(wrong.Id)} 与 {MemoryQuotes.Spell(kept.Id)}", default)).Ok);

        var dropped = await CallAsync(tools, "forget", $$"""{"group":"组","id":"{{wrong.Id}}"}""");
        Assert.True(dropped.Ok, dropped.Error?.Message);
        Assert.Contains("忘了", dropped.Value!.Content, StringComparison.Ordinal);

        // The line is gone...
        var found = await CallAsync(tools, "search_memory", """{"group":"组","query":"记错"}""");
        Assert.Contains("没有和", found.Value!.Content, StringComparison.Ordinal);
        // ...the other one is not...
        Assert.Equal("这条是对的", (await library.ReadLinesAsync("组", [kept.Id], default)).Value!.Single().Text);
        // ...and the body's quote says so instead of looking backed.
        var read = await CallAsync(tools, "read_memory", """{"group":"组"}""");
        Assert.Contains("引用的这一行已经不在了", read.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("这条是对的", read.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dropping_what_is_not_there_says_so_instead_of_failing()
    {
        var (_, tools) = Open();
        Assert.True((await CallAsync(tools, "remember", """{"group":"组","text":"一句话"}""")).Ok);
        var dropped = await CallAsync(tools, "forget", """{"group":"组","id":"l-deadbeef"}""");
        Assert.True(dropped.Ok, dropped.Error?.Message);
        Assert.Contains("没有 l-deadbeef", dropped.Value!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fragment_moved_into_history_can_still_be_found_and_dropped()
    {
        // Folding moves the active file away; the fragments have to stay reachable by the same tools, or "archive it"
        // would mean "forget it" with extra steps.
        var (library, tools) = Open();
        var line = (await library.AppendLineAsync("组", "归档之后还要能找到", null, null, default)).Value!.Line;
        var access = new LocalFileSystemAccess(_root, [FileScopes.Memory]);
        var json = (await access.ReadAsync(FileScopes.Memory, "组/lines.jsonl", default)).Value!.Text;
        Assert.True((await access.WriteAsync(FileScopes.Memory, "组/history/lines-2026-11.jsonl", json, default)).Ok);
        Assert.True((await access.DeleteAsync(FileScopes.Memory, "组/lines.jsonl", false, default)).Ok);

        var found = await CallAsync(tools, "search_memory", """{"group":"组","query":"归档"}""");
        Assert.Contains(line.Id, found.Value!.Content, StringComparison.Ordinal);
        Assert.True((await CallAsync(tools, "forget", $$"""{"group":"组","id":"{{line.Id}}"}""")).Ok);
        Assert.Empty((await library.ReadLinesAsync("组", [line.Id], default)).Value!);
    }
}
