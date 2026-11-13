using ALN.Commands;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Coin;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The purse, tested where it decides things: money in and out, what the script said became what, and the two ways a
/// purchase can fail without taking anything. The SQLite side of <see cref="IAtomicWork"/> is covered by the storage
/// tests; here it is a lock and a dictionary, so a failure is about the purse and not about a database.
/// </summary>
public sealed class CoinTests
{
    private readonly MemoryStateStore _store = new();
    private readonly MemoryAtomic _atomic = new();

    private static CoinCatalogue Catalogue => new(
        [
            new CoinGood("box", "箱子", 30, "shop.box"),
            new CoinGood("brick", "砖", 1, "shop.brick"),
            new CoinGood("rare", "稀罕物", 100, "shop.brick", Stock: 1)
        ],
        [
            new CoinItem("candy", "糖", "use.candy"),
            new CoinItem("book", "书", "use.book"),
            new CoinItem("match", "火柴", "use.match")
        ],
        []);

    private CoinLedger Ledger() => new(_store, _atomic);

    private CoinMarket Market(ICoinScripts scripts, CoinCatalogue? catalogue = null) =>
        new(Ledger(), catalogue ?? Catalogue, scripts);

    private sealed class Scripts(IReadOnlyDictionary<string, string> answers) : ICoinScripts
    {
        public IReadOnlyList<string> Loaded => answers.Keys.Order(StringComparer.Ordinal).ToArray();

        public bool CanRun(string script) => answers.ContainsKey(script);

        public ValueTask<Result<string>> InvokeAsync(string script, string argumentsJson,
            CancellationToken cancellationToken) =>
            answers.TryGetValue(script, out var answer)
                ? ValueTask.FromResult(Result<string>.Success(answer))
                : ValueTask.FromResult(Result<string>.Failure(new(ErrorCode.Invalid, $"no script {script}")));
    }

    private static Scripts Giving(string script, string json) => new(new Dictionary<string, string> { [script] = json });

    [Fact]
    public async Task Buying_takes_the_money_and_puts_what_the_script_said_into_the_bag()
    {
        var market = Market(Giving("shop.box", """{"bag":[{"item":"candy","count":2}],"text":"滚出两块糖。"}"""));
        await Ledger().CreditAsync(100, "topup", "test", default);

        var bought = await market.BuyAsync("s-1", "box", 1, "model:s-1", default);

        Assert.True(bought.Ok, bought.Error?.Message);
        Assert.Equal(70, bought.Value!.Balance);
        Assert.Equal("滚出两块糖。", bought.Value.Text);

        var state = (await Ledger().ReadAsync(default)).Value!;
        Assert.Equal(70, state.Balance);
        Assert.Equal(2, state.CountOf("candy"));
        Assert.Equal(1, state.SoldOf("box"));
    }

    [Fact]
    public async Task Not_enough_money_takes_nothing_and_says_how_much_is_missing()
    {
        var market = Market(Giving("shop.box", "{}"));
        await Ledger().CreditAsync(10, "topup", "test", default);

        var bought = await market.BuyAsync("s-1", "box", 1, "model:s-1", default);

        Assert.False(bought.Ok);
        Assert.Equal(ErrorCode.Denied, bought.Error!.Code);
        Assert.Contains("20", bought.Error.Message);   // 还差 20

        var state = (await Ledger().ReadAsync(default)).Value!;
        Assert.Equal(10, state.Balance);               // untouched
        Assert.Empty(state.Bag);
    }

    [Fact]
    public async Task A_script_that_fails_hands_the_money_back()
    {
        // The script says nothing usable: the purchase must not keep the money, or "paid and got nothing because the
        // shop was broken" reads as theft.
        var market = Market(Giving("shop.box", "not json at all"));
        await Ledger().CreditAsync(100, "topup", "test", default);

        var bought = await market.BuyAsync("s-1", "box", 1, "model:s-1", default);

        Assert.False(bought.Ok);
        Assert.Equal(100, (await Ledger().ReadAsync(default)).Value!.Balance);
        Assert.Contains("退", bought.Error!.Message);
    }

    [Fact]
    public async Task Use_consumes_exactly_what_the_script_says()
    {
        await Ledger().CreditAsync(100, "topup", "test", default);

        // Zero: you read a book; it is still in the bag.
        _store.Seed("""{"balance":100,"revision":1,"bag":[{"item":"book","count":1}],"sold":{},"purchased":{}}""");
        var book = await Market(Giving("use.book", """{"text":"读完了。","used":0}""")).UseAsync("s-1", "book", 1, "a", default);
        Assert.True(book.Ok, book.Error?.Message);
        Assert.Equal(0, book.Value!.Used);
        Assert.Equal(1, (await Ledger().ReadAsync(default)).Value!.CountOf("book"));

        // One.
        _store.Seed("""{"balance":100,"revision":1,"bag":[{"item":"candy","count":3}],"sold":{},"purchased":{}}""");
        var candy = await Market(Giving("use.candy", """{"text":"甜。","used":1}""")).UseAsync("s-1", "candy", 1, "a", default);
        Assert.True(candy.Ok, candy.Error?.Message);
        Assert.Equal(2, (await Ledger().ReadAsync(default)).Value!.CountOf("candy"));

        // All of them, whatever the count is.
        _store.Seed("""{"balance":100,"revision":1,"bag":[{"item":"match","count":4}],"sold":{},"purchased":{}}""");
        var matches = await Market(Giving("use.match", """{"text":"全烧了。","used":-1}""")).UseAsync("s-1", "match", 1, "a", default);
        Assert.True(matches.Ok, matches.Error?.Message);
        Assert.Equal(4, matches.Value!.Used);
        Assert.Equal(0, (await Ledger().ReadAsync(default)).Value!.CountOf("match"));
    }

    [Fact]
    public async Task A_good_the_shop_does_not_have_is_refused_before_any_money_moves()
    {
        var market = Market(Giving("shop.box", "{}"));
        await Ledger().CreditAsync(100, "topup", "test", default);

        var bought = await market.BuyAsync("s-1", "nothing-like-it", 1, "model:s-1", default);

        Assert.False(bought.Ok);
        Assert.Equal(100, (await Ledger().ReadAsync(default)).Value!.Balance);
    }

    [Fact]
    public async Task Stock_is_spent_once()
    {
        var market = Market(Giving("shop.brick", """{"bag":[{"item":"candy","count":1}]}"""));
        await Ledger().CreditAsync(1000, "topup", "test", default);

        var first = await market.BuyAsync("s-1", "rare", 1, "model:s-1", default);
        var second = await market.BuyAsync("s-2", "rare", 1, "model:s-2", default);

        Assert.True(first.Ok, first.Error?.Message);
        Assert.False(second.Ok);
        Assert.Equal(900, (await Ledger().ReadAsync(default)).Value!.Balance);   // the second took nothing
    }

    [Fact]
    public async Task The_purse_is_one_number_for_the_whole_framework()
    {
        var market = Market(Giving("shop.brick", """{"bag":[{"item":"candy","count":1}]}"""));
        await Ledger().CreditAsync(10, "topup", "a", default);

        // Two Sessions, one purse: what the first spends, the second cannot.
        var one = await market.BuyAsync("s-1", "brick", 5, "model:s-1", default);
        var two = await market.BuyAsync("s-2", "brick", 6, "model:s-2", default);

        Assert.True(one.Ok, one.Error?.Message);
        Assert.False(two.Ok);
        Assert.Contains("还差 1", two.Error!.Message);
    }

    [Fact]
    public async Task A_retried_call_does_not_buy_twice()
    {
        var market = Market(Giving("shop.box", """{"bag":[{"item":"candy","count":2}]}"""));
        await Ledger().CreditAsync(100, "topup", "test", default);

        // The same tool call id, twice: a timeout and a retry must not cost two boxes.
        var first = await market.BuyAsync("s-1", "box", 1, "model:s-1", default, "call-1");
        var second = await market.BuyAsync("s-1", "box", 1, "model:s-1", default, "call-1");
        var different = await market.BuyAsync("s-1", "box", 1, "model:s-1", default, "call-2");

        Assert.True(first.Ok, first.Error?.Message);
        Assert.False(second.Ok);
        Assert.Equal(ErrorCode.Conflict, second.Error!.Code);
        Assert.True(different.Ok, different.Error?.Message);

        var state = (await Ledger().ReadAsync(default)).Value!;
        Assert.Equal(40, state.Balance);        // 100 - 30 - 30, never three times
        Assert.Equal(2, state.SoldOf("box"));
    }

    [Fact]
    public async Task A_once_per_session_good_is_refused_the_second_time_for_the_same_session()
    {
        var catalogue = new CoinCatalogue(
            [new CoinGood("hint", "提示", 10, "shop.hint", OncePerSession: true)], [], []);
        var market = Market(Giving("shop.hint", """{"bag":[],"text":"一条提示"}"""), catalogue);
        await Ledger().CreditAsync(100, "topup", "test", default);

        var first = await market.BuyAsync("s-1", "hint", 1, "model:s-1", default, "c-1");
        var second = await market.BuyAsync("s-1", "hint", 1, "model:s-1", default, "c-2");
        var other = await market.BuyAsync("s-2", "hint", 1, "model:s-2", default, "c-3");

        Assert.True(first.Ok, first.Error?.Message);
        Assert.False(second.Ok);
        Assert.Contains("只能买一次", second.Error!.Message);
        Assert.True(other.Ok, other.Error?.Message);        // the limit is per Session, not per purse
        Assert.Equal(80, (await Ledger().ReadAsync(default)).Value!.Balance);
    }

    [Fact]
    public void Moving_money_by_script_needs_full_and_says_so_in_the_table()
    {
        var plane = new RecordingPlane();

        Assert.True(CoinActions.Register(plane, Ledger()).Ok);

        // The depth of a verb is what stops a model from crediting itself: "coin.control" is not in AccessLevels, so
        // it answers Full — which is a depth only a hand-written card or a trusted script can have.
        var credit = Assert.Single(plane.Registered, action => action.Id == CoinActions.Credit);
        Assert.Equal(AccessLevel.Full, credit.RequiredLevel);
        var debit = Assert.Single(plane.Registered, action => action.Id == CoinActions.Debit);
        Assert.Equal(AccessLevel.Full, debit.RequiredLevel);
    }

    // ---- a coin from a person (//coin toss) -----------------------------------------------------

    private static readonly Address Chat = new("adapter", "qq", "main");
    private static readonly Address Channel = new("channel", "conversation", "qq-group-1");
    private const string Conversation = "qq-group-1";

    private static Person Xiaolin => new("xiaolin", "小林");

    private FixedTime Clock(DateTimeOffset now) => new(now);

    private (CommandRegistry Registry, RecordingPlane Plane) Installed(TimeProvider time, CoinNotice? notice = null)
    {
        var registry = new CommandRegistry(new CommandOptions());
        var plane = new RecordingPlane();
        var installed = CoinService.Install(new SessionExtensionRegistry(), registry, plane, Ledger(),
            new CoinMarket(Ledger(), Catalogue, Giving("shop.box", "{}")), time, notice);
        Assert.True(installed.Ok, installed.Error?.Message);
        return (registry, plane);
    }

    private static CommandInvocation From(string text, Person? person) =>
        new(text, Chat, Conversation, "m-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), person, "1234567", "群名片");

    private async Task<long> BalanceAsync() => (await Ledger().ReadAsync(default)).Value!.Balance;

    [Fact]
    public async Task A_person_gives_one_coin_a_day_and_the_table_keeps_only_today()
    {
        var clock = Clock(new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero));
        var gifts = new CoinGifts(Ledger(), clock);

        var first = await gifts.GiveAsync(Xiaolin, "command:xiaolin", default);
        var again = await gifts.GiveAsync(Xiaolin, "command:xiaolin", default);

        Assert.True(first.Ok, first.Error?.Message);
        Assert.Equal(1, first.Value!.Balance);
        Assert.Equal(1, first.Value.GiversToday);
        Assert.False(again.Ok);
        Assert.Equal(ErrorCode.Conflict, again.Error!.Code);
        Assert.Contains("今天已经给过了", again.Error.Message);

        // A second person is their own day: the limit is per person, not per purse.
        var other = await gifts.GiveAsync(new Person("ada", "Ada"), "command:ada", default);
        Assert.True(other.Ok, other.Error?.Message);
        Assert.Equal(2, other.Value!.Balance);
        Assert.Equal(2, other.Value.GiversToday);

        // Tomorrow: yesterday's row no longer blocks him and no longer counts as "today".
        clock.Now = new DateTimeOffset(2026, 6, 6, 9, 0, 0, TimeSpan.Zero);
        var tomorrow = await gifts.GiveAsync(Xiaolin, "command:xiaolin", default);
        Assert.True(tomorrow.Ok, tomorrow.Error?.Message);
        Assert.Equal(3, tomorrow.Value!.Balance);
        Assert.Equal(1, tomorrow.Value.GiversToday);

        var state = (await Ledger().ReadAsync(default)).Value!;
        Assert.Single(state.Gifts!);                    // only today's people survive the write
        Assert.True(state.GaveOn("xiaolin", "2026-06-06"));
    }

    [Fact]
    public async Task A_stranger_cannot_give_and_a_known_person_can()
    {
        var (registry, _) = Installed(Clock(new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero)));

        // Nobody on the identity table: the refusal is the whole point of "only people we know".
        var stranger = await registry.InvokeAsync(From("//coin toss", null), default);
        Assert.False(stranger.Ok);
        Assert.Equal(ErrorCode.Denied, stranger.Error!.Code);
        Assert.Equal(0, await BalanceAsync());

        var known = await registry.InvokeAsync(From("//coin toss", Xiaolin), default);
        Assert.True(known.Ok, known.Error?.Message);
        Assert.Contains("收下了 1 枚", known.Value!.Reply);
        Assert.Equal(1, await BalanceAsync());

        // Same person, same day, second instruction: no second coin.
        var twice = await registry.InvokeAsync(From("//coin toss", Xiaolin), default);
        Assert.False(twice.Ok);
        Assert.Equal(ErrorCode.Conflict, twice.Error!.Code);
        Assert.Equal(1, await BalanceAsync());
    }

    [Fact]
    public async Task A_gift_is_announced_to_the_session_behind_the_channel()
    {
        var world = new FakeConversations(Channel, session: "s-1");
        var (registry, _) = Installed(Clock(new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero)),
            new CoinNotice(world));

        var given = await registry.InvokeAsync(From("//coin toss", Xiaolin), default);

        Assert.True(given.Ok, given.Error?.Message);
        var note = Assert.Single(world.Delivered);
        Assert.Equal(Channel, world.Asked);                        // the Channel that carried the instruction
        Assert.Equal("s-1", note.Session);
        Assert.Contains("小林", note.Text);
        Assert.Contains("1 枚", note.Text);
    }

    [Fact]
    public async Task A_conversation_with_nobody_behind_it_still_gets_the_coin()
    {
        // Nothing bound, and then a Channel with no Session yet: both are "there is nobody to tell", never "the gift
        // failed". The coin is in the purse either way, which is the only part the person was promised.
        var unbound = new FakeConversations(channel: null, session: null);
        var (first, _) = Installed(Clock(new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero)),
            new CoinNotice(unbound));
        var withoutChannel = await first.InvokeAsync(From("//coin toss", Xiaolin), default);

        var empty = new FakeConversations(Channel, session: null);
        var (second, _) = Installed(Clock(new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero)),
            new CoinNotice(empty));
        var withoutSession = await second.InvokeAsync(From("//coin toss", new Person("ada", "Ada")), default);

        Assert.True(withoutChannel.Ok, withoutChannel.Error?.Message);
        Assert.True(withoutSession.Ok, withoutSession.Error?.Message);
        Assert.Empty(unbound.Delivered);
        Assert.Empty(empty.Delivered);
        Assert.Equal(2, await BalanceAsync());
    }

    /// <summary>A clock that can be moved: "one a day" is a rule about days, and a test that cannot change the day cannot test it.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }


    /// <summary>
    /// The three answers a notice asks for, and nothing else: which Channel a conversation is bound to, which Session
    /// serves it, and where the note went. That the seam is three methods is the point — the Channel and Session managers
    /// answer thirty questions between them, and a coin needs three of them.
    /// </summary>
    private sealed class FakeConversations(Address? channel, string? session) : ICoinConversations
    {
        public List<(string Session, string Text)> Delivered { get; } = [];

        /// <summary>The Channel this world claims serves the conversation, so a test can check it was asked at all.</summary>
        public Address? Asked { get; private set; }

        public ValueTask<Result<Address>> ChannelOfAsync(Address adapter, string conversation,
            CancellationToken cancellationToken)
        {
            Asked = channel;
            return ValueTask.FromResult(channel is { } address
                ? Result<Address>.Success(address)
                : Result<Address>.Failure(new(ErrorCode.Invalid, "nothing is bound here")));
        }

        public ValueTask<Result<string>> SessionOfAsync(Address channel, CancellationToken cancellationToken) =>
            ValueTask.FromResult(session is { } id
                ? Result<string>.Success(id)
                : Result<string>.Failure(new(ErrorCode.Unavailable, "no Session here yet")));

        public ValueTask<Result> PostAsync(string session, string text, CancellationToken cancellationToken)
        {
            Delivered.Add((session, text));
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed class RecordingPlane : IControlPlane
    {
        public List<ActionDescriptor> Registered { get; } = [];

        public IReadOnlyList<ActionDescriptor> Catalog => Registered;

        public Result Register(ActionDescriptor descriptor, ActionHandler handler)
        {
            Registered.Add(descriptor);
            return Result.Success();
        }

        public Result Register(ActionDescriptor descriptor, AuthorizedActionHandler handler)
        {
            Registered.Add(descriptor);
            return Result.Success();
        }

        public ValueTask<Result<System.Text.Json.JsonElement>> InvokeAsync(Actor actor, string action,
            System.Text.Json.JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryStateStore : IStateStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public void Seed(string json)
        {
            lock (_gate) _values[$"{CoinLedger.Scope}/{CoinLedger.Key}"] = json;
        }

        public ValueTask<Result<string?>> ReadAsync(string scope, string key, CancellationToken cancellationToken)
        {
            lock (_gate)
                return ValueTask.FromResult(Result<string?>.Success(
                    _values.TryGetValue($"{scope}/{key}", out var value) ? value : null));
        }

        public ValueTask<Result> WriteAsync(string scope, string key, string json, CancellationToken cancellationToken)
        {
            lock (_gate) _values[$"{scope}/{key}"] = json;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> DeleteAsync(string scope, string key, CancellationToken cancellationToken)
        {
            lock (_gate) _values.Remove($"{scope}/{key}");
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
