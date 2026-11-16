using System.Text;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The byte half of the model's file surface: <c>fs_inspect</c> and <c>fs_patch</c>.
///
/// These two exist because "the model can only write text" and "the model can only move text" are different rules, and
/// only the second one was ever load-bearing (docs/24 §7.2). What they must never become is a channel for content the
/// model cannot read back: an inspection returns text about bytes, and an edit takes a position plus a bounded literal
/// (or a range copied out of another file). So the tests here are mostly about the two halves of that line — what is
/// refused, and what the answer says when it is.
/// </summary>
public sealed class BinaryFileToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-bin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LocalFileSystemAccess Access() =>
        new(_root, [FileScopes.Workspace, FileScopes.Memory], [FileScopes.Workspace]);

    private static RoundContextRequest Request() => new("s-1", Guid.NewGuid(), 1);

    private static async Task<Result<SessionToolResult>> CallAsync(ISessionToolSource source, string tool,
        string arguments)
    {
        var offer = (await source.ToolsAsync(Request(), default)).Value!.Single(o => o.Definition.Name == tool);
        return await offer.Tool.ExecuteAsync(new ModelToolCall("call-1", tool, arguments), default);
    }

    private string Workspace => Path.Combine(_root, FileScopes.Workspace);

    private void Given(string name, byte[] content)
    {
        Directory.CreateDirectory(Workspace);
        File.WriteAllBytes(Path.Combine(Workspace, name), content);
    }

    private byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Workspace, name));

    [Fact]
    public async Task Both_tools_are_offered_when_a_scope_accepts_bytes()
    {
        var source = new FileSystemTools(Access(), [FileScopes.Workspace]);
        var names = (await source.ToolsAsync(Request(), default)).Value!.Select(o => o.Definition.Name).ToArray();
        Assert.Contains("fs_inspect", names);
        Assert.Contains("fs_patch", names);

        // A deployment whose port has no byte half offers neither: a tool that can only answer "denied" teaches the
        // model nothing and costs it a round.
        var textOnly = new FileSystemTools(new NoBinary(new LocalFileSystemAccess(_root, [FileScopes.Workspace])),
            [FileScopes.Workspace]);
        var offered = (await textOnly.ToolsAsync(Request(), default)).Value!.Select(o => o.Definition.Name).ToArray();
        Assert.DoesNotContain("fs_inspect", offered);
        Assert.DoesNotContain("fs_patch", offered);
    }

    [Fact]
    public async Task An_inspection_says_what_a_file_is_and_shows_it_as_bytes()
    {
        // A tiny ELF header: enough to be recognisably not text, and to have something to read at a known offset.
        Given("blob.bin", [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                           0x03, 0x00, 0x3e, 0x00, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o']);
        var source = new FileSystemTools(Access(), [FileScopes.Workspace]);

        var summary = await CallAsync(source, "fs_inspect", """{"scope":"workspace","path":"blob.bin"}""");
        Assert.True(summary.Ok, summary.Error?.Message);
        Assert.Contains("25 字节", summary.Value!.Content, StringComparison.Ordinal);
        // The hash is of the file, so it is the same answer every time somebody asks.
        Assert.Matches(@"sha256 [0-9a-f]{64}", summary.Value.Content);
        Assert.Contains("7f454c4602", summary.Value.Content, StringComparison.Ordinal);

        // A dump: addresses on the left, the same bytes as text on the right.
        var dump = await CallAsync(source, "fs_inspect",
            """{"scope":"workspace","path":"blob.bin","mode":"hex","offset":16,"length":9}""");
        Assert.Contains("00000010  03 00 3e 00 68 65 6c 6c  6f", dump.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("|..>.hello", dump.Value.Content, StringComparison.Ordinal);

        // Strings are what makes an opaque file searchable: "which version is in this thing". Two runs separated by a
        // NUL exercise the offset arithmetic — an offset that is off by the length of a run is an answer that sends
        // somebody to the wrong place in a file they cannot read.
        Given("marks.bin", [.. Encoding.ASCII.GetBytes("hello"), 0, 0, .. Encoding.ASCII.GetBytes("world"), 0]);
        var strings = await CallAsync(source, "fs_inspect", """{"scope":"workspace","path":"marks.bin","mode":"strings"}""");
        Assert.Contains("00000000  hello", strings.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("00000007  world", strings.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inspection_never_returns_a_file_as_content()
    {
        // 1 MiB of noise: a summary is a summary, whatever the file weighs. If this ever starts returning bytes, the
        // number below stops being a line count and starts being a context window.
        var noise = new byte[1024 * 1024];
        Random.Shared.NextBytes(noise);
        Given("noise.bin", noise);
        var source = new FileSystemTools(Access(), [FileScopes.Workspace]);

        var summary = await CallAsync(source, "fs_inspect", """{"scope":"workspace","path":"noise.bin"}""");
        Assert.True(summary.Ok, summary.Error?.Message);
        Assert.Contains("1048576 字节", summary.Value!.Content, StringComparison.Ordinal);
        Assert.True(summary.Value.Content.Length < 512, $"a summary of 1 MiB came back as {summary.Value.Content.Length} characters");

        // Asking for a huge dump is clamped rather than obeyed.
        var dump = await CallAsync(source, "fs_inspect",
            """{"scope":"workspace","path":"noise.bin","mode":"hex","length":999999}""");
        Assert.True(dump.Value!.Content.Length < 40_000, "a hex dump is bounded by the request, not by the file");
    }

    [Fact]
    public async Task Overwriting_bytes_leaves_the_rest_of_the_file_alone()
    {
        Given("hdr.bin", Encoding.ASCII.GetBytes("0123456789ABCDEF"));
        var source = new FileSystemTools(Access(), [FileScopes.Workspace]);

        var patched = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"hdr.bin","op":"overwrite","offset":4,"hex":"de ad be ef"}""");
        Assert.True(patched.Ok, patched.Error?.Message);
        Assert.Contains("现在 16 字节", patched.Value!.Content, StringComparison.Ordinal);
        // Replaced, not inserted: the file is the same length and only the four bytes at the offset moved.
        Assert.Equal("30313233DEADBEEF3839414243444546", Convert.ToHexString(Read("hdr.bin")));
    }

    [Fact]
    public async Task Splicing_inserts_and_pushes_the_rest_along()
    {
        Given("hdr.bin", Encoding.ASCII.GetBytes("0123456789"));
        var source = new FileSystemTools(Access(), [FileScopes.Workspace]);

        var spliced = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"hdr.bin","op":"splice","offset":3,"base64":"WFla"}""");
        Assert.True(spliced.Ok, spliced.Error?.Message);
        Assert.Contains("现在 13 字节", spliced.Value!.Content, StringComparison.Ordinal);
        Assert.Equal("012XYZ3456789", Encoding.ASCII.GetString(Read("hdr.bin")));
    }

    [Fact]
    public async Task Appending_grows_a_file_and_appending_from_a_file_never_passes_through_the_conversation()
    {
        Given("a.bin", Encoding.ASCII.GetBytes("head-"));
        Given("b.bin", Encoding.ASCII.GetBytes("tail"));
        var source = new FileSystemTools(Access(), [FileScopes.Workspace]);

        var literal = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"a.bin","op":"append","hex":"4142"}""");
        Assert.True(literal.Ok, literal.Error?.Message);
        Assert.Equal("head-AB", Encoding.ASCII.GetString(Read("a.bin")));

        // The whole-file join is the operation that makes "put the two parts back together" answerable at any size:
        // the bytes go from disk to disk, so neither file's size is a limit here.
        var joined = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"a.bin","op":"append","sourcePath":"b.bin"}""");
        Assert.True(joined.Ok, joined.Error?.Message);
        Assert.Equal("head-ABtail", Encoding.ASCII.GetString(Read("a.bin")));
    }

    [Fact]
    public async Task Splicing_a_range_out_of_another_file_copies_without_naming_the_bytes()
    {
        Given("dst.bin", Encoding.ASCII.GetBytes("<>"));
        Given("src.bin", Encoding.ASCII.GetBytes("XXXXMARKERXXXX"));
        var source = new FileSystemTools(Access(), [FileScopes.Workspace]);

        var spliced = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"dst.bin","op":"splice","offset":1,"sourcePath":"src.bin","sourceOffset":4,"sourceLength":6}""");
        Assert.True(spliced.Ok, spliced.Error?.Message);
        Assert.Equal("<MARKER>", Encoding.ASCII.GetString(Read("dst.bin")));
    }

    [Fact]
    public async Task Truncating_only_shortens_and_says_so_when_asked_to_grow()
    {
        Given("long.bin", Encoding.ASCII.GetBytes("0123456789"));
        var source = new FileSystemTools(Access(), [FileScopes.Workspace]);

        var cut = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"long.bin","op":"truncate","length":4}""");
        Assert.True(cut.Ok, cut.Error?.Message);
        Assert.Equal("0123", Encoding.ASCII.GetString(Read("long.bin")));

        var grow = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"long.bin","op":"truncate","length":100}""");
        Assert.False(grow.Ok);
        Assert.Equal(ErrorCode.Invalid, grow.Error!.Code);
        Assert.Contains("Truncate only shortens", grow.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_literal_larger_than_the_limit_is_refused_with_the_way_around_it()
    {
        Given("big.bin", [1, 2, 3]);
        var source = new FileSystemTools(Access(), [FileScopes.Workspace]);
        var tooMuch = new string('a', 4097 * 2);

        var refused = await CallAsync(source, "fs_patch",
            $$"""{"scope":"workspace","path":"big.bin","op":"append","hex":"{{tooMuch}}"}""");
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);
        // The refusal names the limit and the operation that has no limit, because "too big" without a next step is
        // how a model ends up splitting a file into four hundred tool calls.
        Assert.Contains("4096", refused.Error.Message, StringComparison.Ordinal);
        Assert.Contains("sourcePath", refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_in_a_text_scope_or_on_a_missing_file_is_refused_and_the_refusal_is_usable()
    {
        Given("nothing.bin", []);
        var source = new FileSystemTools(Access(), [FileScopes.Workspace, FileScopes.Memory]);
        Assert.True((await CallAsync(source, "fs_write",
            """{"scope":"memory","path":"block.md","text":"结论：部署改到周五"}""")).Ok);

        // `memory` holds text on purpose: it is the library the model tidies, and a tarball parked there would be
        // bytes nobody can read back (docs/24 §7.1). The refusal names the scopes that do take bytes, so the model
        // has somewhere to go instead of trying the same call twice.
        var memory = await CallAsync(source, "fs_patch",
            """{"scope":"memory","path":"block.md","op":"append","hex":"00"}""");
        Assert.False(memory.Ok);
        Assert.Contains("does not allow binary transfers", memory.Error!.Message, StringComparison.Ordinal);

        // A patch of something that is not there is a silent create if you let it through, so it is refused instead.
        var missing = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"nothing-here.bin","op":"append","hex":"00"}""");
        Assert.False(missing.Ok);
        Assert.Contains("does not exist", missing.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_patch_past_the_end_says_where_the_end_is()
    {
        Given("short.bin", [1, 2, 3]);
        var source = new FileSystemTools(Access(), [FileScopes.Workspace]);

        var past = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"short.bin","op":"overwrite","offset":99,"hex":"00"}""");
        Assert.False(past.Ok);
        Assert.Contains("3 bytes", past.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("offset 99", past.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Media_is_readable_through_the_tools_and_still_not_writable()
    {
        // `media` is the third kind of scope: the model may read it, look at what is in it and copy bytes out of it, and
        // may never write to it — a session's record names the files it looked at, so a caller that could rewrite one
        // could change what it is recorded as having seen (docs/28 §2). Read access is what makes "look at the picture
        // I sent yesterday" and "send that one again" answerable, so both halves of that are checked here.
        var access = new LocalFileSystemAccess(_root, [FileScopes.Workspace, FileScopes.Media],
            binaryScopes: [FileScopes.Workspace, FileScopes.Media], readOnlyScopes: [FileScopes.Media]);
        Directory.CreateDirectory(Path.Combine(_root, FileScopes.Media, "image", "ab"));
        var stored = Path.Combine(_root, FileScopes.Media, "image", "ab", "ab12.png");
        await File.WriteAllBytesAsync(stored,
            [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R']);
        var source = new FileSystemTools(access, [FileScopes.Workspace, FileScopes.Media]);

        var listed = await CallAsync(source, "fs_list", """{"scope":"media","path":"image/ab"}""");
        Assert.True(listed.Ok, listed.Error?.Message);
        Assert.Contains("ab12.png", listed.Value!.Content, StringComparison.Ordinal);

        // A picture is not text, but its bytes are not a mystery any more either: the PNG magic number is the first line.
        var inspected = await CallAsync(source, "fs_inspect",
            """{"scope":"media","path":"image/ab/ab12.png","mode":"hex","length":16}""");
        Assert.True(inspected.Ok, inspected.Error?.Message);
        Assert.Contains("89 50 4e 47 0d 0a 1a 0a", inspected.Value!.Content, StringComparison.Ordinal);

        // Copying bytes out of media into the workspace is a read of media and a write of the workspace — allowed. It
        // needs something in the workspace to append to, which is the only reason the first attempt fails.
        var nothingYet = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"copy.png","op":"append","sourcePath":"image/ab/ab12.png","sourceScope":"media"}""");
        Assert.False(nothingYet.Ok);
        Assert.Contains("does not exist", nothingYet.Error!.Message, StringComparison.Ordinal);

        Given("copy.png", []);
        var copied = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"copy.png","op":"append","sourcePath":"image/ab/ab12.png","sourceScope":"media"}""");
        Assert.True(copied.Ok, copied.Error?.Message);
        Assert.Equal(16, new FileInfo(Path.Combine(Workspace, "copy.png")).Length);

        var refused = await CallAsync(source, "fs_patch",
            """{"scope":"media","path":"image/ab/ab12.png","op":"overwrite","offset":0,"hex":"00"}""");
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Equal(0x89, File.ReadAllBytes(stored)[0]);
    }

    [Fact]
    public async Task The_model_can_copy_a_picture_out_of_media_with_the_tools_it_has_today()
    {
        // "Take that picture and keep it": the bytes go from media into a workspace file without ever being text. It
        // takes two calls because there is no copy verb — an empty file, then the bytes appended up to it — and this
        // test exists to say what the answer actually is today rather than what it ought to be.
        var access = new LocalFileSystemAccess(_root, [FileScopes.Workspace, FileScopes.Media],
            binaryScopes: [FileScopes.Workspace, FileScopes.Media], readOnlyScopes: [FileScopes.Media]);
        Directory.CreateDirectory(Path.Combine(_root, FileScopes.Media, "image", "ab"));
        byte[] picture = [0xff, 0xd8, 0xff, 0xe0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1];
        await File.WriteAllBytesAsync(Path.Combine(_root, FileScopes.Media, "image", "ab", "ab12.jpg"), picture);
        var source = new FileSystemTools(access, [FileScopes.Workspace, FileScopes.Media]);

        // Step one: an empty file. Empty text is a legal write — it is how a target gets made without inventing bytes.
        var made = await CallAsync(source, "fs_write", """{"scope":"workspace","path":"kept.jpg","text":""}""");
        Assert.True(made.Ok, made.Error?.Message);
        Assert.Equal(0, new FileInfo(Path.Combine(Workspace, "kept.jpg")).Length);

        // Step two: the bytes, from media.
        var copied = await CallAsync(source, "fs_patch",
            """{"scope":"workspace","path":"kept.jpg","op":"append","sourceScope":"media","sourcePath":"image/ab/ab12.jpg"}""");
        Assert.True(copied.Ok, copied.Error?.Message);
        Assert.Equal(picture, Read("kept.jpg"));
    }

    /// <summary>A port with the text half only, which is how a deployment without byte scopes looks.</summary>
    private sealed class NoBinary(IFileSystemAccess inner) : IFileSystemAccess
    {
        public IReadOnlyList<string> Scopes => inner.Scopes;
        public ValueTask<Result<IReadOnlyList<FileEntry>>> ListAsync(string scope, string path, CancellationToken ct) => inner.ListAsync(scope, path, ct);
        public ValueTask<Result<IReadOnlyList<FileNode>>> TreeAsync(string scope, string path, CancellationToken ct) => inner.TreeAsync(scope, path, ct);
        public ValueTask<Result<FileText?>> ReadAsync(string scope, string path, CancellationToken ct) => inner.ReadAsync(scope, path, ct);
        public ValueTask<Result<FileEntry?>> StatAsync(string scope, string path, CancellationToken ct) => inner.StatAsync(scope, path, ct);
        public ValueTask<Result<FileText>> WriteAsync(string scope, string path, string text, CancellationToken ct) => inner.WriteAsync(scope, path, text, ct);
        public ValueTask<Result<long>> AppendAsync(string scope, string path, string text, CancellationToken ct) => inner.AppendAsync(scope, path, text, ct);
        public ValueTask<Result> MoveAsync(string scope, string from, string to, CancellationToken ct) => inner.MoveAsync(scope, from, to, ct);
        public ValueTask<Result> DeleteAsync(string scope, string path, bool recursive, CancellationToken ct) => inner.DeleteAsync(scope, path, recursive, ct);
    }
}
