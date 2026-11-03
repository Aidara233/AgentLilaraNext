using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>
/// Spec documents on disk: one directory per module instance, named after the id, holding a <c>spec.json</c>.
///
/// <b>The path says nothing about what a module is.</b> There is no category directory and no type directory —
/// a spec's type lives in the file, on the line that says <c>"type"</c>, and a store decides what to read by
/// looking there. The only thing the path carries is the name, which is why nothing has to be kept in sync
/// between where a file sits and what it contains.
/// </summary>
internal static class SpecTree
{
    public const string FileName = "spec.json";

    /// <summary>One spec document plus where it was found, so the store can name the path in an error.</summary>
    internal sealed record Entry<T>(string Path, T Document);

    internal static async ValueTask<Result> WriteAsync<T>(string root, ModuleId id, T document,
        JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        if (!ModuleId.IsLegal(id.Value))
            return Result.Failure(new(ErrorCode.Invalid, $"Module id is not a file-safe name: {id.Value}"));
        var directory = Path.Combine(root, id.Value);
        try
        {
            SafeFileSystem.EnsureDirectory(directory);
            var path = Path.Combine(directory, FileName);
            SafeFileSystem.VerifyNewOrRegularFile(path, directory);
            var temporary = path + ".partial";
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(document, options), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new(ErrorCode.Unavailable, "Spec could not be written.", ex.Message));
        }
    }

    internal static async ValueTask<Result> DeleteAsync(string root, ModuleId id, CancellationToken cancellationToken)
    {
        if (!ModuleId.IsLegal(id.Value))
            return Result.Failure(new(ErrorCode.Invalid, $"Module id is not a file-safe name: {id.Value}"));
        var directory = Path.Combine(root, id.Value);
        try
        {
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                SafeFileSystem.VerifyNewOrRegularFile(path, directory);
                File.Delete(path);
            }
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            RemoveIfEmpty(directory);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new(ErrorCode.Unavailable, "Spec could not be deleted.", ex.Message));
        }
    }

    /// <summary>
    /// Every spec under the root whose <c>type</c> the caller claims. The type is read from the file, never from
    /// the directory it sits in — which is what lets one directory hold adapters, channels and models together.
    /// </summary>
    internal static async ValueTask<Result<IReadOnlyList<Entry<T>>>> LoadAllAsync<T>(string root,
        Func<string, bool> belongs, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return Result<IReadOnlyList<Entry<T>>>.Success([]);
        try
        {
            // A file loose in the root cannot be found by name and would read as "nothing configured".
            foreach (var stray in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
                return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid,
                    $"Spec file is not in an instance directory: {stray}; specs live at {root}/<id>/{FileName}."));

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var entries = new List<Entry<T>>();
            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, FileName);
                if (!File.Exists(path)) continue;
                SafeFileSystem.VerifyNewOrRegularFile(path, root);
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

                var type = ReadType(bytes);
                if (string.IsNullOrWhiteSpace(type))
                    return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid,
                        $"Spec has no type: {path}"));
                if (!belongs(type)) continue;

                var document = JsonSerializer.Deserialize<T>(bytes, options);
                if (document is null)
                    return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid, $"Spec is empty: {path}"));
                entries.Add(new Entry<T>(path, document));
            }
            return Result<IReadOnlyList<Entry<T>>>.Success(entries);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid, "Specs could not be read.", ex.Message));
        }
    }

    private static string? ReadType(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        // Case-insensitive because a person edits this file by hand and "Type" is the same word.
        foreach (var property in document.RootElement.EnumerateObject())
            if (string.Equals(property.Name, "type", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }

    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory that cannot be removed is not a failure: the spec is gone, which is what was asked.
        }
    }
}
