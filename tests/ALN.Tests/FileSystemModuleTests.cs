using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The file system module: which areas a model may touch, and what it is told when it picks one that it may not.
///
/// The whitelist is configuration, so the interesting failures are configuration failures — a typo in a scope name,
/// a config that is not JSON, a config that names everything (which means "nothing was decided"). All of them are
/// refused at startup, where a person can see it, rather than becoming "the model has no files".
/// </summary>
public sealed class FileSystemModuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-fsmod-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LocalFileSystemAccess Access(params string[] scopes) =>
        new(_root, scopes.Length == 0 ? [FileScopes.Memory] : scopes);

    private static ModuleManifest Manifest(string? config) =>
        new(new ModuleId(FileSystemModule.TypeId), FileSystemModule.TypeId, Enabled: true, ConfigJson: config);

    private static RoundContextRequest Request() => new("s-1", Guid.NewGuid(), 1);

    [Fact]
    public void The_whitelist_comes_from_the_modules_own_configuration()
    {
        var extensions = new SessionExtensionRegistry();
        var built = FileSystemModule.FromManifest(Manifest("""{"scopes":["memory"]}"""), Access(), extensions);
        Assert.True(built.Ok, built.Error?.Message);
        var module = Assert.IsType<FileSystemModule>(built.Value);
        Assert.Equal([FileScopes.Memory], module.Scopes);
    }

    [Fact]
    public void A_scope_that_does_not_exist_is_refused_instead_of_dropped()
    {
        // Dropping it would look exactly like a model with nothing to read — a silent "no files" where the truth is
        // "you typed the name wrong".
        var built = FileSystemModule.FromManifest(Manifest("""{"scopes":["memroy"]}"""), Access(), new SessionExtensionRegistry());
        Assert.False(built.Ok);
        Assert.Equal(ErrorCode.Invalid, built.Error!.Code);
        Assert.Contains("memroy", built.Error.Message, StringComparison.Ordinal);
        Assert.Contains(FileScopes.Memory, built.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all", "not valid JSON")]
    [InlineData("""{"scopes":"memory"}""", "must be an array")]
    [InlineData("""{"scopes":[1]}""", "non-empty string")]
    [InlineData("""{"scopes":["  "]}""", "non-empty string")]
    public void A_config_that_cannot_mean_anything_is_refused(string config, string reason)
    {
        var built = FileSystemModule.FromManifest(Manifest(config), Access(), new SessionExtensionRegistry());
        Assert.False(built.Ok);
        Assert.Equal(ErrorCode.Invalid, built.Error!.Code);
        Assert.Contains(reason, built.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_module_without_a_configuration_offers_nothing_rather_than_everything()
    {
        // The default of a missing decision is "no access", not "all of it".
        var module = Assert.IsType<FileSystemModule>(
            FileSystemModule.FromManifest(Manifest(null), Access(), new SessionExtensionRegistry()).Value);
        Assert.Empty(module.Scopes);
    }

    [Fact]
    public async Task Registering_the_module_offers_the_tools_and_stopping_takes_them_back()
    {
        var extensions = new SessionExtensionRegistry();
        var module = Assert.IsType<FileSystemModule>(
            FileSystemModule.FromManifest(Manifest("""{"scopes":["memory"]}"""), Access(), extensions).Value);

        Assert.True((await module.InitializeAsync(default)).Ok);
        var source = Assert.Single(extensions.Tools);
        var offers = (await source.ToolsAsync(Request(), default)).Value!;
        Assert.Equal(
            ["fs_tree", "fs_list", "fs_read", "fs_write", "fs_append", "fs_move", "fs_remove"],
            offers.Select(offer => offer.Definition.Name));
        // Writing a file is not speaking to anyone: the loop must not count these as the turn's reply.
        Assert.All(offers, offer => Assert.False(offer.ActsExternally));

        Assert.True((await module.StopAsync(default)).Ok);
        Assert.Empty(extensions.Tools);
    }

    [Fact]
    public async Task A_module_with_no_scopes_offers_no_tools_at_all()
    {
        var module = Assert.IsType<FileSystemModule>(
            FileSystemModule.FromManifest(Manifest(null), Access(), new SessionExtensionRegistry()).Value);
        Assert.True((await module.InitializeAsync(default)).Ok);

        var extensions = new SessionExtensionRegistry();
        var source = new FileSystemTools(Access(), []);
        Assert.Empty((await source.ToolsAsync(Request(), default)).Value!);
        _ = extensions;
        Assert.True((await module.StopAsync(default)).Ok);
    }

    [Fact]
    public async Task A_module_declared_the_way_the_host_declares_it_gets_its_whitelist()
    {
        // The whole travel of a configuration: written by the host, scanned by the kernel, parsed by the module. This is
        // exactly the chain that broke — the file carried "configJson" (an escaped string) while the scan read
        // "config", so the module came up with an empty whitelist and offered no tools at all. Every other test here
        // started from a hand-built manifest and could not have seen it.
        var store = new ModuleManifestStore(_root);
        Assert.True((await store.WriteIfMissingAsync(
            new ModuleManifest(new ModuleId(FileSystemModule.TypeId), FileSystemModule.TypeId, Enabled: true,
                ConfigJson: FileSystemModule.DefaultConfig), default)).Ok);

        var scan = await store.ScanAsync(types: null, default);
        var manifest = Assert.Single(scan.Manifests);
        var built = FileSystemModule.FromManifest(manifest,
            Access(FileScopes.Memory, FileScopes.Workspace, FileScopes.ScriptsDraft, FileScopes.Media),
            new SessionExtensionRegistry());

        Assert.True(built.Ok, built.Error?.Message);
        // The default configuration is what the host writes: the memory library, a workspace for chores, the folder a
        // script is drafted in, and the media area the host fills and the model only reads (docs/28).
        Assert.Equal([FileScopes.Memory, FileScopes.Workspace, FileScopes.ScriptsDraft, FileScopes.Media],
            ((FileSystemModule)built.Value!).Scopes);
    }

    // ---- what the model experiences -------------------------------------------------------------

    [Fact]
    public async Task The_media_area_is_offered_to_the_tools_and_refuses_every_write()
    {
        // Media is a normal scope for reading — the tools find it, list it, and a text file in it reads back — and a
        // closed door for writing. The refusal is the port's, so it holds for every tool at once instead of for the
        // seven handlers somebody remembered to change (docs/28 §2).
        var access = new LocalFileSystemAccess(_root, [FileScopes.Media, FileScopes.Workspace],
            binaryScopes: [FileScopes.Media, FileScopes.Workspace], readOnlyScopes: [FileScopes.Media]);
        Directory.CreateDirectory(Path.Combine(_root, "media", "image"));
        await File.WriteAllTextAsync(Path.Combine(_root, "media", "image", "note.md"), "记一笔");
        var source = new FileSystemTools(access, [FileScopes.Media, FileScopes.Workspace]);

        var listed = await CallAsync(source, "fs_list", """{"scope":"media","path":"image"}""");
        Assert.True(listed.Ok, listed.Error?.Message);
        Assert.Contains("note.md", listed.Value!.Content, StringComparison.Ordinal);
        var read = await CallAsync(source, "fs_read", """{"scope":"media","path":"image/note.md"}""");
        Assert.Equal("记一笔", read.Value!.Content);

        foreach (var (tool, arguments) in new[]
                 {
                     ("fs_write", """{"scope":"media","path":"image/x.md","text":"写"}"""),
                     ("fs_append", """{"scope":"media","path":"image/note.md","text":"补"}"""),
                     ("fs_remove", """{"scope":"media","path":"image/note.md"}"""),
                     ("fs_move", """{"scope":"media","from":"image/note.md","to":"image/y.md"}""")
                 })
        {
            var refused = await CallAsync(source, tool, arguments);
            Assert.False(refused.Ok, tool);
            Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
            Assert.Contains("read-only", refused.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("记一笔", await File.ReadAllTextAsync(Path.Combine(_root, "media", "image", "note.md")));
    }

    private static async Task<Result<SessionToolResult>> CallAsync(ISessionToolSource source, string tool, string arguments)
    {
        var offer = (await source.ToolsAsync(Request(), default)).Value!.Single(o => o.Definition.Name == tool);
        return await offer.Tool.ExecuteAsync(new ModelToolCall("call-1", tool, arguments), default);
    }

    [Fact]
    public async Task A_file_written_through_the_tools_reads_back_through_the_tools()
    {
        var source = new FileSystemTools(Access(), [FileScopes.Memory]);

        var written = await CallAsync(source, "fs_write",
            """{"scope":"memory","path":"关系-小林/block.md","text":"结论：部署改到周五"}""");
        Assert.True(written.Ok, written.Error?.Message);
        Assert.Contains("关系-小林/block.md", written.Value!.Content, StringComparison.Ordinal);

        var read = await CallAsync(source, "fs_read", """{"scope":"memory","path":"关系-小林/block.md"}""");
        Assert.Equal("结论：部署改到周五", read.Value!.Content);

        var tree = await CallAsync(source, "fs_tree", """{"scope":"memory","path":"关系-小林"}""");
        Assert.Contains("block.md", tree.Value!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_that_tries_to_leave_the_scope_is_refused_with_a_reason()
    {
        var source = new FileSystemTools(Access(), [FileScopes.Memory]);
        var refused = await CallAsync(source, "fs_read", """{"scope":"memory","path":"../../../etc/passwd"}""");
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);
        // A model that is told why can fix it on the next call; "denied" alone just gets tried again.
        Assert.Contains("parent", refused.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_workspace_is_a_normal_contained_scope_where_a_model_may_make_a_mess()
    {
        // The workspace exists so that half-finished things have somewhere to go that is not the record. It is not a
        // special case in the port: same refusal of "..", same rules, one more registered directory — and the only one
        // whose contents nothing else in this process reads.
        var source = new FileSystemTools(Access(FileScopes.Memory, FileScopes.Workspace),
            [FileScopes.Memory, FileScopes.Workspace]);

        var written = await CallAsync(source, "fs_write",
            """{"scope":"workspace","path":"草稿/分析.md","text":"先写到这儿，等确认再进记忆"}""");
        Assert.True(written.Ok, written.Error?.Message);
        var read = await CallAsync(source, "fs_read", """{"scope":"workspace","path":"草稿/分析.md"}""");
        Assert.Equal("先写到这儿，等确认再进记忆", read.Value!.Content);

        // Deleting it is not a loss, so `fs_remove` on the whole subtree is allowed here — that is what a scratch room is.
        var removed = await CallAsync(source, "fs_remove", """{"scope":"workspace","path":"草稿","recursive":true}""");
        Assert.True(removed.Ok, removed.Error?.Message);
        var gone = await CallAsync(source, "fs_read", """{"scope":"workspace","path":"草稿/分析.md"}""");
        Assert.Contains("不存在", gone.Value!.Content, StringComparison.Ordinal);

        // Same containment as anywhere else: the workspace is a room, not an exit.
        var refused = await CallAsync(source, "fs_read", """{"scope":"workspace","path":"../memory/block.md"}""");
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);
    }

    [Fact]
    public async Task A_scope_outside_the_whitelist_is_refused_and_says_which_ones_exist()
    {
        var source = new FileSystemTools(Access(), [FileScopes.Memory]);
        var refused = await CallAsync(source, "fs_list", """{"scope":"workspace","path":""}""");
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Contains(FileScopes.Memory, refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_file_is_read_in_slices_and_the_answer_says_where_to_continue()
    {
        // 32k characters used to be the end of the road: the answer said "已截断" and stopped, so a session that needed
        // the rest of a log had no move left but to guess. The slice is still there — a context window is a real budget
        // — but it now comes with the number that continues it, so "too long to read at once" is no longer "unreadable".
        var body = string.Concat(Enumerable.Repeat("0123456789", 8_000)); // 80 000 characters
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        await File.WriteAllTextAsync(Path.Combine(_root, "workspace", "run.log"), body);
        var source = new FileSystemTools(Access(FileScopes.Workspace), [FileScopes.Workspace]);

        var first = await CallAsync(source, "fs_read", """{"scope":"workspace","path":"run.log"}""");
        Assert.True(first.Ok, first.Error?.Message);
        // Where it stopped, how much there is, and the argument that reads on.
        Assert.Contains("字符 0–32000", first.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("共 80000 个", first.Value.Content, StringComparison.Ordinal);
        Assert.Contains("offset=32000", first.Value.Content, StringComparison.Ordinal);

        var second = await CallAsync(source, "fs_read",
            """{"scope":"workspace","path":"run.log","offset":32000,"maxChars":1000}""");
        Assert.StartsWith("0123456789", second.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("字符 32000–33000", second.Value.Content, StringComparison.Ordinal);

        // Zero means the rest, the same spelling the transfer ceilings use.
        var rest = await CallAsync(source, "fs_read",
            """{"scope":"workspace","path":"run.log","offset":64000,"maxChars":0}""");
        Assert.Contains("已经读到结尾", rest.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("字符 64000–80000", rest.Value.Content, StringComparison.Ordinal);

        // A file that fits comes back whole and unadorned, exactly as before: no footer to parse for the common case.
        await File.WriteAllTextAsync(Path.Combine(_root, "workspace", "short.txt"), "两行\n就够了");
        var whole = await CallAsync(source, "fs_read", """{"scope":"workspace","path":"short.txt"}""");
        Assert.Equal("两行\n就够了", whole.Value!.Content);
    }

    [Fact]
    public async Task An_offset_past_the_end_says_so_instead_of_returning_an_empty_string()
    {
        var source = new FileSystemTools(Access(), [FileScopes.Memory]);
        Assert.True((await CallAsync(source, "fs_write",
            """{"scope":"memory","path":"tiny.md","text":"短"}""")).Ok);

        var past = await CallAsync(source, "fs_read", """{"scope":"memory","path":"tiny.md","offset":99}""");
        Assert.True(past.Ok, past.Error?.Message);
        Assert.Contains("只有 1 个字符", past.Value!.Content, StringComparison.Ordinal);

        var negative = await CallAsync(source, "fs_read", """{"scope":"memory","path":"tiny.md","offset":-1}""");
        Assert.False(negative.Ok);
        Assert.Equal(ErrorCode.Invalid, negative.Error!.Code);
    }

    [Fact]
    public async Task Reading_a_file_that_is_not_there_says_so_instead_of_returning_nothing()
    {
        var source = new FileSystemTools(Access(), [FileScopes.Memory]);
        var read = await CallAsync(source, "fs_read", """{"scope":"memory","path":"没有/这一篇.md"}""");
        Assert.True(read.Ok, read.Error?.Message);
        Assert.Contains("不存在", read.Value!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arguments_that_are_not_json_are_refused_by_name()
    {
        var source = new FileSystemTools(Access(), [FileScopes.Memory]);
        var refused = await CallAsync(source, "fs_read", "oops");
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);
        Assert.Contains("fs_read", refused.Error.Message, StringComparison.Ordinal);
    }
}
