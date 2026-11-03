using ALN.Contracts;
using ALN.Contracts.Archive;
using ALN.Contracts.Sessions;

/// <summary>
/// What the host does when a Session's declared deadline passes.
///
/// Releasing is archiving, never deleting: "release" is the model's own verdict that it is finished, and the
/// model can be wrong. A wrong verdict should cost a move to the archive, not the conversation.
/// </summary>
internal sealed class SessionDeadlineHandler(IArchiveRegistry archive) : ISessionDeadlineHandler
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

    public ValueTask<Result> WakeAsync(SessionSummary session, DateTimeOffset dueAt, CancellationToken cancellationToken) =>
        // Nothing can wake a Session yet: activation still insists on a Channel message, so there is no second
        // way in. Saying so every pass keeps the deadline visible instead of pretending the promise was kept —
        // the sweep counts this as a failure, and the count is the point.
        ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable,
            $"Session {session.Id} asked to be woken at {dueAt:O}, but this build has no non-Channel activation.")));
}
