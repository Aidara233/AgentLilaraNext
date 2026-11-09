using System.Security.Cryptography;
using ALN.Contracts;

namespace ALN.Ssh;

/// <summary>
/// The other half of the two channels: private keys that outlive the process.
///
/// The directory is created 0700 and every file is created 0600 <i>at creation time</i> — not written and then
/// narrowed, because the window between those two moments is exactly the one that shows up in somebody else's dump.
/// Nothing here encrypts: the threat model says the host disk is the last line of defence, and a half-encrypted
/// format whose key also sits on the same disk would only buy the feeling of safety. A real secret store is a later
/// module; this is the plain implementation it will replace, behind the same interface.
/// </summary>
public sealed class FileSecretStore(string root) : ISecretStore
{
    private readonly string _root = Path.GetFullPath(root);

    /// <summary>Where keys live. Exposed so callers can say "put the file here" and be right.</summary>
    public string Root => _root;

    public async ValueTask<Result> PutAsync(string name, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
    {
        if (!SecretName.IsValid(name))
            return Result.Failure(new(ErrorCode.Invalid, SecretName.Complain(name)));
        try
        {
            Directory.CreateDirectory(_root);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var path = PathOf(name);
            // Create-then-write with the mode already set: an existing file keeps its mode, a new one never exists as
            // 0644 for even one syscall.
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(path, options))
            {
                await stream.WriteAsync(secret, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The message never repeats the secret; a path is fine, its contents are not.
            return Result.Failure(new(ErrorCode.Internal, $"Could not write key \"{name}\".", ex.Message));
        }
    }

    public ValueTask<Result<SecretContent>> OpenAsync(string name, CancellationToken cancellationToken)
    {
        if (!SecretName.IsValid(name))
            return ValueTask.FromResult(Result<SecretContent>.Failure(new(ErrorCode.Invalid, SecretName.Complain(name))));
        try
        {
            var path = PathOf(name);
            if (!File.Exists(path))
                return ValueTask.FromResult(Result<SecretContent>.Success(new SecretContent(null)));
            return ValueTask.FromResult(Result<SecretContent>.Success(new SecretContent(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Result<SecretContent>.Failure(new(ErrorCode.Internal, $"Could not read key \"{name}\".", ex.Message)));
        }
    }

    public ValueTask<Result<bool>> ExistsAsync(string name, CancellationToken cancellationToken)
    {
        if (!SecretName.IsValid(name))
            return ValueTask.FromResult(Result<bool>.Failure(new(ErrorCode.Invalid, SecretName.Complain(name))));
        return ValueTask.FromResult(Result<bool>.Success(File.Exists(PathOf(name))));
    }

    public ValueTask<Result> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        if (!SecretName.IsValid(name))
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, SecretName.Complain(name))));
        try
        {
            var path = PathOf(name);
            if (!File.Exists(path)) return ValueTask.FromResult(Result.Success());
            // Overwrite before removing: deletion alone leaves the blocks for whatever reads the disk next.
            var length = new FileInfo(path).Length;
            if (length > 0)
            {
                var options = new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Write };
                using var stream = new FileStream(path, options);
                stream.Write(new byte[length]);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(path);
            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Internal, $"Could not delete key \"{name}\".", ex.Message)));
        }
    }

    /// <summary>
    /// Names are already restricted by <see cref="SecretName"/>, so this cannot escape the root; the containment check
    /// is kept anyway because "cannot" is a claim about a validator someone may edit later.
    /// </summary>
    private string PathOf(string name)
    {
        var path = Path.GetFullPath(Path.Combine(_root, name));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Key name \"{name}\" escapes the key directory.");
        return path;
    }
}
