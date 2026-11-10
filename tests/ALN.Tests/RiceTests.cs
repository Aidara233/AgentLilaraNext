using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Rice;

namespace ALN.Tests;

/// <summary>
/// The larder and the limit it enforces. One bowl per round, and a round is identified by <c>(session, step)</c> — which
/// is what makes a retried round cost nothing.
/// </summary>
public sealed class RiceTests
{
    private readonly MemoryStore _store = new();
    private readonly MemoryAtomic _atomic = new();

    private RiceLedger Ledger() => new(_store, _atomic);

    [Fact]
    public async Task A_round_eats_one_bowl_and_the_same_round_never_eats_twice()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(3, "topup", "test", default)).Ok);

        Assert.True((await ledger.ChargeRoundAsync("s-1", 1, default)).Ok);
        Assert.Equal(2, (await ledger.ReadAsync(default)).Value!.Bites);

        // The framework retrying step 1 must not cost a second bowl: the cursor, not a window of keys, is the guard.
        var again = await ledger.ChargeRoundAsync("s-1", 1, default);
        Assert.True(again.Ok);
        Assert.Equal(2, again.Value!.Bites);

        Assert.True((await ledger.ChargeRoundAsync("s-1", 2, default)).Ok);
        Assert.Equal(1, (await ledger.ReadAsync(default)).Value!.Bites);
    }

    [Fact]
    public async Task The_larder_goes_negative_rather_than_stopping_a_round()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(1, "topup", "test", default)).Ok);

        var one = await ledger.ChargeRoundAsync("s-1", 1, default);
        var two = await ledger.ChargeRoundAsync("s-2", 1, default);

        Assert.True(one.Ok, one.Error?.Message);
        Assert.True(two.Ok, two.Error?.Message);
        Assert.Equal(-1, (await ledger.ReadAsync(default)).Value!.Bites);
    }

    [Fact]
    public async Task An_empty_larder_never_refuses_a_round()
    {
        var admission = new RiceAdmission(Ledger());

        var verdict = await admission.AdmitAsync(new RoundAdmissionRequest("s-1", 1, AccessLevel.Agent), default);

        Assert.True(verdict.Ok);
        Assert.True(verdict.Value!.Admitted);
    }

    [Fact]
    public async Task A_broken_larder_does_not_starve_the_Session()
    {
        // The store failing is our bug, not the model's problem: an admission that cannot answer admits.
        var admission = new RiceAdmission(new RiceLedger(new BrokenStore(), new MemoryAtomic()));

        var verdict = await admission.AdmitAsync(new RoundAdmissionRequest("s-1", 1, AccessLevel.Agent), default);

        Assert.True(verdict.Ok);
        Assert.True(verdict.Value!.Admitted);
    }

    [Fact]
    public async Task Taking_more_than_there_is_is_refused_rather_than_going_negative()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(2, "topup", "test", default)).Ok);

        var taken = await ledger.TakeAsync(5, "spill", "test", default);

        Assert.False(taken.Ok);
        Assert.Equal(2, (await ledger.ReadAsync(default)).Value!.Bites);
    }

    private sealed class MemoryStore : IStateStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public ValueTask<Result<string?>> ReadAsync(string scope, string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<string?>.Success(
                _values.TryGetValue($"{scope}/{key}", out var value) ? value : null));

        public ValueTask<Result> WriteAsync(string scope, string key, string json, CancellationToken cancellationToken)
        {
            _values[$"{scope}/{key}"] = json;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> DeleteAsync(string scope, string key, CancellationToken cancellationToken)
        {
            _values.Remove($"{scope}/{key}");
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed class BrokenStore : IStateStore
    {
        public ValueTask<Result<string?>> ReadAsync(string scope, string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<string?>.Failure(new(ErrorCode.Unavailable, "the store is down")));

        public ValueTask<Result> WriteAsync(string scope, string key, string json, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "the store is down")));

        public ValueTask<Result> DeleteAsync(string scope, string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "the store is down")));
    }

    private sealed class MemoryAtomic : IAtomicWork
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async ValueTask<Result> AtomicallyAsync(Func<CancellationToken, ValueTask<Result>> body,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try { return await body(cancellationToken); }
            finally { _gate.Release(); }
        }
    }
}
