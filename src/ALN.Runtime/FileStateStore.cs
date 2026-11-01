using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>
/// <see cref="IStateStore"/> on disk: <c>&lt;dataRoot&gt;/state/&lt;scope&gt;/&lt;key&gt;.json</c>.
///
/// Two levels of names, both validated as tokens, so a caller cannot climb out of the data root by
/// choosing a clever scope or key. Writes are write-then-rename: a crash costs one stale value, never a
/// half-parsed one.
/// </summary>
public sealed class FileStateStore(string root) : IStateStore
{
    public async ValueTask<Result<string?>> ReadAsync(string scope, string key, CancellationToken cancellationToken)
    {
        var path = PathFor(scope, key);
        if (path is null) return Result<string?>.Failure(new(ErrorCode.Invalid, $"Invalid state scope or key: {scope}/{key}"));
        try
        {
            if (!File.Exists(path)) return Result<string?>.Success(null);
            SafeFileSystem.VerifyNewOrRegularFile(path, root);
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return Result<string?>.Success(text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string?>.Failure(new(ErrorCode.Unavailable, "State value could not be read.", ex.Message));
        }
    }

    public async ValueTask<Result> WriteAsync(string scope, string key, string json, CancellationToken cancellationToken)
    {
        if (json is null) return Result.Failure(new(ErrorCode.Invalid, "State value must not be null."));
        var path = PathFor(scope, key);
        if (path is null) return Result.Failure(new(ErrorCode.Invalid, $"Invalid state scope or key: {scope}/{key}"));
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            // Each level is created (and verified) in turn: EnsureDirectory insists the parents already exist,
            // which is exactly what we want to keep a scope name from reaching outside the data root.
            SafeFileSystem.EnsureDirectory(root);
            SafeFileSystem.EnsureDirectory(directory);
            SafeFileSystem.VerifyNewOrRegularFile(path, directory);
            var temporary = path + ".partial";
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new(ErrorCode.Unavailable, "State value could not be written.", ex.Message));
        }
    }

    public ValueTask<Result> DeleteAsync(string scope, string key, CancellationToken cancellationToken)
    {
        var path = PathFor(scope, key);
        if (path is null) return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"Invalid state scope or key: {scope}/{key}")));
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "State value could not be deleted.", ex.Message)));
        }
    }

    private string? PathFor(string scope, string key)
    {
        if (!IsToken(scope) || !IsToken(key)) return null;
        return Path.Combine(root, scope, key + ".json");
    }

    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
        value is not "." and not "..";
}
