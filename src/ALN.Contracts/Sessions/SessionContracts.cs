using ALN.Contracts.Models;
using System.Text.Json.Serialization;

namespace ALN.Contracts.Sessions;

public enum SessionStatus { Running, Dormant }

/// <summary>Immutable behavior and prompt snapshot created with one session.</summary>
/// <param name="Prompts">
/// Which prompt file answers each group, e.g. <c>{ "1身份": "标准", "5安全": "标准" }</c> — at most one
/// file per group, because a dictionary cannot hold the same key twice. Empty or null means no system
/// message at all, which is a legal choice (a script that only performs an action does not need a persona)
/// and is said out loud in the signal log rather than left to be guessed.
///
/// Only the *names* are frozen here, never the text: prompts are files a person edits, and editing one must
/// change the next turn rather than break the session. What stays immutable is which prompt was chosen.
/// </param>
/// <summary>
/// Unknown members are refused rather than ignored: a spec that still says <c>systemPrompt</c> must fail loudly
/// instead of running with no prompt at all, and a typo in a field name must not silently drop a requirement.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionSpec(
    string ModelProfileId,
    string Objective,
    string FormatVersion,
    IReadOnlyDictionary<string, string>? Prompts = null,
    IReadOnlyList<ModelToolDefinition>? Tools = null);

/// <summary>Persisted state. Phase is intentionally transient and is not stored here.</summary>
public sealed record SessionState(
    SessionStatus Status,
    int StepNumber,
    long Revision,
    bool Locked,
    SessionUsage Usage,
    int SchemaVersion,
    /// <summary>
    /// The Channel address this Session answers for, as text. The Session id is a hash, so this is the only
    /// way anybody — an operator, a maintenance action — can tell which Channel a directory belongs to.
    /// </summary>
    string? Channel = null,
    /// <summary>
    /// When this Session last finished a turn. Absence of a turn is not evidence of disuse, so freshness has
    /// to be recorded rather than guessed from file timestamps: a backup or a virus scanner rewrites those.
    /// </summary>
    DateTimeOffset? LastActiveAt = null,
    /// <summary>
    /// When a run was refused because the caller's spec no longer matches the stored one. The spec is immutable,
    /// so this Session can only ever conflict again — the note is what turns "unexplained dead weight" into
    /// "a stuck instance somebody can decide to drop".
    /// </summary>
    DateTimeOffset? SupersededAt = null);

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

/// <summary>Durable channel messages accepted for one Session activation, plus the Channel they came from.</summary>
public sealed record SessionTurnInput(
    IReadOnlyList<SessionChannelMessage> Messages,
    Address? Channel = null,
    ChannelBinding? Binding = null);

public sealed record RoundContextRequest(
    string SessionId,
    Guid ActivationId,
    int StepNumber,
    Address? Channel = null,
    ChannelBinding? Binding = null);

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

public interface ISessionManager
{
    /// <summary>
    /// Run one turn: model steps and tool rounds until a tool ends the turn. <paramref name="tools"/> is the
    /// per-turn toolbox the host built (see <see cref="ISessionToolbox"/>); null means "this turn has only the
    /// built-in tools".
    /// </summary>
    ValueTask<Result<SessionTurnResult>> RunTurnAsync(
        string sessionId,
        SessionSpec spec,
        Guid activationId,
        SessionTurnInput input,
        ISessionToolbox? tools,
        CancellationToken cancellationToken);

    Result<SessionState> GetState(string sessionId);

    /// <summary>
    /// Deletes every Session that belongs to one Channel, and says how many went. A Channel's Sessions are
    /// derived from it — the id is a hash of the Channel and the spec is immutable — so a Channel re-created
    /// with the same address would otherwise inherit a Session built from a script that no longer exists.
    /// </summary>
    ValueTask<Result<int>> DeleteChannelAsync(string channel, CancellationToken cancellationToken);

    /// <summary>
    /// Every Session on disk, with the facts a keep-or-archive decision needs. Reads the session directory —
    /// deliberately not done at startup: an instance nobody talks to should cost nothing.
    /// </summary>
    ValueTask<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Moves one Session's data out of its living place into <paramref name="destinationDirectory"/>, holding
    /// its turn gate meanwhile. The layout stays the manager's business — a caller says "take it away", not
    /// "here are the files".
    /// </summary>
    ValueTask<Result> ArchiveAsync(string sessionId, string destinationDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Moves archived data back into place. Refuses when that place is occupied: restoring into a Session that
    /// was rebuilt since would splice two different conversations into one.
    /// </summary>
    ValueTask<Result> RestoreAsync(string sessionId, string sourceDirectory, CancellationToken cancellationToken);
}
