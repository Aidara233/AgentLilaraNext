using System.Collections.Concurrent;
using System.Text;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>
/// The one implementation of <see cref="IFileSystemAccess"/>: a scope is a directory under the data root, and every
/// path is resolved inside it.
///
/// Two rules live here and nowhere else, because getting either wrong is invisible until a file is gone:
/// <list type="bullet">
/// <item><b>Containment</b>: the absolute path is checked to be inside its scope, and reparse points are refused, so
/// a <c>..</c> that survived the contract's rule still cannot leave the scope, and a symlink cannot point out.</item>
/// <item><b>Durability</b>: writes are temp-then-rename and flushed to the device, appends take a per-file lock.
/// A caller that gets a success has a file that survives a power cut, not merely a process exit.</item>
/// </list>
///
/// Text only: a file with NUL bytes or invalid UTF-8 is refused rather than guessed at, because a model that reads
/// mojibake will confidently reason about it.
/// </summary>
public sealed class LocalFileSystemAccess : IFileSystemAccess, IBinaryFileAccess
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly char[] Separators = ['/', '\\'];

    private readonly string _root;
    private readonly IReadOnlyList<string> _scopes;
    private readonly IReadOnlyList<string> _binaryScopes;
    private readonly IReadOnlyList<string> _readOnlyScopes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _appendLocks = new(StringComparer.Ordinal);

    /// <param name="dataRoot">Where the host keeps its data; scopes are directories directly under it.</param>
    /// <param name="scopes">
    /// The scopes this instance may touch. A scope's name is one path segment (it becomes a directory name), and it is
    /// registered here rather than derived: "which directories exist" is the host's decision (C-20).
    /// </param>
    /// <param name="binaryScopes">
    /// The subset of <paramref name="scopes"/> that accept bytes as well as text. Empty by default: a directory is a
    /// text room until somebody says otherwise, because "the model can park a tarball here" is a different grant from
    /// "the model can keep notes here" (docs/24 §5).
    /// </param>
    /// <param name="readOnlyScopes">
    /// The subset of <paramref name="scopes"/> that a caller may read but never write: today that is
    /// <c>media</c>, the area the host fills (docs/28). Its purpose is not tidiness — a session's record names the
    /// files it looked at, so a caller that could rewrite one could change what it is recorded as having seen.
    /// Enforced here rather than in the tools because there is more than one way in (text and bytes), and two entry
    /// points with two rules is how one of them ends up forgetting.
    /// </param>
    public LocalFileSystemAccess(string dataRoot, IReadOnlyList<string> scopes,
        IReadOnlyList<string>? binaryScopes = null, IReadOnlyList<string>? readOnlyScopes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(scopes);
        _root = Path.GetFullPath(dataRoot);

        var registered = new List<string>(scopes.Count);
        foreach (var scope in scopes)
        {
            // Refused, not rewritten: a scope that needed fixing is a configuration mistake, and fixing it here would
            // mean the host and this class disagree about which directory a caller can reach.
            //
            // A scope is a relative path, not necessarily one segment. "scripts/draft" is a directory under the data
            // root like any other, and naming it that way keeps "the folder the model may write" visibly apart from
            // "the folder the catalogue loads" without introducing a second root.
            if (FilePaths.Split(scope) is not { Ok: true } split || split.Value!.Count == 0)
                throw new ArgumentException($"A scope must be a legal relative path: '{scope}'.", nameof(scopes));
            var canonical = string.Join("/", split.Value);
            if (registered.Contains(canonical, StringComparer.Ordinal))
                throw new ArgumentException($"Scope is registered twice: '{scope}'.", nameof(scopes));
            registered.Add(canonical);
        }
        _scopes = registered.AsReadOnly();

        var binary = new List<string>();
        foreach (var scope in binaryScopes ?? [])
        {
            var canonical = FilePaths.Split(scope) is { Ok: true } split && split.Value!.Count > 0
                ? string.Join("/", split.Value)
                : scope;
            // Refused rather than dropped: a binary scope that does not exist reads exactly like "transfers are broken".
            if (!registered.Contains(canonical, StringComparer.Ordinal))
                throw new ArgumentException($"'{scope}' is not a registered scope, so it cannot allow binary.", nameof(binaryScopes));
            binary.Add(canonical);
        }
        _binaryScopes = binary.AsReadOnly();

        var readOnly = new List<string>();
        foreach (var scope in readOnlyScopes ?? [])
        {
            var canonical = FilePaths.Split(scope) is { Ok: true } split && split.Value!.Count > 0
                ? string.Join("/", split.Value)
                : scope;
            // Same rule as the binary ones: an unregistered read-only scope is a configuration mistake that would
            // otherwise behave exactly like "the model is allowed to write there".
            if (!registered.Contains(canonical, StringComparer.Ordinal))
                throw new ArgumentException($"'{scope}' is not a registered scope, so it cannot be read-only.", nameof(readOnlyScopes));
            readOnly.Add(canonical);
        }
        _readOnlyScopes = readOnly.AsReadOnly();

        // Created once, like the mailbox's folders: a caller may ask what is in an empty scope, and "the directory
        // has not been made yet" is not that answer.
        foreach (var scope in _scopes)
        {
            var directory = Path.Combine(_root, scope);
            SafeFileSystem.EnsureDirectory(directory);
        }
    }

    public IReadOnlyList<string> Scopes => _scopes;

    public IReadOnlyList<string> BinaryScopes => _binaryScopes;

    /// <summary>The scopes that may be read but never written. Empty unless the host said otherwise.</summary>
    public IReadOnlyList<string> ReadOnlyScopes => _readOnlyScopes;

    /// <summary>
    /// The refusal every write path shares. A read-only scope is not "a scope with a permission bit missing": the
    /// caller is told which area it is and where its own files belong, because "denied" with no alternative is how a
    /// model retries the same thing three times.
    /// </summary>
    private Error? RefuseWrite(string scope)
    {
        if (!_readOnlyScopes.Contains(scope, StringComparer.Ordinal)) return null;
        var writable = _scopes.Where(s => !_readOnlyScopes.Contains(s, StringComparer.Ordinal)).ToArray();
        return new(ErrorCode.Denied,
            $"Scope '{scope}' is read-only: the host writes it, and a session only reads it. "
            + (writable.Length == 0 ? "No scope accepts writes." : $"Writable scopes: {string.Join(", ", writable)}."));
    }

    /// <summary>
    /// Opens a file as bytes. Same containment as every other operation, plus one more refusal: the scope has to be one
    /// that accepts binary at all. Reads are streamed, not slurped, so a 64 MiB transfer costs a buffer rather than 64 MiB
    /// of managed heap per retry.
    /// </summary>
    public ValueTask<Result<Stream>> OpenReadAsync(string scope, string path, CancellationToken cancellationToken) =>
        OpenReadStreamAsync(scope, path, IBinaryFileAccess.MaxTransferBytes, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result<Stream>> OpenReadStreamAsync(string scope, string path, long ceiling,
        CancellationToken cancellationToken) =>
        Run(cancellationToken, () =>
        {
            if (!_binaryScopes.Contains(scope, StringComparer.Ordinal))
                return Result<Stream>.Failure(new(ErrorCode.Denied,
                    $"Scope '{scope}' does not allow binary transfers. Scopes that do: "
                    + (_binaryScopes.Count == 0 ? "(none)" : string.Join(", ", _binaryScopes)) + "."));
            if (Resolve(scope, path, out var absolute, out var denied) is false) return Result<Stream>.Failure(denied!);
            if (Directory.Exists(absolute))
                return Result<Stream>.Failure(new(ErrorCode.Invalid, $"'{Canonical(scope, path)}' is a directory."));
            if (!File.Exists(absolute))
                return Result<Stream>.Failure(new(ErrorCode.Invalid, $"'{Canonical(scope, path)}' does not exist."));
            var length = new FileInfo(absolute).Length;
            if (length > ceiling)
                return Result<Stream>.Failure(new(ErrorCode.Invalid,
                    $"'{Canonical(scope, path)}' is {length} bytes, more than the {ceiling} allowed."));
            // FileShare.Read: two readers are fine, a writer is not — an upload reading a half-written file would send
            // half a file and call it done.
            return Result<Stream>.Success(new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read));
        });

    /// <summary>
    /// Streams bytes into a file: temporary file beside the target, then rename. Never buffers the whole thing, and a
    /// caller that dies mid-way leaves the old file untouched.
    /// </summary>
    public ValueTask<Result> SaveAsync(string scope, string path, Stream content, CancellationToken cancellationToken) =>
        SaveCoreAsync(scope, path, content, asHost: false, cancellationToken);

    /// <summary>
    /// The host's own write path: the one way into a read-only scope.
    ///
    /// <c>internal</c> on purpose — it is on no interface a module can hold, so "the host fills media, a model only
    /// reads it" is a fact about the assembly rather than a rule somebody has to remember to check. Everything else is
    /// the same code as <see cref="SaveAsync"/>: same scope check, same containment, same rename-into-place.
    /// </summary>
    internal ValueTask<Result> SaveAsHostAsync(string scope, string path, Stream content, CancellationToken cancellationToken) =>
        SaveCoreAsync(scope, path, content, asHost: true, cancellationToken);

    private ValueTask<Result> SaveCoreAsync(string scope, string path, Stream content, bool asHost,
        CancellationToken cancellationToken) =>
        Run(cancellationToken, () =>
        {
            ArgumentNullException.ThrowIfNull(content);
            if (!asHost && RefuseWrite(scope) is { } readOnly) return Result.Failure(readOnly);
            if (!_binaryScopes.Contains(scope, StringComparer.Ordinal))
                return Result.Failure(new(ErrorCode.Denied,
                    $"Scope '{scope}' does not allow binary transfers. Scopes that do: "
                    + (_binaryScopes.Count == 0 ? "(none)" : string.Join(", ", _binaryScopes)) + "."));
            if (Resolve(scope, path, out var absolute, out var denied) is false) return Result.Failure(denied!);
            if (Directory.Exists(absolute))
                return Result.Failure(new(ErrorCode.Invalid, $"'{Canonical(scope, path)}' is a directory."));

            var scopeRoot = Path.Combine(_root, scope);
            var parent = Path.GetDirectoryName(absolute)!;
            SafeFileSystem.EnsureDirectory(parent);
            SafeFileSystem.VerifyNewOrRegularFile(absolute, scopeRoot);

            var temporary = Path.Combine(parent, $".{Path.GetFileName(absolute)}.{Guid.NewGuid():N}.tmp");
            SafeFileSystem.VerifyNewOrRegularFile(temporary, scopeRoot);
            try
            {
                long written = 0;
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81_920];
                    int read;
                    while ((read = content.ReadAsync(buffer, cancellationToken).AsTask().GetAwaiter().GetResult()) > 0)
                    {
                        written += read;
                        // Checked as it goes: refusing after the disk is full is not a refusal, it is a mess.
                        if (written > IBinaryFileAccess.MaxTransferBytes)
                            return Result.Failure(new(ErrorCode.Invalid,
                                $"The content is larger than the {IBinaryFileAccess.MaxTransferBytes} allowed for one transfer."));
                        stream.Write(buffer, 0, read);
                    }
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, absolute, overwrite: true);
                return Result.Success();
            }
            finally
            {
                if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { }
            }
        });

    public ValueTask<Result<IReadOnlyList<FileEntry>>> ListAsync(string scope, string path, CancellationToken cancellationToken) =>
        Run(cancellationToken, () =>
        {
            if (Resolve(scope, path, out var directory, out var denied) is false) return Result<IReadOnlyList<FileEntry>>.Failure(denied!);
            if (!Directory.Exists(directory))
                return Result<IReadOnlyList<FileEntry>>.Failure(new(ErrorCode.Invalid, $"No such directory: {Canonical(scope, path)}"));
            SafeFileSystem.VerifyDirectory(directory);
            var entries = new List<FileEntry>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(e => e, StringComparer.Ordinal))
            {
                var info = new FileSystemInfo[] { new FileInfo(entry), new DirectoryInfo(entry) }
                    .First(i => i.Exists);
                if (info is FileInfo file && (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    return Result<IReadOnlyList<FileEntry>>.Failure(new(ErrorCode.Invalid,
                        $"'{file.Name}' is a link; links are not part of this surface.", file.FullName));
                entries.Add(Describe(info));
            }
            return Result<IReadOnlyList<FileEntry>>.Success(entries);
        });

    public ValueTask<Result<IReadOnlyList<FileNode>>> TreeAsync(string scope, string path, CancellationToken cancellationToken) =>
        Run(cancellationToken, () =>
        {
            if (Resolve(scope, path, out var start, out var denied) is false) return Result<IReadOnlyList<FileNode>>.Failure(denied!);
            if (!Directory.Exists(start))
                return Result<IReadOnlyList<FileNode>>.Failure(new(ErrorCode.Invalid, $"No such directory: {Canonical(scope, path)}"));
            SafeFileSystem.VerifyDirectory(start);
            var nodes = new List<FileNode>();
            Walk(start, "", nodes);
            return nodes.Count > IFileSystemAccess.MaxTreeNodes
                ? Result<IReadOnlyList<FileNode>>.Failure(new(ErrorCode.Invalid,
                    $"Subtree is larger than {IFileSystemAccess.MaxTreeNodes} entries; ask for a smaller directory."))
                : Result<IReadOnlyList<FileNode>>.Success(nodes);

            void Walk(string directory, string prefix, List<FileNode> collected)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(e => e, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = new FileSystemInfo[] { new FileInfo(entry), new DirectoryInfo(entry) }.First(i => i.Exists);
                    var relative = prefix.Length == 0 ? info.Name : $"{prefix}/{info.Name}";
                    collected.Add(new FileNode(relative, info is DirectoryInfo, info is FileInfo file ? file.Length : 0,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                    if (collected.Count > IFileSystemAccess.MaxTreeNodes) return;
                    // Not followed: a walk that followed links could leave the scope and could not be bounded.
                    if (info is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                        Walk(sub.FullName, relative, collected);
                }
            }
        });

    public ValueTask<Result<FileText?>> ReadAsync(string scope, string path, CancellationToken cancellationToken) =>
        Run(cancellationToken, () =>
        {
            if (Resolve(scope, path, out var absolute, out var denied) is false) return Result<FileText?>.Failure(denied!);
            if (Directory.Exists(absolute))
                return Result<FileText?>.Failure(new(ErrorCode.Invalid, $"'{Canonical(scope, path)}' is a directory."));
            if (!File.Exists(absolute)) return Result<FileText?>.Success(null);
            SafeFileSystem.VerifyFile(absolute, Path.Combine(_root, scope));
            var info = new FileInfo(absolute);
            if (info.Length > IFileSystemAccess.MaxFileBytes)
                return Result<FileText?>.Failure(new(ErrorCode.Invalid,
                    $"File is larger than {IFileSystemAccess.MaxFileBytes} bytes: {info.Length}."));
            return ReadText(absolute, scope, path) is { } text
                ? Result<FileText?>.Success(text)
                : Result<FileText?>.Failure(new(ErrorCode.Invalid,
                    $"'{Canonical(scope, path)}' is not UTF-8 text.", absolute));
        });

    public ValueTask<Result<FileEntry?>> StatAsync(string scope, string path, CancellationToken cancellationToken) =>
        Run(cancellationToken, () =>
        {
            if (Resolve(scope, path, out var absolute, out var denied) is false) return Result<FileEntry?>.Failure(denied!);
            if (File.Exists(absolute))
            {
                if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
                    return Result<FileEntry?>.Failure(new(ErrorCode.Invalid, "That entry is a link; links are not part of this surface."));
                return Result<FileEntry?>.Success(Describe(new FileInfo(absolute)));
            }
            if (Directory.Exists(absolute)) return Result<FileEntry?>.Success(Describe(new DirectoryInfo(absolute)));
            return Result<FileEntry?>.Success(null);
        });

    public ValueTask<Result<FileText>> WriteAsync(string scope, string path, string text, CancellationToken cancellationToken) =>
        Run(cancellationToken, () =>
        {
            ArgumentNullException.ThrowIfNull(text);
            if (RefuseWrite(scope) is { } readOnly) return Result<FileText>.Failure(readOnly);
            if (Encoding.UTF8.GetByteCount(text) > IFileSystemAccess.MaxFileBytes)
                return Result<FileText>.Failure(new(ErrorCode.Invalid, $"Text is larger than {IFileSystemAccess.MaxFileBytes} bytes."));
            if (Resolve(scope, path, out var absolute, out var denied) is false) return Result<FileText>.Failure(denied!);
            if (Directory.Exists(absolute))
                return Result<FileText>.Failure(new(ErrorCode.Invalid, $"'{Canonical(scope, path)}' is a directory."));

            var scopeRoot = Path.Combine(_root, scope);
            var parent = Path.GetDirectoryName(absolute)!;
            SafeFileSystem.EnsureDirectory(parent);
            SafeFileSystem.VerifyNewOrRegularFile(absolute, scopeRoot);

            // Temp-then-rename in the same directory: the rename is atomic, so a reader sees the old file or the new
            // one and never a half of either. The flush is to the device, not just to the OS — a success that a power
            // cut erases is not a success this system is allowed to report.
            var temporary = Path.Combine(parent, $".{Path.GetFileName(absolute)}.{Guid.NewGuid():N}.tmp");
            SafeFileSystem.VerifyNewOrRegularFile(temporary, scopeRoot);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, absolute, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { }
            }
            return Result<FileText>.Success(new FileText(FilePaths.Canonical(path) ?? path, text, Encoding.UTF8.GetByteCount(text),
                new DateTimeOffset(File.GetLastWriteTimeUtc(absolute), TimeSpan.Zero)));
        });

    public ValueTask<Result<long>> AppendAsync(string scope, string path, string text, CancellationToken cancellationToken) =>
        Run(cancellationToken, () =>
        {
            ArgumentNullException.ThrowIfNull(text);
            if (RefuseWrite(scope) is { } readOnly) return Result<long>.Failure(readOnly);
            if (Resolve(scope, path, out var absolute, out var denied) is false) return Result<long>.Failure(denied!);
            if (Directory.Exists(absolute))
                return Result<long>.Failure(new(ErrorCode.Invalid, $"'{Canonical(scope, path)}' is a directory."));

            var scopeRoot = Path.Combine(_root, scope);
            var parent = Path.GetDirectoryName(absolute)!;
            SafeFileSystem.EnsureDirectory(parent);

            // One lock per file, held across open-write-flush: two callers appending at the same moment must produce
            // two whole runs, not an interleaving that neither of them wrote.
            var gate = _appendLocks.GetOrAdd(absolute, _ => new SemaphoreSlim(1, 1));
            gate.Wait(cancellationToken);
            try
            {
                SafeFileSystem.VerifyNewOrRegularFile(absolute, scopeRoot);
                var bytes = Encoding.UTF8.GetBytes(text);
                using var stream = new FileStream(absolute, FileMode.Append, FileAccess.Write, FileShare.None);
                if (stream.Length + bytes.Length > IFileSystemAccess.MaxFileBytes)
                    return Result<long>.Failure(new(ErrorCode.Invalid,
                        $"Appending would take the file past {IFileSystemAccess.MaxFileBytes} bytes; consolidate or archive it."));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
                return Result<long>.Success(stream.Length);
            }
            finally { gate.Release(); }
        });

    public ValueTask<Result> MoveAsync(string scope, string from, string to, CancellationToken cancellationToken) =>
        MoveCoreAsync(scope, from, to, asHost: false, cancellationToken);

    /// <summary>The host's own rename, for filling a read-only scope. See <see cref="SaveAsHostAsync"/> for why this is internal.</summary>
    internal ValueTask<Result> MoveAsHostAsync(string scope, string from, string to, CancellationToken cancellationToken) =>
        MoveCoreAsync(scope, from, to, asHost: true, cancellationToken);

    private ValueTask<Result> MoveCoreAsync(string scope, string from, string to, bool asHost,
        CancellationToken cancellationToken) =>
        Run(cancellationToken, () =>
        {
            if (!asHost && RefuseWrite(scope) is { } readOnly) return Result.Failure(readOnly);
            if (Resolve(scope, from, out var source, out var fromDenied) is false) return Result.Failure(fromDenied!);
            if (Resolve(scope, to, out var destination, out var toDenied) is false) return Result.Failure(toDenied!);
            var scopeRoot = Path.Combine(_root, scope);
            if (!File.Exists(source) && !Directory.Exists(source))
                return Result.Failure(new(ErrorCode.Invalid, $"Nothing to move: {Canonical(scope, from)}"));
            var parent = Path.GetDirectoryName(destination)!;
            // The host's own moves name their destination after its content, which means the shard folder may not exist
            // yet. A caller's move still has to say where it is going — that rule is about not inventing a directory a
            // person did not ask for, and the host inventing one is the whole point of content addressing.
            if (asHost) SafeFileSystem.EnsureDirectory(parent);
            if (!Directory.Exists(parent))
                return Result.Failure(new(ErrorCode.Invalid, $"Destination directory does not exist: {Canonical(scope, to)}"));
            if (File.Exists(destination) || Directory.Exists(destination))
                return Result.Failure(new(ErrorCode.Conflict, $"Destination already exists: {Canonical(scope, to)}"));
            SafeFileSystem.VerifyNewOrRegularFile(destination, scopeRoot);
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
            return Result.Success();
        });

    public ValueTask<Result> DeleteAsync(string scope, string path, bool recursive, CancellationToken cancellationToken) =>
        DeleteCoreAsync(scope, path, recursive, asHost: false, cancellationToken);

    /// <summary>The host's own delete, for tidying up after a failed ingest in a read-only scope.</summary>
    internal ValueTask<Result> DeleteAsHostAsync(string scope, string path, CancellationToken cancellationToken) =>
        DeleteCoreAsync(scope, path, recursive: false, asHost: true, cancellationToken);

    private ValueTask<Result> DeleteCoreAsync(string scope, string path, bool recursive, bool asHost,
        CancellationToken cancellationToken) =>
        Run(cancellationToken, () =>
        {
            if (!asHost && RefuseWrite(scope) is { } readOnly) return Result.Failure(readOnly);
            if (Resolve(scope, path, out var absolute, out var denied) is false) return Result.Failure(denied!);
            if (File.Exists(absolute))
            {
                SafeFileSystem.VerifyFile(absolute, Path.Combine(_root, scope));
                File.Delete(absolute);
                return Result.Success();
            }
            if (!Directory.Exists(absolute)) return Result.Success();   // Deleting what is not there is a statement, not a request to check.
            if (Path.GetFullPath(absolute) == Path.Combine(_root, scope))
                return Result.Failure(new(ErrorCode.Invalid, "A scope root cannot be deleted."));
            SafeFileSystem.VerifyDirectory(absolute);
            if (!recursive && Directory.EnumerateFileSystemEntries(absolute).Any())
                return Result.Failure(new(ErrorCode.Conflict,
                    $"Directory is not empty: {Canonical(scope, path)}. Pass recursive to delete the subtree."));
            Directory.Delete(absolute, recursive);
            return Result.Success();
        });

    // ---- internals ---------------------------------------------------------------------------

    private static FileEntry Describe(FileSystemInfo info) => new(
        info.Name,
        info is DirectoryInfo,
        info is FileInfo file ? file.Length : 0,
        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));

    /// <summary>Scope check, path check, and the absolute path — or the reason it is refused.</summary>
    private bool Resolve(string scope, string path, out string absolute, out Error? denied)
    {
        absolute = "";
        denied = null;
        if (scope is null || !_scopes.Contains(scope, StringComparer.Ordinal))
        {
            denied = new(ErrorCode.Denied, $"Scope '{scope}' is not one this caller may use.");
            return false;
        }
        var split = FilePaths.Split(path);
        if (!split.Ok)
        {
            denied = split.Error;
            return false;
        }
        var root = Path.Combine(_root, scope);
        absolute = split.Value!.Count == 0 ? root : Path.Combine([root, .. split.Value]);
        return true;
    }

    private static string Canonical(string scope, string path) =>
        FilePaths.Canonical(path) is { Length: > 0 } canonical ? $"{scope}/{canonical}" : scope;

    private static FileText? ReadText(string absolute, string scope, string path)
    {
        var bytes = File.ReadAllBytes(absolute);
        if (bytes.AsSpan().IndexOf((byte)0) >= 0) return null;
        try
        {
            var text = StrictUtf8.GetString(bytes);
            return new FileText(FilePaths.Canonical(path) ?? path, text, bytes.Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(absolute), TimeSpan.Zero));
        }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>
    /// Runs one synchronous file operation off the caller's thread and turns the file system's exceptions into
    /// results. Every refusal above is a result; an exception here is the disk itself saying no, which is
    /// <see cref="ErrorCode.Unavailable"/> — retryable, and not the caller's fault.
    /// </summary>
    private static async ValueTask<Result<T>> Run<T>(CancellationToken cancellationToken, Func<Result<T>> operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or System.Security.SecurityException or ArgumentException)
        {
            return Result<T>.Failure(new(ErrorCode.Unavailable, "File operation failed.", ex.Message));
        }
    }

    private static async ValueTask<Result> Run(CancellationToken cancellationToken, Func<Result> operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or System.Security.SecurityException or ArgumentException)
        {
            return Result.Failure(new(ErrorCode.Unavailable, "File operation failed.", ex.Message));
        }
    }
}
