using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Graph.Contracts;

namespace ALN.Runtime;

/// <summary>
/// What a schedule row is fired at, in a host that has both a Session manager and a graph.
///
/// Two verbs and no policy: a Session is woken with <see cref="ActivationReason.ScheduleDue"/> (so its turn says a rule
/// came due rather than "your own wait expired"), and a script is handed <see cref="EventNames.ScheduleDue"/> on the
/// instance the mount sits on. The sweep decides what is due and what was skipped; this only carries it over.
/// </summary>
public sealed class HostScheduleTargets(ISessionManager sessions, IGraphRuntime graph) : IScheduleTargets
{
    public async ValueTask<Result> WakeSessionAsync(string sessionId, DateTimeOffset dueAt, string scheduleId,
        string? payload, CancellationToken cancellationToken)
    {
        var woken = await sessions.ActivateAsync(sessionId, ActivationReason.ScheduleDue, dueAt, cancellationToken, scheduleId, payload)
            .ConfigureAwait(false);
        // A busy Session is retryable by contract; the sweep turns that into a skip rather than a failed beat.
        return woken.Ok ? Result.Success() : Result.Failure(woken.Error!);
    }

    public async ValueTask<Result> DispatchAsync(Address source, string payloadJson, CancellationToken cancellationToken)
    {
        var dispatched = await graph.DispatchAsync(
            new RoutedEvent(EventNames.ScheduleDue, source, Guid.NewGuid(), payloadJson, Target: source),
            cancellationToken).ConfigureAwait(false);
        return dispatched.Ok ? Result.Success() : Result.Failure(dispatched.Error!);
    }
}
