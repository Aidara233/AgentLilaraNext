using System.Security.Cryptography;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Media on disk: content-addressed, split by kind, named by its own hash.
///
/// <c>&lt;dataRoot&gt;/media/&lt;kind&gt;/&lt;xx&gt;/&lt;hash16&gt;.&lt;ext&gt;</c>, where <c>xx</c> is the first two hex
/// characters of the hash. Three consequences, all intended:
///
/// <list type="bullet">
///   <item><b>The same picture twice is the same file.</b> A group that reposts an image, a user who sends the same
///   photo again, a record that points at something already there — the second one costs one stat and no bytes. This is
///   why there are no date folders: retention deletes day folders, and media is not derived data on a clock, it is what
///   somebody sent. (The artifact store prunes by day because a stored model call is a copy of a request that was
///   already answered. Nothing here is a copy of anything.)</item>
///   <item><b>A record can be checked against what it points at</b>: <see cref="MediaRef.Hash"/> is the file's name, so
///   "the bytes changed" is a question with an answer.</item>
///   <item><b>The shard exists so one directory does not hold every picture this deployment ever received.</b> Two hex
///   characters is 256 directories per kind, which is enough to keep listings cheap for years and small enough to
///   explain.</item>
/// </list>
///
/// The bytes are written through the port (the host's own write path into a read-only scope), so containment and
/// atomicity are the same code as every other write. What is decided here is only the name.
/// </summary>
public sealed class FileMediaStore(LocalFileSystemAccess access) : IMediaStore
{
    /// <summary>The scope media lives in (docs/24 §2 — a scope is a name the host registered, not a path a module invents).</summary>
    public const string Scope = FileScopes.Media;

    /// <summary>Where a half-taken-in file waits. A legal path segment on purpose: the port refuses names that start with a dot.</summary>
    private const string PendingFolder = "pending";

    public async ValueTask<Result<MediaRef>> IngestAsync(MediaKind kind, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var folder = Folder(kind);
        var pending = $"{folder}/{PendingFolder}/{Guid.NewGuid():N}.bin";

        string hash;
        int captured;
        byte[] header;
        try
        {
            // One pass: read once, hash while copying, keep the first 64 KiB for the probe. The wrapper stops at the cap
            // rather than trusting the caller, so a bridge handing over a disk image cannot fill the data root first and
            // be told no afterwards.
            using var capture = new HashingCaptureStream(content, MediaProbe.HeaderBytes, IMediaStore.MaxIngestBytes);
            var stored = await access.SaveAsHostAsync(Scope, pending, capture, cancellationToken).ConfigureAwait(false);
            if (!stored.Ok) return Result<MediaRef>.Failure(stored.Error!);
            if (capture.Exceeded)
            {
                await DiscardAsync(pending, cancellationToken).ConfigureAwait(false);
                return Result<MediaRef>.Failure(new(ErrorCode.Invalid,
                    $"Media is larger than the {IMediaStore.MaxIngestBytes} bytes this build takes in."));
            }
            hash = capture.Hash;
            header = capture.Header;
            captured = header.Length;
        }
        catch
        {
            await DiscardAsync(pending, cancellationToken).ConfigureAwait(false);
            throw;
        }

        try
        {
            if (captured == 0)
            {
                await DiscardAsync(pending, cancellationToken).ConfigureAwait(false);
                return Result<MediaRef>.Failure(new(ErrorCode.Invalid, "Media is empty."));
            }

            // The bytes decide the format; the caller only says what it expected. A text file somebody labelled an image
            // stays a file, and never becomes something a vision model is asked to look at.
            var info = MediaProbe.Detect(header);
            var resolved = Resolve(kind, info);
            var path = $"{Folder(resolved)}/{hash[..2]}/{hash[..16]}.{info.Extension}";

            var existing = await access.StatAsync(Scope, path, cancellationToken).ConfigureAwait(false);
            if (existing.Ok && existing.Value is { } found)
            {
                // Already here: the same bytes, under the same name. Drop the copy and answer with what is there.
                await DiscardAsync(pending, cancellationToken).ConfigureAwait(false);
                return Result<MediaRef>.Success(Reference(Scope, path, resolved, info, found.Bytes, hash));
            }

            var moved = await access.MoveAsHostAsync(Scope, pending, path, cancellationToken).ConfigureAwait(false);
            if (!moved.Ok) return Result<MediaRef>.Failure(moved.Error!);
            var stat = await access.StatAsync(Scope, path, cancellationToken).ConfigureAwait(false);
            var size = stat.Ok && stat.Value is not null ? stat.Value.Bytes : 0;
            return Result<MediaRef>.Success(Reference(Scope, path, resolved, info, size, hash));
        }
        catch
        {
            await DiscardAsync(pending, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<Result<MediaRef>> DescribeAsync(string scope, string path, CancellationToken cancellationToken)
    {
        var stat = await access.StatAsync(scope, path, cancellationToken).ConfigureAwait(false);
        // Absence comes back as a result whose Value is null and whose Error is null — and Ok is false for that, so
        // reading Ok first would report "no such file" as a failure with no reason at all.
        if (stat.Value is null)
            return Result<MediaRef>.Failure(stat.Error ?? new(ErrorCode.Invalid, $"'{scope}/{path}' does not exist."));
        if (stat.Value.IsDirectory)
            return Result<MediaRef>.Failure(new(ErrorCode.Invalid, $"'{scope}/{path}' is a directory."));

        // Opened without a size ceiling, and that is not a loosening: describing reads the first few dozen bytes, so the
        // question "how big may this file be" does not apply — the transfer cap answers a different one, and a file too
        // big to copy through this process is still perfectly describable (docs/28 §6.6). Everything else is unchanged:
        // the scope must accept bytes at all, the path goes through the same rules, and the caller gets a pointer.
        var opened = await access.OpenReadStreamAsync(scope, path, long.MaxValue, cancellationToken).ConfigureAwait(false);
        if (!opened.Ok || opened.Value is null) return Result<MediaRef>.Failure(opened.Error!);
        byte[] header;
        await using (var stream = opened.Value)
        {
            var buffer = new byte[MediaProbe.HeaderBytes];
            // Only the header: describing a file must not cost reading it. A JPEG whose frame header sits past this
            // window answers "size unknown", which is a true statement about our knowledge rather than about the file.
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            header = buffer[..read];
        }

        var info = MediaProbe.Detect(header);
        var canonical = FilePaths.Canonical(path) ?? path;
        return Result<MediaRef>.Success(Reference(scope, canonical, Resolve(MediaKind.File, info), info, stat.Value.Bytes));
    }

    /// <summary>
    /// The kind to file it under: what the bytes say when they say anything, else what the caller expected.
    ///
    /// The asymmetry is the point. Bytes that look like a picture make it a picture even if it arrived labelled a file
    /// (somebody dragged one in); bytes that look like nothing make it a file even if they arrived labelled a picture
    /// (a bridge with a wrong guess, or a text file with a nice extension). Only the second direction matters for
    /// safety, and it is the one that decides whether a model is ever asked to look at it.
    /// </summary>
    private static MediaKind Resolve(MediaKind expected, MediaInfo info) =>
        info.Kind != MediaKind.File ? info.Kind
        : expected == MediaKind.Image ? MediaKind.File
        : expected;

    private static MediaRef Reference(string scope, string path, MediaKind kind, MediaInfo info, long bytes = 0, string? hash = null) =>
        new(scope, path, kind, info.MediaType, bytes, info.Width, info.Height, hash);

    /// <summary>The directory a kind lives under. Named explicitly so a new kind cannot end up in another one's folder.</summary>
    private static string Folder(MediaKind kind) => kind switch
    {
        MediaKind.Image => "image",
        MediaKind.Voice => "voice",
        MediaKind.Video => "video",
        MediaKind.Card => "card",
        _ => "file"
    };

    /// <summary>Best effort: a file left behind is a stray, a failed ingest that also throws is a bug report.</summary>
    private async ValueTask DiscardAsync(string pending, CancellationToken cancellationToken)
    {
        try { await access.DeleteAsHostAsync(Scope, pending, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    /// <summary>
    /// Copies a stream while hashing it and keeping its first bytes, and refuses to read past the cap.
    ///
    /// Stopping at the cap (rather than counting and complaining afterwards) is what makes the ceiling real: the disk
    /// never holds more than it should, even for a moment, even when the sender lies about the size.
    /// </summary>
    private sealed class HashingCaptureStream(Stream inner, int captureBytes, long limit) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly MemoryStream _header = new(captureBytes);
        private long _total;

        public bool Exceeded { get; private set; }

        public byte[] Header => _header.ToArray();

        public string Hash => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _total; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_total >= limit)
            {
                // Exactly at the ceiling: the only remaining question is whether there is more input, and one byte
                // answers it without ever holding more than the ceiling on disk.
                if (inner.Read(stackalloc byte[1]) > 0) Exceeded = true;
                return 0;
            }
            var wanted = buffer[..(int)Math.Min(buffer.Length, limit - _total)];
            var read = inner.Read(wanted);
            return Absorb(wanted[..read]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_total >= limit)
            {
                var probe = new byte[1];
                if (await inner.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0) Exceeded = true;
                return 0;
            }
            var wanted = buffer[..(int)Math.Min(buffer.Length, limit - _total)];
            var read = await inner.ReadAsync(wanted, cancellationToken).ConfigureAwait(false);
            return Absorb(wanted.Span[..read]);
        }

        private int Absorb(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return 0;
            _total += data.Length;
            _hash.AppendData(data);
            if (_header.Length < captureBytes)
            {
                var room = (int)Math.Min(data.Length, captureBytes - _header.Length);
                _header.Write(data[..room]);
            }
            return data.Length;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
