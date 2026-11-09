using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// Media on disk: what a run of bytes is, and where it ends up.
///
/// Two properties are the whole point and everything here is aimed at them. <b>The bytes decide the format</b> — a
/// text file with a nice extension never becomes something a model is asked to look at — and <b>the same bytes twice
/// is the same file</b> — a group that reposts a picture does not cost a second copy or a second name.
///
/// Nothing here decodes pixels, so these tests build headers rather than pictures: a real 33-byte PNG header is
/// enough to prove the reader, and it is the only part of the file this process ever interprets.
/// </summary>
public sealed class MediaStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-media-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LocalFileSystemAccess Access() =>
        new(_root, [FileScopes.Workspace, FileScopes.Media],
            binaryScopes: [FileScopes.Workspace, FileScopes.Media],
            readOnlyScopes: [FileScopes.Media]);

    private FileMediaStore Store() => new(Access());

    // ---- what a run of bytes is ----------------------------------------------------------------

    private static byte[] Png(int width, int height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        0x08, 0x06, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    private static byte[] Gif(int width, int height) =>
    [
        (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
        (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8),
        0x00, 0x00, 0x00
    ];

    private static byte[] WebpVp8X(int width, int height)
    {
        var w = width - 1;
        var h = height - 1;
        return
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x00, 0x00, 0x00, 0x00,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P',
            (byte)'V', (byte)'P', (byte)'8', (byte)'X', 0x0A, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            (byte)(w & 0xFF), (byte)((w >> 8) & 0xFF), (byte)((w >> 16) & 0xFF),
            (byte)(h & 0xFF), (byte)((h >> 8) & 0xFF), (byte)((h >> 16) & 0xFF)
        ];
    }

    /// <summary>
    /// A JPEG whose EXIF block contains <c>FF C0</c> followed by a plausible-looking size.
    ///
    /// This is not a contrived shape: any JPEG with a thumbnail in its EXIF has a nested image in it, and that nested
    /// image starts with the same markers. A reader that searched for the first <c>FF C0</c> would report the
    /// thumbnail's size as the picture's — which is exactly the sort of bug that shows up as a wrong token estimate
    /// nobody can explain.
    /// </summary>
    private static byte[] JpegWithDecoyFrameHeader() =>
    [
        0xFF, 0xD8,
        0xFF, 0xE1, 0x00, 0x0C,                     // APP1, length 12 → 10 bytes of payload
        (byte)'P', (byte)'h', (byte)'o', (byte)'t', (byte)'o', 0x00,
        0xFF, 0xC0, 0x00, 0x11,                     // decoy: inside the payload, must be skipped
        0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x01, 0xF4, 0x03, 0x20, 0x01,   // real frame: 800×500
        0xFF, 0xD9
    ];

    [Fact]
    public void Each_format_is_recognised_by_its_bytes_and_reports_its_size()
    {
        var png = MediaProbe.Detect(Png(1920, 1080));
        Assert.Equal(MediaKind.Image, png.Kind);
        Assert.Equal("image/png", png.MediaType);
        Assert.Equal((1920, 1080), (png.Width, png.Height));

        var gif = MediaProbe.Detect(Gif(320, 200));
        Assert.Equal("image/gif", gif.MediaType);
        Assert.Equal((320, 200), (gif.Width, gif.Height));

        var webp = MediaProbe.Detect(WebpVp8X(320, 200));
        Assert.Equal("image/webp", webp.MediaType);
        Assert.Equal((320, 200), (webp.Width, webp.Height));

        var jpeg = MediaProbe.Detect(JpegWithDecoyFrameHeader());
        Assert.Equal("image/jpeg", jpeg.MediaType);
        Assert.Equal((800, 500), (jpeg.Width, jpeg.Height));
    }

    [Fact]
    public void A_header_cut_short_is_still_that_format_with_no_size()
    {
        // Eight bytes is a complete PNG signature and nothing else. "Not known" is the answer; 0×0 would make every
        // estimate built on it confidently wrong.
        var info = MediaProbe.Detect(Png(1, 1)[..8]);
        Assert.Equal("image/png", info.MediaType);
        Assert.Null(info.Width);
        Assert.Null(info.Height);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("hello, 这是一段文字")]
    [InlineData("")]
    public void Anything_not_in_the_short_list_is_a_file(string content)
    {
        // SVG included, on purpose: it is XML, several providers refuse it, and calling it a picture would put a
        // document in front of a vision model.
        var info = MediaProbe.Detect(System.Text.Encoding.UTF8.GetBytes(content));
        Assert.Equal(MediaKind.File, info.Kind);
        Assert.Equal("application/octet-stream", info.MediaType);
    }

    // ---- where a file ends up ----------------------------------------------------------------

    [Fact]
    public async Task Ingest_names_the_file_by_its_content_and_reports_what_it_is()
    {
        var store = Store();
        var bytes = Png(640, 480);

        var stored = await store.IngestAsync(MediaKind.Image, new MemoryStream(bytes), default);

        Assert.True(stored.Ok, stored.Error?.Message);
        var media = stored.Value!;
        Assert.Equal(FileScopes.Media, media.Scope);
        Assert.Equal(MediaKind.Image, media.Kind);
        Assert.Equal("image/png", media.MediaType);
        Assert.Equal(bytes.Length, media.Bytes);
        Assert.Equal((640, 480), (media.Width, media.Height));
        Assert.Equal("640×480", media.Size);
        Assert.True(media.IsViewable);

        // image/<first two hex>/<sixteen hex>.png — the name is the hash, so "the same bytes" is a lookup.
        Assert.StartsWith($"image/{media.Hash![..2]}/{media.Hash[..16]}.png", media.Path, StringComparison.Ordinal);
        Assert.Equal(64, media.Hash!.Length);
        var onDisk = Path.Combine(_root, "media", media.Path.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(onDisk));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(onDisk));
    }

    [Fact]
    public async Task The_same_bytes_twice_is_one_file()
    {
        var store = Store();
        var bytes = Png(16, 16);

        var first = await store.IngestAsync(MediaKind.Image, new MemoryStream(bytes), default);
        var second = await store.IngestAsync(MediaKind.Image, new MemoryStream(bytes), default);

        Assert.True(first.Ok && second.Ok);
        Assert.Equal(first.Value!.Path, second.Value!.Path);
        var files = Directory.GetFiles(Path.Combine(_root, "media", "image"), "*", SearchOption.AllDirectories);
        Assert.Single(files);
        // The refused copy is not left behind in the pending folder.
        Assert.False(Directory.Exists(Path.Combine(_root, "media", "image", "pending"))
                     && Directory.GetFiles(Path.Combine(_root, "media", "image", "pending")).Length > 0);
    }

    [Fact]
    public async Task Bytes_that_are_not_a_picture_stay_a_file_even_when_the_sender_says_image()
    {
        var store = Store();
        var stored = await store.IngestAsync(MediaKind.Image, new MemoryStream("这不是图片"u8.ToArray()), default);

        Assert.True(stored.Ok, stored.Error?.Message);
        Assert.Equal(MediaKind.File, stored.Value!.Kind);
        Assert.False(stored.Value.IsViewable);
        Assert.StartsWith("file/", stored.Value.Path, StringComparison.Ordinal);
        Assert.Equal("application/octet-stream", stored.Value.MediaType);
    }

    [Fact]
    public async Task Bytes_that_are_a_picture_are_an_image_even_when_the_sender_said_file()
    {
        // Somebody drags a .png into a chat client that calls every attachment a file. The bytes are the better witness.
        var store = Store();
        var stored = await store.IngestAsync(MediaKind.File, new MemoryStream(Png(10, 10)), default);

        Assert.True(stored.Ok, stored.Error?.Message);
        Assert.Equal(MediaKind.Image, stored.Value!.Kind);
        Assert.StartsWith("image/", stored.Value.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_too_large_to_take_in_is_refused_and_leaves_nothing_behind()
    {
        var store = Store();
        // One byte past the ceiling: the wrapper stops there rather than trusting the sender, so the disk never holds
        // more than the cap even for a moment.
        var oversized = new byte[IMediaStore.MaxIngestBytes + 1];
        Png(8, 8).CopyTo(oversized, 0);

        var stored = await store.IngestAsync(MediaKind.Image, new MemoryStream(oversized), default);

        Assert.False(stored.Ok);
        Assert.Equal(ErrorCode.Invalid, stored.Error!.Code);
        Assert.Contains("larger than", stored.Error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "media"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Empty_input_is_refused_rather_than_stored_as_an_empty_file()
    {
        var stored = await Store().IngestAsync(MediaKind.Image, new MemoryStream([]), default);
        Assert.False(stored.Ok);
        Assert.Equal(ErrorCode.Invalid, stored.Error!.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "media"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task What_was_ingested_can_be_read_back_through_the_port()
    {
        var access = Access();
        var stored = await new FileMediaStore(access).IngestAsync(MediaKind.Image, new MemoryStream(Png(4, 4)), default);

        await using var stream = (await access.OpenReadAsync(FileScopes.Media, stored.Value!.Path, default)).Value!;
        var buffer = new byte[8];
        Assert.Equal(8, await stream.ReadAsync(buffer, default));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], buffer);

        // And the model still cannot write there: ingesting is the host's privilege, not a hole in the read-only rule.
        var overwrite = await access.SaveAsync(FileScopes.Media, stored.Value.Path, new MemoryStream([1]), default);
        Assert.Equal(ErrorCode.Denied, overwrite.Error!.Code);
    }

    // ---- describing something already on disk ------------------------------------------------

    [Fact]
    public async Task A_picture_in_the_workspace_is_described_where_it_is_without_being_copied()
    {
        var access = Access();
        var store = new FileMediaStore(access);
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        var mine = Path.Combine(_root, "workspace", "shot.png");
        await File.WriteAllBytesAsync(mine, Png(1280, 720));

        var described = await store.DescribeAsync(FileScopes.Workspace, "shot.png", default);

        Assert.True(described.Ok, described.Error?.Message);
        Assert.Equal(FileScopes.Workspace, described.Value!.Scope);
        Assert.Equal("shot.png", described.Value.Path);
        Assert.Equal("image/png", described.Value.MediaType);
        Assert.Equal((1280, 720), (described.Value.Width, described.Value.Height));
        // Nothing was ingested: the workspace copy is the owner's, and the record points at it.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "media"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Describing_something_that_is_not_there_is_an_invalid_request_with_a_reason()
    {
        var described = await Store().DescribeAsync(FileScopes.Workspace, "nothing.png", default);
        Assert.False(described.Ok);
        Assert.Equal(ErrorCode.Invalid, described.Error!.Code);
    }
}
