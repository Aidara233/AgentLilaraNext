using System.Text.Json;
using ALN.Contracts;

namespace ALN.Rice;

/// <summary>
/// An allowance that pays for a round instead of the larder.
///
/// This is the difference between "this round is free" and "this round was paid for and then refunded". The larder's
/// own number never moves, so what a person reads is what is true, and a policy that forgets to pay something back
/// cannot quietly cost somebody their food. It is also why the whole feature is one row of data rather than a script
/// watching for a deduction: a deduction is not something a script can watch (there is no such event), and "take it
/// back afterwards" is only a design if nothing ever goes wrong in between.
/// </summary>
/// <param name="Id">What a person says to cancel it. Stable, and chosen by the writer, because the thing being
/// cancelled is an intention ("today is on the house"), not a counter.</param>
/// <param name="Channel">Which Channel it pays for; null pays for any. Null is not sloppiness: it is the only way to
/// write "today is free everywhere" without naming every conversation, and a Channel-specific allowance still wins
/// nothing over a global one — the earliest deadline is served first either way.</param>
/// <param name="Remaining">How many rounds it still pays for. "A temporary 500 bites" is exactly this number, because
/// a round costs one bite: the two ways of saying it are the same fact. Meaningless when <paramref name="Unlimited"/>
/// is set, and left at whatever the writer put there rather than being kept in step.</param>
/// <param name="Until">When it stops paying. Expiry is lazy — a waiver past this instant is simply never matched, and
/// is dropped the next time the table is written — so nothing has to run at midnight, and a host that was down at
/// midnight still gets the right answer.</param>
/// <param name="Unlimited">
/// "Every round until it expires", rather than a count. It is a flag and not a very large number because a number that
/// means "infinite" has to be recognised as such in every place that reads it — the countdown, the "used up" purge, the
/// display — and one of those places would eventually forget. A count of rounds is also honest about what it is: a
/// promise someone can read off and check.
/// </param>
public sealed record RiceWaiver(string Id, string? Channel, long Remaining, DateTimeOffset Until, string Reason,
    DateTimeOffset CreatedAt, bool Unlimited = false)
{
    /// <summary>Whether this allowance can pay for a round belonging to <paramref name="channel"/> right now.</summary>
    public bool Covers(Address? channel, DateTimeOffset now) =>
        (Unlimited || Remaining > 0) &&
        Until > now &&
        (Channel is null || (channel is { } target && string.Equals(Channel, target.ToString(), StringComparison.Ordinal)));

    /// <summary>What is left, in the words a person reads. One place, because it is said in three.</summary>
    public string RoundsLabel => Unlimited ? "不限" : $"{Remaining} 轮";

    /// <summary>Whether this row is still worth keeping: not spent, not expired.</summary>
    public bool IsLive(DateTimeOffset now) => (Unlimited || Remaining > 0) && Until > now;
}

/// <summary>
/// A larder, plus what has been promised about it.
///
/// The cursor (<see cref="Charged"/>) is the idempotency key. A round is identified by <c>(session, step)</c> and steps
/// only go up, so remembering the last step charged for a Session is enough — no window of keys to age out, and a
/// retried step cannot be charged twice, whether it was paid by the larder or by an allowance.
/// </summary>
public sealed record RiceState(long Bites, long Revision, IReadOnlyDictionary<string, int> Charged,
    IReadOnlyList<RiceWaiver>? Waivers = null)
{
    public static RiceState Empty(long bites = 0) => new(bites, 0,
        new Dictionary<string, int>(StringComparer.Ordinal), []);

    public int ChargedUpTo(string sessionId) => Charged.TryGetValue(sessionId, out var step) ? step : 0;

    /// <summary>The allowances, never null: state written before this field existed reads as "none".</summary>
    public IReadOnlyList<RiceWaiver> Allowances => Waivers ?? [];

    /// <summary>
    /// The allowance that pays for this round, or null when the larder does.
    ///
    /// Earliest deadline first, and that is the only order that cannot waste food: an allowance about to expire is
    /// worth more than one that is not, and serving it first is what stops "the one that was about to run out" from
    /// running out unused. A null <paramref name="now"/> means the caller had no instant to offer — a round that cannot
    /// say when it happened cannot claim today's allowance, so it pays like it always did.
    /// </summary>
    public RiceWaiver? WaiverFor(Address? channel, DateTimeOffset? now) =>
        now is not { } at || at == default
            ? null
            : Allowances.Where(waiver => waiver.Covers(channel, at))
                .OrderBy(waiver => waiver.Until)
                .ThenBy(waiver => waiver.CreatedAt)
                .FirstOrDefault();
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
    /// Eats one round that brought no source information: no Channel, no instant. It pays the way it always did, because
    /// a round that cannot say which conversation it belongs to or when it happened cannot claim an allowance.
    /// </summary>
    public ValueTask<Result<RiceState>> ChargeRoundAsync(string sessionId, int step,
        CancellationToken cancellationToken) =>
        ChargeRoundAsync(sessionId, step, channel: null, now: null, cancellationToken);

    /// <summary>
    /// Eats one round for <c>(session, step)</c>, out of an allowance when one covers it and out of the larder otherwise.
    ///
    /// Charging the same step twice is a no-op, not a second bowl — and that covers the allowance too: a retried round
    /// must not spend the same waiver twice. A retried round is the framework's own retry, and the framework's retries
    /// do not cost the model anything.
    /// </summary>
    public ValueTask<Result<RiceState>> ChargeRoundAsync(string sessionId, int step, Address? channel,
        DateTimeOffset? now, CancellationToken cancellationToken) =>
        ChangeAsync($"eat:{sessionId}", $"session:{sessionId}", state =>
        {
            if (state.ChargedUpTo(sessionId) >= step) return Result<RiceState>.Success(state);
            var charged = new Dictionary<string, int>(state.Charged, StringComparer.Ordinal) { [sessionId] = step };
            var waiver = state.WaiverFor(channel, now);
            if (waiver is null)
                return Result<RiceState>.Success(state with
                {
                    Bites = state.Bites - 1,
                    Revision = state.Revision + 1,
                    Charged = charged,
                    // Paying the larder is a write too, so it is also when dead rows go: a table that tidied itself only
                    // on the days something matched would keep expired rows for as long as nobody bought anything.
                    Waivers = now is { } paidAt
                        ? state.Allowances.Where(entry => entry.IsLive(paidAt)).ToArray()
                        : state.Waivers
                });

            // Paid by the allowance: the larder does not move, but the allowance does. This write is also the one that
            // drops what has expired or been used up, so the table cannot grow forever and no sweep has to run at
            // midnight — a host that was down at midnight still gets the right answer on its first round back.
            // WaiverFor only matches when there was an instant to match against, so this is that instant.
            var chargedAt = now!.Value;
            var kept = state.Allowances
                .Select(entry => string.Equals(entry.Id, waiver.Id, StringComparison.Ordinal) && !entry.Unlimited
                    ? entry with { Remaining = entry.Remaining - 1 }
                    : entry)
                .Where(entry => entry.IsLive(chargedAt))
                .ToArray();
            return Result<RiceState>.Success(state with
            {
                Revision = state.Revision + 1,
                Charged = charged,
                Waivers = kept
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

    /// <summary>
    /// Promises that the next <paramref name="bites"/> rounds are on the house, on one Channel or (with null) on every
    /// Channel, until <paramref name="until"/>.
    ///
    /// Adding, not replacing: two campaigns on the same Channel are two allowances, and the one expiring first is spent
    /// first. A name that is already in the table is refused rather than silently overwritten — cancelling an intention
    /// and then re-using its name would make "why did this stop working" a question with no answer.
    ///
    /// This write also drops what has expired or been spent, so the table stays the size of what is still true.
    /// </summary>
    public ValueTask<Result<RiceState>> GrantWaiverAsync(RiceWaiver waiver, string actor,
        CancellationToken cancellationToken) =>
        ChangeAsync($"waive:{waiver.Id}", actor, state =>
        {
            if (state.Allowances.Any(entry => string.Equals(entry.Id, waiver.Id, StringComparison.Ordinal)))
                return Result<RiceState>.Failure(new(ErrorCode.Conflict, $"已经有一条豁免叫 {waiver.Id} 了。"));
            if (!waiver.Unlimited && waiver.Remaining <= 0)
                return Result<RiceState>.Failure(new(ErrorCode.Invalid, "豁免的口数必须大于 0。"));
            if (waiver.Until <= waiver.CreatedAt)
                return Result<RiceState>.Failure(new(ErrorCode.Invalid, "豁免的到期时间必须晚于创建时间。"));

            var live = state.Allowances
                .Where(entry => entry.IsLive(waiver.CreatedAt))
                .ToArray();
            return Result<RiceState>.Success(state with
            {
                Revision = state.Revision + 1,
                Waivers = [.. live, waiver]
            });
        }, cancellationToken);

    /// <summary>
    /// Takes an allowance back. Refused when nothing has that name: "cancelled" and "was never there" are different
    /// answers, and a silent success would leave a person believing a free round they are still paying for.
    /// </summary>
    public ValueTask<Result<RiceState>> RevokeWaiverAsync(string id, string actor,
        CancellationToken cancellationToken) =>
        ChangeAsync($"unwaive:{id}", actor, state =>
        {
            var kept = state.Allowances
                .Where(entry => !string.Equals(entry.Id, id, StringComparison.Ordinal))
                .ToArray();
            return kept.Length == state.Allowances.Count
                ? Result<RiceState>.Failure(new(ErrorCode.Invalid, $"没有叫 {id} 的豁免。"))
                : Result<RiceState>.Success(state with { Revision = state.Revision + 1, Waivers = kept });
        }, cancellationToken);

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
                ["revision"] = state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // How many allowances were live at this write. Bites not moving while this is above zero is the whole
                // story of a waived round, and it is the one place a later reader can see it.
                ["allowances"] = state.Allowances.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
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
