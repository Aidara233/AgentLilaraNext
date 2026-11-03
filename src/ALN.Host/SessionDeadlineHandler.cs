using ALN.Contracts;
using ALN.Contracts.Archive;
using ALN.Contracts.Sessions;

/// <summary>
/// What the host does when a Session's declared deadline passes.
///
/// Releasing is archiving, never deleting: "release" is the model's own verdict that it is finished, and the
/// model can be wrong. A wrong verdict should cost a move to the archive, not the conversation.
/// </summary>
internal sealed class SessionDeadlineHandler(IArchiveRegistry archive, ISessionManager sessions) : ISessionDeadlineHandler
{
    /// <summary>
    /// How often the sweep looks. Coarse on purpose: what it acts on is a promise measured in minutes and hours,
    /// and a finer interval would only make the host busier, not more punctual in any way anyone can notice.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public async ValueTask<Result> ReleaseAsync(SessionSummary session, CancellationToken cancellationToken)
    {
        var archived = await archive.ArchiveAsync(ArchiveKinds.Session, [session.Id], dryRun: false, cancellationToken)
            .ConfigureAwait(false);
        return archived.Ok ? Result.Success() : Result.Failure(archived.Error!);
    }

    /// <summary>
    /// Nobody came, so the Session runs a turn of its own: empty input and a note saying exactly that. The manager
    /// refuses rather than queues when the Session is already busy, and a refused wake-up is a skipped one — the next
    /// sweep tries again.
    /// </summary>
    public async ValueTask<Result> WakeAsync(SessionSummary session, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        var woken = await sessions.ActivateAsync(session.Id, ActivationReason.DeadlineDue, dueAt, cancellationToken)
            .ConfigureAwait(false);
        return woken.Ok ? Result.Success() : Result.Failure(woken.Error!);
    }
}
