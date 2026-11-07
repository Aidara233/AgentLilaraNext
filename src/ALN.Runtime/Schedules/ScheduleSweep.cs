using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// What a due row is fired at. Two verbs, because there are two things in this host that can be asked to act — and
/// keeping them behind an interface is what lets the sweep be tested without a Session manager or a graph.
/// </summary>
public interface IScheduleTargets
{
    /// <summary>Runs a turn for a Session with nobody's message behind it.</summary>
    ValueTask<Result> WakeSessionAsync(string sessionId, DateTimeOffset dueAt, string scheduleId, CancellationToken cancellationToken);

    /// <summary>Hands a mounted script the event that says its schedule came due.</summary>
    ValueTask<Result> DispatchAsync(Address source, string payloadJson, CancellationToken cancellationToken);
}

/// <summary>What one pass of the schedule sweep did. Reported rather than logged, so "found nothing" and "never ran"
/// cannot look the same from outside.</summary>
public sealed record ScheduleSweepReport(int Due, int Fired, int Skipped, int Missed, int Failed, int Unhandled)
{
    public bool AnythingHappened => Due > 0;
}

/// <summary>
/// One pass over the stored schedules: what is due, fire it, write down what happened.
///
/// The three decisions this class is made of:
/// <list type="number">
/// <item><b>Missed beats are counted, not replayed.</b> A host that was down three days runs an hourly rule once and
/// records how many beats it slept through; the alternative is an afternoon of catch-up nobody asked for.</item>
/// <item><b>A row does not overlap itself.</b> If the previous firing of the same row is still running, this one is
/// skipped and counted — script dispatch is a single-file event stream and queueing beats turns one slow run into a
/// backlog.</item>
/// <item><b>Every firing advances the row, success or not.</b> A target that keeps failing must not become a hot loop
/// that retries every thirty seconds; the row records the failure and moves to its next beat. The one exception is a
/// busy Session, which is a "try again in a moment" rather than a failure — it is skipped, and the next pass retries.</item>
/// </list>
/// </summary>
public sealed class ScheduleSweep(ScheduleStore store, IScheduleTargets? targets, ISignalLog? signals = null)
{
    /// <summary>How often the host looks. Coarse on purpose: schedules are promises measured in minutes and hours.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<Result<ScheduleSweepReport>> PulseAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await store.DueAsync(now, cancellationToken).ConfigureAwait(false);
        if (!due.Ok) return Result<ScheduleSweepReport>.Failure(due.Error!);
        var rows = due.Value!;
        if (rows.Count == 0) return Result<ScheduleSweepReport>.Success(new(0, 0, 0, 0, 0, 0));

        var fired = 0;
        var skipped = 0;
        var missed = 0;
        var failed = 0;
        var unhandled = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // One row at a time: two passes of the sweep would otherwise both fire the same beat.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var claim = false;
            try { claim = _running.Add(row.Id); }
            finally { _gate.Release(); }
            if (!claim)
            {
                await store.RecordSkipAsync(row.Id, now, cancellationToken).ConfigureAwait(false);
                Write(row, "skipped", now, "the previous firing of this row is still running");
                skipped++;
                continue;
            }

            try
            {
                var spec = ScheduleSpec.Parse(row.Kind, row.Expression, row.TimeZone, row.CreatedAt, row.Days);
                if (!spec.Ok)
                {
                    // Stored rows were validated when written; one that no longer parses means this build reads the
                    // expression differently. Say so and turn it off rather than firing on a guess.
                    await store.RecordRunAsync(row.Id, now, "invalid", 0, cancellationToken).ConfigureAwait(false);
                    Write(row, "failed", now, spec.Error?.Message ?? "the expression no longer parses");
                    failed++;
                    continue;
                }
                var behind = row.NextDue is { } planned
                    ? spec.Value!.CountFiringsAfter(planned, now, cap: 1000)
                    : 0;
                if (targets is null)
                {
                    // Nothing can be fired in this host. The row is left due, so the next pass with a target fires it
                    // — and the report says so instead of pretending the beat was spent.
                    Write(row, "unhandled", now, "this host has nowhere to fire a schedule");
                    unhandled++;
                    continue;
                }
                var outcome = row.TargetKind == ScheduleTargetKind.Session
                    ? await targets.WakeSessionAsync(row.TargetId, row.NextDue ?? now, row.Id, cancellationToken)
                        .ConfigureAwait(false)
                    : await targets.DispatchAsync(
                            Address.TryParse(row.TargetAddress, out var source) ? source : new Address("schedule", "unbound", row.Id),
                            PayloadOf(row, now), cancellationToken).ConfigureAwait(false);

                if (!outcome.Ok && outcome.Error!.Retryable)
                {
                    // A busy target is "in a moment", not "failed": count the skip and leave the row due.
                    await store.RecordSkipAsync(row.Id, now, cancellationToken).ConfigureAwait(false);
                    Write(row, "skipped", now, outcome.Error.Message);
                    skipped++;
                    continue;
                }

                await store.RecordRunAsync(row.Id, now, outcome.Ok ? "ok" : "failed", behind, cancellationToken)
                    .ConfigureAwait(false);
                Write(row, outcome.Ok ? "ok" : "failed", now, outcome.Error?.Message, behind);
                if (outcome.Ok) fired++; else failed++;
                missed += behind;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await store.RecordRunAsync(row.Id, now, "failed", 0, CancellationToken.None).ConfigureAwait(false);
                Write(row, "failed", now, ex.Message);
                failed++;
            }
            finally
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { _running.Remove(row.Id); }
                finally { _gate.Release(); }
            }
        }
        return Result<ScheduleSweepReport>.Success(
            new(rows.Count, fired, skipped, missed, failed, unhandled));
    }

    /// <summary>Runs <see cref="PulseAsync"/> on a beat until cancelled. A failure in one pass never stops the loop.</summary>
    public async Task RunAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pulse = await PulseAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                if (!pulse.Ok) WriteFailure(pulse.Error!);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { WriteFailure(new(ErrorCode.Internal, "Schedule sweep failed.", ex.Message)); }
            try { await Task.Delay(Interval, timeProvider, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static string PayloadOf(ScheduleRow row, DateTimeOffset now)
    {
        var late = row.NextDue is { } planned ? (long)Math.Max(0, (now - planned).TotalMilliseconds) : 0;
        return JsonSerializer.Serialize(new
        {
            scheduleId = row.Id,
            description = row.Description,
            dueAt = row.NextDue,
            firedAt = now,
            lateMs = late,
            run = row.Runs + 1,
            payload = row.Payload
        });
    }

    private void Write(ScheduleRow row, string outcome, DateTimeOffset now, string? message = null, int missed = 0)
    {
        signals?.Write(new()
        {
            Group = "schedule",
            Name = row.Id,
            From = "schedule",
            To = row.TargetId,
            Mechanism = "schedule.due",
            Outcome = outcome,
            Detail = new Dictionary<string, string>
            {
                ["description"] = row.Description ?? row.Expression,
                ["dueAt"] = (row.NextDue ?? now).ToString("O"),
                ["target"] = $"{row.TargetKind}:{row.TargetId}",
                ["runs"] = row.Runs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["missed"] = missed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["message"] = message ?? ""
            }
        });
    }

    private void WriteFailure(Error error) =>
        signals?.Write(new()
        {
            Group = "schedule",
            Name = "sweep",
            Mechanism = "schedule.sweep",
            Outcome = "failed",
            Level = SignalLevel.Warn,
            Detail = new Dictionary<string, string> { ["message"] = error.Message, ["detail"] = error.Detail ?? "" }
        });
}
