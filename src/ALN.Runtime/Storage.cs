using System.Security;
using System.Text;
using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime.Hosting;

public sealed class DataDirectory
{
    private DataDirectory(string root) => Root = root;
    public string Root { get; }

    public static DataDirectory Open(string? path = null)
    {
        var selected = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentLilaraNext")
            : path;
        var fullPath = Path.GetFullPath(selected, Environment.CurrentDirectory);
        SafeFileSystem.EnsureDirectory(fullPath);
        return new DataDirectory(fullPath);
    }
}

/// <summary>Host-owned file mailbox rooted at one adapter instance directory.</summary>
public sealed class FileMailbox : IFileMailbox
{
    private const int MaxInputBytes = 64 * 1024;
    private const int BatchSize = 64;
    private const string ReadySuffix = ".ready.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _root;
    private readonly string _inbox;
    private readonly string _processed;
    private readonly string _rejected;
    private readonly string _outbox;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public FileMailbox(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root, Environment.CurrentDirectory);
        SafeFileSystem.EnsureDirectory(_root);
        _inbox = Path.Combine(_root, "inbox");
        _processed = Path.Combine(_root, "processed");
        _rejected = Path.Combine(_root, "rejected");
        _outbox = Path.Combine(_root, "outbox");
        SafeFileSystem.EnsureDirectory(_inbox);
        SafeFileSystem.EnsureDirectory(_processed);
        SafeFileSystem.EnsureDirectory(_rejected);
        SafeFileSystem.EnsureDirectory(_outbox);
    }

    public string Root => _root;

    public async ValueTask<IReadOnlyList<MailboxInput>> ReadPendingAsync(CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeFileSystem.VerifyDirectory(_root);
            SafeFileSystem.VerifyDirectory(_inbox);
            var paths = Directory.EnumerateFiles(_inbox, "*" + ReadySuffix, SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .Take(BatchSize)
                .ToArray();
            var inputs = new List<MailboxInput>(paths.Length);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(path);
                var token = fileName[..^ReadySuffix.Length];
                if (!IsValidToken(token))
                    throw new IOException($"Invalid mailbox input token: {fileName}");
                SafeFileSystem.VerifyFile(path, _inbox);
                var bytes = await ReadAtMostAsync(path, MaxInputBytes + 1, cancellationToken).ConfigureAwait(false);
                string json;
                if (bytes.Length > MaxInputBytes)
                    json = "null"; // The adapter rejects this and records the oversized file in rejected/.
                else
                {
                    try { json = StrictUtf8.GetString(bytes); }
                    catch (DecoderFallbackException) { json = "null"; }
                }
                inputs.Add(new MailboxInput(token, json));
            }
            return inputs;
        }
        finally { _ioLock.Release(); }
    }

    public async ValueTask CompleteAsync(string token, bool accepted, string? error, CancellationToken cancellationToken)
    {
        ValidateToken(token);
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeFileSystem.VerifyDirectory(_root);
            SafeFileSystem.VerifyDirectory(_inbox);
            var source = Path.Combine(_inbox, token + ReadySuffix);
            var destinationDirectory = accepted ? _processed : _rejected;
            SafeFileSystem.VerifyDirectory(destinationDirectory);
            SafeFileSystem.VerifyFile(source, _inbox);
            var destination = Path.Combine(destinationDirectory, token + ".json");
            SafeFileSystem.VerifyNewFilePath(destination, destinationDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            // File.Move without overwrite leaves both records intact if a destination already exists.
            System.IO.File.Move(source, destination);
        }
        finally { _ioLock.Release(); }
    }

    public async ValueTask<string> WriteOutputAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(message.PlatformConversationId) || string.IsNullOrWhiteSpace(message.Text))
            throw new ArgumentException("Outbound conversation and text must be non-empty.", nameof(message));

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeFileSystem.VerifyDirectory(_root);
            SafeFileSystem.VerifyDirectory(_outbox);
            var attemptId = Guid.NewGuid().ToString("N");
            var destination = Path.Combine(_outbox, attemptId + ".json");
            var temporary = Path.Combine(_outbox, "." + attemptId + ".tmp");
            SafeFileSystem.VerifyNewFilePath(temporary, _outbox);
            SafeFileSystem.VerifyNewFilePath(destination, _outbox);
            var json = JsonSerializer.SerializeToUtf8Bytes(message);
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                SafeFileSystem.VerifyFile(temporary, _outbox);
                SafeFileSystem.VerifyNewFilePath(destination, _outbox);
                System.IO.File.Move(temporary, destination);
                return attemptId;
            }
            catch
            {
                // Only remove this invocation's private temporary file; never remove user data.
                if (System.IO.File.Exists(temporary))
                {
                    SafeFileSystem.VerifyFile(temporary, _outbox);
                    System.IO.File.Delete(temporary);
                }
                throw;
            }
        }
        finally { _ioLock.Release(); }
    }

    private static async ValueTask<byte[]> ReadAtMostAsync(string path, int limit, CancellationToken cancellationToken)
    {
        SafeFileSystem.VerifyFile(path, Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[limit];
        var count = 0;
        while (count < limit)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, limit - count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        return count == buffer.Length ? buffer : buffer[..count];
    }

    private static bool IsValidToken(string token) =>
        token.Length is > 0 and <= 128 && token.All(c =>
            c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    private static void ValidateToken(string token)
    {
        if (token is null || !IsValidToken(token))
            throw new ArgumentException("Mailbox token contains invalid characters.", nameof(token));
    }
}

/// <summary>Serialized, append-only JSON Lines audit output owned by the host.</summary>
public sealed class JsonlAuditSink : IAuditSink, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly SemaphoreSlim _appendLock = new(1, 1);
    private bool _disposed;

    public JsonlAuditSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path, Environment.CurrentDirectory);
        var parent = Path.GetDirectoryName(_path) ?? throw new ArgumentException("Audit path must have a parent directory.", nameof(path));
        SafeFileSystem.EnsureDirectory(parent);
        SafeFileSystem.VerifyNewOrRegularFile(_path, parent);
    }

    public async ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var parent = Path.GetDirectoryName(_path)!;
            SafeFileSystem.VerifyDirectory(parent);
            SafeFileSystem.VerifyNewOrRegularFile(_path, parent);
            var json = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            var line = new byte[json.Length + 1];
            json.CopyTo(line, 0);
            line[^1] = (byte)'\n';
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _appendLock.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _appendLock.Dispose();
    }
}

internal static class SafeFileSystem
{
    internal static void EnsureDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        VerifyExistingAncestors(full);
        Directory.CreateDirectory(full);
        VerifyDirectory(full);
    }

    internal static void VerifyDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        VerifyExistingAncestors(full);
        var info = new DirectoryInfo(full);
        if (!info.Exists) throw new DirectoryNotFoundException(full);
        RejectReparsePoint(info);
    }

    internal static void VerifyFile(string path, string scopedDirectory)
    {
        EnsureContained(path, scopedDirectory);
        VerifyDirectory(scopedDirectory);
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists) throw new FileNotFoundException("Mailbox file was not found.", path);
        RejectReparsePoint(info);
    }

    internal static void VerifyNewFilePath(string path, string scopedDirectory)
    {
        EnsureContained(path, scopedDirectory);
        VerifyDirectory(scopedDirectory);
        var full = Path.GetFullPath(path);
        if (System.IO.File.Exists(full) || Directory.Exists(full))
        {
            var info = new FileInfo(full);
            RejectReparsePoint(info);
            throw new IOException($"Destination already exists: {Path.GetFileName(full)}");
        }
    }

    internal static void VerifyNewOrRegularFile(string path, string scopedDirectory)
    {
        EnsureContained(path, scopedDirectory);
        VerifyDirectory(scopedDirectory);
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full)) throw new IOException("Audit path points to a directory.");
        if (System.IO.File.Exists(full)) RejectReparsePoint(new FileInfo(full));
    }

    private static void EnsureContained(string path, string scope)
    {
        var fullScope = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scope));
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullScope, fullPath);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new SecurityException("Path escapes its scoped directory.");
    }

    private static void VerifyExistingAncestors(string fullPath)
    {
        var current = Path.GetPathRoot(fullPath) ?? throw new IOException("Path has no root.");
        RejectIfReparse(current);
        var remainder = fullPath[current.Length..];
        foreach (var part in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current) || System.IO.File.Exists(current)) RejectIfReparse(current);
            else break;
        }
    }

    private static void RejectIfReparse(string path)
    {
        FileAttributes attributes;
        try { attributes = System.IO.File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException($"Reparse points are not allowed in scoped storage: {path}");
    }

    private static void RejectReparsePoint(FileSystemInfo info)
    {
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException($"Reparse points are not allowed in scoped storage: {info.FullName}");
    }
}
