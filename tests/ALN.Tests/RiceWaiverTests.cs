using ALN.Contracts;
using ALN.Rice;

namespace ALN.Tests;

/// <summary>
/// Allowances: rounds that the larder does not pay for.
///
/// The point of every test here is the same sentence — the larder's own number does not move — because that is what
/// separates an allowance from "paid, then refunded". A refunding design is correct only while nothing goes wrong
/// between the two halves, which is not a property a test can be written for.
/// </summary>
public sealed class RiceWaiverTests
{
    /// <summary>Midday in the offset a person reading "今日" would mean; all arithmetic below is in this frame.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 11, 10, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Midnight = new(2026, 11, 11, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly Address Group = new("channel", "conversation", "qq-group-1");
    private static readonly Address Other = new("channel", "conversation", "qq-private-2");

    private readonly MemoryStore _store = new();
    private readonly MemoryAtomic _atomic = new();

    private RiceLedger Ledger() => new(_store, _atomic);

    private static RiceWaiver Allowance(string id, string? channel, long remaining, DateTimeOffset until,
        DateTimeOffset? createdAt = null) =>
        new(id, channel, remaining, until, "test", createdAt ?? Noon.AddHours(-1));

    [Fact]
    public async Task A_channel_allowance_pays_that_channel_and_leaves_the_others_alone()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(10, "topup", "test", default)).Ok);
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-1", Group.ToString(), 1, Midnight), "test", default)).Ok);

        var here = await ledger.ChargeRoundAsync("s-group", 1, Group, Noon, default);

        Assert.True(here.Ok, here.Error?.Message);
        Assert.Equal(10, here.Value!.Bites);            // the larder did not move
        Assert.Empty(here.Value.Allowances);            // and the allowance was spent in the same write

        var elsewhere = await ledger.ChargeRoundAsync("s-private", 1, Other, Noon, default);
        Assert.Equal(9, elsewhere.Value!.Bites);        // somebody else's conversation pays as before
    }

    [Fact]
    public async Task An_allowance_with_no_channel_pays_every_channel()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(10, "topup", "test", default)).Ok);
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-all", null, 3, Midnight), "test", default)).Ok);

        var group = await ledger.ChargeRoundAsync("s-group", 1, Group, Noon, default);
        var direct = await ledger.ChargeRoundAsync("s-private", 1, Other, Noon, default);

        Assert.Equal(10, group.Value!.Bites);
        Assert.Equal(10, direct.Value!.Bites);
        Assert.Equal(1, direct.Value.Allowances.Single().Remaining);   // two rounds gone, one left
    }

    [Fact]
    public async Task When_the_allowance_runs_out_the_larder_pays_again()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(10, "topup", "test", default)).Ok);
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-1", null, 1, Midnight), "test", default)).Ok);

        var first = await ledger.ChargeRoundAsync("s-1", 1, Group, Noon, default);
        var second = await ledger.ChargeRoundAsync("s-1", 2, Group, Noon, default);

        Assert.Equal(10, first.Value!.Bites);
        Assert.Empty(first.Value.Allowances);           // used up, and dropped in the same write
        Assert.Equal(9, second.Value!.Bites);
    }

    [Fact]
    public async Task An_expired_allowance_pays_nothing()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(10, "topup", "test", default)).Ok);
        // Granted while it was still today, charged after it stopped: "until midnight" has to mean midnight.
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-old", null, 5, Noon), "test", default)).Ok);

        var charged = await ledger.ChargeRoundAsync("s-1", 1, Group, Noon, default);

        Assert.Equal(9, charged.Value!.Bites);
    }

    [Fact]
    public async Task The_soonest_deadline_is_spent_first()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(10, "topup", "test", default)).Ok);
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-late", null, 1, Midnight), "test", default)).Ok);
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-early", null, 1, Noon.AddHours(1)), "test", default)).Ok);

        var charged = await ledger.ChargeRoundAsync("s-1", 1, Group, Noon, default);

        // Insertion order is late-first, so anything that served the first match would have burned w-late and let the
        // allowance that expires in an hour run out unused.
        Assert.Equal(["w-late"], charged.Value!.Allowances.Select(entry => entry.Id));
    }

    [Fact]
    public async Task A_retried_round_spends_nothing_twice()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(10, "topup", "test", default)).Ok);
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-1", null, 5, Midnight), "test", default)).Ok);

        var first = await ledger.ChargeRoundAsync("s-1", 1, Group, Noon, default);
        var again = await ledger.ChargeRoundAsync("s-1", 1, Group, Noon, default);

        Assert.Equal(4, first.Value!.Allowances.Single().Remaining);
        Assert.Equal(4, again.Value!.Allowances.Single().Remaining);
        Assert.Equal(10, again.Value.Bites);
    }

    [Fact]
    public async Task Revoking_an_allowance_makes_the_larder_pay_again()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(10, "topup", "test", default)).Ok);
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-1", null, 5, Midnight), "test", default)).Ok);

        var revoked = await ledger.RevokeWaiverAsync("w-1", "test", default);
        var charged = await ledger.ChargeRoundAsync("s-1", 1, Group, Noon, default);

        Assert.True(revoked.Ok, revoked.Error?.Message);
        Assert.Empty(charged.Value!.Allowances);
        Assert.Equal(9, charged.Value.Bites);
    }

    [Fact]
    public async Task Revoking_a_name_that_is_not_there_is_refused_rather_than_silently_done()
    {
        var ledger = Ledger();

        var revoked = await ledger.RevokeWaiverAsync("w-ghost", "test", default);

        Assert.False(revoked.Ok);
        Assert.Contains("w-ghost", revoked.Error!.Message);
    }

    [Fact]
    public async Task A_round_that_cannot_say_when_it_happened_pays_the_larder()
    {
        // The old overload, and any request built before the question existed: no instant means no claim on "today".
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(10, "topup", "test", default)).Ok);
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-1", null, 5, Midnight), "test", default)).Ok);

        var charged = await ledger.ChargeRoundAsync("s-1", 1, default);

        Assert.Equal(9, charged.Value!.Bites);
    }

    [Fact]
    public async Task A_write_drops_what_has_already_expired()
    {
        var ledger = Ledger();
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-old", null, 5, Noon), "test", default)).Ok);

        // Granted an hour after the first one expired: the table is not a history, so the dead row goes.
        var later = Allowance("w-new", null, 5, Midnight, createdAt: Noon.AddHours(2));
        Assert.True((await ledger.GrantWaiverAsync(later, "test", default)).Ok);

        var state = (await ledger.ReadAsync(default)).Value!;
        Assert.Equal(["w-new"], state.Allowances.Select(entry => entry.Id));
    }

    [Fact]
    public async Task One_name_cannot_be_promised_twice()
    {
        var ledger = Ledger();
        Assert.True((await ledger.GrantWaiverAsync(Allowance("w-1", null, 5, Midnight), "test", default)).Ok);

        var second = await ledger.GrantWaiverAsync(Allowance("w-1", null, 9, Midnight), "test", default);

        Assert.False(second.Ok);
        Assert.Equal(5, (await ledger.ReadAsync(default)).Value!.Allowances.Single().Remaining);
    }

    [Fact]
    public async Task An_allowance_that_ends_before_it_starts_is_refused()
    {
        var ledger = Ledger();

        var granted = await ledger.GrantWaiverAsync(Allowance("w-1", null, 5, Noon, createdAt: Noon), "test", default);

        Assert.False(granted.Ok);
        Assert.Empty((await ledger.ReadAsync(default)).Value!.Allowances);
    }

    [Fact]
    public async Task An_unlimited_allowance_pays_every_round_until_it_expires()
    {
        var ledger = Ledger();
        Assert.True((await ledger.AddAsync(10, "topup", "test", default)).Ok);
        // Remaining is zero on purpose: an unlimited allowance carries no count, and granting one must not be read as
        // "a promise of zero rounds".
        var unlimited = new RiceWaiver("w-u", Group.ToString(), 0, Midnight, "test", Noon.AddHours(-1), Unlimited: true);
        Assert.True((await ledger.GrantWaiverAsync(unlimited, "test", default)).Ok);

        for (var step = 1; step <= 5; step++)
            Assert.Equal(10, (await ledger.ChargeRoundAsync("s-1", step, Group, Noon, default)).Value!.Bites);

        var live = Assert.Single((await ledger.ReadAsync(default)).Value!.Allowances);
        Assert.True(live.Unlimited);
        Assert.Equal(0, live.Remaining);            // never counted down, never "used up"

        // Midnight is where the promise ends: not because anything ran at midnight, but because the next round after it
        // no longer matches. The dead row goes with that write.
        var after = await ledger.ChargeRoundAsync("s-1", 6, Group, Midnight, default);
        Assert.Equal(9, after.Value!.Bites);
        Assert.Empty(after.Value.Allowances);
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
