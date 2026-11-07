using System.Globalization;

namespace ALN.Contracts;

/// <summary>
/// Five cron fields, parsed once and answered by set membership.
///
/// Deliberately not a cron library. What is here is what docs/26 §2 promises: minutes only, `*`, numbers, names, lists,
/// ascending ranges and steps; no seconds, no <c>?</c>/<c>L</c>/<c>W</c>/<c>#</c>, no macros, no wrapping ranges. Every
/// refusal names the field and the reason, because "bad syntax" makes the reader count spaces.
///
/// One deliberate difference from Vixie cron, recorded in docs/26 §2.3: <b>day of month and day of week are ANDed</b>.
/// Restricting both in Vixie means "either"; here it means "both", because that is what a person reading "每月 1 日、
/// 且是星期一" expects.
/// </summary>
public sealed class CronFields
{
    private static readonly string[] FieldNames = ["minute", "hour", "day of month", "month", "day of week"];

    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _daysOfMonth;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _daysOfWeek;
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;
    private readonly string _text;

    private CronFields(string text, HashSet<int> minutes, HashSet<int> hours, HashSet<int> daysOfMonth,
        HashSet<int> months, HashSet<int> daysOfWeek, bool domRestricted, bool dowRestricted)
    {
        _text = text;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _domRestricted = domRestricted;
        _dowRestricted = dowRestricted;
    }

    /// <summary>The expression as written, for storing and for showing back.</summary>
    public string Text => _text;

    /// <summary>Whether this rule can fire on that date at all (month, day of month, day of week).</summary>
    public bool MatchesDate(DateOnly day)
    {
        if (!_months.Contains(day.Month)) return false;
        // A field written as "*" was filled with its whole range, so it matches everything: the plain conjunction covers
        // all four cases (neither restricted, only one, both). When both are restricted this is AND — docs/26 §2.3 —
        // which is where it differs from Vixie cron's "either".
        return _daysOfMonth.Contains(day.Day) && _daysOfWeek.Contains((int)day.DayOfWeek);
    }

    /// <summary>
    /// The times of day this rule fires, ascending, skipping anything at or before <paramref name="startMinute"/>
    /// (a minute-of-day; pass -1 for "from midnight").
    /// </summary>
    public IEnumerable<(int Hour, int Minute)> TimesFrom(int startMinute)
    {
        foreach (var hour in _hours.Order())
        {
            foreach (var minute in _minutes.Order())
            {
                if (startMinute >= 0 && hour * 60 + minute <= startMinute) continue;
                yield return (hour, minute);
            }
        }
    }

    /// <summary>Every minute this rule names on that date — used by tests, not by the next-due walk.</summary>
    public int OccurrencesOn(DateOnly day) => MatchesDate(day) ? _hours.Count * _minutes.Count : 0;

    /// <summary>A structural sentence: what each field allows, plus the one quirk worth repeating.</summary>
    public string Describe()
    {
        var parts = new List<string>
        {
            $"分 {Words(_minutes, 0, 59, "每分")}",
            $"时 {Words(_hours, 0, 23, "每时")}",
            $"日 {Words(_daysOfMonth, 1, 31, "每天")}",
            $"月 {Words(_months, 1, 12, "每月")}",
            $"周 {WeekWords(_daysOfWeek)}"
        };
        if (_domRestricted && _dowRestricted)
            parts.Add("日与周同时限定时取「与」（与 Vixie cron 的「或」不同）");
        return string.Join("；", parts);
    }

    private static string Words(HashSet<int> values, int min, int max, string all) =>
        values.Count == max - min + 1 ? all : Compact(values.Order());

    private static string WeekWords(HashSet<int> days) =>
        days.Count == 7 ? "每天" : string.Join("、", days.Order().Select(Weekday));

    private static string Weekday(int day) => day switch
    {
        0 => "周日",
        1 => "周一",
        2 => "周二",
        3 => "周三",
        4 => "周四",
        5 => "周五",
        6 => "周六",
        _ => "周日"
    };

    /// <summary>Runs of consecutive numbers collapse to "a-b"; everything else is listed. "0,15,30,45" stays readable.</summary>
    private static string Compact(IEnumerable<int> ordered)
    {
        var values = ordered.ToArray();
        var parts = new List<string>();
        for (var index = 0; index < values.Length; index++)
        {
            var start = values[index];
            var end = start;
            while (index + 1 < values.Length && values[index + 1] == end + 1) end = values[++index];
            parts.Add(end - start >= 2 ? $"{start}-{end}" : start == end ? $"{start}" : $"{start},{end}");
        }
        return string.Join(",", parts);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Parses five fields. Errors name the field's number, its name, and what about it is wrong.</summary>
    public static Result<CronFields> Parse(string text)
    {
        var fields = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
            return Result<CronFields>.Failure(new(ErrorCode.Invalid,
                $"a cron expression has exactly five fields (minute hour day-of-month month day-of-week), got {fields.Length}. " +
                "There is no seconds field here."));

        if (!TryField(fields[0], 0, 59, None, out var minutes, out _, out var minuteWhy))
            return Fail(1, minuteWhy);
        if (!TryField(fields[1], 0, 23, None, out var hours, out _, out var hourWhy))
            return Fail(2, hourWhy);
        if (!TryField(fields[2], 1, 31, None, out var daysOfMonth, out var domStar, out var domWhy))
            return Fail(3, domWhy);
        if (!TryField(fields[3], 1, 12, MonthNames, out var months, out _, out var monthWhy))
            return Fail(4, monthWhy);
        if (!TryField(fields[4], 0, 7, DayNames, out var daysOfWeek, out var dowStar, out var dowWhy))
            return Fail(5, dowWhy);

        // 0 and 7 are both Sunday (docs/26 §2.2): an expression copied from elsewhere must not run a day late.
        if (daysOfWeek.Remove(7)) daysOfWeek.Add(0);

        return Result<CronFields>.Success(new CronFields(string.Join(' ', fields), minutes, hours, daysOfMonth, months,
            daysOfWeek, domRestricted: !domStar, dowRestricted: !dowStar));

        static Result<CronFields> Fail(int field, string why) =>
            Result<CronFields>.Failure(new(ErrorCode.Invalid, $"field {field} ({FieldNames[field - 1]}): {why}"));
    }

    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JAN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4, ["MAY"] = 5, ["JUN"] = 6,
        ["JUL"] = 7, ["AUG"] = 8, ["SEP"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DEC"] = 12
    };

    private static readonly Dictionary<string, int> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUN"] = 0, ["MON"] = 1, ["TUE"] = 2, ["WED"] = 3, ["THU"] = 4, ["FRI"] = 5, ["SAT"] = 6
    };

    private static readonly Dictionary<string, int> None = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One field: <c>*</c>, a value, a name, a list of those, an ascending range, and a step on either side of it.
    /// </summary>
    private static bool TryField(string text, int min, int max, Dictionary<string, int> names,
        out HashSet<int> values, out bool wasStar, out string why)
    {
        values = [];
        wasStar = false;
        why = "";
        if (text.Length == 0)
        {
            why = "the field is empty.";
            return false;
        }
        foreach (var item in text.Split(','))
        {
            if (item.Length == 0)
            {
                why = "it has an empty list element (a stray comma).";
                return false;
            }

            var body = item;
            var step = 1;
            var slash = body.IndexOf('/');
            if (slash >= 0)
            {
                var stepText = body[(slash + 1)..];
                body = body[..slash];
                if (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    why = $"'{item}' has a step that is not a positive whole number.";
                    return false;
                }
                if (body.Length == 0)
                {
                    why = $"'{item}' has a step with nothing to step over.";
                    return false;
                }
                if (body != "*" && !body.Contains('-'))
                {
                    why = $"'{item}' steps over a single value. Write a range with the step, or drop the step.";
                    return false;
                }
            }

            int start, end;
            if (body == "*")
            {
                start = min;
                end = max;
                if (slash < 0) wasStar = true;
            }
            else if (body.Contains('-'))
            {
                var bounds = body.Split('-');
                if (bounds.Length != 2 || !TryValue(bounds[0], min, max, names, out start, out why) ||
                    !TryValue(bounds[1], min, max, names, out end, out why))
                {
                    if (why.Length == 0) why = $"'{item}' is not a range.";
                    return false;
                }
                if (end < start)
                {
                    why = $"'{item}' runs backwards. Ranges must ascend here; wrapping ranges (fri-mon) are not supported.";
                    return false;
                }
            }
            else
            {
                if (!TryValue(body, min, max, names, out start, out why)) return false;
                end = start;
            }

            for (var value = start; value <= end; value += step) values.Add(value);
        }
        return true;
    }

    private static bool TryValue(string text, int min, int max, Dictionary<string, int> names, out int value, out string why)
    {
        why = "";
        if (names.Count > 0 && names.TryGetValue(text.Trim(), out value)) return true;
        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            if (value < min || value > max)
            {
                why = $"'{text.Trim()}' is outside {min}-{max}.";
                return false;
            }
            return true;
        }
        value = 0;
        why = names.Count > 0
            ? $"'{text.Trim()}' is not a number or a known name ({string.Join(", ", names.Keys.Order())})."
            : $"'{text.Trim()}' is not a number.";
        return false;
    }
}
