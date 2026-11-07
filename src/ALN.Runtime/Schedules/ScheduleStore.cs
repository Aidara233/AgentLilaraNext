using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>What a firing runs. Two kinds, because there are two things in this host that can be asked to act.</summary>
public enum ScheduleTargetKind
{
    /// <summary>A Session: the firing runs a turn with nobody's message behind it.</summary>
    Session,
    /// <summary>A mounted script: the firing is an event the graph dispatches.</summary>
    Script
}

/// <summary>
/// One schedule, as stored. A row is the whole truth: what to run, when, how often it may still run, and what happened
/// last time. Nothing about a schedule lives in memory only — I-11 makes that the only honest shape, and it is also what
/// lets somebody answer "what will this system do on its own" by reading a file.
/// </summary>
/// <param name="NextDue">When it fires next. Recomputed by the store from the expression, never typed by hand.</param>
/// <param name="MaxRuns">null = no cap. The action applies a default; the store only obeys.</param>
/// <param name="Until">null = no expiry. Same division as <paramref name="MaxRuns"/>.</param>
/// <param name="TargetAddress">For scripts: the instance the mount sits on, as an address string.</param>
/// <param name="Payload">Text handed to the run — a note for the Session, or the event's payload for a script.</param>
/// <param name="DisabledReason">Why it stopped being enabled, in words, so a row that went quiet explains itself.</param>
public sealed record ScheduleRow(
    string Id,
    ScheduleKind Kind,
    string Expression,
    string TimeZone,
    ScheduleTargetKind TargetKind,
    string TargetId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextDue = null,
    IReadOnlyList<DayOfWeek>? Days = null,
    string? TargetAddress = null,
    string? Payload = null,
    int Runs = 0,
    int? MaxRuns = null,
    DateTimeOffset? Until = null,
    bool Enabled = true,
    string? CreatedBy = null,
    string? Description = null,
    DateTimeOffset? LastRunAt = null,
    string? LastOutcome = null,
    int Missed = 0,
    int Skipped = 0,
    string? DisabledReason = null);

/// <summary>
/// Where schedule rows live: one state key, beside the mount rules, read on start and written on every change.
///
/// Three things it deliberately does not do. It holds no timer (the sweep asks). It keeps no in-memory truth that the
/// file does not have (a rule that exists only in memory disappears at the next restart, and the caller would have no
/// way to know). And it applies no policy about how many runs are sensible: "1000 times or 30 days" is a default the
/// action puts on a new row, not something a store should decide.
///
/// <see cref="ScheduleRow.CreatedAt"/> is the anchor an <c>every</c> schedule counts from, which is why it is stored:
/// re-parsing on restore with "now" as the anchor would move every beat to the restart.
/// </summary>
public sealed class ScheduleStore(IStateStore? state, ISignalLog? signals = null)
{
    private const string Scope = "schedules";
    private const string Key = "rows";
    private const int Schema = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ScheduleRow>? _rows;

    /// <summary>A fresh id. Readable prefix, so a row in a log can be recognised for what it is.</summary>
    public static string NewId() => "sch-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Every stored row, expired ones included: a row that stopped is still an answer to "what was scheduled".</summary>
    public async ValueTask<Result<IReadOnlyList<ScheduleRow>>> ListAsync(CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok) return Result<IReadOnlyList<ScheduleRow>>.Failure(loaded.Error!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Result<IReadOnlyList<ScheduleRow>>.Success(_rows!.ToArray()); }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result<ScheduleRow>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Result<ScheduleRow>.Failure(new(ErrorCode.Invalid, "A schedule id is required."));
        var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok) return Result<ScheduleRow>.Failure(loaded.Error!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var row = _rows!.FirstOrDefault(existing => string.Equals(existing.Id, id.Trim(), StringComparison.Ordinal));
            return row is null
                ? Result<ScheduleRow>.Failure(new(ErrorCode.Invalid, $"No schedule is called {id.Trim()}."))
                : Result<ScheduleRow>.Success(row);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Creates or replaces one row. The expression is parsed <b>here</b>, so a rule that cannot fire is refused at the
    /// moment somebody writes it rather than discovered by a sweep that keeps finding nothing to do.
    /// </summary>
    public async ValueTask<Result<ScheduleRow>> SetAsync(ScheduleRow draft, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (draft is null) return Result<ScheduleRow>.Failure(new(ErrorCode.Invalid, "A schedule row is required."));
        if (string.IsNullOrWhiteSpace(draft.TargetId))
            return Result<ScheduleRow>.Failure(new(ErrorCode.Invalid,
                "A schedule needs a target: the Session or script it fires for."));
        var spec = ScheduleSpec.Parse(draft.Kind, draft.Expression, draft.TimeZone, draft.CreatedAt, draft.Days);
        if (!spec.Ok) return Result<ScheduleRow>.Failure(spec.Error!);

        var id = string.IsNullOrWhiteSpace(draft.Id) ? NewId() : draft.Id.Trim();
        var next = draft.NextDue ?? spec.Value!.NextDueAfter(draft.CreatedAt, now);
        if (next is null)
            return Result<ScheduleRow>.Failure(new(ErrorCode.Invalid,
                $"{spec.Value!.Describe()} has no next firing: for a one-off, the instant has already passed."));
        if (draft.MaxRuns is <= 0)
            return Result<ScheduleRow>.Failure(new(ErrorCode.Invalid, "maxRuns must be at least 1, or left out for no cap."));
        if (draft.Until is { } until && until <= now)
            return Result<ScheduleRow>.Failure(new(ErrorCode.Invalid,
                $"until is {until:yyyy-MM-dd HH:mm zzz}, which has already passed."));

        var row = draft with
        {
            Id = id,
            Expression = draft.Expression.Trim(),
            TimeZone = draft.TimeZone.Trim(),
            NextDue = next,
            Days = draft.Days is { Count: > 0 } ? draft.Days.Distinct().Order().ToArray() : null,
            Description = draft.Description ?? spec.Value!.Describe(),
            CreatedBy = draft.CreatedBy,
            Enabled = draft.Enabled
        };

        var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok) return Result<ScheduleRow>.Failure(loaded.Error!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _rows!.FindIndex(existing => string.Equals(existing.Id, id, StringComparison.Ordinal));
            var before = index < 0 ? null : _rows[index];
            if (index < 0) _rows.Add(row); else _rows[index] = row;
            var persisted = await PersistAsync(cancellationToken).ConfigureAwait(false);
            if (!persisted.Ok)
            {
                // Persist before claiming success: a rule that exists only in memory is one that disappears at restart.
                if (index < 0) _rows.Remove(row); else _rows[index] = before!;
                return Result<ScheduleRow>.Failure(persisted.Error!);
            }
            return Result<ScheduleRow>.Success(row);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes a row. Removing something absent is not an error: "gone" is the outcome that was wanted.</summary>
    public async ValueTask<Result<bool>> CancelAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Result<bool>.Failure(new(ErrorCode.Invalid, "A schedule id is required."));
        var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok) return Result<bool>.Failure(loaded.Error!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = _rows!.RemoveAll(row => string.Equals(row.Id, id.Trim(), StringComparison.Ordinal)) > 0;
            if (!removed) return Result<bool>.Success(false);
            var persisted = await PersistAsync(cancellationToken).ConfigureAwait(false);
            return persisted.Ok ? Result<bool>.Success(true) : Result<bool>.Failure(persisted.Error!);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Rows whose time has come: enabled, due, and not past their own expiry.</summary>
    public async ValueTask<Result<IReadOnlyList<ScheduleRow>>> DueAsync(DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok) return Result<IReadOnlyList<ScheduleRow>>.Failure(loaded.Error!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var due = _rows!
                .Where(row => row.Enabled && row.NextDue is { } next && next <= now)
                .Where(row => row.Until is not { } until || row.NextDue!.Value <= until)
                .OrderBy(row => row.NextDue)
                .ToArray();
            return Result<IReadOnlyList<ScheduleRow>>.Success(due);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Records one firing and advances the row. <paramref name="missed"/> is how many beats went by between the one this
    /// replaces and now — counted, never replayed (docs/26 §1): a host that was down for three days owes nobody 72 runs.
    /// </summary>
    public async ValueTask<Result<ScheduleRow>> RecordRunAsync(string id, DateTimeOffset runAt, string outcome,
        int missed, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok) return Result<ScheduleRow>.Failure(loaded.Error!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _rows!.FindIndex(row => string.Equals(row.Id, id, StringComparison.Ordinal));
            if (index < 0) return Result<ScheduleRow>.Failure(new(ErrorCode.Invalid, $"No schedule is called {id}."));
            var row = _rows[index];
            var spec = ScheduleSpec.Parse(row.Kind, row.Expression, row.TimeZone, row.CreatedAt, row.Days);
            var runs = row.Runs + 1;
            // A row whose expression this build cannot read cannot be advanced — but the firing is still a fact and is
            // still recorded. Refusing to write it down would leave the row looking untouched while the sweep reported a
            // failure, which is exactly the pair of statements that makes a stuck rule invisible.
            var next = !spec.Ok
                ? null
                : row.MaxRuns is { } cap && runs >= cap
                    ? null
                    : spec.Value!.NextDueAfter(row.NextDue ?? row.CreatedAt, runAt);
            var exhaustedByUntil = next is { } candidate && row.Until is { } until && candidate > until;
            var disabled = next is null || exhaustedByUntil;
            var advanced = row with
            {
                Runs = runs,
                LastRunAt = runAt,
                LastOutcome = outcome,
                Missed = row.Missed + Math.Max(0, missed),
                NextDue = disabled ? row.NextDue : next,
                Enabled = disabled ? false : row.Enabled,
                DisabledReason = disabled
                    ? !spec.Ok
                        ? $"the stored expression no longer parses: {spec.Error?.Message}"
                        : next is null
                            ? $"ran {runs} time(s), which is its maxRuns" + (row.MaxRuns is { } limit ? $" of {limit}" : "")
                            : $"the next firing would be past its until ({row.Until:yyyy-MM-dd HH:mm zzz})"
                    : row.DisabledReason
            };
            _rows[index] = advanced;
            var persisted = await PersistAsync(cancellationToken).ConfigureAwait(false);
            if (!persisted.Ok)
            {
                _rows[index] = row;
                return Result<ScheduleRow>.Failure(persisted.Error!);
            }
            if (disabled)
                signals?.Write(new()
                {
                    Group = "schedule", Name = advanced.Id, From = "schedule", Mechanism = "expire", Outcome = "disabled",
                    Level = SignalLevel.Info,
                    Detail = new Dictionary<string, string>
                    {
                        ["reason"] = advanced.DisabledReason ?? "",
                        ["runs"] = runs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                });
            return Result<ScheduleRow>.Success(advanced);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Notes a beat that was skipped because the previous one was still running. Not an error: a report.</summary>
    public async ValueTask<Result> RecordSkipAsync(string id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok) return loaded;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _rows!.FindIndex(row => string.Equals(row.Id, id, StringComparison.Ordinal));
            if (index < 0) return Result.Success();
            _rows[index] = _rows[index] with { Skipped = _rows[index].Skipped + 1 };
            return await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------------------------------------------------------

    private async ValueTask<Result> LoadAsync(CancellationToken cancellationToken)
    {
        if (_rows is not null) return Result.Success();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rows is not null) return Result.Success();
            if (state is null) { _rows = []; return Result.Success(); }
            var read = await state.ReadAsync(Scope, Key, cancellationToken).ConfigureAwait(false);
            if (read.Error is { } readError) return Result.Failure(readError);
            if (read.Value is null) { _rows = []; return Result.Success(); }

            Stored? stored;
            try { stored = JsonSerializer.Deserialize<Stored>(read.Value, Json); }
            catch (JsonException ex)
            {
                // Same stance as the mount rules: keep the evidence, start with none. Refusing to boot over one state
                // file helps nobody, and pretending it parsed would run things nobody asked for.
                var keeper = StateName.Encode("unreadable-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await state.WriteAsync(Scope, keeper, read.Value, CancellationToken.None).ConfigureAwait(false);
                await state.DeleteAsync(Scope, Key, CancellationToken.None).ConfigureAwait(false);
                signals?.Write(new()
                {
                    Group = "schedule", Name = "rows", From = "schedule", Mechanism = "state", Outcome = "unreadable",
                    Level = SignalLevel.Warn,
                    Detail = new Dictionary<string, string> { ["message"] = ex.Message, ["kept"] = keeper }
                });
                _rows = [];
                return Result.Success();
            }
            if (stored is null || stored.SchemaVersion != Schema)
            {
                _rows = [];
                return Result.Success();
            }
            _rows = [.. stored.Rows];
            return Result.Success();
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<Result> PersistAsync(CancellationToken cancellationToken)
    {
        if (state is null) return Result.Success();
        var json = JsonSerializer.Serialize(new Stored(Schema, _rows!.ToArray()), Json);
        return await state.WriteAsync(Scope, Key, json, cancellationToken).ConfigureAwait(false);
    }

    private sealed record Stored(int SchemaVersion, IReadOnlyList<ScheduleRow> Rows);
}
