using ALN.Contracts.Models;

namespace ALN.Contracts.Sessions;

public enum SessionStatus { Running, Dormant }

/// <summary>Immutable behavior and prompt snapshot created with one session.</summary>
public sealed record SessionSpec(
    string ModelProfileId,
    string Objective,
    string SystemPrompt,
    string FormatVersion,
    IReadOnlyList<ModelToolDefinition>? Tools = null);

/// <summary>Persisted state. Phase is intentionally transient and is not stored here.</summary>
public sealed record SessionState(
    SessionStatus Status,
    int StepNumber,
    long Revision,
    bool Locked,
    SessionUsage Usage,
    int SchemaVersion);

public sealed record SessionUsage(long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0)
{
    public SessionUsage Add(ModelUsage usage) => new(
        InputTokens + usage.InputTokens,
        OutputTokens + usage.OutputTokens,
        CacheReadTokens + (usage.CacheReadTokens ?? 0),
        CacheWriteTokens + (usage.CacheWriteTokens ?? 0));
}

/// <summary>A committed, immutable timeline record.</summary>
public sealed record SessionHistoryEntry(
    string Id,
    string ActivationId,
    int StepNumber,
    string Role,
    string? Content,
    DateTimeOffset At,
    IReadOnlyList<ModelToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    ModelUsage? Usage = null,
    StopReason? StopReason = null,
    IReadOnlyList<SessionChannelMessage>? ChannelMessages = null);

public sealed record SessionChannelMessage(
    string Id,
    string Source,
    string? Sender,
    string Content,
    DateTimeOffset At);

/// <summary>Durable channel messages accepted for one Session activation.</summary>
public sealed record SessionTurnInput(IReadOnlyList<SessionChannelMessage> Messages);

public sealed record RoundContextRequest(string SessionId, Guid ActivationId, int StepNumber);

/// <summary>Ephemeral context to render into this model request; it is never persisted in Session history.</summary>
public sealed record RoundContextContribution(string Id, string Section, string Content, int Order = 0);

public sealed record SessionTimelineContribution(string Id, string? Sender, string Content, DateTimeOffset At);

public interface ISessionTimelineWriter
{
    void Add(SessionTimelineContribution contribution);
}

public interface IRoundContextWriter
{
    void Add(RoundContextContribution contribution);
}

/// <summary>A registered module that performs just-in-time queries before a model request.</summary>
public interface IRoundContextContributor
{
    string Id { get; }
    ValueTask ContributeAsync(RoundContextRequest request, ISessionTimelineWriter timeline,
        IRoundContextWriter roundContext, CancellationToken cancellationToken);
}

public interface IRoundContextContributorRegistry
{
    Result Register(IRoundContextContributor contributor);
    Result Unregister(string contributorId);
    IReadOnlyList<IRoundContextContributor> Snapshot();
}

public sealed record SessionTurnResult(
    string SessionId,
    int StepNumber,
    string Text,
    IReadOnlyList<ModelToolCall> ToolCalls,
    StopReason StopReason,
    ModelUsage Usage,
    bool DuplicateActivation = false);

public interface ISessionToolExecutor
{
    ValueTask<Result<string>> ExecuteAsync(string sessionId, Guid activationId, ModelToolCall call, CancellationToken cancellationToken);
}

public interface ISessionManager
{
    /// <summary>Run one serialized model step and commit its completed turn to the session journal.</summary>
    ValueTask<Result<SessionTurnResult>> RunTurnAsync(
        string sessionId,
        SessionSpec spec,
        Guid activationId,
        SessionTurnInput input,
        ISessionToolExecutor? toolExecutor,
        CancellationToken cancellationToken);

    Result<SessionState> GetState(string sessionId);
}
