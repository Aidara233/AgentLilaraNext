namespace ALN.Contracts.Sessions;

/// <summary>
/// One Session as maintenance sees it. The Session id is a hash, so everything a keep-or-archive decision
/// needs — whose it is, when it last spoke, how much room it takes — has to be carried here.
/// </summary>
/// <param name="Steps">Committed steps: one per model call, so a turn with tool calls counts more than once.</param>
/// <param name="LastActiveAt">The recorded time, or — for a Session that never recorded one — its newest file.
/// Never null for a directory that exists, because "no answer" would read as "idle since forever".</param>
/// <param name="SupersededAt">When a run was refused because the caller's spec had moved on, if that happened.</param>
/// <param name="WaitUntil">When this Session asked to be woken on its own, if it asked. Null means it is only
/// waiting for somebody to write — which is not a deadline and must never be read as one.</param>
/// <param name="WaitRelease">Whether the deadline above ends with the Session being archived.</param>
public sealed record SessionSummary(
    string Id,
    string? Channel,
    DateTimeOffset? LastActiveAt,
    int Steps,
    long Bytes,
    DateTimeOffset? SupersededAt = null,
    DateTimeOffset? WaitUntil = null,
    bool WaitRelease = false);

/// <summary>
/// What to do with a Session whose declared deadline has passed. Passed in rather than owned because the two
/// answers belong to different places: archiving needs the archive, and waking needs a way to run a turn that
/// nothing wrote a message for.
/// </summary>
public interface ISessionDeadlineHandler
{
    /// <summary>The Session asked to be released if nobody came. Nobody came.</summary>
    ValueTask<Result> ReleaseAsync(SessionSummary session, CancellationToken cancellationToken);

    /// <summary>The Session asked to be woken at <paramref name="dueAt"/> on its own, with no message behind it.</summary>
    ValueTask<Result> WakeAsync(SessionSummary session, DateTimeOffset dueAt, CancellationToken cancellationToken);
}

/// <summary>
/// What one pass of the deadline sweep did. Reported rather than logged so that "the sweep ran and found
/// nothing" and "the sweep never ran" cannot look the same from outside.
/// </summary>
/// <param name="Unhandled">Due, but nothing in this host could act on it. A number here is a promise the
/// system cannot keep, which is worth seeing.</param>
public sealed record SessionSweepReport(int Due, int Released, int Woken, int Failed, int Unhandled);
