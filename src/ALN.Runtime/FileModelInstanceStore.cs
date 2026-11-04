using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Runtime;

/// <summary>
/// The model configuration tree: one JSON file per provider instance, under <c>models/&lt;category&gt;/&lt;instance&gt;.json</c>.
///
/// It exists as a type rather than as a lambda in the host because two callers now write it — startup reads it and
/// the API edits it — and a format owned by one of them is a format the other will eventually disagree with. The
/// same record the loader deserializes is what a save writes back, so a round trip through the settings page
/// cannot quietly drop a field.
/// </summary>
public sealed class FileModelInstanceStore(string root) : IModelInstanceStore
{
    /// <summary>Long enough for a version tag, short enough to be a file name a person can read.</summary>
    private const int MaxNameBytes = 64;
    private const int MaxFileBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.GetFullPath(root);

    public async ValueTask<Result<IReadOnlyList<ModelInstanceEntry>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var entries = new List<ModelInstanceEntry>();
        if (!Directory.Exists(_root)) return Result<IReadOnlyList<ModelInstanceEntry>>.Success(entries);
        try
        {
            foreach (var categoryDirectory in Directory.EnumerateDirectories(_root).Order(StringComparer.Ordinal))
            {
                var category = Path.GetFileName(categoryDirectory);
                if (!IsName(category)) continue;
                foreach (var path in Directory.EnumerateFiles(categoryDirectory, "*.json", SearchOption.TopDirectoryOnly)
                             .Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var instance = Path.GetFileNameWithoutExtension(path);
                    if (!IsName(instance)) continue;
                    var read = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
                    if (!read.Ok || read.Value is null) return Result<IReadOnlyList<ModelInstanceEntry>>.Failure(read.Error!);
                    entries.Add(new ModelInstanceEntry(category, instance, read.Value));
                }
            }
            return Result<IReadOnlyList<ModelInstanceEntry>>.Success(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result<IReadOnlyList<ModelInstanceEntry>>.Failure(new(ErrorCode.Unavailable, "Model configuration could not be read.", ex.Message)); }
    }

    public async ValueTask<Result<ModelInstanceSpec?>> ReadAsync(string category, string instance, CancellationToken cancellationToken)
    {
        if (!IsName(category) || !IsName(instance))
            return Result<ModelInstanceSpec?>.Failure(new(ErrorCode.Invalid, $"Not a usable model instance name: {category}/{instance}"));
        var path = PathFor(category, instance);
        if (!File.Exists(path)) return Result<ModelInstanceSpec?>.Success(null);
        return await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result> SaveAsync(string category, string instance, ModelInstanceSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!IsName(category) || !IsName(instance))
            return Result.Failure(new(ErrorCode.Invalid, $"Not a usable model instance name: {category}/{instance}"));
        if (spec.Configuration.ValueKind != JsonValueKind.Object)
            return Result.Failure(new(ErrorCode.Invalid, "Model provider configuration must be a JSON object."));
        try
        {
            var directory = Path.Combine(_root, category);
            Directory.CreateDirectory(directory);
            var path = PathFor(category, instance);
            // Written aside and moved into place: a half-written configuration is a process that will not start
            // and a file that looks fine.
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(spec, JsonOptions), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result.Failure(new(ErrorCode.Unavailable, "Model configuration could not be written.", ex.Message)); }
    }

    public ValueTask<Result> DeleteAsync(string category, string instance, CancellationToken cancellationToken)
    {
        if (!IsName(category) || !IsName(instance))
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"Not a usable model instance name: {category}/{instance}")));
        try
        {
            var path = PathFor(category, instance);
            if (File.Exists(path)) File.Delete(path);
            // Deleting what is not there is a success: "this provider should not exist" is a statement, not a query.
            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "Model configuration could not be deleted.", ex.Message))); }
    }

    private string PathFor(string category, string instance) => Path.Combine(_root, category, instance + ".json");

    private static async ValueTask<Result<ModelInstanceSpec?>> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaxFileBytes)
            return Result<ModelInstanceSpec?>.Failure(new(ErrorCode.Invalid, $"Model configuration exceeds {MaxFileBytes / 1024} KB: {path}"));
        try
        {
            var spec = JsonSerializer.Deserialize<ModelInstanceSpec>(bytes, JsonOptions);
            if (spec is null)
                return Result<ModelInstanceSpec?>.Failure(new(ErrorCode.Invalid, $"Model configuration is incomplete: {path}"));
            if (spec.Configuration.ValueKind != JsonValueKind.Object)
                return Result<ModelInstanceSpec?>.Failure(new(ErrorCode.Invalid, $"Model provider configuration must be a JSON object: {path}"));
            return Result<ModelInstanceSpec?>.Success(spec);
        }
        catch (JsonException ex)
        { return Result<ModelInstanceSpec?>.Failure(new(ErrorCode.Invalid, $"Model configuration is not valid JSON: {path}", ex.Message)); }
    }

    /// <summary>
    /// A category or instance name is a path segment, so it is checked as one: no separators, no dots that mean
    /// "the directory above", nothing that a file system would reinterpret.
    /// </summary>
    private static bool IsName(string? name) =>
        name is { Length: > 0 and <= MaxNameBytes } &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') &&
        name is not "." and not ".." &&
        !name.Contains("..", StringComparison.Ordinal);
}
