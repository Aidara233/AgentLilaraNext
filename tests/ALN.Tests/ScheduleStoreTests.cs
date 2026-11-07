using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// A schedule row survives a restart, refuses to be born unreadable, and stops explaining itself when it stops.
///
/// The store is the piece that decides "is this a rule or a thought": everything here is about the file being the truth
/// — persisted before success is claimed, read back with the anchor it was created with, and kept as evidence when it
/// cannot be parsed rather than deleted.
/// </summary>
public sealed class ScheduleStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T09:00:00+08:00",
        System.Globalization.CultureInfo.InvariantCulture);

    private const string Shanghai = "Asia/Shanghai";

    /// <summary>The smallest honest state store: what a host would keep, and what a test can read back.</summary>
    private sealed class MemoryState : IStateStore
    {
        public Dictionary<(string Scope, string Key), string> Values { get; } = [];

        public ValueTask<Result<string?>> ReadAsync(string scope, string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<string?>.Success(
                Values.TryGetValue((scope, key), out var value) ? value : null));

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

    private static ScheduleRow Draft(ScheduleKind kind, string expression, DateTimeOffset? createdAt = null,
        int? maxRuns = null, DateTimeOffset? until = null) =>
        new(ScheduleStore.NewId(), kind, expression, Shanghai, ScheduleTargetKind.Session, "s-1",
            createdAt ?? Now, MaxRuns: maxRuns, Until: until, CreatedBy: "test");

    [Fact]
    public async Task A_row_is_persisted_before_it_is_reported_and_comes_back_after_a_restart()
    {
        var state = new MemoryState();
        var store = new ScheduleStore(state);
        var set = await store.SetAsync(Draft(ScheduleKind.DailyAt, "08:00"), Now, default);
        Assert.True(set.Ok, set.Error?.Message);

        // A different store over the same state is what a restart looks like.
        var reopened = new ScheduleStore(state);
        var listed = await reopened.ListAsync(default);
        Assert.True(listed.Ok, listed.Error?.Message);
        var row = Assert.Single(listed.Value!);
        Assert.Equal(set.Value!.Id, row.Id);
        Assert.Equal(Now.AddHours(23), row.NextDue);          // 08:00 tomorrow, in +08:00
        Assert.Equal("每天 08:00（Asia/Shanghai）", row.Description);
    }

    [Fact]
    public async Task An_every_schedule_keeps_its_grid_across_a_restart()
    {
        // The anchor is the row's CreatedAt, not "now at load time": otherwise beating every 5 minutes moves to
        // whenever the host happened to start, which is a drift nobody asked for.
        var state = new MemoryState();
        var created = Now.AddMinutes(-2);
        var first = new ScheduleStore(state);
        var set = await first.SetAsync(Draft(ScheduleKind.Every, "5m", created), Now, default);
        Assert.True(set.Ok, set.Error?.Message);
        Assert.Equal(created.AddMinutes(5), set.Value!.NextDue);

        var again = await new ScheduleStore(state).GetAsync(set.Value.Id, default);
        Assert.True(again.Ok, again.Error?.Message);
        Assert.Equal(created.AddMinutes(5), again.Value!.NextDue);
    }

    [Fact]
    public async Task An_expression_that_cannot_fire_is_refused_when_it_is_written()
    {
        var store = new ScheduleStore(new MemoryState());
        var bad = await store.SetAsync(Draft(ScheduleKind.Cron, "0 8 32 * *"), Now, default);
        Assert.False(bad.Ok);
        Assert.Contains("field 3 (day of month)", bad.Error!.Message, StringComparison.Ordinal);

        var past = await store.SetAsync(Draft(ScheduleKind.Once, "2026-01-01T09:00:00+08:00"), Now, default);
        Assert.False(past.Ok);
        Assert.Contains("has no next firing", past.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_with_no_target_is_refused()
    {
        var store = new ScheduleStore(new MemoryState());
        var draft = Draft(ScheduleKind.DailyAt, "08:00") with { TargetId = "" };
        var set = await store.SetAsync(draft, Now, default);
        Assert.False(set.Ok);
        Assert.Contains("needs a target", set.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_due_enabled_rows_inside_their_own_expiry_are_returned()
    {
        var store = new ScheduleStore(new MemoryState());
        var hourly = await store.SetAsync(Draft(ScheduleKind.Every, "1h"), Now, default);
        var daily = await store.SetAsync(Draft(ScheduleKind.DailyAt, "08:00"), Now, default);
        var expiring = await store.SetAsync(Draft(ScheduleKind.Every, "1h", until: Now.AddMinutes(30)), Now, default);
        Assert.True(hourly.Ok && daily.Ok && expiring.Ok,
            hourly.Error?.Message ?? daily.Error?.Message ?? expiring.Error?.Message);

        // Nothing is due before its own next firing.
        Assert.Empty((await store.DueAsync(Now.AddMinutes(30), default)).Value!);

        // One hour on: the hourly row only — the daily one fires at 08:00 tomorrow.
        var atHour = await store.DueAsync(Now.AddHours(1), default);
        Assert.Equal([hourly.Value!.Id], atHour.Value!.Select(row => row.Id));

        // Long after: both of the open ones, never the one whose `until` has gone by.
        var muchLater = await store.DueAsync(Now.AddDays(3), default);
        Assert.Contains(muchLater.Value!, row => row.Id == hourly.Value!.Id);
        Assert.Contains(muchLater.Value!, row => row.Id == daily.Value!.Id);
        Assert.DoesNotContain(muchLater.Value!, row => row.Id == expiring.Value!.Id);
    }

    [Fact]
    public async Task A_firing_advances_the_row_and_a_cap_turns_it_off_with_a_reason()
    {
        var store = new ScheduleStore(new MemoryState());
        var set = await store.SetAsync(Draft(ScheduleKind.Every, "1h", maxRuns: 2), Now, default);
        Assert.True(set.Ok, set.Error?.Message);

        var first = await store.RecordRunAsync(set.Value!.Id, Now.AddHours(1), "ok", missed: 0, default);
        Assert.True(first.Ok, first.Error?.Message);
        Assert.True(first.Value!.Enabled);
        Assert.Equal(1, first.Value.Runs);
        Assert.Equal(Now.AddHours(2), first.Value.NextDue);

        var second = await store.RecordRunAsync(set.Value.Id, Now.AddHours(2), "ok", missed: 3, default);
        Assert.True(second.Ok, second.Error?.Message);
        Assert.False(second.Value!.Enabled);
        Assert.Equal(2, second.Value.Runs);
        Assert.Equal(3, second.Value.Missed);
        Assert.Contains("maxRuns", second.Value.DisabledReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missed_beats_are_counted_not_replayed()
    {
        // A host that was away for a day: the row records the gap and lands on the next grid beat, not 24 runs.
        var store = new ScheduleStore(new MemoryState());
        var set = await store.SetAsync(Draft(ScheduleKind.Every, "1h"), Now, default);
        var run = await store.RecordRunAsync(set.Value!.Id, Now.AddHours(25), "ok", missed: 24, default);
        Assert.True(run.Ok, run.Error?.Message);
        Assert.Equal(24, run.Value!.Missed);
        Assert.Equal(Now.AddHours(26), run.Value.NextDue);
    }

    [Fact]
    public async Task An_unreadable_state_file_is_kept_as_evidence_and_the_host_starts_with_none()
    {
        var state = new MemoryState();
        state.Values[("schedules", "rows")] = "{ this is not json";
        var store = new ScheduleStore(state, new RecordingSignals());

        var listed = await store.ListAsync(default);
        Assert.True(listed.Ok, listed.Error?.Message);
        Assert.Empty(listed.Value!);
        // The bytes are somewhere, and the main key is gone: a half-parsed file would run things nobody asked for.
        Assert.Contains(state.Values.Keys, key => key.Scope == "schedules" && key.Key.StartsWith("unreadable-", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Values.Keys, key => key.Scope == "schedules" && key.Key == "rows");
    }

    [Fact]
    public async Task Cancelling_a_row_removes_it_and_says_so_either_way()
    {
        var state = new MemoryState();
        var store = new ScheduleStore(state);
        var set = await store.SetAsync(Draft(ScheduleKind.DailyAt, "08:00"), Now, default);

        var cancelled = await store.CancelAsync(set.Value!.Id, default);
        Assert.True(cancelled.Ok, cancelled.Error?.Message);
        Assert.True(cancelled.Value);
        // Gone is what was wanted, so removing what is already absent is not a failure.
        var again = await store.CancelAsync(set.Value.Id, default);
        Assert.True(again.Ok, again.Error?.Message);
        Assert.False(again.Value);
        Assert.Empty((await new ScheduleStore(state).ListAsync(default)).Value!);
    }

    private sealed class RecordingSignals : ISignalLog
    {
        public List<SignalRecord> Written { get; } = [];
        public SignalLevel MinLevel => SignalLevel.Debug;
        public bool IsEnabled(SignalLevel level) => true;
        public void Write(SignalRecord record) => Written.Add(record);
        public IReadOnlyList<SignalRecord> Snapshot() => Written;
    }
}
