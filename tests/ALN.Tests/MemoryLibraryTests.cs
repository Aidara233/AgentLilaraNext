using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The memory library as a directory: a group is a folder, a block is its body, fragments are lines.
///
/// The tests concentrate on the two things that make this shape worth having. First, a fragment can be quoted by a
/// block and still resolve after it has been folded away into <c>history/</c> — that is what lets consolidation delete
/// the active file without deleting the evidence. Second, everything the files could be mangled into (a bad line, a
/// missing block, a stray file in the root) is survived and reported, because these files are edited by a model.
/// </summary>
public sealed class MemoryLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-memlib-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (IFileSystemAccess Access, FileMemoryLibrary Library) Open()
    {
        var access = new LocalFileSystemAccess(_root, [FileScopes.Memory]);
        return (access, new FileMemoryLibrary(access));
    }

    [Fact]
    public async Task Appending_a_fragment_creates_the_group_and_says_it_did()
    {
        var (_, library) = Open();
        var first = await library.AppendLineAsync("关系-小林", "他说部署改到周五", "小林(同事)", "channel.conversation#chat-1", default);
        Assert.True(first.Ok, first.Error?.Message);
        Assert.True(first.Value!.CreatedGroup);
        Assert.Equal("他说部署改到周五", first.Value.Line.Text);
        Assert.Equal("小林(同事)", first.Value.Line.Who);
        Assert.StartsWith("l-", first.Value.Line.Id, StringComparison.Ordinal);
        Assert.Null(first.Value.Note);                          // one line is not a pile yet

        var second = await library.AppendLineAsync("关系-小林", "他又提了一次", null, null, default);
        Assert.True(second.Ok);
        Assert.False(second.Value!.CreatedGroup);

        var groups = await library.GroupsAsync(default);
        Assert.True(groups.Ok, groups.Error?.Message);
        var group = Assert.Single(groups.Value!);
        Assert.Equal("关系-小林", group.Name);
        Assert.Equal(2, group.Lines);
        Assert.True(group.Bytes > 0);
        Assert.False(group.NeedsConsolidation);
    }

    [Fact]
    public async Task A_group_that_does_not_exist_is_null_but_a_group_without_a_block_is_not()
    {
        var (_, library) = Open();
        Assert.Null((await library.GroupAsync("没建过", default)).Value);
        Assert.Null((await library.ReadBlockAsync("没建过", default)).Value);

        Assert.True((await library.AppendLineAsync("只有碎片", "一句话", null, null, default)).Ok);
        var block = await library.ReadBlockAsync("只有碎片", default);
        Assert.True(block.Ok);
        Assert.NotNull(block.Value);
        Assert.False(block.Value!.Exists);                     // a state, not a missing group
        Assert.Equal("", block.Value.Text);
    }

    [Fact]
    public async Task A_block_is_written_whole_and_replaced_whole()
    {
        var (_, library) = Open();
        Assert.True((await library.WriteBlockAsync("项目-ALN", "第一版：三件事", default)).Ok);
        var replaced = await library.WriteBlockAsync("项目-ALN", "第二版：只剩一件", default);
        Assert.True(replaced.Ok, replaced.Error?.Message);
        Assert.Equal("第二版：只剩一件", replaced.Value!.Text);

        var read = await library.ReadBlockAsync("项目-ALN", default);
        Assert.Equal("第二版：只剩一件", read.Value!.Text);
        Assert.True(read.Value.Exists);
    }

    [Fact]
    public async Task A_quoted_fragment_resolves_even_after_it_is_folded_into_history()
    {
        // This is the whole reason quotes are ids and not copies: consolidation moves the active file away, and the
        // block's evidence has to survive that.
        var (access, library) = Open();
        var line = (await library.AppendLineAsync("关系-小林", "部署改到周五，因为周四客户冻结", "小林", null, default)).Value!.Line;
        Assert.True((await library.WriteBlockAsync("关系-小林", $"部署推迟到周五（客户冻结）。见 {MemoryQuotes.Spell(line.Id)}", default)).Ok);

        var quoted = MemoryQuotes.Find((await library.ReadBlockAsync("关系-小林", default)).Value!.Text);
        Assert.Equal([line.Id], quoted);
        Assert.Single((await library.ReadLinesAsync("关系-小林", quoted, default)).Value!);

        // Fold: copy the line into history/, drop the active file. The id is the same line in a different place.
        var (_, library2) = Open();
        var json = (await access.ReadAsync(FileScopes.Memory, "关系-小林/lines.jsonl", default)).Value!.Text;
        Assert.True((await access.WriteAsync(FileScopes.Memory, "关系-小林/history/lines-2026-11.jsonl", json, default)).Ok);
        Assert.True((await access.DeleteAsync(FileScopes.Memory, "关系-小林/lines.jsonl", false, default)).Ok);

        var stillFound = await library2.ReadLinesAsync("关系-小林", [line.Id], default);
        Assert.True(stillFound.Ok, stillFound.Error?.Message);
        Assert.Equal("部署改到周五，因为周四客户冻结", Assert.Single(stillFound.Value!).Text);

        // And it is still searchable from the archive.
        var searched = await library2.SearchLinesAsync("关系-小林", "部署", 10, DateTimeOffset.UtcNow, default);
        Assert.Equal(line.Id, Assert.Single(searched.Value!.Hits).Line.Id);
    }

    [Fact]
    public async Task A_line_that_cannot_be_read_is_skipped_and_counted_rather_than_hidden()
    {
        // The files are editable by a model, so a mangled line is a fact about the library — one it has to report,
        // because a silently vanished fragment is exactly the sort of thing nobody notices.
        var (access, library) = Open();
        var good = (await library.AppendLineAsync("组", "这一条是好的", null, null, default)).Value!.Line;
        Assert.True((await access.AppendAsync(FileScopes.Memory, "组/lines.jsonl", "这不是 JSON\n", default)).Ok);
        Assert.True((await access.AppendAsync(FileScopes.Memory, "组/lines.jsonl", """{"id":"l-x","text":"没有时间"}""" + "\n", default)).Ok);

        var searched = await library.SearchLinesAsync("组", "", 10, DateTimeOffset.UtcNow, default);
        Assert.True(searched.Ok, searched.Error?.Message);
        Assert.Equal(good.Id, Assert.Single(searched.Value!.Hits).Line.Id);
        Assert.Equal(2, searched.Value.Skipped);
    }

    [Fact]
    public async Task An_empty_query_returns_recent_fragments_and_a_query_that_matches_nothing_returns_none()
    {
        var (_, library) = Open();
        Assert.True((await library.AppendLineAsync("组", "部署改到周五", null, null, default)).Ok);
        Assert.True((await library.AppendLineAsync("组", "买菜记得买葱", null, null, default)).Ok);

        var recent = await library.SearchLinesAsync("组", "", 10, DateTimeOffset.UtcNow, default);
        Assert.Equal(2, recent.Value!.Hits.Count);

        var matched = await library.SearchLinesAsync("组", "部署", 10, DateTimeOffset.UtcNow, default);
        Assert.Equal("部署改到周五", Assert.Single(matched.Value!.Hits).Line.Text);

        // No match is an empty answer, not "the newest lines dressed up as a match".
        Assert.Empty((await library.SearchLinesAsync("组", "火箭发射", 10, DateTimeOffset.UtcNow, default)).Value!.Hits);
    }

    [Fact]
    public async Task A_file_dropped_in_the_library_root_is_ignored_rather_than_promoted_to_a_group()
    {
        var (access, library) = Open();
        Assert.True((await library.AppendLineAsync("真组", "内容", null, null, default)).Ok);
        Assert.True((await access.WriteAsync(FileScopes.Memory, "散落的.md", "手滑写进来的", default)).Ok);

        var groups = await library.GroupsAsync(default);
        Assert.True(groups.Ok);
        Assert.Equal(["真组"], groups.Value!.Select(group => group.Name));
    }

    [Fact]
    public async Task The_tree_of_a_group_shows_its_block_fragments_and_archive()
    {
        var (access, library) = Open();
        Assert.True((await library.WriteBlockAsync("组", "正文", default)).Ok);
        Assert.True((await library.AppendLineAsync("组", "碎片", null, null, default)).Ok);
        Assert.True((await access.WriteAsync(FileScopes.Memory, "组/history/lines-2026-10.jsonl", "", default)).Ok);

        var tree = await library.TreeAsync("组", default);
        Assert.True(tree.Ok, tree.Error?.Message);
        Assert.Equal(["block.md", "history", "history/lines-2026-10.jsonl", "lines.jsonl"],
            tree.Value!.Select(node => node.Path));
    }

    [Fact]
    public async Task Dropping_a_group_removes_it_and_dropping_it_again_is_fine()
    {
        var (_, library) = Open();
        Assert.True((await library.AppendLineAsync("临时", "用完就删", null, null, default)).Ok);
        Assert.True((await library.DropGroupAsync("临时", default)).Ok);
        Assert.Null((await library.GroupAsync("临时", default)).Value);
        Assert.True((await library.DropGroupAsync("临时", default)).Ok);
    }

    [Theory]
    [InlineData("../逃逸")]
    [InlineData("组/子组")]
    [InlineData("")]
    [InlineData("memory")]
    public async Task A_group_name_that_is_not_one_legal_segment_is_refused(string name)
    {
        var (_, library) = Open();
        var refused = await library.AppendLineAsync(name, "内容", null, null, default);
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);
    }

    [Fact]
    public async Task A_fragment_is_normalised_and_an_empty_one_is_refused()
    {
        var (_, library) = Open();
        var written = await library.AppendLineAsync("组", "  部署   改到\n周五  ", null, null, default);
        Assert.Equal("部署 改到 周五", written.Value!.Line.Text);

        var empty = await library.AppendLineAsync("组", "   ", null, null, default);
        Assert.False(empty.Ok);
        Assert.Equal(ErrorCode.Invalid, empty.Error!.Code);
    }
}
