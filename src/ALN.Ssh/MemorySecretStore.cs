using System.Security.Cryptography;
using ALN.Contracts;

namespace ALN.Ssh;

/// <summary>
/// Private keys for one Session's live configuration: they exist in this dictionary and nowhere else, so the disk never
/// holds a copy and there is nothing to shred when the configuration is replaced.
///
/// Memory is not automatically safer than a 0600 file — the host disk is trusted here, and a process being inspected
/// loses either way. What this buys is a smaller window and no residue: a key that was only ever in RAM cannot be left
/// behind by a backup, a sync client, or somebody's <c>grep</c> three weeks later. That is worth the inconvenience of
/// regenerating after a restart, which is why it is the default and persisting is the exception.
/// </summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ValueTask<Result> PutAsync(string name, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
    {
        if (!SecretName.IsValid(name))
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, SecretName.Complain(name))));
        lock (_gate)
        {
            // Replacing is a write, not a leak: the old bytes are cleared before the handle is dropped, so a later dump
            // does not contain two generations of the same key.
            if (_secrets.TryGetValue(name, out var existing)) CryptographicOperations.ZeroMemory(existing);
            _secrets[name] = secret.ToArray();
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result<SecretContent>> OpenAsync(string name, CancellationToken cancellationToken)
    {
        if (!SecretName.IsValid(name))
            return ValueTask.FromResult(Result<SecretContent>.Failure(new(ErrorCode.Invalid, SecretName.Complain(name))));
        lock (_gate)
        {
            if (!_secrets.TryGetValue(name, out var bytes))
                return ValueTask.FromResult(Result<SecretContent>.Success(new SecretContent(null)));
            // A copy, so closing the stream cannot clear the store's only copy; the copy is what the SSH client is
            // expected to drop when it is done with it.
            return ValueTask.FromResult(Result<SecretContent>.Success(
                new SecretContent(new MemoryStream(bytes.ToArray(), writable: false))));
        }
    }

    public ValueTask<Result<bool>> ExistsAsync(string name, CancellationToken cancellationToken)
    {
        if (!SecretName.IsValid(name))
            return ValueTask.FromResult(Result<bool>.Failure(new(ErrorCode.Invalid, SecretName.Complain(name))));
        lock (_gate) return ValueTask.FromResult(Result<bool>.Success(_secrets.ContainsKey(name)));
    }

    public ValueTask<Result> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        if (!SecretName.IsValid(name))
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, SecretName.Complain(name))));
        lock (_gate)
        {
            if (_secrets.Remove(name, out var bytes)) CryptographicOperations.ZeroMemory(bytes);
        }
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>
/// The names a secret may have, checked in one place because every implementation has to agree: a name becomes a file
/// name in <see cref="FileSecretStore"/> and a dictionary key in <see cref="MemorySecretStore"/>, and a name that could
/// travel ("../x", "/etc/x", "a/b") would turn "which key" into "which file on this host".
/// </summary>
public static class SecretName
{
    public const int MaxLength = 64;

    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxLength
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static string Complain(string? name) =>
        $"A key name must be {MaxLength} characters or fewer, start with a letter or digit, and contain only letters, "
        + $"digits, '-', '_' and '.'. Got: \"{name}\".";
}
