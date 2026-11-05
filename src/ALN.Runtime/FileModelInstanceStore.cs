using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Runtime;

/// <summary>
/// A model provider's configuration, as its own module directory: <c>modules/model.openai-chat#one/module.json</c>
/// (docs/22 §2).
///
/// It exists as a type rather than as a lambda in the host because two callers write it — startup reads it and the
/// API edits it — and a format owned by one of them is a format the other will eventually disagree with. The same
/// record the loader deserializes is what a save writes back, so a round trip through the settings page cannot
/// quietly drop a field.
///
/// Why the directory and not <c>models/&lt;category&gt;/&lt;instance&gt;.json</c>: an instance is a node, a node's
/// directory is named by its id, and there is exactly one file per node. A provider whose configuration lived
/// somewhere else would be the one node whose directory says nothing — and the day something asks "what is in
/// <c>modules/</c>", the answer would be a lie.
/// </summary>
public sealed class FileModelInstanceStore(string root) : IModelInstanceStore
{
    /// <summary>The prefix every model provider type starts with; the store only reads files that name one.</summary>
    private const string TypePrefix = "model.";

    private const int MaxFileBytes = 256 * 1024;
    private const string ModulesDirectory = "modules";
    private const string ManifestFile = "module.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.GetFullPath(root);

    public async ValueTask<Result<IReadOnlyList<ModelInstanceEntry>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var entries = new List<ModelInstanceEntry>();
        var modules = Path.Combine(_root, ModulesDirectory);
        if (!Directory.Exists(modules)) return Result<IReadOnlyList<ModelInstanceEntry>>.Success(entries);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(modules).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, ManifestFile);
                if (!File.Exists(path)) continue;
                var read = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
                // Not a model provider's file: adapters, channels and sessions live here too, and each of them is
                // somebody else's to read. A file this store cannot parse *as a provider* is not its to complain
                // about either — except when it names a model type, which is the "invalid" case below.
                if (read.Error is { } error)
                {
                    if (await NamesModelTypeAsync(path, cancellationToken).ConfigureAwait(false))
                        return Result<IReadOnlyList<ModelInstanceEntry>>.Failure(error);
                    continue;
                }
                if (read.Value is null) continue;
                entries.Add(read.Value);
            }
            return Result<IReadOnlyList<ModelInstanceEntry>>.Success(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result<IReadOnlyList<ModelInstanceEntry>>.Failure(new(ErrorCode.Unavailable, "Model configuration could not be read.", ex.Message)); }
    }

    public async ValueTask<Result<ModelInstanceSpec?>> ReadAsync(string provider, CancellationToken cancellationToken)
    {
        if (!TryProvider(provider, out var id))
            return Result<ModelInstanceSpec?>.Failure(new(ErrorCode.Invalid, $"Not a model provider address: {provider}"));
        var path = PathFor(id);
        if (!File.Exists(path)) return Result<ModelInstanceSpec?>.Success(null);
        var read = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (read.Error is { } error) return Result<ModelInstanceSpec?>.Failure(error);
        // Not a model provider's file, or one that names a different node than the directory it sits in: either way
        // there is no configuration for this address, and inventing one would configure the wrong thing.
        if (read.Value is null) return Result<ModelInstanceSpec?>.Success(null);
        return string.Equals(read.Value.Provider.ToString(), id, StringComparison.Ordinal)
            ? Result<ModelInstanceSpec?>.Success(read.Value.Spec)
            : Result<ModelInstanceSpec?>.Failure(new(ErrorCode.Invalid,
                $"{path} names \"{read.Value.Provider}\", not \"{id}\"."));
    }

    public async ValueTask<Result> SaveAsync(string provider, ModelInstanceSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!TryProvider(provider, out var id))
            return Result.Failure(new(ErrorCode.Invalid, $"Not a model provider address: {provider}"));
        if (spec.Configuration.ValueKind != JsonValueKind.Object)
            return Result.Failure(new(ErrorCode.Invalid, "Model provider configuration must be a JSON object."));
        try
        {
            var directory = Path.Combine(_root, ModulesDirectory, id);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ManifestFile);
            // Written aside and moved into place: a half-written module.json is a module that will not start and a
            // file that looks fine.
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(ManifestOf(id, spec), JsonOptions),
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result.Failure(new(ErrorCode.Unavailable, "Model configuration could not be written.", ex.Message)); }
    }

    public ValueTask<Result> DeleteAsync(string provider, CancellationToken cancellationToken)
    {
        if (!TryProvider(provider, out var id))
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"Not a model provider address: {provider}")));
        try
        {
            var directory = Path.Combine(_root, ModulesDirectory, id);
            var path = Path.Combine(directory, ManifestFile);
            if (File.Exists(path)) File.Delete(path);
            // The directory goes only when the configuration was the only thing in it. A provider that left a log or
            // a cache behind keeps its directory: deleting somebody else's files was never what this method promised.
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
            // Deleting what is not there is a success: "this provider should not exist" is a statement, not a query.
            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "Model configuration could not be deleted.", ex.Message))); }
    }

    private string PathFor(string id) => Path.Combine(_root, ModulesDirectory, id, ManifestFile);

    /// <summary>The file as the kernel would read it, with the model domain's payload under "config".</summary>
    private static object ManifestOf(string id, ModelInstanceSpec spec) => new
    {
        id,
        // The type is the part of the address before '#', which is how the address was built: a provider's kind *is*
        // its type tail ("model.openai-chat" ⇄ kind "openai-chat"). Not parsed from anything else.
        type = TypePrefix + KindOf(id),
        config = new
        {
            enabled = spec.Enabled,
            configuration = spec.Configuration,
            profiles = spec.Profiles
        }
    };

    private static string KindOf(string id)
    {
        var hash = id.IndexOf('#');
        var module = hash < 0 ? id : id[..hash];
        return module.StartsWith(TypePrefix, StringComparison.Ordinal) ? module[TypePrefix.Length..] : module;
    }

    /// <summary>
    /// An address is a module id, so it is checked the way a directory name is: one segment, no separators, not
    /// "..". The name is the identity; nothing here reads a category out of it.
    /// </summary>
    private static bool TryProvider(string provider, out string id)
    {
        id = provider?.Trim() ?? "";
        if (!ModuleId.TryParse(id, out var parsed)) return false;
        return string.Equals(parsed.Value, id, StringComparison.Ordinal) &&
               id.Contains('#', StringComparison.Ordinal) &&
               id.StartsWith(TypePrefix, StringComparison.Ordinal);
    }

    private static async ValueTask<Result<ModelInstanceEntry?>> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaxFileBytes)
            return Result<ModelInstanceEntry?>.Failure(new(ErrorCode.Invalid, $"Model configuration exceeds {MaxFileBytes / 1024} KB: {path}"));
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!TryText(root, "id", out var id) || !TryText(root, "type", out var type))
                return Result<ModelInstanceEntry?>.Failure(new(ErrorCode.Invalid, $"Model configuration needs \"id\" and \"type\": {path}"));
            if (!type.StartsWith(TypePrefix, StringComparison.Ordinal))
                return Result<ModelInstanceEntry?>.Success(null);
            if (!Address.TryParse(id, out var address) || !address.IsInstance)
                return Result<ModelInstanceEntry?>.Failure(new(ErrorCode.Invalid, $"Model configuration names an address that cannot be one: {id} ({path})"));
            if (!root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
                return Result<ModelInstanceEntry?>.Failure(new(ErrorCode.Invalid, $"Model configuration has no \"config\" object: {path}"));
            if (!config.TryGetProperty("configuration", out var provider) || provider.ValueKind != JsonValueKind.Object)
                return Result<ModelInstanceEntry?>.Failure(new(ErrorCode.Invalid, $"Model provider configuration must be a JSON object: {path}"));
            var spec = new ModelInstanceSpec(provider.Clone(),
                ReadProfiles(config),
                !(config.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False));
            return Result<ModelInstanceEntry?>.Success(new ModelInstanceEntry(type, address, spec));
        }
        catch (JsonException ex)
        { return Result<ModelInstanceEntry?>.Failure(new(ErrorCode.Invalid, $"Model configuration is not valid JSON: {path}", ex.Message)); }
    }

    private static IReadOnlyList<ModelProfile>? ReadProfiles(JsonElement config)
    {
        if (!config.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array) return null;
        var parsed = new List<ModelProfile>();
        foreach (var profile in profiles.EnumerateArray())
        {
            if (!TryText(profile, "id", out var id) || !TryText(profile, "modelId", out var model)) continue;
            SamplingSettings? sampling = null;
            if (profile.TryGetProperty("defaultSampling", out var value) && value.ValueKind == JsonValueKind.Object)
                sampling = value.Deserialize<SamplingSettings>(JsonOptions);
            // The provider is implied by the file the profile lives in, so it stays empty here and the loader fills it
            // from the address: a file that repeats its own address is a file that can disagree with its directory.
            parsed.Add(new ModelProfile(id, default, model, sampling));
        }
        return parsed;
    }

    /// <summary>Whether a file that failed to parse is one this store should be loud about.</summary>
    private static async ValueTask<bool> NamesModelTypeAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   TryText(document.RootElement, "type", out var type) &&
                   type.StartsWith(TypePrefix, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryText(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var found) ||
            found.ValueKind != JsonValueKind.String) return false;
        value = found.GetString() ?? string.Empty;
        return value.Length > 0;
    }
}
