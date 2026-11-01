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
    private readonly string _inflight;
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
        // Claimed but not yet acknowledged: a file sits here between "the Adapter took it" and "the event
        // that carries it was handled". It is the only directory whose contents are unfinished work.
        _inflight = Path.Combine(_root, "inflight");
        _processed = Path.Combine(_root, "processed");
        _rejected = Path.Combine(_root, "rejected");
        _outbox = Path.Combine(_root, "outbox");
        SafeFileSystem.EnsureDirectory(_inbox);
        SafeFileSystem.EnsureDirectory(_inflight);
        SafeFileSystem.EnsureDirectory(_processed);
        SafeFileSystem.EnsureDirectory(_rejected);
        SafeFileSystem.EnsureDirectory(_outbox);
    }

    public string Root => _root;

    public async ValueTask<IReadOnlyList<string>> GetConversationIdsAsync(CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeFileSystem.VerifyDirectory(_root);
            SafeFileSystem.VerifyDirectory(_inbox);
            var conversations = Directory.EnumerateDirectories(_inbox, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
            foreach (var directory in conversations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateConversationId(Path.GetFileName(directory));
                SafeFileSystem.VerifyDirectory(directory);
            }
            return conversations.Select(directory => Path.GetFileName(directory)!).ToArray();
        }
        finally { _ioLock.Release(); }
    }

    public async ValueTask<IReadOnlyList<MailboxInput>> ReadPendingAsync(CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeFileSystem.VerifyDirectory(_root);
            SafeFileSystem.VerifyDirectory(_inbox);
            var paths = new List<(string ConversationId, string Directory, string Path)>();
            foreach (var conversationDirectory in Directory.EnumerateDirectories(_inbox, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conversationId = Path.GetFileName(conversationDirectory);
                ValidateConversationId(conversationId);
                SafeFileSystem.VerifyDirectory(conversationDirectory);
                foreach (var path in Directory.EnumerateFiles(conversationDirectory, "*" + ReadySuffix, SearchOption.TopDirectoryOnly))
                    paths.Add((conversationId, conversationDirectory, path));
            }
            var batch = paths.OrderBy(p => p.ConversationId, StringComparer.Ordinal)
                .ThenBy(p => Path.GetFileName(p.Path), StringComparer.Ordinal).Take(BatchSize).ToArray();
            var inputs = new List<MailboxInput>(batch.Length);
            foreach (var item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(item.Path);
                var token = fileName[..^ReadySuffix.Length];
                ValidateToken(token);
                SafeFileSystem.VerifyFile(item.Path, item.Directory);
                // Claim it: the rename is what stops a second reader from publishing the same input twice,
                // and what leaves it recoverable if this process dies mid-round.
                var claimedDirectory = Path.Combine(_inflight, item.ConversationId);
                SafeFileSystem.EnsureDirectory(claimedDirectory);
                var claimed = Path.Combine(claimedDirectory, token + ".json");
                SafeFileSystem.VerifyNewFilePath(claimed, claimedDirectory);
                System.IO.File.Move(item.Path, claimed);
                var bytes = await ReadAtMostAsync(claimed, MaxInputBytes + 1, cancellationToken).ConfigureAwait(false);
                string json;
                if (bytes.Length > MaxInputBytes) json = "null";
                else
                {
                    try { json = StrictUtf8.GetString(bytes); }
                    catch (DecoderFallbackException) { json = "null"; }
                }
                inputs.Add(new(item.ConversationId, token, json));
            }
            return inputs;
        }
        finally { _ioLock.Release(); }
    }

    public async ValueTask CompleteAsync(string conversationId, string token, bool accepted, string? error, CancellationToken cancellationToken)
    {
        ValidateConversationId(conversationId);
        ValidateToken(token);
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeFileSystem.VerifyDirectory(_root);
            // A claimed input lives in inflight; an unclaimed one (rejected before publication) is still in
            // the inbox. Both are completed the same way, so callers never have to know which it was.
            var inflightDirectory = Path.Combine(_inflight, conversationId);
            var srcDirectory = Path.Combine(_inbox, conversationId);
            string source;
            string sourceDirectory;
            if (System.IO.File.Exists(Path.Combine(inflightDirectory, token + ".json")))
            {
                sourceDirectory = inflightDirectory;
                source = Path.Combine(sourceDirectory, token + ".json");
            }
            else
            {
                sourceDirectory = srcDirectory;
                source = Path.Combine(sourceDirectory, token + ReadySuffix);
            }
            SafeFileSystem.VerifyDirectory(sourceDirectory);
            var destinationRoot = accepted ? _processed : _rejected;
            SafeFileSystem.EnsureDirectory(destinationRoot);
            var destinationDirectory = Path.Combine(destinationRoot, conversationId);
            SafeFileSystem.EnsureDirectory(destinationDirectory);
            SafeFileSystem.VerifyFile(source, sourceDirectory);
            var destination = Path.Combine(destinationDirectory, token + ".json");
            SafeFileSystem.VerifyNewFilePath(destination, destinationDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            System.IO.File.Move(source, destination);
        }
        finally { _ioLock.Release(); }
    }

    public async ValueTask<int> ReclaimAsync(CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeFileSystem.VerifyDirectory(_root);
            SafeFileSystem.VerifyDirectory(_inflight);
            var reclaimed = 0;
            foreach (var conversationDirectory in Directory.EnumerateDirectories(_inflight, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conversationId = Path.GetFileName(conversationDirectory);
                ValidateConversationId(conversationId);
                SafeFileSystem.VerifyDirectory(conversationDirectory);
                var inboxDirectory = Path.Combine(_inbox, conversationId);
                SafeFileSystem.EnsureDirectory(inboxDirectory);
                foreach (var path in Directory.EnumerateFiles(conversationDirectory, "*.json", SearchOption.TopDirectoryOnly).ToArray())
                {
                    var token = Path.GetFileName(path)[..^".json".Length];
                    if (ValidateTokenOrFalse(token) is false) continue;
                    var destination = Path.Combine(inboxDirectory, token + ReadySuffix);
                    SafeFileSystem.VerifyFile(path, conversationDirectory);
                    // Back to "ready": whatever claimed this input is gone, so it has to be offered again.
                    System.IO.File.Move(path, destination);
                    reclaimed++;
                }
            }
            return reclaimed;
        }
        finally { _ioLock.Release(); }
    }

    private static bool ValidateTokenOrFalse(string token)
    {
        try { ValidateToken(token); return true; }
        catch (Exception) { return false; }
    }

    public async ValueTask ReleaseAsync(string conversationId, string token, CancellationToken cancellationToken)
    {
        ValidateConversationId(conversationId);
        ValidateToken(token);
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var claimedDirectory = Path.Combine(_inflight, conversationId);
            var claimed = Path.Combine(claimedDirectory, token + ".json");
            if (!System.IO.File.Exists(claimed)) return;
            var inboxDirectory = Path.Combine(_inbox, conversationId);
            SafeFileSystem.EnsureDirectory(inboxDirectory);
            SafeFileSystem.VerifyFile(claimed, claimedDirectory);
            // Back to "ready": the publication it was claimed for did not happen, so this is a retry.
            System.IO.File.Move(claimed, Path.Combine(inboxDirectory, token + ReadySuffix));
        }
        finally { _ioLock.Release(); }
    }

    public async ValueTask<string> WriteOutputAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateConversationId(message.PlatformConversationId);
        if (string.IsNullOrWhiteSpace(message.Text))
            throw new ArgumentException("Outbound text must be non-empty.", nameof(message));

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeFileSystem.VerifyDirectory(_root);
            var conversationDirectory = Path.Combine(_outbox, message.PlatformConversationId);
            SafeFileSystem.EnsureDirectory(conversationDirectory);
            var attemptId = Guid.NewGuid().ToString("N");
            var destination = Path.Combine(conversationDirectory, attemptId + ".json");
            var temporary = Path.Combine(conversationDirectory, "." + attemptId + ".tmp");
            SafeFileSystem.VerifyNewFilePath(temporary, conversationDirectory);
            SafeFileSystem.VerifyNewFilePath(destination, conversationDirectory);
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
                SafeFileSystem.VerifyFile(temporary, conversationDirectory);
                SafeFileSystem.VerifyNewFilePath(destination, conversationDirectory);
                System.IO.File.Move(temporary, destination);
                return attemptId;
            }
            catch
            {
                if (System.IO.File.Exists(temporary))
                {
                    SafeFileSystem.VerifyFile(temporary, conversationDirectory);
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

    private static void ValidateConversationId(string conversationId)
    {
        if (conversationId is null || !IsValidToken(conversationId))
            throw new ArgumentException("Conversation ids must be 1–128 ASCII letters, digits, '-' or '_'.", nameof(conversationId));
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
