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
    StopReason? StopReason = null);

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
        string userInput,
        ISessionToolExecutor? toolExecutor,
        CancellationToken cancellationToken);

    Result<SessionState> GetState(string sessionId);
}
