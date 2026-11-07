using System.Globalization;

namespace ALN.Contracts;

/// <summary>What kind of "when" a schedule row carries. Four words, one function.</summary>
public enum ScheduleKind
{
    /// <summary>A fixed interval, anchored so that a night of downtime does not make it drift.</summary>
    Every,
    /// <summary>A clock time on the days named: the human form of a rhythm.</summary>
    DailyAt,
    /// <summary>One instant, then nothing.</summary>
    Once,
    /// <summary>Five cron fields, for the expressions people copy in from somewhere else.</summary>
    Cron
}

/// <summary>
/// One schedule, parsed.
///
/// The whole point of this type is that it is a <b>pure function of time</b>: no timers, no state, no I/O. I-11 forbids
/// Sessions and scripts from holding a beat of their own, and the answer to "when does this next run" has to be
/// computable from two instants — otherwise it lives in somebody's memory and dies with the process.
///
/// <see cref="NextDueAfter"/> is the contract: the first firing strictly after <paramref name="previous"/> and at or
/// after <paramref name="now"/>. Two consequences worth stating, because both are decisions:
/// <list type="bullet">
/// <item><b>Missed beats are skipped, not queued.</b> A host that was down for three days runs an hourly schedule once,
/// because the next due lands after now rather than becoming three days of catch-up.</item>
/// <item><b>Anchored, not drifting.</b> <see cref="ScheduleKind.Every"/> counts from the anchor it was created with, so a
/// five-minute rhythm is still on the grid after a week, which "now + interval" would not be.</item>
/// </list>
/// </summary>
public sealed record ScheduleSpec
{
    private readonly TimeSpan? _interval;
    private readonly TimeOnly _clock;
    private readonly IReadOnlyList<DayOfWeek>? _days;
    private readonly CronFields? _cron;
    private readonly DateTimeOffset _once;

    private ScheduleSpec(ScheduleKind kind, string expression, TimeZoneInfo zone, string zoneId, TimeSpan? interval,
        TimeOnly clock, IReadOnlyList<DayOfWeek>? days, CronFields? cron, DateTimeOffset once)
    {
        Kind = kind;
        Expression = expression;
        ZoneId = zoneId;
        Zone = zone;
        _interval = interval;
        _clock = clock;
        _days = days;
        _cron = cron;
        _once = once;
    }

    public ScheduleKind Kind { get; }

    /// <summary>The text as it was written — what a person edits, and what gets stored.</summary>
    public string Expression { get; }

    /// <summary>IANA id. Required for every kind: a schedule without a zone is not a schedule (docs/26 §2).</summary>
    public string ZoneId { get; }

    public TimeZoneInfo Zone { get; }

    /// <summary>The instant the rule was written at: the grid every <see cref="ScheduleKind.Every"/> beat is counted from.</summary>
    public DateTimeOffset Anchor { get; private init; }

    /// <summary>
    /// The sentence a person confirms. Rendered here rather than in a UI so that "what this means" has one answer: the
    /// store, the action's reply and the listing all show this string.
    /// </summary>
    public string Describe()
    {
        var zone = $"（{ZoneId}）";
        return Kind switch
        {
            ScheduleKind.Every => $"每 {HumanInterval(_interval!.Value)}{zone}",
            ScheduleKind.Once => $"{_once:yyyy-MM-dd HH:mm zzz} 一次，之后就没有了",
            ScheduleKind.DailyAt => $"{DaysWords()}{_clock:HH\\:mm}{zone}",
            ScheduleKind.Cron => $"cron「{Expression}」{zone}：{_cron!.Describe()}",
            _ => Expression
        };
    }

    /// <summary>The next firing this schedule has, or null when there will not be one.</summary>
    public DateTimeOffset? NextDueAfter(DateTimeOffset previous, DateTimeOffset now)
    {
        return Kind switch
        {
            ScheduleKind.Every => NextEvery(previous, previous > now ? previous : now),
            ScheduleKind.Once => _once > previous && _once >= now ? _once : null,
            ScheduleKind.DailyAt => NextDaily(previous, now),
            ScheduleKind.Cron => NextCron(previous, now),
            _ => null
        };
    }

    private string DaysWords() => _days switch
    {
        null => "每天 ",
        { Count: 0 } => "每天 ",
        { Count: 5 } days when days.Order().SequenceEqual(new[]
            { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }) =>
            "每个工作日 ",
        { Count: 2 } days when days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday) => "每个周末 ",
        var days => "每周" + string.Join("、", days.Order().Select(WeekdayWord)) + " "
    };

    private static string WeekdayWord(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日"
    };

    private static string HumanInterval(TimeSpan interval) =>
        interval.TotalDays >= 1 && interval.TotalDays % 1 == 0 ? $"{interval.TotalDays:F0} 天"
        : interval.TotalHours >= 1 && interval.TotalHours % 1 == 0 ? $"{interval.TotalHours:F0} 小时"
        : interval.TotalMinutes >= 1 && interval.TotalMinutes % 1 == 0 ? $"{interval.TotalMinutes:F0} 分钟"
        : $"{interval.TotalSeconds:F0} 秒";

    /// <summary>
    /// Anchor arithmetic, not "now + interval": the anchor is what the rule was created with, so the beats stay on the
    /// grid they started on however long the process was away.
    /// </summary>
    private DateTimeOffset NextEvery(DateTimeOffset previous, DateTimeOffset from)
    {
        var interval = _interval!.Value;
        if (from < Anchor) return Anchor;
        var elapsed = from - Anchor;
        var beats = (long)Math.Floor(elapsed.Ticks / (double)interval.Ticks) + 1;
        var next = Anchor + TimeSpan.FromTicks(beats * interval.Ticks);
        return next > previous ? next : previous + interval;
    }

    /// <summary>
    /// How many firings this schedule has after <paramref name="after"/> and up to and including
    /// <paramref name="upTo"/> — the number a sweep reports as "missed". Capped, because the answer is used to write a
    /// number down, not to build a queue: a row that slept through a year of minutes owes one run and one count.
    /// </summary>
    public int CountFiringsAfter(DateTimeOffset after, DateTimeOffset upTo, int cap = 1000)
    {
        if (upTo <= after) return 0;
        switch (Kind)
        {
            case ScheduleKind.Every:
                return (int)Math.Min(cap, Math.Floor((upTo - after).Ticks / (double)_interval!.Value.Ticks));
            case ScheduleKind.Once:
                return 0;
            default:
            {
                var count = 0;
                var cursor = after;
                while (count < cap)
                {
                    // Ask for the next firing after the cursor without letting "now" skip ahead, so the walk counts
                    // every beat instead of jumping to the end.
                    var next = NextDueAfter(cursor, cursor);
                    if (next is null || next.Value > upTo) return count;
                    count++;
                    cursor = next.Value;
                }
                return count;
            }
        }
    }

    /// <summary>
    /// The next clock time on a day this schedule names. Days are walked rather than minutes: a rule that only fires on
    /// the 29th of February must not cost half a million comparisons to answer "when next".
    ///
    /// The window starts at <paramref name="now"/> but the answer must also be <b>strictly after</b>
    /// <paramref name="previous"/>; without that second condition the repeated hour of a fall-back night answers with the
    /// instant that just fired, and the sweep would run it twice.
    /// </summary>
    private DateTimeOffset? NextDaily(DateTimeOffset previous, DateTimeOffset now)
    {
        var from = previous > now ? previous : now;
        var localFrom = TimeZoneInfo.ConvertTime(from, Zone);
        var day = DateOnly.FromDateTime(localFrom.DateTime);
        for (var step = 0; step < 8; step++, day = day.AddDays(1))
        {
            if (_days is { Count: > 0 } allowed && !allowed.Contains(day.DayOfWeek)) continue;
            if (step == 0 && _clock < TimeOnly.FromDateTime(localFrom.DateTime)) continue;
            if (TryPlace(Zone, day.ToDateTime(_clock), out var instant) && instant > previous && instant >= now)
                return instant;
            // A time the clocks skipped that day has no instant: the day has no firing for this rule (docs/26 §2.4).
        }
        return null;
    }

    private DateTimeOffset? NextCron(DateTimeOffset previous, DateTimeOffset now)
    {
        var from = previous > now ? previous : now;
        var localFrom = TimeZoneInfo.ConvertTime(from, Zone);
        var day = DateOnly.FromDateTime(localFrom.DateTime);
        var startMinute = localFrom.Hour * 60 + localFrom.Minute;
        // A year of days is the horizon: beyond it, "the next time" is a rule nobody will be awake for.
        for (var step = 0; step < 366; step++, day = day.AddDays(1), startMinute = -1)
        {
            if (!_cron!.MatchesDate(day)) continue;
            foreach (var (hour, minute) in _cron.TimesFrom(startMinute))
            {
                if (!TryPlace(Zone, day.ToDateTime(new TimeOnly(hour, minute)), out var instant)) continue;
                if (instant > previous && instant >= now) return instant;
            }
        }
        return null;
    }

    /// <summary>
    /// A local wall-clock time made into an instant. A skipped hour has no instant (refused, callers move on); an hour
    /// that happens twice resolves to its first occurrence, which is what "eight o'clock" means to somebody waiting.
    /// </summary>
    internal static bool TryPlace(TimeZoneInfo zone, DateTime local, out DateTimeOffset instant)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) { instant = default; return false; }
        instant = new DateTimeOffset(local, OffsetFor(zone, local));
        return true;
    }

    /// <summary>
    /// Which of the two readings of an ambiguous wall-clock time to use: the earlier one.
    /// <see cref="TimeZoneInfo.GetAmbiguousTimeOffsets(DateTime)"/> throws for the ordinary case rather than answering,
    /// so it is asked only when the time really is ambiguous.
    /// </summary>
    internal static TimeSpan OffsetFor(TimeZoneInfo zone, DateTime local) =>
        zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);

    // ---------------------------------------------------------------------------------------------------------------
    // Parsing. Every refusal names the field and the reason: "bad syntax" makes the reader count spaces.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses one schedule. <paramref name="anchor"/> is the moment it is created at, which <see cref="ScheduleKind.Every"/>
    /// counts from — passed in rather than read from the clock so the result is a pure function of its inputs.
    /// </summary>
    public static Result<ScheduleSpec> Parse(ScheduleKind kind, string expression, string timeZoneId,
        DateTimeOffset anchor, IReadOnlyList<DayOfWeek>? days = null)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return Result<ScheduleSpec>.Failure(new(ErrorCode.Invalid,
                "A schedule needs a timeZone (IANA id, e.g. Asia/Shanghai). There is no default: \"08:00\" without a zone is not a time."));
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim()); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Result<ScheduleSpec>.Failure(new(ErrorCode.Invalid,
                $"timeZone does not name a known zone: {timeZoneId}. Use an IANA id such as Asia/Shanghai or Europe/Berlin."));
        }
        var zoneId = timeZoneId.Trim();
        var text = (expression ?? "").Trim();
        if (text.Length == 0)
            return Result<ScheduleSpec>.Failure(new(ErrorCode.Invalid, "A schedule needs an expression."));

        switch (kind)
        {
            case ScheduleKind.Every:
                if (!TryInterval(text, out var interval, out var everyWhy))
                    return Result<ScheduleSpec>.Failure(new(ErrorCode.Invalid, $"every: {everyWhy}"));
                return Result<ScheduleSpec>.Success(
                    new ScheduleSpec(kind, text, zone, zoneId, interval, default, null, null, default) { Anchor = anchor });
            case ScheduleKind.Once:
                if (!TryInstant(text, zone, out var instant, out var onceWhy))
                    return Result<ScheduleSpec>.Failure(new(ErrorCode.Invalid, $"once: {onceWhy}"));
                return Result<ScheduleSpec>.Success(
                    new ScheduleSpec(kind, text, zone, zoneId, null, default, null, null, instant) { Anchor = anchor });
            case ScheduleKind.DailyAt:
                if (!TimeOnly.TryParseExact(text, ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var clock))
                    return Result<ScheduleSpec>.Failure(new(ErrorCode.Invalid,
                        $"dailyAt: \"{text}\" is not a clock time. Use \"HH:mm\" (24-hour), e.g. \"08:00\"."));
                return Result<ScheduleSpec>.Success(
                    new ScheduleSpec(kind, text, zone, zoneId, null, clock, NormalizeDays(days), null, default)
                        { Anchor = anchor });
            case ScheduleKind.Cron:
                var parsed = CronFields.Parse(text);
                if (!parsed.Ok)
                    return Result<ScheduleSpec>.Failure(
                        new(ErrorCode.Invalid, $"cron: {parsed.Error!.Message}", parsed.Error.Detail));
                return Result<ScheduleSpec>.Success(
                    new ScheduleSpec(kind, text, zone, zoneId, null, default, null, parsed.Value, default)
                        { Anchor = anchor });
            default:
                return Result<ScheduleSpec>.Failure(new(ErrorCode.Invalid, $"Unknown schedule kind: {kind}."));
        }
    }

    private static IReadOnlyList<DayOfWeek>? NormalizeDays(IReadOnlyList<DayOfWeek>? days) =>
        days is null || days.Count == 0 ? null : days.Distinct().Order().ToArray();

    /// <summary>
    /// "5m", "1h", "2d", "30s". Seconds below a minute are refused because the sweep runs every 30 seconds: anything
    /// finer would be a promise the host cannot keep.
    /// </summary>
    private static bool TryInterval(string text, out TimeSpan interval, out string why)
    {
        interval = default;
        why = "";
        if (text.Length < 2)
        {
            why = $"\"{text}\" is not an interval. Use e.g. \"5m\", \"1h\", \"2d\".";
            return false;
        }
        var unit = text[^1];
        if (!int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount < 1)
        {
            why = $"\"{text}\" is not an interval. Use a whole number followed by s, m, h or d, e.g. \"5m\".";
            return false;
        }
        interval = unit switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => TimeSpan.Zero
        };
        if (interval == TimeSpan.Zero)
        {
            why = $"\"{text}\" uses an unknown unit '{unit}'. Use s, m, h or d.";
            return false;
        }
        if (interval < TimeSpan.FromMinutes(1))
        {
            why = $"\"{text}\" is finer than a minute. The sweep looks every 30 seconds, so a faster schedule would be a " +
                  "promise this host cannot keep; write \"1m\" at the least.";
            return false;
        }
        if (interval > TimeSpan.FromDays(366))
        {
            why = $"\"{text}\" is longer than a year. A rhythm that slow is better written when it is due.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// An instant with an offset stands alone; without one it is read in the schedule's zone. A local time the clocks
    /// skip is refused rather than moved — the same rule <c>wait(at)</c> follows.
    /// </summary>
    private static bool TryInstant(string text, TimeZoneInfo zone, out DateTimeOffset instant, out string why)
    {
        instant = default;
        why = "";
        var carriesOffset = text.EndsWith('Z') || text.EndsWith('z') ||
                            (text.Length > 6 && (text[^6] is '+' or '-') && text[^3] == ':');
        if (carriesOffset)
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant))
                return true;
            why = $"\"{text}\" is not an instant this host can read. Use e.g. 2026-11-08T09:00:00+08:00.";
            return false;
        }
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            why = $"\"{text}\" is not a date and time this host can read.";
            return false;
        }
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            why = $"{local:yyyy-MM-dd HH:mm} does not exist in {zone.Id}: the clocks skip that hour.";
            return false;
        }
        instant = new DateTimeOffset(local, OffsetFor(zone, local));
        return true;
    }
}
