using System.Text.Json;

namespace ALN.Contracts.Models;

[Flags]
public enum ModelCapabilities
{
    None = 0,
    Streaming = 1,
    ToolCalls = 2,
    Vision = 4,
    Reasoning = 8
}

public sealed record ModelProviderDescriptor(
    string TypeId,
    string Category,
    string DisplayName,
    ModelCapabilities Capabilities);

public sealed record ModelProviderSpec(Address Address, string ConfigurationJson,
    /// <summary>The type this instance implements. A fact of the spec, never read out of the address.</summary>
    string TypeId);

public sealed record ModelProviderInfo(Address Address, ModelProviderDescriptor Descriptor, int InFlight);

public sealed record ModelProfile(
    string Id,
    Address Provider,
    string ModelId,
    SamplingSettings? DefaultSampling = null);

/// <summary>
/// One model provider instance as it is written on disk: <c>modules/&lt;type&gt;#&lt;instance&gt;/module.json</c>, with
/// the provider's own settings under <c>config.configuration</c> (docs/22 §2).
///
/// The file, not the running manager, is the thing an operator edits — a key is rotated in a file and the process
/// is restarted, because a provider holds its connection. Keeping the shape in the contracts is what lets the
/// startup loader and the API write the same file without either owning the other's format.
/// </summary>
/// <param name="Configuration">Provider configuration as the provider's own type expects it (endpoint, key, …).</param>
/// <param name="Profiles">Profiles this provider serves. Ids are global: "default" is resolved by name.</param>
/// <param name="Enabled">False keeps the file as documentation and loads nothing.</param>
public sealed record ModelInstanceSpec(JsonElement Configuration, IReadOnlyList<ModelProfile>? Profiles = null, bool Enabled = true);

/// <summary>One instance's file, named by the node it configures: <c>modules/model.openai-chat#one/module.json</c>.</summary>
/// <param name="TypeId">The provider type the file declares — what the manager builds, not something derived from the id.</param>
/// <param name="Provider">The instance address, which is also the module id and the directory name (docs/22 §2).</param>
public sealed record ModelInstanceEntry(string TypeId, Address Provider, ModelInstanceSpec Spec);

/// <summary>
/// The model configuration, as module directories. Nothing here knows how to talk to a provider.
///
/// Keyed by the provider's module id (its address) and not by category: a category is a grouping the settings page
/// uses, while the file has to sit in the directory named after the node it configures. Two keys for one thing is
/// how a save ends up writing a file the loader never reads.
/// </summary>
public interface IModelInstanceStore
{
    ValueTask<Result<IReadOnlyList<ModelInstanceEntry>>> LoadAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Null when no such file — an absent instance is not an error, but note that <c>Ok</c> is false for a null Value
    /// (that is how Result<T> is defined): decide with <c>Error is null</c>, not with <c>Ok</c>.
    /// </summary>
    ValueTask<Result<ModelInstanceSpec?>> ReadAsync(string provider, CancellationToken cancellationToken);

    /// <summary>Writes the file, creating the module directory when needed.</summary>
    ValueTask<Result> SaveAsync(string provider, ModelInstanceSpec spec, CancellationToken cancellationToken);

    /// <summary>Deletes the file (and the directory it was the only thing in). Absent is a success.</summary>
    ValueTask<Result> DeleteAsync(string provider, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes a configuration JSON while hiding the parts that are secrets.
///
/// Two rules, both about not making things worse:
/// <list type="bullet">
///   <item>Reading replaces the value of any key that looks like a credential with <c>***</c>. A screenshot of the
///   settings page should not be a key leak, and neither should a bug report.</item>
///   <item>Writing treats an incoming <c>***</c> as "unchanged". Without that rule every save would silently erase
///   the key it could not display, which is the one bug a masked form reliably causes.</item>
/// </list>
/// </summary>
public static class ModelSecrets
{
    public const string Placeholder = "***";

    private static readonly string[] SecretWords = ["apikey", "token", "secret", "password", "credentials"];

    public static bool LooksSecret(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return SecretWords.Any(word => normalized.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>The JSON with credential values replaced. Non-object input is returned as it came.</summary>
    public static JsonElement Mask(JsonElement configuration)
    {
        if (configuration.ValueKind != JsonValueKind.Object) return configuration;
        var masked = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in configuration.EnumerateObject())
        {
            masked[property.Name] = LooksSecret(property.Name) && property.Value.ValueKind == JsonValueKind.String
                ? JsonSerializer.SerializeToElement(Placeholder)
                : Mask(property.Value);
        }
        return JsonSerializer.SerializeToElement(masked);
    }

    /// <summary>
    /// The JSON to store: <paramref name="incoming"/> with every <c>***</c> replaced by what <paramref name="existing"/>
    /// had at the same path. A credential the caller did not see is a credential the caller did not mean to change.
    /// </summary>
    public static JsonElement Restore(JsonElement incoming, JsonElement existing)
    {
        if (incoming.ValueKind != JsonValueKind.Object) return incoming;
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in incoming.EnumerateObject())
        {
            var previous = existing.ValueKind == JsonValueKind.Object && existing.TryGetProperty(property.Name, out var found)
                ? found
                : (JsonElement?)null;
            merged[property.Name] = property.Value.ValueKind == JsonValueKind.String &&
                                    property.Value.GetString() == Placeholder && previous is { } kept
                ? kept
                : Restore(property.Value, previous ?? default);
        }
        return JsonSerializer.SerializeToElement(merged);
    }
}

public sealed record ModelRequest(
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<ModelToolDefinition>? Tools = null,
    SamplingSettings? Sampling = null)
{
    public string ModelId { get; init; } = "";
}

public sealed record ModelMessage(
    string Role,
    string? Content,
    IReadOnlyList<ModelToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null);

public sealed record ModelToolDefinition(string Name, string Description, string ParametersJson);
public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);

public sealed record SamplingSettings(
    double? Temperature = null,
    int? MaxOutputTokens = null,
    double? TopP = null,
    IReadOnlyList<string>? Stop = null);

public abstract record ModelEvent;
public sealed record ThinkingDelta(string Text) : ModelEvent;
public sealed record TextDelta(string Text) : ModelEvent;
public sealed record ToolCallDelta(int Index, string? CallId, string? Name, string ArgumentsJsonFragment) : ModelEvent;
public sealed record ModelCompleted(StopReason Reason, ModelUsage Usage, string? ProviderResponseId = null) : ModelEvent;
public sealed record ModelFailed(Error Error) : ModelEvent;

public sealed record ModelUsage(int InputTokens, int OutputTokens, int? CacheReadTokens = null, int? CacheWriteTokens = null);

public enum StopReason { EndTurn, ToolUse, MaxTokens, ContentFilter, StopSequence }

public sealed record ModelCallRecord(
    string CallId,
    DateTimeOffset At,
    string ProfileId,
    Address Provider,
    string ModelId,
    ModelRequest Request,
    string? ResponseText,
    IReadOnlyList<ModelToolCall> ToolCalls,
    StopReason? StopReason,
    ModelUsage? Usage,
    double DurationMs,
    int EventCount,
    string? Error,
    string? ArtifactPath = null);

public interface IModelProvider : IAsyncDisposable, IAddressed, IModule
{
    ModelProviderDescriptor Describe { get; }
    IAsyncEnumerable<ModelEvent> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
}

public interface IModelProviderFactory
{
    string TypeId { get; }
    ModelProviderDescriptor Describe { get; }
    ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken);
}

public sealed record ModelProviderTypeInfo(string TypeId, string Category, string DisplayName, ModelCapabilities Capabilities);

public interface IModelManager
{
    Result RegisterFactory(string packageId, IModelProviderFactory factory);
    Result RemovePackageFactories(string packageId);
    /// <summary>The type this provider implements, as declared in its spec.</summary>
    /// <summary>Stand-ins (test fakes) may legitimately know nothing; a real manager overrides this.</summary>
    string? TypeOf(Address address) => null;
    IReadOnlyList<ModelProviderTypeInfo> ListTypes();

    ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken);
    ValueTask<Result<IModelProvider>> CreateAsync(string category, string instance, string configurationJson, CancellationToken cancellationToken);
    ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken);
    Result<IModelProvider> Get(Address provider);
    IReadOnlyList<ModelProviderInfo> List();

    Result RegisterProfile(ModelProfile profile);
    Result RemoveProfile(string profileId);
    IReadOnlyList<ModelProfile> ListProfiles();
    ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request, CancellationToken cancellationToken);
}
