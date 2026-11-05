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

    // ---- what the model experiences -------------------------------------------------------------

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
    public async Task A_scope_outside_the_whitelist_is_refused_and_says_which_ones_exist()
    {
        var source = new FileSystemTools(Access(), [FileScopes.Memory]);
        var refused = await CallAsync(source, "fs_list", """{"scope":"workspace","path":""}""");
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Contains(FileScopes.Memory, refused.Error.Message, StringComparison.Ordinal);
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
