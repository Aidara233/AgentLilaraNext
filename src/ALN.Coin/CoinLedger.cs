using System.Text.Json;
using ALN.Contracts;

namespace ALN.Coin;

/// <summary>
/// The only way the balance ever changes.
///
/// Three callers, one door each: a purchase debits, a top-up credits, and a script that was given the depth may do
/// either (it reaches this through the <c>coin.credit</c> / <c>coin.debit</c> actions, whose capability is
/// unregistered in <c>AccessLevels</c> and therefore <c>full</c> — a script may move money exactly when it could
/// already do anything else).
///
/// Every write is one <see cref="IAtomicWork.AtomicallyAsync"/>: read, decide, write, commit. There is no window in
/// which the money is gone and the goods are not.
/// </summary>
public sealed class CoinLedger(IStateStore store, IAtomicWork atomic, ISignalLog? signals = null, IAuditSink? audit = null)
{
    public const string Scope = "coin";
    public const string Key = "state";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Address Purse = new("coin", "wallet", "state");

    public async ValueTask<Result<CoinState>> ReadAsync(CancellationToken cancellationToken)
    {
        var read = await store.ReadAsync(Scope, Key, cancellationToken).ConfigureAwait(false);
        // Not "!read.Ok": Result&lt;T&gt;.Ok is also false when the value is null, and "this key was never written" is the
        // normal first state of a purse. The error is the only thing that means "the store itself failed".
        if (read.Error is not null) return Result<CoinState>.Failure(read.Error);
        if (string.IsNullOrWhiteSpace(read.Value)) return Result<CoinState>.Success(CoinState.Empty());
        try
        {
            var parsed = JsonSerializer.Deserialize<CoinState>(read.Value, Json);
            return parsed is null
                ? Result<CoinState>.Failure(new(ErrorCode.Invalid, "The purse is empty of meaning: it parsed to null."))
                : Result<CoinState>.Success(parsed);
        }
        catch (JsonException ex)
        {
            // A purse nobody can read is not silently reset to zero: that would erase money without a record.
            return Result<CoinState>.Failure(new(ErrorCode.Invalid, "The purse is not readable JSON.", ex.Message));
        }
    }

    /// <summary>Takes money out, or explains in one sentence how much is missing.</summary>
    public ValueTask<Result<CoinState>> DebitAsync(long amount, string reason, string actor,
        CancellationToken cancellationToken) =>
        ChangeAsync(reason, actor, state =>
        {
            if (amount <= 0) return Result<CoinState>.Failure(new(ErrorCode.Invalid, $"要扣的钱必须大于 0（收到 {amount}）。"));
            if (state.Balance < amount)
                return Result<CoinState>.Failure(new(ErrorCode.Denied,
                    $"还差 {amount - state.Balance}，当前 {state.Balance}，这次要 {amount}。"));
            return Result<CoinState>.Success(state with
            {
                Balance = state.Balance - amount,
                Revision = state.Revision + 1
            });
        }, cancellationToken);

    /// <summary>Puts money in. Only a top-up, and a script that was given the depth, get here.</summary>
    public ValueTask<Result<CoinState>> CreditAsync(long amount, string reason, string actor,
        CancellationToken cancellationToken) =>
        ChangeAsync(reason, actor, state =>
        {
            if (amount <= 0) return Result<CoinState>.Failure(new(ErrorCode.Invalid, $"要存的钱必须大于 0（收到 {amount}）。"));
            return Result<CoinState>.Success(state with
            {
                Balance = state.Balance + amount,
                Revision = state.Revision + 1
            });
        }, cancellationToken);

    /// <summary>
    /// Anything else that changes the purse (goods into the bag, stock down, a purchase recorded). The caller's change
    /// function runs <b>inside</b> the transaction, so it may read the balance it is about to modify.
    /// </summary>
    public ValueTask<Result<CoinState>> ChangeAsync(string reason, string actor,
        Func<CoinState, Result<CoinState>> change, CancellationToken cancellationToken) =>
        new(TracedAsync(reason, actor, change, cancellationToken));

    private async Task<Result<CoinState>> TracedAsync(string reason, string actor,
        Func<CoinState, Result<CoinState>> change, CancellationToken cancellationToken)
    {
        Result<CoinState> written = Result<CoinState>.Failure(new(ErrorCode.Internal, "The transaction did not run."));
        var outcome = await atomic.AtomicallyAsync(async token =>
        {
            var read = await ReadAsync(token).ConfigureAwait(false);
            if (!read.Ok || read.Value is null) return Result.Failure(read.Error ?? new(ErrorCode.Internal, "读不到钱包。"));

            var changed = change(read.Value);
            if (!changed.Ok || changed.Value is null) return Result.Failure(changed.Error!);

            var json = JsonSerializer.Serialize(changed.Value, Json);
            var saved = await store.WriteAsync(Scope, Key, json, token).ConfigureAwait(false);
            if (!saved.Ok) return saved;

            written = changed;
            return Result.Success();
        }, cancellationToken).ConfigureAwait(false);

        if (!outcome.Ok) return Result<CoinState>.Failure(outcome.Error!);

        // The number is the state; what happened is the log. Money moved, so both lanes say so.
        var state = written.Value!;
        signals?.Write(new SignalRecord
        {
            Group = "coin",
            Name = reason,
            From = actor,
            To = Purse.ToString(),
            Mechanism = "coin",
            Outcome = "ok",
            Level = SignalLevel.Info,
            Detail = new Dictionary<string, string>
            {
                ["balance"] = state.Balance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["revision"] = state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        });
        if (audit is not null)
            await audit.AppendAsync(new AuditRecord(DateTimeOffset.UtcNow, Guid.Empty, actor, Purse, reason, "ok",
                $"balance={state.Balance} revision={state.Revision}"), cancellationToken).ConfigureAwait(false);

        return Result<CoinState>.Success(state);
    }
}
