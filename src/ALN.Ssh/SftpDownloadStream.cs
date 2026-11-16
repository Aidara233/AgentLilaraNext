using System.IO;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace ALN.Ssh;

/// <summary>
/// A download that has not happened yet: one open read stream on the far side, plus the SFTP session that has to
/// outlive it.
///
/// The bytes never collect in this process. That is the whole point of the type — with a buffer, "how large a file
/// may come down" is a memory decision, and every answer is either too small to be useful or an out-of-memory waiting
/// for a typo. Streaming turns it into a bandwidth decision: one buffer whatever the file weighs, so there is nothing
/// to cap.
///
/// The session is owned here rather than by the caller because its lifetime is the stream's: closing the stream is
/// what ends the transfer, and a client disposed while a reader is still using it would cut a file in half. So
/// <see cref="Dispose(bool)"/> closes the stream first and the session second.
/// </summary>
internal sealed class SftpDownloadStream(SftpClient session, SftpFileStream content) : Stream
{
    private bool _disposed;

    /// <summary>
    /// The far side's size as the server reported it, not as this process has seen it. A caller that needs the count
    /// for a receipt can read it without reading the file.
    /// </summary>
    public override long Length => content.Length;

    public override long Position
    {
        get => content.Position;
        set => content.Position = value;
    }

    // Readable and seekable, never writable: the shape a download has.
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed && content.CanSeek;
    public override bool CanWrite => false;

    public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        content.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        content.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => content.Seek(offset, origin);

    public override void Flush() { }

    public override void SetLength(long value) =>
        throw new NotSupportedException("A download is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A download is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            // Stream first, session second: the other order would pull the channel out from under a read that is
            // still in flight, which the library reports as a broken connection rather than as what happened.
            try { content.Dispose(); } catch (Exception ex) when (ex is not OperationCanceledException) { }
            try { session.Dispose(); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await content.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        try { session.Dispose(); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
