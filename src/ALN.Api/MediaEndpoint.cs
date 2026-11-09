using System.Security.Cryptography;
using ALN.Contracts;
using ALN.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ALN.Api;

/// <summary>
/// The read-only door a bridge fetches media through: <c>GET /m?scope=media&amp;path=…&amp;t=…</c> answers with the bytes
/// and nothing else.
///
/// Four limits make it safe enough to expose, and each of them is the reason for a line below. <b>Only scopes the byte
/// surface already allows</b> — this class keeps no list of its own, because "may this caller touch that room" is
/// <see cref="IBinaryFileAccess.BinaryScopes"/> and a second answer here would be a second rule to keep in step
/// (docs/24). <b>Only with the token</b> — a per-process random string in the query, so a URL cannot be guessed and the
/// door cannot be enumerated; deliberately not persisted, because a restart gets a new one and the only thing holding a
/// URL is a message that was already sent. <b>Only read, only one canonical path.</b> And <b>a ceiling per kind</b>:
/// a picture is capped at what the base64 path would send anyway, while a file is capped at what the deployment said,
/// which is a gigabyte-shaped number (docs/28 §6.6).
///
/// It serves files as well as pictures, which it deliberately did not at first. What changed is not the appetite for
/// being a file server — the token, the read-only rule and the scope rule are unchanged — it is that a bridge sending a
/// file has the same problem a bridge sending a picture had: the bytes are on this machine and the bridge is not. The
/// header still decides what the bytes are (<see cref="MediaProbe"/>), so a name never promises a format.
///
/// Failure is not fatal anywhere: if the socket cannot be opened the host says so and every sender falls back to base64,
/// which is exactly what it did before this existed (docs/16 §12.4).
/// </summary>
public sealed class MediaEndpoint : IMediaUrlSource, IAsyncDisposable
{
    /// <summary>
    /// Nothing bigger than a picture worth sending: the same ceiling the base64 path uses. Kept apart from the file
    /// ceiling on purpose — a picture has to be decoded by a model or re-encoded by a bridge, and something in the tens
    /// of megabytes is not a picture anybody meant to send (docs/16 §12.3).
    /// </summary>
    public const long MaxImageBytes = 8L * 1024 * 1024;

    /// <summary>
    /// What a file may weigh by default: gigabyte-scale, because that is the size at which "send me the log/export/the
    /// build" stops being a picture and becomes the reason this path exists. It costs this process one buffer — the bytes
    /// go from disk to the socket — so the number is about the sender's disk and the bridge's patience, not about heap.
    /// </summary>
    public const long DefaultMaxFileBytes = 2L * 1024 * 1024 * 1024;

    private readonly WebApplication _app;
    private readonly IBinaryFileAccess _files;
    private readonly string _token;
    private readonly long _maxFileBytes;
    /// <summary>Optional one-line-per-request record: who asked, what happened. Silent by default.</summary>
    private readonly Action<string>? _log;
    /// <summary>Set at start: with a port of 0 the URL keeps the port that was actually bound.</summary>
    private string _baseUrl;

    private MediaEndpoint(WebApplication app, IBinaryFileAccess files, string baseUrl, string token,
        long maxFileBytes, Action<string>? log)
    {
        _app = app;
        _files = files;
        _baseUrl = baseUrl;
        _token = token;
        _maxFileBytes = maxFileBytes;
        _log = log;
    }

    /// <summary>The address this endpoint hands out, without the trailing slash: what a bridge must be able to reach.</summary>
    public Uri BaseUrl => new(_baseUrl);

    /// <summary>What a non-picture may weigh here. Readable so a receipt can say it without guessing.</summary>
    public long MaxFileBytes => _maxFileBytes;

    /// <summary>
    /// Starts listening. <paramref name="publicBaseUrl"/> is what goes into the URLs — the address the bridge uses, which
    /// is not necessarily this machine's loopback: <c>--media-endpoint http://192.168.1.5:5310</c> means "listen on 5310,
    /// hand out that host".
    /// </summary>
    public static async Task<Result<MediaEndpoint>> StartAsync(string publicBaseUrl, IBinaryFileAccess files,
        CancellationToken cancellationToken, Action<string>? log = null,
        long maxFileBytes = DefaultMaxFileBytes)
    {
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            return Result<MediaEndpoint>.Failure(new(ErrorCode.Invalid,
                $"--media-endpoint must be an absolute http:// or https:// url: {publicBaseUrl}"));
        if (maxFileBytes <= 0)
            return Result<MediaEndpoint>.Failure(new(ErrorCode.Invalid,
                $"the file ceiling must be a positive number of bytes: {maxFileBytes}"));

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Listening on every interface is the point: the bridge is usually another machine. The URL keeps the host that
        // was configured, because that is the name the other side can resolve. Port 0 means "pick one" — a test wants it,
        // and so does a machine whose port is taken.
        builder.WebHost.UseUrls($"http://0.0.0.0:{address.Port}");
        var app = builder.Build();
        var endpoint = new MediaEndpoint(app, files, publicBaseUrl.TrimEnd('/'), token, maxFileBytes, log);

        app.MapGet("/m", async context => await endpoint.ServeAsync(context).ConfigureAwait(false));
        app.MapGet("/healthz", () => Results.Text("ok"));

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            return Result<MediaEndpoint>.Failure(new(ErrorCode.Unavailable,
                $"the media endpoint could not listen on port {address.Port}; senders fall back to base64.", ex.Message));
        }

        var bound = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (address.Port == 0 && bound is not null && Uri.TryCreate(bound, UriKind.Absolute, out var actual))
            endpoint._baseUrl = new UriBuilder(address) { Port = actual.Port }.ToString().TrimEnd('/');
        Console.WriteLine($"media endpoint on {bound} (urls under {endpoint._baseUrl}, files up to {maxFileBytes} bytes)");
        return Result<MediaEndpoint>.Success(endpoint);
    }

    /// <inheritdoc />
    public Uri? UrlFor(string scope, string path)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(path)) return null;
        // The one rule for which rooms may leave, asked rather than restated: a scope that accepts no binary at all is
        // not a scope this door can hand out, and that is the byte surface's answer to give.
        if (!_files.BinaryScopes.Contains(scope, StringComparer.Ordinal)) return null;
        // Canonical first: a path that would not survive the file rules is not one worth printing in a URL either.
        if (FilePaths.Canonical(path) is not { } canonical) return null;
        return new Uri($"{_baseUrl}/m?scope={Uri.EscapeDataString(scope)}&path={Uri.EscapeDataString(canonical)}&t={_token}");
    }

    private async Task ServeAsync(HttpContext context)
    {
        var query = context.Request.Query;
        // A wrong or missing token is a 404 rather than a 403: a door that says "you need a token" is a door that says it
        // exists and is worth guessing at.
        if (!string.Equals(query["t"], _token, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var scope = query["scope"].ToString();
        var path = query["path"].ToString();
        if (!_files.BinaryScopes.Contains(scope, StringComparer.Ordinal) || FilePaths.Canonical(path) is not { } canonical)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Opened with the widest ceiling this door might allow, because which one applies is decided below by what the
        // bytes turn out to be. Opening twice to ask first would be two answers to "may this caller touch that".
        var opened = await _files.OpenReadStreamAsync(scope, canonical, _maxFileBytes, context.RequestAborted)
            .ConfigureAwait(false);
        if (!opened.Ok || opened.Value is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using var stream = opened.Value;
        // The header decides, and it is read the same way the store reads it: a name never promises a format, so a
        // ".png" full of text is served as what it is — a file — rather than as a broken picture.
        var header = new byte[MediaProbe.HeaderBytes];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, context.RequestAborted)
            .ConfigureAwait(false);
        var info = MediaProbe.Detect(header.AsSpan(0, read));
        var limit = info.Kind == MediaKind.Image ? MaxImageBytes : _maxFileBytes;

        context.Response.ContentType = info.MediaType;
        context.Response.Headers.CacheControl = "private, max-age=300";

        if (stream.CanSeek)
        {
            var length = stream.Length;
            if (length > limit)
            {
                _log?.Invoke($"refused {scope}/{canonical}: {length} bytes is more than this door serves for {info.Kind}");
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            // Advertised even when this request asks for the whole thing: a bridge that downloads in pieces looks for it,
            // and its multi-threaded download (NapCat's `download_file` with `thread_count`) sends ranged GETs.
            context.Response.Headers.AcceptRanges = "bytes";
            var range = ParseRange(context.Request.Headers.Range, length, out var start, out var end);
            if (range == RangeKind.Unsatisfiable)
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.Headers.ContentRange = $"bytes */{length}";
                return;
            }
            if (range == RangeKind.Partial)
            {
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.ContentLength = end - start + 1;
                context.Response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
                stream.Seek(start, SeekOrigin.Begin);
                _log?.Invoke($"served {scope}/{canonical} bytes {start}-{end}/{length} ({info.MediaType})");
                await CopyExactlyAsync(stream, context.Response.Body, end - start + 1, context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }
            context.Response.ContentLength = length;
            stream.Seek(0, SeekOrigin.Begin);
            _log?.Invoke($"served {scope}/{canonical} ({length} bytes, {info.MediaType})");
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // Not seekable: no ranges (there is nothing to seek to), the bytes already read are the beginning of the answer,
        // and the rest is counted on the way out so a stream that never ends still stops at the ceiling.
        await context.Response.Body.WriteAsync(header.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
        var total = read;
        var chunk = new byte[81920];
        int copied;
        while ((copied = await stream.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            total += copied;
            if (total > limit) break;
            await context.Response.Body.WriteAsync(chunk.AsMemory(0, copied), context.RequestAborted).ConfigureAwait(false);
        }
        _log?.Invoke($"served {scope}/{canonical} ({total} bytes, {info.MediaType}, not seekable)");
    }

    private enum RangeKind { Whole, Partial, Unsatisfiable }

    /// <summary>
    /// Which bytes a <c>Range</c> header asks for. <b>One range only:</b> the multi-range form exists so a client can ask
    /// for several pieces of one resource in a single response, and nothing that fetches a file from this door does that —
    /// answering with the whole file is a valid reply to a range request and keeps this from becoming a byte-range router.
    /// </summary>
    private static RangeKind ParseRange(string? header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (string.IsNullOrWhiteSpace(header) || length == 0) return RangeKind.Whole;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return RangeKind.Whole;
        var spec = header["bytes=".Length..].Trim();
        if (spec.Contains(',', StringComparison.Ordinal)) return RangeKind.Whole;
        var dash = spec.IndexOf('-');
        if (dash < 0) return RangeKind.Whole;
        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();
        if (left.Length == 0)
        {
            // "bytes=-N": the last N bytes. A zero-length suffix asks for nothing, which is not a range.
            if (!long.TryParse(right, out var suffix) || suffix <= 0) return RangeKind.Whole;
            start = Math.Max(0, length - suffix);
            end = length - 1;
            return RangeKind.Partial;
        }
        if (!long.TryParse(left, out start) || start < 0) return RangeKind.Whole;
        if (start >= length) return RangeKind.Unsatisfiable;
        end = length - 1;
        if (right.Length > 0 && (!long.TryParse(right, out end) || end < start)) return RangeKind.Whole;
        end = Math.Min(end, length - 1);
        return RangeKind.Partial;
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        var remaining = count;
        while (remaining > 0)
        {
            var wanted = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(stop.Token).ConfigureAwait(false);
        }
        catch { /* the socket may already be gone */ }
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
