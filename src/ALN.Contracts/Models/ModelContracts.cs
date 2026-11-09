using System.Text.Json;

namespace ALN.Contracts.Models;

[Flags]
public enum ModelCapabilities
{
    None = 0,
    Streaming = 1,
    ToolCalls = 2,
    /// <summary>Pictures may be put in front of this model. Declared on a profile, not on a provider (see <see cref="ModelProfile.Capabilities"/>).</summary>
    Vision = 4,
    Reasoning = 8
}

/// <summary>
/// A capability set is read off a configuration file by a person, so it is spelled: <c>"Streaming, Vision"</c> rather
/// than <c>5</c>. Both are accepted on the way in; only the name is written.
/// </summary>
public static class ModelCapabilitiesJson
{
    public static string Spell(ModelCapabilities capabilities)
    {
        if (capabilities == ModelCapabilities.None) return "None";
        var names = new List<string>(4);
        if (capabilities.HasFlag(ModelCapabilities.Streaming)) names.Add("Streaming");
        if (capabilities.HasFlag(ModelCapabilities.ToolCalls)) names.Add("ToolCalls");
        if (capabilities.HasFlag(ModelCapabilities.Vision)) names.Add("Vision");
        if (capabilities.HasFlag(ModelCapabilities.Reasoning)) names.Add("Reasoning");
        return string.Join(", ", names);
    }
}

/// <summary>
/// Reads a profile's capabilities as names (<c>"Streaming, Vision"</c>) or as a number, and always writes names.
///
/// The file is written by a person and read by a person, so <c>5</c> is the wrong thing to require them to compute;
/// the number is still accepted because a file written by an older build (or by a script) should keep working.
/// </summary>
public sealed class ModelCapabilitiesJsonConverter : System.Text.Json.Serialization.JsonConverter<ModelCapabilities?>
{
    public override ModelCapabilities? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null) return null;
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number) return (ModelCapabilities)reader.GetInt32();
        if (reader.TokenType != System.Text.Json.JsonTokenType.String)
            throw new System.Text.Json.JsonException("\"capabilities\" must be a string such as \"Streaming, Vision\".");
        var text = reader.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text)) return ModelCapabilities.None;
        var combined = ModelCapabilities.None;
        foreach (var piece in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<ModelCapabilities>(piece, ignoreCase: true, out var single) ||
                !Enum.IsDefined(typeof(ModelCapabilities), single))
                throw new System.Text.Json.JsonException($"Unknown model capability: {piece}");
            combined |= single;
        }
        return combined;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, ModelCapabilities? value,
        JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(ModelCapabilitiesJson.Spell(value.Value));
    }
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

/// <summary>
/// One model as a Session calls it: which provider, which model id, and how much room one request has.
/// </summary>
/// <param name="ContextWindow">
/// How many tokens one request may carry, as this model really accepts them. Declared rather than derived: the number
/// belongs to the model and to the operator's arrangement with it, and no provider answer reports it. Null means
/// nobody said — which a Session must read as "assume a small window", never as "no limit": a guessed limit costs
/// some forgotten history, an unbounded request costs every turn after it.
/// </param>
public sealed record ModelProfile(
    string Id,
    Address Provider,
    string ModelId,
    SamplingSettings? DefaultSampling = null,
    int? ContextWindow = null,
    /// <summary>
    /// What this <b>model</b> can do, as opposed to what its provider's protocol can express. Null means nobody said,
    /// which is read as "assume the least" — the same rule <see cref="ContextWindow"/> follows, for the same reason:
    /// a guess in the permissive direction turns into a request the provider refuses, and every request after it.
    ///
    /// Vision belongs here and not on the provider because it is a fact about the model: one OpenAI-compatible endpoint
    /// serves a model that reads pictures and one that answers 400 to them, and a provider descriptor cannot say which.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonConverter(typeof(ModelCapabilitiesJsonConverter))]
    ModelCapabilities? Capabilities = null)
{
    /// <summary>
    /// Whether a picture may be put in front of this model. False unless the profile says otherwise: an undeclared
    /// profile gets a receipt instead of an image, which costs a sentence; the other way round costs a failed turn.
    /// </summary>
    public bool CanSeeImages => Capabilities is { } declared && declared.HasFlag(ModelCapabilities.Vision);

    /// <summary>
    /// What a Session assumes when the profile declares no <see cref="ContextWindow"/>. 128k is the smallest window
    /// among the models this system is run against, and guessing low keeps a Session working on a smaller model too —
    /// guessing high turns the first symptom into a provider error nobody can act on.
    /// </summary>
    public const int AssumedContextWindow = 128 * 1024;

    /// <summary>The window to plan against: what was declared, or the floor a Session can still be useful in.</summary>
    public int EffectiveContextWindow => ContextWindow is > 0 and var declared ? declared : AssumedContextWindow;

    /// <summary>Whether the window above is a guess, so that a receipt can say so instead of implying a fact.</summary>
    public bool ContextWindowAssumed => ContextWindow is not > 0;
}

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

/// <summary>One instance's file, named by the instance it configures: <c>modules/model.openai-chat#one/module.json</c>.</summary>
/// <param name="TypeId">The provider type the file declares — what the manager builds, not something derived from the id.</param>
/// <param name="Provider">The instance address, which is also the module id and the directory name (docs/22 §2).</param>
public sealed record ModelInstanceEntry(string TypeId, Address Provider, ModelInstanceSpec Spec);

/// <summary>
/// The model configuration, as module directories. Nothing here knows how to talk to a provider.
///
/// Keyed by the provider's module id (its address) and not by category: a category is a grouping the settings page
/// uses, while the file has to sit in the directory named after the instance it configures. Two keys for one thing is
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

/// <summary>
/// One piece of a message's content, when text alone is not enough.
///
/// This is the room <c>docs/11 §1</c> always left open and the implementation never took: a message was a string, so a
/// picture could not be part of one. The shape below is deliberately small — text and a picture, nothing else — because
/// every additional part type is a provider dialect to negotiate, and two of them pay for themselves today.
///
/// The JSON shape is the contract too, since a script can hand a request over as JSON:
/// <c>{"type":"text","text":"…"}</c> and <c>{"type":"image","mediaType":"image/png","bytes":"&lt;base64&gt;"}</c>.
/// </summary>
[System.Text.Json.Serialization.JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FailSerialization)]
[System.Text.Json.Serialization.JsonDerivedType(typeof(TextPart), "text")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ImagePart), "image")]
public abstract record ModelContentPart;

/// <summary>A run of text. The ordinary case, and the only one every provider accepts.</summary>
public sealed record TextPart(string Text) : ModelContentPart;

/// <summary>
/// A picture, as the bytes to send.
///
/// Bytes and not a reference, because this shape is what a provider is handed and a provider has no business reading
/// this machine's disk — turning a stored picture into bytes is the assembler's job, and it happens once per request
/// rather than once per record (docs/28 §3).
///
/// <paramref name="Width"/> and <paramref name="Height"/> are here for one reason: the cost of a request is estimated
/// from them. A picture whose size is unknown is priced at the assumed maximum rather than at zero, because an estimate
/// that is wrong low is a request the provider refuses and a history that keeps being refused.
/// </summary>
/// <param name="AltText">What the picture is, in words, for a model that cannot see it — and for the record.</param>
public sealed record ImagePart(string MediaType, ReadOnlyMemory<byte> Bytes, int? Width = null, int? Height = null,
    string? AltText = null) : ModelContentPart
{
    /// <summary>Bytes as they will go on the wire: base64 inside a data URL.</summary>
    public string ToDataUrl() => $"data:{MediaType};base64,{Convert.ToBase64String(Bytes.Span)}";
}

public sealed record ModelMessage(
    string Role,
    string? Content,
    IReadOnlyList<ModelToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null,
    /// <summary>
    /// The message's content when it is more than text. Null — the overwhelming majority — means "Content is the whole
    /// message", and that is the shape every provider has always been sent; a provider only builds a content array when
    /// this is non-null, so nothing about a text-only request changed when pictures arrived.
    /// </summary>
    IReadOnlyList<ModelContentPart>? Parts = null)
{
    /// <summary>Whether this message carries anything a text-only model would have to be told about.</summary>
    public bool HasImage => Parts?.Any(part => part is ImagePart) == true;
}

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
{    string TypeId { get; }
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

/// <summary>
/// The action ids the model registry answers to. Same rule as the channel and adapter ones: an id is part of the
/// interface, so it lives here rather than only in the module that happens to implement it today.
/// </summary>
public static class ModelActions
{
    public const string List = "models.list";
    public const string ProviderGet = "models.provider.get";
    public const string ProviderSave = "models.provider.save";
    public const string ProviderDestroy = "models.provider.destroy";
    public const string ProfileCreate = "models.profile.create";
    public const string ProfileRemove = "models.profile.remove";
}
