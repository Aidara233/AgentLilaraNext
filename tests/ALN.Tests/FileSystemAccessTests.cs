using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The controlled file system: what a module is allowed to reach, and what a caller is allowed to say.
///
/// These pin the two refusals apart on purpose. A path that is merely wrong (<c>../x</c>, <c>C:\x</c>, <c>con</c>) is
/// <see cref="ErrorCode.Invalid"/> and the message names the segment, because a model can fix that on its next call.
/// A scope this caller may not touch is <see cref="ErrorCode.Denied"/>, because that is not a typo.
/// </summary>
public sealed class FileSystemAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-fs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LocalFileSystemAccess Open(params string[] scopes) =>
        new(_root, scopes.Length == 0 ? ["memory", "workspace"] : scopes);

    // ---- the path rule -------------------------------------------------------------------------

    [Theory]
    [InlineData("关系-小林/block.md")]
    [InlineData("a/b/c.txt")]
    [InlineData("a\\b\\c.txt")]          // backslash is a separator too, so it cannot mean "one odd file name"
    [InlineData("名字里有空格.md")]
    [InlineData("")]
    public void Legal_paths_split_into_segments(string path)
    {
        var split = FilePaths.Split(path);
        Assert.True(split.Ok, split.Error?.Message);
    }

    [Theory]
    [InlineData("../etc/passwd", "parent")]
    [InlineData("a/../../b", "parent")]
    [InlineData("/absolute", "absolute")]
    [InlineData("\\absolute", "absolute")]
    [InlineData("C:/temp", "drive letter")]
    [InlineData("a:b", "drive letter")]
    [InlineData("ab:c", "not allowed in a name")]
    [InlineData("a//b", "empty segment")]
    [InlineData(".git/config", "starts with a dot")]
    [InlineData("a/./b", "not allowed")]
    [InlineData("name.", "ends with")]
    [InlineData("name ", "ends with")]
    [InlineData("con", "reserved")]
    [InlineData("CON", "reserved")]
    public void Illegal_paths_are_refused_and_say_why(string path, string reason)
    {
        var split = FilePaths.Split(path);
        Assert.False(split.Ok);
        Assert.Equal(ErrorCode.Invalid, split.Error!.Code);
        Assert.Contains(reason, split.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_canonical_spelling_uses_one_separator_and_no_trailing_one()
    {
        Assert.Equal("a/b", FilePaths.Canonical("a\\b\\"));
        Assert.Equal("a/b", FilePaths.Canonical("a/b"));
        Assert.Equal("", FilePaths.Canonical(""));
        Assert.Null(FilePaths.Canonical("../x"));
    }

    // ---- scope refusal is not a path mistake ---------------------------------------------------

    [Fact]
    public async Task A_scope_this_caller_may_not_use_is_denied_not_invalid()
    {
        var access = Open("memory");
        var read = await access.ReadAsync("etc", "passwd", default);
        Assert.False(read.Ok);
        Assert.Equal(ErrorCode.Denied, read.Error!.Code);
        Assert.Contains("etc", read.Error.Message, StringComparison.Ordinal);

        // And a scope that is registered is usable.
        var scopes = access.Scopes;
        Assert.Equal(["memory"], scopes);
    }

    // ---- reading and writing -------------------------------------------------------------------

    [Fact]
    public async Task A_written_file_reads_back_and_its_directories_did_not_exist_before()
    {
        var access = Open();
        var written = await access.WriteAsync("memory", "关系-小林/block.md", "第一版", default);
        Assert.True(written.Ok, written.Error?.Detail ?? written.Error?.Message);
        Assert.Equal("关系-小林/block.md", written.Value!.Path);
        Assert.Equal("第一版", written.Value.Text);
        Assert.Equal(9, written.Value.Bytes);                       // 三个汉字 = 9 字节，不是 3

        var read = await access.ReadAsync("memory", "关系-小林/block.md", default);
        Assert.True(read.Ok);
        Assert.Equal("第一版", read.Value!.Text);
    }

    [Fact]
    public async Task Writing_replaces_the_whole_file_and_leaves_no_temporary_behind()
    {
        var access = Open();
        Assert.True((await access.WriteAsync("memory", "a.md", "旧的这一篇很长很长", default)).Ok);
        Assert.True((await access.WriteAsync("memory", "a.md", "新", default)).Ok);

        Assert.Equal("新", (await access.ReadAsync("memory", "a.md", default)).Value!.Text);
        var left = Directory.GetFiles(Path.Combine(_root, "memory"), "*", SearchOption.AllDirectories);
        Assert.Single(left);
        Assert.EndsWith("a.md", left[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_something_that_is_not_there_is_null_and_not_an_error()
    {
        var access = Open();
        var read = await access.ReadAsync("memory", "还没有/也没有.md", default);
        Assert.False(read.Ok);                 // Result<T>.Ok is false when the value is null
        Assert.Null(read.Error);               // ...but absence is not a failure
        Assert.Null(read.Value);

        Assert.Null((await access.StatAsync("memory", "nope", default)).Value);
    }

    [Fact]
    public async Task Reading_a_directory_as_a_file_is_refused()
    {
        var access = Open();
        Assert.True((await access.WriteAsync("memory", "组/block.md", "x", default)).Ok);
        var read = await access.ReadAsync("memory", "组", default);
        Assert.False(read.Ok);
        Assert.Equal(ErrorCode.Invalid, read.Error!.Code);
    }

    [Fact]
    public async Task Appending_adds_and_creates_the_file()
    {
        var access = Open();
        Assert.Equal(4, (await access.AppendAsync("memory", "组/lines.jsonl", "一\n", default)).Value);
        Assert.Equal(8, (await access.AppendAsync("memory", "组/lines.jsonl", "二\n", default)).Value);
        Assert.Equal("一\n二\n", (await access.ReadAsync("memory", "组/lines.jsonl", default)).Value!.Text);
    }

    [Fact]
    public async Task Concurrent_appends_do_not_interleave()
    {
        // The reason this test exists: a fragment line is written while other turns are writing too. An interleaved
        // write would not fail — it would silently produce a corrupt line, which is worse.
        var access = Open();
        var writers = Enumerable.Range(0, 40)
            .Select(index => Task.Run(async () =>
                await access.AppendAsync("memory", "组/lines.jsonl", $"第{index}条\n", default)))
            .ToArray();
        var results = await Task.WhenAll(writers);
        Assert.All(results, result => Assert.True(result.Ok, result.Error?.Detail));

        var text = (await access.ReadAsync("memory", "组/lines.jsonl", default)).Value!.Text;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(40, lines.Length);
        // Every line is exactly one writer's text: no half-line, no spliced pair.
        Assert.All(lines, line => Assert.Matches(@"^第\d+条$", line));
    }

    // ---- moving and deleting --------------------------------------------------------------------

    [Fact]
    public async Task Moving_renames_and_refuses_an_occupied_destination()
    {
        var access = Open();
        Assert.True((await access.WriteAsync("memory", "组/lines.jsonl", "旧", default)).Ok);
        Assert.True((await access.WriteAsync("memory", "组/history/lines-2026-11.jsonl", "占位", default)).Ok);

        var occupied = await access.MoveAsync("memory", "组/lines.jsonl", "组/history/lines-2026-11.jsonl", default);
        Assert.False(occupied.Ok);
        Assert.Equal(ErrorCode.Conflict, occupied.Error!.Code);

        var missingParent = await access.MoveAsync("memory", "组/lines.jsonl", "没建过/x.jsonl", default);
        Assert.False(missingParent.Ok);
        Assert.Equal(ErrorCode.Invalid, missingParent.Error!.Code);

        Assert.True((await access.MoveAsync("memory", "组/lines.jsonl", "组/history/lines-2026-10.jsonl", default)).Ok);
        Assert.Null((await access.ReadAsync("memory", "组/lines.jsonl", default)).Value);
        Assert.Equal("旧", (await access.ReadAsync("memory", "组/history/lines-2026-10.jsonl", default)).Value!.Text);
    }

    [Fact]
    public async Task Deleting_a_file_is_forgiving_but_deleting_a_subtree_has_to_be_asked_for()
    {
        var access = Open();
        Assert.True((await access.WriteAsync("memory", "组/a/b.md", "x", default)).Ok);

        // Deleting what is not there is a statement, not a request to check.
        Assert.True((await access.DeleteAsync("memory", "组/不存在.md", false, default)).Ok);

        var notEmpty = await access.DeleteAsync("memory", "组", false, default);
        Assert.False(notEmpty.Ok);
        Assert.Equal(ErrorCode.Conflict, notEmpty.Error!.Code);

        Assert.True((await access.DeleteAsync("memory", "组", true, default)).Ok);
        Assert.Null((await access.StatAsync("memory", "组", default)).Value);
    }

    [Fact]
    public async Task A_scope_root_cannot_be_deleted()
    {
        var access = Open();
        var refused = await access.DeleteAsync("memory", "", true, default);
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);
    }

    // ---- listing and walking --------------------------------------------------------------------

    [Fact]
    public async Task A_listing_is_one_directory_and_a_walk_is_the_whole_subtree()
    {
        var access = Open();
        Assert.True((await access.WriteAsync("memory", "组/block.md", "块", default)).Ok);
        Assert.True((await access.WriteAsync("memory", "组/history/lines-2026-11.jsonl", "{}\n", default)).Ok);

        var listing = await access.ListAsync("memory", "组", default);
        Assert.True(listing.Ok, listing.Error?.Message);
        var entries = listing.Value!;
        Assert.Equal(["block.md", "history"], entries.Select(entry => entry.Name));
        Assert.False(entries[0].IsDirectory);
        Assert.True(entries[1].IsDirectory);

        var tree = await access.TreeAsync("memory", "组", default);
        Assert.True(tree.Ok, tree.Error?.Message);
        var nodes = tree.Value!;
        Assert.Equal(["block.md", "history", "history/lines-2026-11.jsonl"], nodes.Select(node => node.Path));
        Assert.Equal(3, nodes[2].Bytes);                       // "{}\n"
    }

    [Fact]
    public async Task A_walk_can_start_at_the_scope_root()
    {
        var access = Open();
        Assert.True((await access.WriteAsync("memory", "组/block.md", "x", default)).Ok);
        var tree = await access.TreeAsync("memory", "", default);
        Assert.True(tree.Ok);
        Assert.Equal(["组", "组/block.md"], tree.Value!.Select(node => node.Path));
    }

    [Fact]
    public async Task Listing_a_directory_that_does_not_exist_is_an_error_rather_than_an_empty_answer()
    {
        // "Nothing is there" and "there is no such place" are different facts, and a caller that cannot tell them
        // apart will happily create a group it already has.
        var access = Open();
        var listing = await access.ListAsync("memory", "没建过", default);
        Assert.False(listing.Ok);
        Assert.Equal(ErrorCode.Invalid, listing.Error!.Code);
    }

    [Fact]
    public async Task Binary_or_invalid_utf8_is_refused_rather_than_read_as_mojibake()
    {
        var access = Open();
        var path = Path.Combine(_root, "memory", "bin.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [0x00, 0x41, 0x42]);
        var read = await access.ReadAsync("memory", "bin.dat", default);
        Assert.False(read.Ok);
        Assert.Null(read.Value);
        Assert.Equal(ErrorCode.Invalid, read.Error!.Code);
    }
}
