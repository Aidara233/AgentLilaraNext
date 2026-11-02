namespace ALN.Contracts.Sessions;

/// <summary>
/// One Session as maintenance sees it. The Session id is a hash, so everything an operator needs in order to
/// decide "keep or delete" — whose it is, when it last spoke, how much room it takes — has to be carried here.
/// </summary>
/// <param name="Steps">Committed steps: one per model call, so a turn with tool calls counts more than once.</param>
public sealed record SessionSummary(
    string Id,
    string? Channel,
    DateTimeOffset? LastActiveAt,
    int Steps,
    long Bytes);

/// <summary>
/// What maintenance needs to know that the Session store cannot know by itself: which Channels still exist.
/// It is passed in rather than looked up, because the store must not depend on the thing that calls it.
/// </summary>
/// <param name="LiveChannels">Channel addresses that still exist. A Session of any other Channel is an orphan.</param>
/// <param name="DryRun">Default is true: looking is free, deleting a conversation is not.</param>
/// <param name="IdleDays">Sessions untouched this long are offered for deletion; zero or less disables the rule.</param>
public sealed record SessionPruneRequest(
    IReadOnlyCollection<string> LiveChannels,
    bool DryRun = true,
    int IdleDays = 30);

/// <param name="Reason">Why this Session is offered: <c>channel-unknown</c>, <c>channel-gone</c>,
/// <c>spec-superseded</c> or <c>idle</c>.</param>
/// <param name="Removed">False on a dry run, and false when the deletion was attempted and failed.</param>
public sealed record SessionPruneItem(
    string Id,
    string Reason,
    string? Channel,
    DateTimeOffset? LastActiveAt,
    long Bytes,
    bool Removed,
    string? Error = null);

public sealed record SessionPruneResult(bool DryRun, IReadOnlyList<SessionPruneItem> Items)
{
    public long Bytes => Items.Sum(item => item.Bytes);
    public int Removed => Items.Count(item => item.Removed);
}

/// <summary>The reasons maintenance offers a Session for deletion. Stable strings: they end up in logs and UI.</summary>
public static class SessionPruneReasons
{
    /// <summary>Written before the Channel was recorded, so nobody can say which Channel owns it.</summary>
    public const string ChannelUnknown = "channel-unknown";

    /// <summary>Its Channel no longer exists.</summary>
    public const string ChannelGone = "channel-gone";

    /// <summary>A run was refused because the caller's spec has changed; the stored spec can never match again.</summary>
    public const string SpecSuperseded = "spec-superseded";

    /// <summary>Nothing has talked to it for longer than the idle window.</summary>
    public const string Idle = "idle";
}
