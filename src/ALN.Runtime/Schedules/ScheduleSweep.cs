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
    ValueTask<Result> WakeSessionAsync(string sessionId, DateTimeOffset dueAt, string scheduleId, string? payload,
        CancellationToken cancellationToken);

    /// <summary>Hands a mounted script the event that says its schedule came due.</summary>
    ValueTask<Result> DispatchAsync(Address source, string payloadJson, CancellationToken cancellationToken);
}

/// <summary>
/// What one pass of the schedule sweep did. Reported rather than logged, so "found nothing" and "never ran" cannot look
/// the same from outside.
///
/// <paramref name="Fired"/> counts the rows this pass <b>started</b>, not the ones that finished: a wake that runs a
/// whole turn takes as long as the model takes, and waiting for it in the pass is how one slow Session stops every other
/// schedule. The outcome lands in the row, in the signal, and — for a test that needs to see it — behind
/// <see cref="ScheduleSweep.DrainAsync"/>.
/// </summary>
public sealed record ScheduleSweepReport(int Due, int Fired, int Skipped, int Missed, int Failed, int Unhandled)
{
    public bool AnythingHappened => Due > 0;
}

/// <summary>
/// One pass over the stored schedules: what is due, start it, and write down what happened when it comes back.
///
/// The four decisions this class is made of:
/// <list type="number">
/// <item><b>Nothing waits for anything.</b> Each due row is dispatched on its own task. Waking a Session means running a
/// turn, and a turn is as long as the model makes it — measured on a real host at seventeen model calls. Awaiting that
/// inside the pass held every other schedule behind it, which is exactly the failure the scheduler must not have.</item>
/// <item><b>Missed beats are counted, not replayed.</b> A host that was down three days runs an hourly rule once and
/// records how many beats it slept through.</item>
/// <item><b>A row does not overlap itself.</b> While a row's firing is still in flight, later beats are skipped and
/// counted. A claim older than <see cref="MaxRunTime"/> is reported as stalled rather than re-dispatched: a second
/// concurrent run of the same rule is worse than a missed one.</item>
/// <item><b>Every completion advances the row, success or not.</b> A target that keeps failing must not become a hot
/// loop that retries every thirty seconds. The exception is a busy Session, which is a "try again in a moment": it is
/// skipped and left due.</item>
/// </list>
/// </summary>
public sealed class ScheduleSweep(ScheduleStore store, IScheduleTargets? targets, ISignalLog? signals = null)
{
    /// <summary>How often the host looks. Coarse on purpose: schedules are promises measured in minutes and hours.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a firing may stay in flight before the row is called stalled. Ten minutes is longer than any turn should
    /// take and shorter than "forever", which is the point: a wedge must show up as a number, not as silence.
    /// </summary>
    public static readonly TimeSpan MaxRunTime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (DateTimeOffset Started, Task Work)> _inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _stallNoticed = new(StringComparer.Ordinal);
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
            if (!await ClaimAsync(row, now, cancellationToken).ConfigureAwait(false))
            {
                // Still in flight from an earlier pass: count the skip in the row and in the report, and leave it claimed.
                await store.RecordSkipAsync(row.Id, now, cancellationToken).ConfigureAwait(false);
                Write(row, "skipped", now, "the previous firing of this row is still running");
                skipped++;
                continue;
            }

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
            if (targets is null)
            {
                // Nothing can be fired in this host. The row is left due, so the next pass with a target fires it —
                // and the report says so instead of pretending the beat was spent.
                Write(row, "unhandled", now, "this host has nowhere to fire a schedule");
                unhandled++;
                continue;
            }

            var behind = row.NextDue is { } planned ? spec.Value!.CountFiringsAfter(planned, now, cap: 1000) : 0;
            missed += behind;
            fired++;
            // Started, not awaited: see the type comment. The task records its own outcome and releases its claim.
            var work = TrackedRunAsync(row, now, behind, cancellationToken);
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _inflight[row.Id] = (now, work); }
            finally { _gate.Release(); }
        }
        return Result<ScheduleSweepReport>.Success(new(rows.Count, fired, skipped, missed, failed, unhandled));
    }

    /// <summary>
    /// Waits for every firing currently in flight to come back. For tests and for shutdown: production never needs the
    /// answer, which is the whole point of dispatching rather than awaiting inside the pass.
    /// </summary>
    public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        Task[] working;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { working = _inflight.Values.Select(entry => entry.Work).ToArray(); }
        finally { _gate.Release(); }
        if (working.Length == 0) return;
        await Task.WhenAll(working).ConfigureAwait(false);
    }

    private async Task TrackedRunAsync(ScheduleRow row, DateTimeOffset now, int behind, CancellationToken cancellationToken)
    {
        try { await RunAsync(row, now, behind, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* the host is stopping; the row stays due */ }
        catch (Exception ex) { Write(row, "failed", now, ex.Message); }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _inflight.Remove(row.Id); }
            finally { _gate.Release(); }
        }
    }

    private async Task RunAsync(ScheduleRow row, DateTimeOffset now, int behind, CancellationToken cancellationToken)
    {
        var skipped = false;
        var recorded = true;
        string outcome = "failed";
        string? message = null;
        try
        {
            var dispatched = row.TargetKind == ScheduleTargetKind.Session
                ? await targets!.WakeSessionAsync(row.TargetId, row.NextDue ?? now, row.Id, row.Payload, cancellationToken)
                    .ConfigureAwait(false)
                : await targets!.DispatchAsync(
                        Address.TryParse(row.TargetAddress, out var source) ? source : new Address("schedule", "unbound", row.Id),
                        PayloadOf(row, now), cancellationToken).ConfigureAwait(false);

            if (!dispatched.Ok && dispatched.Error!.Retryable)
            {
                // A busy target is "in a moment", not "failed": count the skip and leave the row due.
                skipped = true;
                message = dispatched.Error.Message;
            }
            else
            {
                outcome = dispatched.Ok ? "ok" : "failed";
                message = dispatched.Error?.Message;
            }
        }
        catch (OperationCanceledException)
        {
            // The host stopped with this firing in flight. Nothing is recorded: the row stays due and the next start
            // fires it again, which is the honest answer for work that never finished.
            recorded = false;
            message = "the host stopped while this firing was in flight; the row stays due";
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        if (!recorded)
        {
            Write(row, "cancelled", now, message);
            return;
        }
        if (skipped)
        {
            await store.RecordSkipAsync(row.Id, now, CancellationToken.None).ConfigureAwait(false);
            Write(row, "skipped", now, message);
            return;
        }
        // Recorded here rather than in the pass: this is where the answer exists, and a firing that comes back after
        // the pass has moved on is still a firing that has to land in the row.
        await store.RecordRunAsync(row.Id, now, outcome, behind, CancellationToken.None).ConfigureAwait(false);
        Write(row, outcome, now, message, behind);
    }

    /// <summary>
    /// Takes the claim for one row, or reports that it is already held. A claim older than
    /// <see cref="MaxRunTime"/> is not taken over — a second concurrent run of the same rule is worse than a missed beat —
    /// but it is reported once, so a wedged firing becomes a number in the log instead of silence.
    /// </summary>
    private async ValueTask<bool> ClaimAsync(ScheduleRow row, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_inflight.TryGetValue(row.Id, out var existing))
            {
                // A task that has already finished but has not been cleared yet is not a claim: the two orderings
                // (task completes first / entry is registered first) both happen, and only one of them means "running".
                if (existing.Work.IsCompleted)
                {
                    _inflight.Remove(row.Id);
                    _stallNoticed.Remove(row.Id);
                    return true;
                }
                if (now - existing.Started > MaxRunTime && !_stallNoticed.ContainsKey(row.Id))
                {
                    _stallNoticed[row.Id] = now;
                    Write(row, "stalled", now,
                        $"this firing has been in flight since {existing.Started:yyyy-MM-dd HH:mm:ss}; it is not re-dispatched");
                }
                return false;
            }
            _stallNoticed.Remove(row.Id);
            return true;
        }
        finally { _gate.Release(); }
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
