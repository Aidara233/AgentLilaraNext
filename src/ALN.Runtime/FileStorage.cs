using System.Collections.Concurrent;
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
{    private const int MaxInputBytes = 64 * 1024;
    /// <summary>
    /// How big a file may be when it is not text. A picture a person pasted or dropped is at most a few megabytes; the
    /// store refuses anything past its own ceiling, and this is the same order of magnitude, so the mailbox cannot be
    /// used to make the host read a disk image into memory before saying no.
    /// </summary>
    private const int MaxMediaInputBytes = (int)IMediaStore.MaxIngestBytes;
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

    /// <summary>
    /// Claims every readable file in the inbox and hands back its text.
    ///
    /// The file is the message: its content is what was said, and the name is only the way it was dropped in. So the id
    /// is assigned here rather than parsed out of the name, and two folders may hold a file of the same name without
    /// one shadowing the other. Empty files are left where they are — a file that is still being saved looks empty, and
    /// taking it now would send half a message and delete the rest of what is coming.
    /// </summary>
    public async ValueTask<IReadOnlyList<MailboxInput>> ReadPendingAsync(CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeFileSystem.VerifyDirectory(_root);
            SafeFileSystem.VerifyDirectory(_inbox);
            var candidates = new List<(string ConversationId, string Directory, string Path)>();
            foreach (var conversationDirectory in Directory.EnumerateDirectories(_inbox, "*", SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conversationId = Path.GetFileName(conversationDirectory);
                // A folder whose name cannot be a conversation is not input. Skipped, not thrown: one stray folder used
                // to make every poll fail for as long as it stayed there, which is a mailbox that silently stops.
                if (!IsValidToken(conversationId)) continue;
                SafeFileSystem.VerifyDirectory(conversationDirectory);
                foreach (var path in Directory.EnumerateFiles(conversationDirectory, "*", SearchOption.TopDirectoryOnly)
                             .Order(StringComparer.Ordinal))
                {
                    if (IsTransient(Path.GetFileName(path))) continue;
                    candidates.Add((conversationId, conversationDirectory, path));
                }
            }

            var inputs = new List<MailboxInput>(Math.Min(candidates.Count, BatchSize));
            foreach (var item in candidates.Take(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SafeFileSystem.VerifyFile(item.Path, item.Directory);
                byte[] bytes;
                try { bytes = await ReadAtMostAsync(item.Path, MaxMediaInputBytes + 1, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                // Text first, always: a message is what somebody typed, and the mailbox's oldest rule ("the file's text
                // is the message") does not change because pictures are allowed now. Only bytes that refuse to be text
                // get the second question asked — and then what answers it is the header, never the file name.
                string? text = null;
                if (bytes.Length <= MaxInputBytes)
                {
                    try { text = StrictUtf8.GetString(bytes); }
                    catch (DecoderFallbackException) { /* not text: ask what it is */ }
                }
                IReadOnlyList<InboundAttachment>? attachments = null;
                if (text is null)
                {
                    var info = MediaProbe.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, MediaProbe.HeaderBytes)));
                    if (info.Kind == MediaKind.File)
                    {
                        // Unidentifiable bytes are still refused, and still said so: the alternative is a mailbox that
                        // quietly turns a half-written binary into a message the model reads as code.
                        Reject(item.ConversationId, item.Path, bytes.Length > MaxInputBytes
                            ? $"Input exceeds {MaxInputBytes} bytes and is not a recognised medium."
                            : "Input is not valid UTF-8.");
                        continue;
                    }
                    // A picture, a voice note, a file — it goes on to be stored and referenced, and the model is told
                    // what it was even when it cannot look at it (docs/28 §4).
                    attachments = [new InboundAttachment(info.Kind, info.MediaType, bytes)];
                    text = "";
                }
                // Still being written, or deliberately empty: either way it is not a message yet.
                if (attachments is null && string.IsNullOrWhiteSpace(text)) continue;

                var messageId = Guid.NewGuid().ToString("N");
                var claimedDirectory = Path.Combine(_inflight, item.ConversationId);
                SafeFileSystem.EnsureDirectory(claimedDirectory);
                var claimed = Path.Combine(claimedDirectory, messageId + ".json");
                SafeFileSystem.VerifyNewFilePath(claimed, claimedDirectory);
                // Claim it: the move is what stops a second reader from publishing the same input twice, and what
                // leaves it recoverable if this process dies mid-round (ReclaimAsync puts it back).
                try { System.IO.File.Move(item.Path, claimed); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                inputs.Add(new(item.ConversationId, messageId, text, attachments));
            }
            return inputs;
        }
        finally { _ioLock.Release(); }
    }

    /// <summary>
    /// A file that is not finished: an editor's swap file, a dotfile, a half-written .tmp. Never input, never reported.
    /// </summary>
    private static bool IsTransient(string fileName) =>
        fileName.StartsWith('.') || fileName.EndsWith('~')
        || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses one file in place, keeping the original name and writing the reason next to it. The reason is the point:
    /// a rejected file with no explanation is a file somebody will put back unchanged.
    /// </summary>
    private void Reject(string conversationId, string path, string reason)
    {
        try
        {
            var directory = Path.Combine(_rejected, conversationId);
            SafeFileSystem.EnsureDirectory(directory);
            var id = Guid.NewGuid().ToString("N")[..8];
            var name = Path.GetFileNameWithoutExtension(path) + "-" + id + ".txt";
            var destination = Path.Combine(directory, name);
            SafeFileSystem.VerifyNewFilePath(destination, directory);
            System.IO.File.Move(path, destination);
            System.IO.File.WriteAllText(Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + ".error.txt"), reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Refusing to move it is not a reason to fail the poll: it stays in the inbox and is looked at again.
        }
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
            // The reason travels with the file. A rejected input with no explanation is one somebody puts back
            // unchanged, and then again.
            if (!accepted && !string.IsNullOrWhiteSpace(error))
            {
                var reasonPath = Path.Combine(destinationDirectory, token + ".error.txt");
                try { await System.IO.File.WriteAllTextAsync(reasonPath, error, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
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

/// <summary>
/// The host's mailboxes, one per account, under <c>&lt;dataRoot&gt;/io/file/&lt;account&gt;</c>.
///
/// The account id becomes a path segment, so it is validated as one here rather than trusted: it comes from a spec a
/// person or a model wrote, and a name with a directory separator in it would put one account's mail inside another's.
/// Two Adapters with the same account share a mailbox on purpose — that is what "same account" means — and asking twice
/// returns the same object rather than two readers of one directory.
/// </summary>
public sealed class FileMailboxFactory(string dataRoot) : IFileMailboxFactory
{
    private readonly ConcurrentDictionary<string, FileMailbox> _mailboxes = new(StringComparer.Ordinal);

    public string Root { get; } = dataRoot;

    public IFileMailbox For(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 128 ||
            accountId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException($"Not a usable account id for a mailbox: {accountId}", nameof(accountId));
        return _mailboxes.GetOrAdd(accountId, name => new FileMailbox(Path.Combine(Root, "io", "file", name)));
    }
}
