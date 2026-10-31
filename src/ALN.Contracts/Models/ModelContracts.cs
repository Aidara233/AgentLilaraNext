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

public sealed record ModelProviderSpec(Address Address, string ConfigurationJson);

public sealed record ModelProviderInfo(Address Address, ModelProviderDescriptor Descriptor, int InFlight);

public sealed record ModelProfile(
    string Id,
    Address Provider,
    string ModelId,
    SamplingSettings? DefaultSampling = null);

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

public interface IModelProvider : IAsyncDisposable
{
    Address Address { get; }
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
