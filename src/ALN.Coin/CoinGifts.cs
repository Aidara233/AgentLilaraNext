using System.Globalization;
using ALN.Contracts;

namespace ALN.Coin;

/// <summary>
/// A coin somebody gave Lilara today: the number after it, and how many people had given one by then.
/// <see cref="GiversToday"/> is not decoration — it is the row that was just written, so "你是今天第 3 位" is read off
/// the same transaction instead of being computed from a table somebody else may have changed in between.
/// </summary>
public sealed record CoinGiftGiven(long Balance, long Revision, int GiversToday);

/// <summary>
/// The one thing a person — not the model — may add to the purse: a single coin a day.
///
/// <b>Why it is not a tool.</b> <c>coin_buy</c> is the only way the model touches money and it can only spend. A
/// <c>coin_give</c> tool would be a model printing its own allowance. Giving is a person's act, so it arrives as an
/// instruction, and the instruction layer is where "who is this" was already answered (docs/31 §9.3).
///
/// <b>Why the day is in the purse.</b> The coin and the day are one write: if the balance went up and the day did not get
/// recorded, the same person can give again — so both move inside one <see cref="CoinLedger.ChangeAsync"/> transaction,
/// which is also the only door the balance has. A refused gift writes nothing at all.
/// </summary>
/// <param name="time">
/// Which clock decides what "today" is. Local, not UTC: a day is a thing people live in, and a gift at 23:50 and one at
/// 00:10 the next morning are on different days for the person giving them (the same reading as the larder's 今日).
/// </param>
public sealed class CoinGifts(CoinLedger ledger, TimeProvider? time = null)
{
    /// <summary>
    /// One coin per person per day. A constant rather than a parameter: nothing has asked for two, and a number that can
    /// be set is a number somebody has to be trusted with.
    /// </summary>
    public const long CoinsPerDay = 1;

    /// <summary>The day a gift given <paramref name="now"/> belongs to, spelled the one way the purse stores it.</summary>
    public static string DayOf(DateTimeOffset now) => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Puts one coin in the purse, or explains that this person already gave one today. The refusal is a
    /// <see cref="ErrorCode.Conflict"/> and not a quiet success: nothing moved, and the audit should say so.
    /// </summary>
    public ValueTask<Result<CoinGiftGiven>> GiveAsync(Person person, string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (string.IsNullOrWhiteSpace(person.Id))
            return ValueTask.FromResult(Result<CoinGiftGiven>.Failure(
                new(ErrorCode.Invalid, "没有 id 的人不能投币：认不出是谁，就没有“一天一枚”。")));

        var day = DayOf((time ?? TimeProvider.System).GetLocalNow());
        return new(GiveAsync(person.Id, person.Name, day, actor, cancellationToken));
    }

    private async Task<Result<CoinGiftGiven>> GiveAsync(string id, string name, string day, string actor,
        CancellationToken cancellationToken)
    {
        var given = await ledger.ChangeAsync($"gift:{id}", actor, state =>
        {
            if (state.GaveOn(id, day))
                return Result<CoinState>.Failure(new(ErrorCode.Conflict,
                    $"{name} 今天已经给过了，明天再来。"));

            // Only today's rows survive the write: an entry from an earlier day cannot answer "did he give today", and
            // keeping it would leave a table that grows forever and a "今天第几位" that counts people who are not today.
            var gifts = (state.Gifts ?? new Dictionary<string, string>(StringComparer.Ordinal))
                .Where(entry => string.Equals(entry.Value, day, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            gifts[id] = day;

            return Result<CoinState>.Success(state with
            {
                Balance = state.Balance + CoinsPerDay,
                Revision = state.Revision + 1,
                Gifts = gifts
            });
        }, cancellationToken).ConfigureAwait(false);

        if (!given.Ok || given.Value is null) return Result<CoinGiftGiven>.Failure(given.Error!);
        var state = given.Value;
        return Result<CoinGiftGiven>.Success(new CoinGiftGiven(state.Balance, state.Revision, state.GiversOn(day)));
    }
}
