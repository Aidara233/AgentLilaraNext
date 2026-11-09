using ALN.Api;
using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The read-only door a bridge fetches media through: upload costs nothing, and it is the answer to "base64 in one
/// WebSocket frame" for bytes that live on another machine.
///
/// What matters here is not that a GET works — it is that <b>nothing else does</b>. The token, the read-only rule, the
/// one-scope-rule and the per-kind ceilings are what keep this from being a file server with a secret; the tests are
/// mostly about the ways a request is refused, plus the two ways a file is served (whole, and in one range).
/// </summary>
public sealed class MediaEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-endpoint-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A 33-byte PNG header: enough for the probe, and the bytes can be compared exactly afterwards.</summary>
    private static byte[] Png(int width, int height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        0x08, 0x06, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    private LocalFileSystemAccess Access() =>
        new(_root, [FileScopes.Media, FileScopes.Workspace, FileScopes.Memory],
            binaryScopes: [FileScopes.Media, FileScopes.Workspace], readOnlyScopes: [FileScopes.Media]);

    /// <summary>Writes a file straight to disk: this endpoint serves files, it does not create them.</summary>
    private async Task<string> WriteAsync(string scope, string path, byte[] bytes)
    {
        var full = Path.Combine(_root, scope, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes);
        return path;
    }

    /// <summary>
    /// Starts the endpoint on a free port. Port 0 means "pick one", which is what a test wants — and the URL it hands
    /// back carries the port that was actually bound, so the rest of the test can use it.
    /// </summary>
    private static async Task<MediaEndpoint> StartAsync(IBinaryFileAccess files) =>
        (await MediaEndpoint.StartAsync("http://127.0.0.1:0", files, default)).Value!;

    [Fact]
    public async Task A_url_for_a_stored_picture_fetches_those_exact_bytes()
    {
        var picture = Png(4, 3);
        var path = await WriteAsync(FileScopes.Media, "image/ab/x.png", picture);
        await using var endpoint = await StartAsync(Access());

        var url = endpoint.UrlFor(FileScopes.Media, path);
        Assert.NotNull(url);
        // The token is in the URL: a URL is the whole capability, which is why it is unguessable rather than signed.
        Assert.Contains("t=", url!.Query, StringComparison.Ordinal);

        using var client = new HttpClient();
        using var response = await client.GetAsync(url);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(picture, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_picture_in_the_workspace_is_served_too_because_that_is_where_the_model_saves_its_own()
    {
        var picture = Png(2, 2);
        var path = await WriteAsync(FileScopes.Workspace, "shot.png", picture);
        await using var endpoint = await StartAsync(Access());

        using var client = new HttpClient();
        using var response = await client.GetAsync(endpoint.UrlFor(FileScopes.Workspace, path));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(picture, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    public async Task Without_the_right_token_the_door_does_not_exist(string token)
    {
        var path = await WriteAsync(FileScopes.Media, "image/ab/x.png", Png(4, 3));
        await using var endpoint = await StartAsync(Access());
        var url = endpoint.UrlFor(FileScopes.Media, path)!;

        // Same request, token replaced: a 404 rather than a 403, because "you need a token" is a door worth guessing at.
        var withoutToken = url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !pair.StartsWith("t=", StringComparison.Ordinal));
        var tampered = $"{url.GetLeftPart(UriPartial.Path)}?{string.Join('&', withoutToken)}&t={token}";

        using var client = new HttpClient();
        using var response = await client.GetAsync(tampered);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_is_served_as_what_it_is()
    {
        // This door used to refuse anything the probe did not read as a picture, and that was exactly right while it only
        // ever carried pictures. A file has the same problem a picture had — the bytes are here, the bridge is not — and
        // the name still promises nothing: text in a ".png" arrives as octet-stream, not as a broken image.
        var path = await WriteAsync(FileScopes.Media, "file/ab/notes.png", "这不是图片"u8.ToArray());
        await using var endpoint = await StartAsync(Access());

        using var client = new HttpClient();
        var url = endpoint.UrlFor(FileScopes.Media, path);
        Assert.NotNull(url);
        using var response = await client.GetAsync(url);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("这不是图片"u8.ToArray(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_file_may_be_much_bigger_than_a_picture_because_the_ceiling_is_per_kind()
    {
        // The picture ceiling is 8 MiB because a bridge has to hold a whole picture in its own memory and a model has to
        // be able to look at it. A file has neither of those jobs, so the number that applies is the deployment's.
        var document = new byte[MediaEndpoint.MaxImageBytes + 1024];
        "PK"u8.ToArray().CopyTo(document, 0);
        var path = await WriteAsync(FileScopes.Media, "file/ab/bundle.bin", document);
        await using var endpoint = await StartAsync(Access());

        using var client = new HttpClient();
        using var response = await client.GetAsync(endpoint.UrlFor(FileScopes.Media, path));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(document.Length, response.Content.Headers.ContentLength);
        Assert.Equal(document.Length, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task A_ranged_request_gets_that_piece_and_nothing_else()
    {
        // A bridge that downloads in pieces — NapCat's `download_file` takes a `thread_count` — asks for ranges, and a
        // server that answers every range request with the whole file turns a multi-threaded download into a corrupt one.
        var path = await WriteAsync(FileScopes.Media, "file/ab/ten.bin", "0123456789"u8.ToArray());
        await using var endpoint = await StartAsync(Access());
        var url = endpoint.UrlFor(FileScopes.Media, path)!;

        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        using var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 2-5/10", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal("2345", await response.Content.ReadAsStringAsync());

        // Everything else asks for the whole thing and gets it, with the door advertising that it can do pieces.
        using var whole = await client.GetAsync(url);
        Assert.Equal(System.Net.HttpStatusCode.OK, whole.StatusCode);
        Assert.Contains("bytes", whole.Headers.AcceptRanges);
        Assert.Equal("0123456789", await whole.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_suffix_range_asks_for_the_tail_and_a_range_past_the_end_is_refused()
    {
        var path = await WriteAsync(FileScopes.Media, "file/ab/ten.bin", "0123456789"u8.ToArray());
        await using var endpoint = await StartAsync(Access());
        var url = endpoint.UrlFor(FileScopes.Media, path)!;

        using var client = new HttpClient();
        var tail = new HttpRequestMessage(HttpMethod.Get, url);
        tail.Headers.TryAddWithoutValidation("Range", "bytes=-3");
        using var tailed = await client.SendAsync(tail);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, tailed.StatusCode);
        Assert.Equal("789", await tailed.Content.ReadAsStringAsync());

        // Past the end: 416 rather than the whole file. Answering "here is everything" to a request for bytes 100-200 is
        // how a downloader writes the entire file into the wrong offset.
        var beyond = new HttpRequestMessage(HttpMethod.Get, url);
        beyond.Headers.TryAddWithoutValidation("Range", "bytes=100-200");
        using var refused = await client.SendAsync(beyond);
        Assert.Equal(System.Net.HttpStatusCode.RequestedRangeNotSatisfiable, refused.StatusCode);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("image/../../secret.png")]
    [InlineData("/absolute.png")]
    public async Task A_path_that_escapes_is_refused_before_anything_is_opened(string path)
    {
        await using var endpoint = await StartAsync(Access());
        // A path the file rules would not accept has no URL at all — the door refuses it one step earlier.
        Assert.Null(endpoint.UrlFor(FileScopes.Media, path));
    }

    [Fact]
    public async Task Only_the_rooms_the_byte_surface_allows_are_served()
    {
        await WriteAsync(FileScopes.Memory, "x.png", Png(1, 1));
        await using var endpoint = await StartAsync(Access());
        // Not a list this door keeps: `memory` is not a scope that accepts binary at all, so its files are unnameable here
        // for the same reason the file tools refuse them (docs/24). One rule, asked once.
        Assert.Null(endpoint.UrlFor(FileScopes.Memory, "x.png"));
    }

    [Fact]
    public async Task A_picture_bigger_than_the_sending_limit_is_refused()
    {
        var huge = new byte[MediaEndpoint.MaxImageBytes + 1];
        Png(4, 3).CopyTo(huge, 0);
        var path = await WriteAsync(FileScopes.Media, "image/ab/huge.png", huge);
        await using var endpoint = await StartAsync(Access());

        using var client = new HttpClient();
        using var response = await client.GetAsync(endpoint.UrlFor(FileScopes.Media, path));
        // A picture keeps its own ceiling even though files may be far bigger: this one has to survive a bridge's memory
        // and a model's context, which a file does not.
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void No_endpoint_configured_means_no_urls_and_every_sender_keeps_doing_what_it_did()
    {
        // The opt-in is structural: an adapter asks this question and gets null, so base64 — the old behaviour — is what
        // happens when nobody deployed a door.
        Assert.Null(((IMediaUrlSource?)null)?.UrlFor(FileScopes.Media, "image/ab/x.png"));
    }
}
