using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// One pass over what is due: fire it, write down what happened, and make the three promises the design rests on —
/// missed beats are counted rather than replayed, a row never overlaps itself, and a target that keeps failing does not
/// become a thirty-second hot loop.
/// </summary>
public sealed class ScheduleSweepTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T09:00:00+08:00",
        System.Globalization.CultureInfo.InvariantCulture);

    private const string Shanghai = "Asia/Shanghai";

    private sealed class MemoryState : IStateStore
    {
        public Dictionary<(string Scope, string Key), string> Values { get; } = [];

        public ValueTask<Result<string?>> ReadAsync(string scope, string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<string?>.Success(Values.TryGetValue((scope, key), out var value) ? value : null));

        public ValueTask<Result> WriteAsync(string scope, string key, string json, CancellationToken cancellationToken)
        {
            Values[(scope, key)] = json;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> DeleteAsync(string scope, string key, CancellationToken cancellationToken)
        {
            Values.Remove((scope, key));
            return ValueTask.FromResult(Result.Success());
        }
    }

    /// <summary>Records what it was asked to fire, and can be held open or told to refuse.</summary>
    private sealed class Targets : IScheduleTargets
    {
        public List<(string SessionId, DateTimeOffset DueAt, string Schedule)> Woken { get; } = [];
        public List<(Address Source, string Payload)> Dispatched { get; } = [];
        public TaskCompletionSource Hold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; init; }
        public Error? Refuse { get; init; }

        public async ValueTask<Result> WakeSessionAsync(string sessionId, DateTimeOffset dueAt, string scheduleId,
            CancellationToken cancellationToken)
        {
            if (Refuse is { } error) return Result.Failure(error);
            if (Block) await Hold.Task.WaitAsync(cancellationToken);
            Woken.Add((sessionId, dueAt, scheduleId));
            return Result.Success();
        }

        public async ValueTask<Result> DispatchAsync(Address source, string payloadJson, CancellationToken cancellationToken)
        {
            if (Refuse is { } error) return Result.Failure(error);
            if (Block) await Hold.Task.WaitAsync(cancellationToken);
            Dispatched.Add((source, payloadJson));
            return Result.Success();
        }
    }

    private static ScheduleRow Draft(ScheduleKind kind, string expression, ScheduleTargetKind targetKind = ScheduleTargetKind.Session,
        string targetId = "s-1", DateTimeOffset? createdAt = null) =>
        new(ScheduleStore.NewId(), kind, expression, Shanghai, targetKind, targetId, createdAt ?? Now,
            TargetAddress: targetKind == ScheduleTargetKind.Script ? "channel.conversation#chat-1" : null,
            CreatedBy: "test");

    [Fact]
    public async Task A_due_session_row_runs_once_and_advances_to_its_next_beat()
    {
        var store = new ScheduleStore(new MemoryState());
        var set = await store.SetAsync(Draft(ScheduleKind.Every, "1h"), Now, default);
        var targets = new Targets();
        var sweep = new ScheduleSweep(store, targets);

        var report = await sweep.PulseAsync(Now.AddHours(1), default);
        Assert.True(report.Ok, report.Error?.Message);
        Assert.Equal(1, report.Value!.Due);
        Assert.Equal(1, report.Value.Fired);
        var woken = Assert.Single(targets.Woken);
        Assert.Equal(Now.AddHours(1), woken.DueAt);
        Assert.Equal(set.Value!.Id, woken.Schedule);

        var after = await store.GetAsync(set.Value.Id, default);
        Assert.Equal(1, after.Value!.Runs);
        Assert.Equal("ok", after.Value.LastOutcome);
        Assert.Equal(Now.AddHours(2), after.Value.NextDue);

        // Nothing due a second time: the row moved.
        Assert.Empty((await store.DueAsync(Now.AddHours(1).AddMinutes(1), default)).Value!);
    }

    [Fact]
    public async Task A_no_target_host_says_so_and_leaves_the_row_due()
    {
        var store = new ScheduleStore(new MemoryState());
        var set = await store.SetAsync(Draft(ScheduleKind.Every, "1h"), Now, default);
        var sweep = new ScheduleSweep(store, targets: null);

        var report = await sweep.PulseAsync(Now.AddHours(1), default);
        Assert.True(report.Ok, report.Error?.Message);
        Assert.Equal(1, report.Value!.Unhandled);
        // Still due: the beat is owed to whoever can fire it, and the report said nobody could.
        Assert.Single((await store.DueAsync(Now.AddHours(1), default)).Value!);
        Assert.Equal(0, (await store.GetAsync(set.Value!.Id, default)).Value!.Runs);
    }

    [Fact]
    public async Task A_script_row_is_handed_the_event_with_its_name_and_how_late_it_is()
    {
        var store = new ScheduleStore(new MemoryState());
        // An overdue row: planted with a next-due in the past, which is what a host that was down comes back to.
        await store.SetAsync(Draft(ScheduleKind.Cron, "0 8 * * *", ScheduleTargetKind.Script, "channel.digest",
            Now.AddDays(-4)) with { NextDue = Now.AddDays(-1) }, Now, default);
        var targets = new Targets();
        var sweep = new ScheduleSweep(store, targets);

        await sweep.PulseAsync(Now, default);
        var dispatched = Assert.Single(targets.Dispatched);
        Assert.Equal("channel.conversation#chat-1", dispatched.Source.ToString());
        Assert.Contains("\"scheduleId\":", dispatched.Payload, StringComparison.Ordinal);
        Assert.Contains("\"lateMs\":", dispatched.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missed_beats_are_counted_in_the_row_and_report_but_never_replayed()
    {
        var store = new ScheduleStore(new MemoryState());
        var set = await store.SetAsync(Draft(ScheduleKind.Every, "1h"), Now, default);
        var targets = new Targets();
        var sweep = new ScheduleSweep(store, targets);

        // Due at 10:00; the pass runs at 13:00, so 11:00 and 12:00 went by unrun: three beats owed one run.
        var report = await sweep.PulseAsync(Now.AddHours(4), default);
        Assert.True(report.Ok, report.Error?.Message);
        Assert.Equal(1, report.Value!.Fired);
        Assert.Single(targets.Woken);
        var after = await store.GetAsync(set.Value!.Id, default);
        Assert.Equal(3, after.Value!.Missed);
        Assert.Equal(Now.AddHours(5), after.Value.NextDue);
    }

    [Fact]
    public async Task A_busy_target_is_skipped_and_the_row_stays_due()
    {
        var store = new ScheduleStore(new MemoryState());
        var set = await store.SetAsync(Draft(ScheduleKind.Every, "1h"), Now, default);
        var targets = new Targets { Refuse = new(ErrorCode.Conflict, "Session s-1 is busy.", Retryable: true) };
        var sweep = new ScheduleSweep(store, targets);

        var report = await sweep.PulseAsync(Now.AddHours(1), default);
        Assert.Equal(1, report.Value!.Skipped);
        Assert.Equal(0, report.Value.Fired);
        var after = await store.GetAsync(set.Value!.Id, default);
        Assert.Equal(0, after.Value!.Runs);
        Assert.Equal(1, after.Value.Skipped);
        Assert.Equal(Now.AddHours(1), after.Value.NextDue);
    }

    [Fact]
    public async Task A_target_that_keeps_failing_advances_so_it_cannot_become_a_hot_loop()
    {
        var store = new ScheduleStore(new MemoryState());
        var set = await store.SetAsync(Draft(ScheduleKind.Every, "1h"), Now, default);
        var targets = new Targets { Refuse = new(ErrorCode.Unavailable, "the target is gone", Retryable: false) };
        var sweep = new ScheduleSweep(store, targets);

        var report = await sweep.PulseAsync(Now.AddHours(1), default);
        Assert.Equal(1, report.Value!.Failed);
        var after = await store.GetAsync(set.Value!.Id, default);
        Assert.Equal(1, after.Value!.Runs);
        Assert.Equal("failed", after.Value.LastOutcome);
        // Advanced anyway: retrying every thirty seconds is how one broken target becomes a flood.
        Assert.Equal(Now.AddHours(2), after.Value.NextDue);
    }

    [Fact]
    public async Task A_row_that_is_still_running_is_skipped_rather_than_started_twice()
    {
        var store = new ScheduleStore(new MemoryState());
        var set = await store.SetAsync(Draft(ScheduleKind.Every, "1m"), Now, default);
        var targets = new Targets { Block = true };
        var sweep = new ScheduleSweep(store, targets);

        // First pass holds the row open; a second pass while it is still in flight must skip.
        var first = sweep.PulseAsync(Now.AddMinutes(1), default).AsTask();
        await Task.Delay(50);
        var second = await sweep.PulseAsync(Now.AddMinutes(1), default);
        Assert.Equal(1, second.Value!.Skipped);
        Assert.Equal(0, second.Value.Fired);

        targets.Hold.SetResult();
        var firstReport = await first;
        Assert.Equal(1, firstReport.Value!.Fired);
        var after = await store.GetAsync(set.Value!.Id, default);
        Assert.Equal(1, after.Value!.Runs);
        Assert.Equal(1, after.Value.Skipped);
    }

    [Fact]
    public async Task A_row_whose_expression_this_build_cannot_read_is_recorded_as_invalid_rather_than_thrown()
    {
        // A row written by a build that read something this one refuses. The store validated it then; the sweep must not
        // crash on it now, and it must not fire on a guess either: it records "invalid" and moves on.
        var state = new MemoryState();
        var seeded = new ScheduleStore(state);
        var set = await seeded.SetAsync(Draft(ScheduleKind.Cron, "0 8 * * 1") with { NextDue = Now }, Now, default);
        Assert.True(set.Ok, set.Error?.Message);
        state.Values[("schedules", "rows")] = state.Values[("schedules", "rows")]
            .Replace("0 8 * * 1", "0 8 * * 1-5/0", StringComparison.Ordinal);

        var targets = new Targets();
        var sweep = new ScheduleSweep(new ScheduleStore(state), targets);
        var report = await sweep.PulseAsync(Now, default);
        Assert.True(report.Ok, report.Error?.Message);
        Assert.Equal(1, report.Value!.Due);
        Assert.Equal(1, report.Value.Failed);
        Assert.Empty(targets.Woken);
        var after = await new ScheduleStore(state).GetAsync(set.Value!.Id, default);
        Assert.Equal("invalid", after.Value!.LastOutcome);
    }
}
