using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The schedule verbs: what is stored, and writing one down.
///
/// Two depths, because the two questions are different. Listing is an agent's — "when does this host wake up on its own"
/// is something an assistant should be able to answer instead of guessing, and a list nobody can read is a list that
/// drifts. Writing is an operator's: a row decides when something happens with nobody present, which is a decision about
/// the deployment. A model cannot schedule itself, but a Session's own <c>wait</c> stays open to it — a one-off deadline
/// is "look again at this point", not "run forever every five minutes".
///
/// The defaults a new row gets are applied here rather than in the store, and they are stated in the reply: a rule that
/// quietly lives for one thousand runs is a rule that quietly stops.
/// </summary>
public static class ScheduleActions
{
    public const string List = "schedules.list";
    public const string Get = "schedules.get";
    public const string Set = "schedules.set";
    public const string Cancel = "schedules.cancel";

    /// <summary>How long a new row lives when the caller says nothing: long enough to be routine, short enough to be renewed.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);

    public const int DefaultMaxRuns = 1000;

    public static Result Register(IControlPlane plane, ScheduleStore store, TimeProvider? time = null)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        if (store is null) return Result.Failure(new(ErrorCode.Invalid, "A schedule store is required."));
        var clock = time ?? TimeProvider.System;

        var registrations = new (ActionDescriptor Descriptor, AuthorizedActionHandler Handler)[]
        {
            (new(List, "Schedules this host will act on by itself: what runs, when it next runs, how many times it has "
                    + "run, and why it stopped if it did. Expired rows stay in the list so the history stays answerable.",
                Capabilities.SchedulesRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
                async (_, _, ct) =>
                {
                    var listed = await store.ListAsync(ct).ConfigureAwait(false);
                    if (!listed.Ok) return Result<JsonElement>.Failure(listed.Error!);
                    return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                    {
                        schedules = listed.Value!.Select(Project).ToArray()
                    }));
                }),

            (new(Get, "One schedule as stored, including the next instant it is due.", Capabilities.SchedulesRead, false,
                ParamsJson: """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""",
                Subject: "id"),
                async (_, arguments, ct) =>
                {
                    if (!arguments.TryReadText("id", out var id))
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "schedules.get requires 'id'."));
                    var found = await store.GetAsync(id, ct).ConfigureAwait(false);
                    return found.Ok
                        ? Result<JsonElement>.Success(ActionArgs.JsonOf(Project(found.Value!)))
                        : Result<JsonElement>.Failure(found.Error!);
                }),

            (new(Set, "Write a schedule down, or replace one with the same id. kind: every (\"5m\"), dailyAt (\"08:00\" "
                    + "with optional days), once (an instant) or cron (five fields). timeZone is required for every kind: "
                    + "8 o'clock without a zone is not a time. A model cannot call this — writing down when the host acts "
                    + "on its own is an operator's decision.",
                Capabilities.SchedulesControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"Reuse an id to replace that row; omit to create one."},
                  "kind":{"type":"string","enum":["every","dailyAt","once","cron"]},
                  "expression":{"type":"string","description":"\"5m\" / \"08:00\" / an ISO-8601 instant / \"0 8 * * 1-5\"."},
                  "timeZone":{"type":"string","description":"IANA id, e.g. Asia/Shanghai. No default."},
                  "days":{"type":"array","items":{"type":"string"},"description":"mon..sun; dailyAt only."},
                  "target":{"type":"string","enum":["session","script"]},
                  "targetId":{"type":"string","description":"The Session id, or the script id."},
                  "targetAddress":{"type":"string","description":"For a script: the instance its mount sits on."},
                  "payload":{"type":"string","description":"Text handed to the run."},
                  "maxRuns":{"type":"integer","minimum":1,"description":"How many firings before it stops."},
                  "until":{"type":"string","description":"ISO-8601 instant after which it no longer fires."}},
                 "required":["kind","expression","timeZone","target","targetId"],"additionalProperties":false}
                """, Subject: "id"),
                async (actor, arguments, ct) => await SetAsync(actor, arguments, store, clock.GetUtcNow(), ct).ConfigureAwait(false)),

            (new(Cancel, "Remove one schedule. Saying it again is not an error: gone is the outcome that was wanted.",
                Capabilities.SchedulesControl, true,
                ParamsJson: """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""",
                Subject: "id"),
                async (_, arguments, ct) =>
                {
                    if (!arguments.TryReadText("id", out var id))
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "schedules.cancel requires 'id'."));
                    var cancelled = await store.CancelAsync(id, ct).ConfigureAwait(false);
                    return cancelled.Ok
                        ? Result<JsonElement>.Success(ActionArgs.JsonOf(new { id = id.Trim(), removed = cancelled.Value }))
                        : Result<JsonElement>.Failure(cancelled.Error!);
                })
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    private static async ValueTask<Result<JsonElement>> SetAsync(Actor actor, JsonElement arguments, ScheduleStore store,
        DateTimeOffset now, CancellationToken ct)
    {
        if (!arguments.TryReadText("kind", out var kindText) ||
            !Enum.TryParse<ScheduleKind>(kindText.Trim(), ignoreCase: true, out var kind))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                "schedules.set requires 'kind': every, dailyAt, once or cron."));
        if (!arguments.TryReadText("expression", out var expression))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "schedules.set requires 'expression'."));
        if (!arguments.TryReadText("timeZone", out var timeZone) || timeZone.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                "schedules.set requires 'timeZone' (IANA id). There is no default: \"08:00\" without a zone is not a time."));
        if (!arguments.TryReadText("target", out var targetText) ||
            !Enum.TryParse<ScheduleTargetKind>(targetText.Trim(), ignoreCase: true, out var targetKind))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "schedules.set requires 'target': session or script."));
        if (!arguments.TryReadText("targetId", out var targetId) || targetId.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "schedules.set requires 'targetId'."));

        IReadOnlyList<DayOfWeek>? days = null;
        if (arguments.TryGetProperty("days", out var daysJson) && daysJson.ValueKind == JsonValueKind.Array)
        {
            var parsed = new List<DayOfWeek>();
            foreach (var day in daysJson.EnumerateArray())
            {
                if (day.ValueKind != JsonValueKind.String || !TryDay(day.GetString(), out var weekday))
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                        $"days: '{day}' is not a weekday. Use mon, tue, wed, thu, fri, sat, sun."));
                parsed.Add(weekday);
            }
            days = parsed;
        }

        DateTimeOffset? until = null;
        if (arguments.TryReadText("until", out var untilText) && untilText.Trim().Length > 0)
        {
            var parsed = ScheduleSpec.Parse(ScheduleKind.Once, untilText, timeZone, now);
            if (!parsed.Ok) return Result<JsonElement>.Failure(parsed.Error!);
            until = parsed.Value!.NextDueAfter(now, now);
            if (until is null)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"until \"{untilText}\" has already passed."));
        }

        int? maxRuns = null;
        if (arguments.TryGetProperty("maxRuns", out var maxRunsJson) && maxRunsJson.ValueKind == JsonValueKind.Number)
        {
            if (!maxRunsJson.TryGetInt32(out var value) || value < 1)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "maxRuns must be at least 1."));
            maxRuns = value;
        }

        var defaults = new List<string>();
        if (maxRuns is null)
        {
            maxRuns = DefaultMaxRuns;
            defaults.Add($"maxRuns={DefaultMaxRuns}");
        }
        if (until is null)
        {
            until = now + DefaultLifetime;
            defaults.Add($"until={until:yyyy-MM-dd HH:mm zzz}");
        }

        var id = arguments.TryReadText("id", out var existing) && existing.Trim().Length > 0
            ? existing.Trim()
            : ScheduleStore.NewId();
        var draft = new ScheduleRow(id, kind, expression, timeZone, targetKind, targetId.Trim(), now,
            Days: days,
            TargetAddress: arguments.TryReadText("targetAddress", out var address) && address.Trim().Length > 0
                ? address.Trim()
                : null,
            Payload: arguments.TryReadText("payload", out var payload) && payload.Length > 0 ? payload : null,
            MaxRuns: maxRuns,
            Until: until,
            // Who wrote this version. A rule decides what happens with nobody in the room, so "who decided that" is
            // part of the row rather than only a line in an audit log that has since rotated.
            CreatedBy: string.IsNullOrWhiteSpace(actor.Id) ? null : actor.Id);

        var stored = await store.SetAsync(draft, now, ct).ConfigureAwait(false);
        if (!stored.Ok) return Result<JsonElement>.Failure(stored.Error!);
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            schedule = Project(stored.Value!),
            // What it means, in words — and what the caller did not have to say because of the defaults.
            means = stored.Value!.Description,
            defaults
        }));
    }

    private static bool TryDay(string? text, out DayOfWeek day)
    {
        day = DayOfWeek.Monday;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        foreach (var candidate in Enum.GetValues<DayOfWeek>())
        {
            if (trimmed.StartsWith(candidate.ToString()[..3], StringComparison.OrdinalIgnoreCase))
            {
                day = candidate;
                return true;
            }
        }
        return false;
    }

    private static object Project(ScheduleRow row) => new
    {
        id = row.Id,
        kind = row.Kind.ToString().ToLowerInvariant(),
        expression = row.Expression,
        timeZone = row.TimeZone,
        description = row.Description,
        target = row.TargetKind.ToString().ToLowerInvariant(),
        targetId = row.TargetId,
        targetAddress = row.TargetAddress,
        nextDue = row.NextDue,
        enabled = row.Enabled,
        runs = row.Runs,
        maxRuns = row.MaxRuns,
        until = row.Until,
        missed = row.Missed,
        skipped = row.Skipped,
        lastRunAt = row.LastRunAt,
        lastOutcome = row.LastOutcome,
        disabledReason = row.DisabledReason,
        createdAt = row.CreatedAt,
        createdBy = row.CreatedBy
    };
}
