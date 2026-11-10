using System.Text.Json;
using ALN.Contracts;

namespace ALN.Rice;

/// <summary>
/// Lilara's larder: one number of bites for the whole framework, and a cursor per Session.
///
/// The cursor is the idempotency key. A round is identified by <c>(session, step)</c> and steps only go up, so
/// remembering the last step charged for a Session is enough — no window of keys to age out, and a retried step
/// cannot be charged twice.
/// </summary>
public sealed record RiceState(long Bites, long Revision, IReadOnlyDictionary<string, int> Charged)
{
    public static RiceState Empty(long bites = 0) => new(bites, 0,
        new Dictionary<string, int>(StringComparer.Ordinal));

    public int ChargedUpTo(string sessionId) => Charged.TryGetValue(sessionId, out var step) ? step : 0;
}

/// <summary>
/// The larder's single write path: eat one bowl for a round, or add/take bites.
///
/// Every change is one <see cref="IAtomicWork.AtomicallyAsync"/>: read, decide, write. Two Sessions eating at the same
/// moment therefore cannot both spend the last bowl.
/// </summary>
public sealed class RiceLedger(IStateStore store, IAtomicWork atomic, ISignalLog? signals = null,
    IAuditSink? audit = null)
{
    public const string Scope = "rice";
    public const string Key = "state";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Address Larder = new("rice", "larder", "state");

    public async ValueTask<Result<RiceState>> ReadAsync(CancellationToken cancellationToken)
    {
        var read = await store.ReadAsync(Scope, Key, cancellationToken).ConfigureAwait(false);
        // "Never written" is the normal first state, and Result<T>.Ok is false when the value is null — so the error is
        // the only thing that means the store itself failed.
        if (read.Error is not null) return Result<RiceState>.Failure(read.Error);
        if (string.IsNullOrWhiteSpace(read.Value)) return Result<RiceState>.Success(RiceState.Empty());
        try
        {
            var parsed = JsonSerializer.Deserialize<RiceState>(read.Value, Json);
            return parsed is null
                ? Result<RiceState>.Failure(new(ErrorCode.Invalid, "The larder is not readable."))
                : Result<RiceState>.Success(parsed);
        }
        catch (JsonException ex)
        {
            return Result<RiceState>.Failure(new(ErrorCode.Invalid, "The larder is not readable JSON.", ex.Message));
        }
    }

    /// <summary>
    /// Eats one bowl for <c>(session, step)</c>. Charging the same step twice is a no-op, not a second bowl: a retried
    /// round is the framework's own retry, and the framework's retries do not cost the model anything.
    /// </summary>
    public ValueTask<Result<RiceState>> ChargeRoundAsync(string sessionId, int step,
        CancellationToken cancellationToken) =>
        ChangeAsync($"eat:{sessionId}", $"session:{sessionId}", state =>
        {
            if (state.ChargedUpTo(sessionId) >= step) return Result<RiceState>.Success(state);
            var charged = new Dictionary<string, int>(state.Charged, StringComparer.Ordinal) { [sessionId] = step };
            return Result<RiceState>.Success(state with
            {
                Bites = state.Bites - 1,
                Revision = state.Revision + 1,
                Charged = charged
            });
        }, cancellationToken);

    /// <summary>Adds bites. Only a top-up, a shop script, or a person with the depth gets here.</summary>
    public ValueTask<Result<RiceState>> AddAsync(long bites, string reason, string actor,
        CancellationToken cancellationToken) =>
        ChangeAsync(reason, actor, state => bites <= 0
            ? Result<RiceState>.Failure(new(ErrorCode.Invalid, $"补的口数必须大于 0（收到 {bites}）。"))
            : Result<RiceState>.Success(state with { Bites = state.Bites + bites, Revision = state.Revision + 1 }),
            cancellationToken);

    /// <summary>Takes bites away (a spill, an experiment). Never below zero.</summary>
    public ValueTask<Result<RiceState>> TakeAsync(long bites, string reason, string actor,
        CancellationToken cancellationToken) =>
        ChangeAsync(reason, actor, state => bites <= 0
            ? Result<RiceState>.Failure(new(ErrorCode.Invalid, $"扣的口数必须大于 0（收到 {bites}）。"))
            : state.Bites < bites
                ? Result<RiceState>.Failure(new(ErrorCode.Denied, $"只有 {state.Bites} 口，不够扣 {bites}。"))
                : Result<RiceState>.Success(state with { Bites = state.Bites - bites, Revision = state.Revision + 1 }),
            cancellationToken);

    private ValueTask<Result<RiceState>> ChangeAsync(string reason, string actor,
        Func<RiceState, Result<RiceState>> change, CancellationToken cancellationToken) =>
        new(TracedAsync(reason, actor, change, cancellationToken));

    private async Task<Result<RiceState>> TracedAsync(string reason, string actor,
        Func<RiceState, Result<RiceState>> change, CancellationToken cancellationToken)
    {
        Result<RiceState> written = Result<RiceState>.Failure(new(ErrorCode.Internal, "The transaction did not run."));
        var outcome = await atomic.AtomicallyAsync(async token =>
        {
            var read = await ReadAsync(token).ConfigureAwait(false);
            if (read.Error is not null || read.Value is null)
                return Result.Failure(read.Error ?? new(ErrorCode.Internal, "读不到米缸。"));

            var changed = change(read.Value);
            if (!changed.Ok || changed.Value is null) return Result.Failure(changed.Error!);

            var saved = await store.WriteAsync(Scope, Key, JsonSerializer.Serialize(changed.Value, Json), token)
                .ConfigureAwait(false);
            if (!saved.Ok) return saved;

            written = changed;
            return Result.Success();
        }, cancellationToken).ConfigureAwait(false);

        if (!outcome.Ok) return Result<RiceState>.Failure(outcome.Error!);

        var state = written.Value!;
        signals?.Write(new SignalRecord
        {
            Group = "rice",
            Name = reason,
            From = actor,
            To = Larder.ToString(),
            Mechanism = "rice",
            Outcome = "ok",
            Level = SignalLevel.Info,
            Detail = new Dictionary<string, string>
            {
                ["bites"] = state.Bites.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["revision"] = state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        });
        if (audit is not null)
            await audit.AppendAsync(new AuditRecord(DateTimeOffset.UtcNow, Guid.Empty, actor, Larder, reason, "ok",
                $"bites={state.Bites} revision={state.Revision}"), cancellationToken).ConfigureAwait(false);

        return Result<RiceState>.Success(state);
    }
}

/// <summary>
/// The unit a person buys in and the unit a Session eats in.
///
/// One purchase is one bowl, a bowl is <see cref="BitesPerBowl"/> bites, and a round eats one bite — so the number in
/// the larder is also "how many rounds are left", which is the only number the model can plan with. Showing bites
/// first and bowls second keeps the arithmetic visible: 86 bites is 86 rounds, and a bit under one bowl.
/// </summary>
public static class RiceUnits
{
    public const long BitesPerBowl = 100;

    public static double Bowls(long bites) => bites / (double)BitesPerBowl;

    public static string Describe(long bites) =>
        $"{bites} 口（≈{Bowls(bites):0.##} 碗，还能跑 {bites} 轮）";
}
