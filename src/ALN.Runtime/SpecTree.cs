using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>
/// Spec documents on disk: one module directory per instance, named after its id, holding <c>module.json</c>
/// (docs/22 §2).
///
/// <b>The path says nothing about what a module is.</b> There is no category directory and no type directory — a
/// spec's type lives in the file, on the line that says <c>"type"</c>, and a store decides what to read by looking
/// there. The only thing the path carries is the name, which is why nothing has to be kept in sync between where a
/// file sits and what it contains.
///
/// The document is the kernel's shape with the domain's payload under <c>config</c>: <c>{"id","type","config"}</c>.
/// One file per node, readable by a person and by the loader that builds the node — a second file with a second
/// shape would be a second answer to "what is this instance".
/// </summary>
internal static class SpecTree
{
    public const string FileName = "module.json";
    public const string DirectoryName = "modules";

    /// <summary>One spec document plus where it was found, so the store can name the path in an error.</summary>
    /// <param name="Id">The node's id, which is also the directory name.</param>
    /// <param name="Type">The type the file declares — the store's filter, never derived from the id.</param>
    /// <param name="Config">The domain's own payload, deserialized from <c>config</c>.</param>
    internal sealed record Entry<T>(string Path, ModuleId Id, string Type, T Config);

    internal static async ValueTask<Result> WriteAsync<T>(string root, ModuleId id, string typeId, T config,
        JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        if (!ModuleId.IsLegal(id.Value))
            return Result.Failure(new(ErrorCode.Invalid, $"Module id is not a file-safe name: {id.Value}"));
        if (string.IsNullOrWhiteSpace(typeId))
            return Result.Failure(new(ErrorCode.Invalid, $"A module needs a type: {id.Value}"));
        var directory = Path.Combine(Path.Combine(root, DirectoryName), id.Value);
        try
        {
            SafeFileSystem.EnsureDirectory(directory);
            var path = Path.Combine(directory, FileName);
            SafeFileSystem.VerifyNewOrRegularFile(path, directory);
            var temporary = path + ".partial";
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = id.Value,
                type = typeId,
                config
            }, options), cancellationToken).ConfigureAwait(false);
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
        var directory = Path.Combine(Path.Combine(root, DirectoryName), id.Value);
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
    /// Every spec under <c>root/modules</c> whose <c>type</c> the caller claims. The type is read from the file,
    /// never from the directory it sits in — which is what lets one tree hold adapters, channels, sessions and
    /// providers together, each store picking its own kind.
    /// </summary>
    internal static async ValueTask<Result<IReadOnlyList<Entry<T>>>> LoadAllAsync<T>(string root,
        Func<string, bool> belongs, CancellationToken cancellationToken)
    {
        var modules = Path.Combine(root, DirectoryName);
        if (!Directory.Exists(modules)) return Result<IReadOnlyList<Entry<T>>>.Success([]);
        try
        {
            // A file loose in the tree cannot be found by name and would read as "nothing configured".
            foreach (var stray in Directory.EnumerateFiles(modules, "*.json", SearchOption.TopDirectoryOnly))
                return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid,
                    $"Module file is not in a module directory: {stray}; modules live at {modules}/<id>/{FileName}."));

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var entries = new List<Entry<T>>();
            foreach (var directory in Directory.EnumerateDirectories(modules).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, FileName);
                if (!File.Exists(path)) continue;
                SafeFileSystem.VerifyNewOrRegularFile(path, modules);
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

                using var document = JsonDocument.Parse(bytes);
                var (id, type) = ReadIdentity(document.RootElement);
                if (string.IsNullOrWhiteSpace(type))
                    return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid, $"Module has no type: {path}"));
                if (!belongs(type)) continue;
                if (!ModuleId.TryParse(id, out var moduleId) ||
                    !string.Equals(moduleId.Value, Path.GetFileName(directory), StringComparison.Ordinal))
                    return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid,
                        $"Module id \"{id}\" does not match its directory \"{Path.GetFileName(directory)}\" ({path})"));
                if (!document.RootElement.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
                    return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid, $"Module has no config object: {path}"));

                var parsed = config.Deserialize<T>(options);
                if (parsed is null)
                    return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid, $"Module config is empty: {path}"));
                entries.Add(new Entry<T>(path, moduleId, type, parsed));
            }
            return Result<IReadOnlyList<Entry<T>>>.Success(entries);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid, "Specs could not be read.", ex.Message));
        }
    }

    /// <summary>
    /// The id and type of a module file. Case-insensitive on the property names, because a person edits these by
    /// hand and "Type" is the same word — but the values are read exactly as written.
    /// </summary>
    private static (string Id, string Type) ReadIdentity(JsonElement root)
    {
        string id = "";
        string type = "";
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase)) id = property.Value.GetString() ?? "";
            else if (string.Equals(property.Name, "type", StringComparison.OrdinalIgnoreCase)) type = property.Value.GetString() ?? "";
        }
        return (id, type);
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
