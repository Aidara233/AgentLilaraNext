using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// Who may see the schedule and who may write it.
///
/// Two different questions, and the code answers them at two depths: listing is an agent's (an assistant should be able
/// to say when the host wakes up instead of guessing), writing is an operator's (a row decides what happens when nobody
/// is there). The defaults a new row gets are part of the answer too — a rule that quietly stops after a thousand runs
/// is a rule that quietly stopped.
/// </summary>
public sealed class ScheduleActionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T09:00:00+08:00",
        System.Globalization.CultureInfo.InvariantCulture);

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

    private sealed class SilentAudit : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];
        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FrozenTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Actor Agent() => new("model", ActorKind.Model, [Actor.Any], AccessLevel.Agent);
    private static Actor Operator() => new("ops", ActorKind.User, [Actor.Any], AccessLevel.Operator);

    private static (ControlPlane Plane, ScheduleStore Store, SilentAudit Audit) SetUp()
    {
        var audit = new SilentAudit();
        var plane = new ControlPlane(audit, SignalLog.Open(null));
        var store = new ScheduleStore(new MemoryState());
        Assert.True(ScheduleActions.Register(plane, store, new FrozenTime(Now)).Ok);
        return (plane, store, audit);
    }

    private static System.Text.Json.JsonElement Args(object value) =>
        System.Text.Json.JsonSerializer.SerializeToElement(value);

    private static object Daily(string targetId = "s-1") => new
    {
        kind = "dailyAt",
        expression = "08:00",
        timeZone = "Asia/Shanghai",
        target = "session",
        targetId
    };

    [Fact]
    public async Task An_agent_can_see_the_schedule_and_write_one()
    {
        // 2026-11-12 改，原名 An_agent_can_see_the_schedule_but_not_write_one。schedules.control 降到 agent：
        // 一条规则是可读、可改、可删的数据，它自己干不出危险的事——危险的是被它叫醒的那个动作，而闸门在动作上。
        // 原来那条"拒绝也留审计"的断言保留着，只是现在它验的是"允许也留审计"。
        var (plane, _, audit) = SetUp();
        var listed = await plane.InvokeAsync(Agent(), ScheduleActions.List, Args(new { }), default);
        Assert.True(listed.Ok, listed.Error?.Message);

        var written = await plane.InvokeAsync(Agent(), ScheduleActions.Set, Args(Daily()), default);
        Assert.True(written.Ok, written.Error?.Message);
        // Who wrote it is on the row: a rule nobody is named on is a rule nobody can be asked about.
        Assert.Contains(audit.Records, record => record.Action == ScheduleActions.Set && record.Outcome == "allowed");
    }

    [Fact]
    public async Task An_operator_writes_one_and_the_reply_says_what_it_means()
    {
        var (plane, store, audit) = SetUp();
        var written = await plane.InvokeAsync(Operator(), ScheduleActions.Set, Args(Daily()), default);
        Assert.True(written.Ok, written.Error?.Message);
        // Read the reply the way a caller does: the sentence is a value, not something to grep out of escaped bytes.
        var means = written.Value.GetProperty("means").GetString();
        Assert.Equal("每天 08:00（Asia/Shanghai）", means);
        var defaults = written.Value.GetProperty("defaults").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains($"maxRuns={ScheduleActions.DefaultMaxRuns}", defaults);
        Assert.Contains(defaults, item => item!.StartsWith("until=", StringComparison.Ordinal));

        var stored = Assert.Single((await store.ListAsync(default)).Value!);
        Assert.Equal(ScheduleActions.DefaultMaxRuns, stored.MaxRuns);
        Assert.Equal(Now.Add(ScheduleActions.DefaultLifetime), stored.Until);
        Assert.Equal("ops", audit.Records.Last(record => record.Action == ScheduleActions.Set).Actor);
    }

    [Fact]
    public async Task Writing_without_a_zone_is_refused_and_says_why()
    {
        var (plane, _, _) = SetUp();
        var refused = await plane.InvokeAsync(Operator(), ScheduleActions.Set,
            Args(new { kind = "dailyAt", expression = "08:00", target = "session", targetId = "s-1" }), default);
        Assert.False(refused.Ok);
        Assert.Contains("timeZone", refused.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("no default", refused.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cron_row_can_be_written_and_read_back_with_its_next_due()
    {
        var (plane, store, _) = SetUp();
        var written = await plane.InvokeAsync(Operator(), ScheduleActions.Set,
            Args(new
            {
                kind = "cron", expression = "0 8 * * 1-5", timeZone = "Asia/Shanghai", target = "script",
                targetId = "channel.digest", targetAddress = "channel.conversation#chat-1", maxRuns = 5
            }), default);
        Assert.True(written.Ok, written.Error?.Message);

        var id = (await store.ListAsync(default)).Value!.Single().Id;
        var read = await plane.InvokeAsync(Agent(), ScheduleActions.Get, Args(new { id }), default);
        Assert.True(read.Ok, read.Error?.Message);
        Assert.Contains("\"nextDue\":", read.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_removes_the_row_and_saying_it_twice_is_not_an_error()
    {
        var (plane, store, _) = SetUp();
        await plane.InvokeAsync(Operator(), ScheduleActions.Set, Args(Daily()), default);
        var id = (await store.ListAsync(default)).Value!.Single().Id;

        var first = await plane.InvokeAsync(Operator(), ScheduleActions.Cancel, Args(new { id }), default);
        Assert.True(first.Ok, first.Error?.Message);
        Assert.Contains("\"removed\":true", first.Value.GetRawText(), StringComparison.Ordinal);

        var second = await plane.InvokeAsync(Operator(), ScheduleActions.Cancel, Args(new { id }), default);
        Assert.True(second.Ok, second.Error?.Message);
        Assert.Contains("\"removed\":false", second.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_models_read_tool_lists_what_will_happen_and_says_when_nothing_will()
    {
        var store = new ScheduleStore(new MemoryState());
        var tools = new ScheduleTools(store);
        var offers = await tools.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default);
        Assert.True(offers.Ok, offers.Error?.Message);
        var list = offers.Value!.Single().Tool;

        var empty = await list.ExecuteAsync(new("c1", ScheduleTools.ListToolName, "{}"), default);
        Assert.True(empty.Ok, empty.Error?.Message);
        Assert.Contains("No schedule is stored", empty.Value!.Content, StringComparison.Ordinal);

        await store.SetAsync(new ScheduleRow("sch-1", ScheduleKind.DailyAt, "08:00", "Asia/Shanghai",
            ScheduleTargetKind.Session, "s-1", Now, CreatedBy: "ops"), Now, default);
        var listed = await list.ExecuteAsync(new("c2", ScheduleTools.ListToolName, "{}"), default);
        Assert.True(listed.Ok, listed.Error?.Message);
        Assert.Contains("每天 08:00（Asia/Shanghai）", listed.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("session s-1", listed.Value.Content, StringComparison.Ordinal);
        Assert.Contains("next 2026-11-08 08:00", listed.Value.Content, StringComparison.Ordinal);
    }
}
