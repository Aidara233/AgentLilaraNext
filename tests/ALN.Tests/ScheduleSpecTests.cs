using ALN.Contracts;

namespace ALN.Tests;

/// <summary>
/// When a schedule next fires, asked as a pure function of two instants.
///
/// The tests worth having are the ones a year of production would eventually hand back as a bug report: an interval
/// whose anchor drifts, a host that was down long enough to miss a hundred beats, an hour the clocks skip, an hour
/// they repeat, a range that only exists once every four years, and the cron quirk where day-of-month and day-of-week
/// mean "both" here and "either" in Vixie.
/// </summary>
public sealed class ScheduleSpecTests
{
    private const string Shanghai = "Asia/Shanghai";
    private const string Berlin = "Europe/Berlin";

    private static DateTimeOffset At(string text) =>
        DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static ScheduleSpec Spec(ScheduleKind kind, string expression, DateTimeOffset anchor,
        string zone = Shanghai, IReadOnlyList<DayOfWeek>? days = null)
    {
        var parsed = ScheduleSpec.Parse(kind, expression, zone, anchor, days);
        Assert.True(parsed.Ok, parsed.Error?.Message);
        return parsed.Value!;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // every
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_interval_counts_from_its_anchor_so_it_does_not_drift()
    {
        var anchor = At("2026-11-07T09:02:00+08:00");
        var every = Spec(ScheduleKind.Every, "5m", anchor);

        // Beat after beat, the grid stays on :02/:07/:12 — "now + interval" would walk off it.
        var first = every.NextDueAfter(anchor, anchor)!.Value;
        Assert.Equal(At("2026-11-07T09:07:00+08:00"), first);
        Assert.Equal(At("2026-11-07T09:12:00+08:00"), every.NextDueAfter(first, first)!.Value);
    }

    [Fact]
    public void A_host_that_was_away_catches_up_once_and_lands_on_the_grid()
    {
        var anchor = At("2026-11-01T00:00:00+08:00");
        var every = Spec(ScheduleKind.Every, "1h", anchor);
        var lastFired = At("2026-11-01T00:00:00+08:00");
        var now = At("2026-11-04T07:31:00+08:00");

        // Three days of missed beats: one run now, and the next lands on the hour — not 72 runs, not :31 forever.
        var next = every.NextDueAfter(lastFired, now)!.Value;
        Assert.Equal(At("2026-11-04T08:00:00+08:00"), next);
    }

    [Fact]
    public void An_interval_finer_than_a_minute_is_refused()
    {
        var parsed = ScheduleSpec.Parse(ScheduleKind.Every, "10s", Shanghai, At("2026-11-07T09:00:00+08:00"));
        Assert.False(parsed.Ok);
        Assert.Contains("finer than a minute", parsed.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interval_with_an_unknown_unit_says_which_unit()
    {
        var parsed = ScheduleSpec.Parse(ScheduleKind.Every, "5w", Shanghai, At("2026-11-07T09:00:00+08:00"));
        Assert.False(parsed.Ok);
        Assert.Contains("unknown unit 'w'", parsed.Error!.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // once
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_once_instant_is_used_as_written_and_then_there_is_nothing()
    {
        var anchor = At("2026-11-07T09:00:00+08:00");
        var once = Spec(ScheduleKind.Once, "2026-11-08T09:00:00+08:00", anchor);
        var due = once.NextDueAfter(anchor, anchor)!.Value;
        Assert.Equal(At("2026-11-08T09:00:00+08:00"), due);
        Assert.Null(once.NextDueAfter(due, due));
    }

    [Fact]
    public void A_once_instant_without_an_offset_is_read_in_the_schedules_zone()
    {
        var anchor = At("2026-11-07T09:00:00+08:00");
        var once = Spec(ScheduleKind.Once, "2026-11-08T09:00:00", anchor);
        var due = once.NextDueAfter(anchor, anchor)!.Value;
        Assert.Equal(TimeSpan.FromHours(8), due.Offset);
        Assert.Equal(new DateTime(2026, 11, 8, 9, 0, 0), due.DateTime);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // dailyAt
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_daily_time_today_that_has_passed_rolls_to_tomorrow()
    {
        var now = At("2026-11-07T09:30:00+08:00");
        var daily = Spec(ScheduleKind.DailyAt, "08:00", now);
        Assert.Equal(At("2026-11-08T08:00:00+08:00"), daily.NextDueAfter(now, now)!.Value);
    }

    [Fact]
    public void A_daily_time_still_ahead_fires_today()
    {
        var now = At("2026-11-07T07:30:00+08:00");
        var daily = Spec(ScheduleKind.DailyAt, "08:00", now);
        Assert.Equal(At("2026-11-07T08:00:00+08:00"), daily.NextDueAfter(now, now)!.Value);
    }

    [Fact]
    public void Weekdays_skip_the_weekend()
    {
        // 2026-11-07 is a Saturday.
        var now = At("2026-11-07T09:00:00+08:00");
        var weekdays = Spec(ScheduleKind.DailyAt, "08:00", now, Shanghai,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
        var next = weekdays.NextDueAfter(now, now)!.Value;
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(At("2026-11-09T08:00:00+08:00"), next);
        Assert.Contains("每个工作日", weekdays.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_time_the_clocks_skip_is_dropped_not_moved()
    {
        // Europe/Berlin jumps 02:00 → 03:00 on 2027-03-28, so 02:30 does not exist that day.
        var now = At("2027-03-27T12:00:00+02:00");
        var daily = Spec(ScheduleKind.DailyAt, "02:30", now, Berlin);
        var next = daily.NextDueAfter(now, now)!.Value;
        // The day after the change, not a silently shifted 03:30 on the day of it.
        Assert.Equal(new DateOnly(2027, 3, 29), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(next, daily.Zone).DateTime));
    }

    [Fact]
    public void A_time_the_clocks_repeat_fires_once_on_the_first_reading()
    {
        // Europe/Berlin falls back 03:00 → 02:00 on 2026-10-25, so 02:30 happens twice.
        var now = At("2026-10-25T00:30:00+02:00");
        var daily = Spec(ScheduleKind.DailyAt, "02:30", now, Berlin);
        var next = daily.NextDueAfter(now, now)!.Value;
        var local = TimeZoneInfo.ConvertTime(next, daily.Zone);
        Assert.Equal(new DateTime(2026, 10, 25, 2, 30, 0), local.DateTime);
        Assert.Equal(TimeSpan.FromHours(2), local.Offset);
        // And the second reading the same night is not a second firing.
        var after = daily.NextDueAfter(next, next)!.Value;
        Assert.Equal(new DateOnly(2026, 10, 26), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, daily.Zone).DateTime));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // cron
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_weekday_cron_fires_at_the_named_time()
    {
        var now = At("2026-11-07T09:00:00+08:00"); // Saturday
        var cron = Spec(ScheduleKind.Cron, "0 8 * * 1-5", now);
        Assert.Equal(At("2026-11-09T08:00:00+08:00"), cron.NextDueAfter(now, now)!.Value);
    }

    [Fact]
    public void Zero_and_seven_are_both_Sunday()
    {
        var now = At("2026-11-07T09:00:00+08:00"); // Saturday
        var zero = Spec(ScheduleKind.Cron, "0 8 * * 0", now);
        var seven = Spec(ScheduleKind.Cron, "0 8 * * 7", now);
        var sunday = At("2026-11-08T08:00:00+08:00");
        Assert.Equal(sunday, zero.NextDueAfter(now, now)!.Value);
        Assert.Equal(sunday, seven.NextDueAfter(now, now)!.Value);
    }

    [Fact]
    public void Day_of_month_and_day_of_week_are_both_required()
    {
        // 2026-12-01 is a Tuesday, so "1st AND Monday" skips it; the next is 2027-02-01.
        var now = At("2026-11-07T09:00:00+08:00");
        var cron = Spec(ScheduleKind.Cron, "0 8 1 * 1", now);
        var next = cron.NextDueAfter(now, now)!.Value;
        Assert.Equal(new DateOnly(2027, 2, 1), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(next, cron.Zone).DateTime));
        Assert.Contains("取「与」", cron.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_names_and_steps_are_read_the_way_they_are_written()
    {
        var now = At("2026-11-07T07:30:00+08:00");
        // Every 15 minutes, but only in the 8 o'clock hour, only in JAN and NOV, only on Saturday.
        var cron = Spec(ScheduleKind.Cron, "*/15 8 * JAN,NOV SAT", now);
        var next = cron.NextDueAfter(now, now)!.Value;
        var local = TimeZoneInfo.ConvertTime(next, cron.Zone);
        Assert.Equal(new DateTime(2026, 11, 7, 8, 0, 0), local.DateTime);
    }

    [Fact]
    public void A_leap_day_rule_finds_its_day_or_reports_none_within_the_horizon()
    {
        var now = At("2026-11-07T09:00:00+08:00");
        var leap = Spec(ScheduleKind.Cron, "0 0 29 2 *", now);
        // 2028 is the next leap year, outside the one-year horizon: no next due, and the caller can say so.
        Assert.Null(leap.NextDueAfter(now, now));
    }

    [Theory]
    [InlineData("0 8 * *", "exactly five fields")]
    [InlineData("0 8 32 * *", "field 3 (day of month)")]
    [InlineData("0 24 * * *", "field 2 (hour)")]
    [InlineData("0 8 * * FRI-MON", "runs backwards")]
    [InlineData("0 8 * * 1-5/0", "step that is not a positive whole number")]
    [InlineData("5/15 8 * * *", "steps over a single value")]
    [InlineData("0 8 * * 8", "outside 0-7")]
    [InlineData("0 8 * FOO *", "not a number or a known name")]
    public void A_cron_that_cannot_be_read_says_which_field_and_why(string expression, string expected)
    {
        var parsed = ScheduleSpec.Parse(ScheduleKind.Cron, expression, Shanghai, At("2026-11-07T09:00:00+08:00"));
        Assert.False(parsed.Ok);
        Assert.Contains(expected, parsed.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_schedule_without_a_zone_is_refused()
    {
        var parsed = ScheduleSpec.Parse(ScheduleKind.DailyAt, "08:00", "", At("2026-11-07T09:00:00+08:00"));
        Assert.False(parsed.Ok);
        Assert.Contains("needs a timeZone", parsed.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sentence_a_person_confirms_says_what_the_rule_means()
    {
        var anchor = At("2026-11-07T09:00:00+08:00");
        Assert.Equal("每天 08:00（Asia/Shanghai）", Spec(ScheduleKind.DailyAt, "08:00", anchor).Describe());
        Assert.Equal("每 5 分钟（Asia/Shanghai）", Spec(ScheduleKind.Every, "5m", anchor).Describe());
        Assert.Contains("每周一、三 ", Spec(ScheduleKind.DailyAt, "08:00", anchor, Shanghai,
            [DayOfWeek.Monday, DayOfWeek.Wednesday]).Describe(), StringComparison.Ordinal);
    }
}
