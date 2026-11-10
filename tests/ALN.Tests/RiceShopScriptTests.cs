using ALN.Contracts;
using ALN.Coin;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Scripting.Jint;

namespace ALN.Tests;

/// <summary>
/// The four rice packages, with the <b>real</b> sample script: the purse pays, Jint runs
/// <c>samples/market/scripts/shop.rice/main.js</c>, and the bites it asks the larder to add come back out as the
/// settlement.
///
/// The point is not the arithmetic — it is that one script serves four goods and the shelf decides which. A good whose
/// id the script does not know must fail loudly instead of quietly stocking one bowl, and <c>count</c> must multiply:
/// selling three pots but adding one is the shape of bug that looks like a rounding error and is theft.
/// </summary>
public sealed class RiceShopScriptTests
{
    private static CoinCatalogue Shelf => new(
        [
            new CoinGood("rice-bowl", "一碗白饭", 2, "shop.rice"),
            new CoinGood("rice-basin", "一盆白饭", 8, "shop.rice"),
            new CoinGood("rice-pot", "一锅白饭", 30, "shop.rice"),
            new CoinGood("rice-bucket", "一桶白饭", 70, "shop.rice"),
            new CoinGood("rice-unlimited", "免费续饭", 5, "shop.rice.unlimited"),
            new CoinGood("rice-instant", "自热米饭", 6, "shop.rice.instant"),
            // Deliberately not in the script's menu: the shelf is one file a person edits, the menu is another.
            new CoinGood("rice-forklift", "白饭叉车", 5, "shop.rice")
        ],
        [],
        []);

    [Theory]
    [InlineData("rice-bowl", 1, 100)]
    [InlineData("rice-basin", 1, 500)]
    [InlineData("rice-pot", 1, 2000)]
    [InlineData("rice-bucket", 1, 5000)]
    [InlineData("rice-pot", 2, 4000)]   // count multiplies the package, not just the price
    [InlineData("rice-basin", 3, 1500)]
    public async Task Each_good_stocks_its_own_number_of_bites(string good, int count, long bites)
    {
        var api = new RecordingApi();
        var (market, ledger) = Market(api);
        await ledger.CreditAsync(1000, "topup", "test", default);

        var bought = await market.BuyAsync("s-1", good, count, "model:s-1", default, "call-1");

        Assert.True(bought.Ok, bought.Error?.Message);
        var call = Assert.Single(api.Calls);
        Assert.Equal("rice.add", call.Action);
        Assert.Equal(bites, Bites(call.ArgumentsJson));
        // The script's own words, not the host's: the model reads what the script said it did.
        Assert.Contains($"{bites} 口", bought.Value!.Text);
    }

    [Fact]
    public async Task A_good_the_script_does_not_know_fails_and_the_money_comes_back()
    {
        // The shelf and the script are two files a person edits separately, so "someone added a good and forgot the
        // menu" has to be a failed purchase with a refund — not a bowl of rice for the price of a bucket.
        var api = new RecordingApi();
        var (market, ledger) = Market(api);
        await ledger.CreditAsync(100, "topup", "test", default);

        var bought = await market.BuyAsync("s-1", "rice-forklift", 1, "model:s-1", default, "call-1");
        Assert.False(bought.Ok);
        Assert.Empty(api.Calls);                                          // the larder was never touched
        Assert.Equal(100, (await ledger.ReadAsync(default)).Value!.Balance);
    }

    [Fact]
    public async Task The_free_refill_sells_one_Channels_day_from_the_channel_the_settlement_carried()
    {
        var api = new RecordingApi();
        var (market, ledger) = Market(api, "shop.rice.unlimited");
        await ledger.CreditAsync(100, "topup", "test", default);

        var bought = await market.BuyAsync("s-1", "rice-unlimited", 1, "model:s-1", default, "call-1",
            channel: "channel.conversation#qq-group-616565017");

        Assert.True(bought.Ok, bought.Error?.Message);
        var call = Assert.Single(api.Calls);
        Assert.Equal("rice.waive", call.Action);

        using var grant = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
        Assert.True(grant.RootElement.GetProperty("unlimited").GetBoolean());
        Assert.Equal("channel.conversation#qq-group-616565017", grant.RootElement.GetProperty("channel").GetString());
        // No `until`: the server's default is the end of today, and a script that spelled that out would have to know
        // which timezone it is running in — which is exactly the thing it cannot know.
        Assert.False(grant.RootElement.TryGetProperty("until", out _));
    }

    [Fact]
    public async Task The_free_refill_will_not_guess_when_nobody_named_a_channel()
    {
        var api = new RecordingApi();
        var (market, ledger) = Market(api, "shop.rice.unlimited");
        await ledger.CreditAsync(100, "topup", "test", default);

        // A Session with no Channel — started by a script, or by hand — has nothing to point the promise at. Guessing
        // would be selling somebody else's conversation, so the purchase fails and the money goes back.
        var bought = await market.BuyAsync("s-1", "rice-unlimited", 1, "model:s-1", default, "call-1");

        Assert.False(bought.Ok);
        Assert.Empty(api.Calls);
        Assert.Equal(100, (await ledger.ReadAsync(default)).Value!.Balance);
    }

    [Theory]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    public async Task The_instant_rice_buys_its_rounds_and_multiplies_by_count(int count, long bites)
    {
        var api = new RecordingApi();
        var (market, ledger) = Market(api, "shop.rice.instant");
        await ledger.CreditAsync(100, "topup", "test", default);

        var bought = await market.BuyAsync("s-1", "rice-instant", count, "model:s-1", default, "call-1");

        Assert.True(bought.Ok, bought.Error?.Message);
        var call = Assert.Single(api.Calls);
        Assert.Equal("rice.waive", call.Action);

        using var grant = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal(bites, grant.RootElement.GetProperty("bites").GetInt64());
        // Not "unlimited", and not spelled out with an `until`: it is a number of rounds, and the day it expires on is
        // the host's business.
        Assert.False(grant.RootElement.TryGetProperty("unlimited", out _));
        Assert.False(grant.RootElement.TryGetProperty("until", out _));
    }

    /// <summary>Builds the purse on a real script host reading the sample folder the way a person would copy it.</summary>
    private static (CoinMarket Market, CoinLedger Ledger) Market(IScriptApi api, string script = "shop.rice")
    {
        var ledger = new CoinLedger(new MemoryStore(), new MemoryAtomic());
        return (new CoinMarket(ledger, Shelf, new SampleScript(api, script)), ledger);
    }

    private static long Bites(string argumentsJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
        return document.RootElement.GetProperty("bites").GetInt64();
    }

    private static string SampleScriptPath(string id = "shop.rice")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ALN.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var script = Path.Combine(directory!.FullName, "samples", "market", "scripts", id, "main.js");
        Assert.True(File.Exists(script), $"no sample script at {script}");
        return script;
    }

    /// <summary>The sample script, unchanged, behind the narrow port the purse uses.</summary>
    private sealed class SampleScript(IScriptApi api, string id = "shop.rice") : ICoinScripts
    {
        private static readonly Address Target = new("channel", "test", "one");

        public IReadOnlyList<string> Loaded => [id];

        public bool CanRun(string script) => string.Equals(script, id, StringComparison.Ordinal);

        public ValueTask<Result<string>> InvokeAsync(string script, string argumentsJson,
            CancellationToken cancellationToken)
        {
            var package = new ScriptPackage(
                new ScriptManifest(id, 1, "channel", MountMode.Manual, [], "main", ["rice.control"],
                    TrustDomain.Trusted, Call: "buy", Tool: new ScriptToolSpec(id, "白饭", "{}")),
                File.ReadAllText(SampleScriptPath(id)));
            return new JintScriptExecutor().InvokeAsync(package, Target, argumentsJson, api, cancellationToken);
        }
    }

    /// <summary>Remembers what the script asked for; nothing else about the world interests a settlement.</summary>
    private sealed class RecordingApi : IScriptApi
    {
        public List<(string Action, string ArgumentsJson)> Calls { get; } = [];

        public Task<string> CallActionAsync(string action, string argumentsJson, CancellationToken cancellationToken)
        {
            Calls.Add((action, argumentsJson));
            return Task.FromResult("{\"ok\":true,\"bites\":0}");
        }

        public Task<string> RunSessionAsync(string s, string i, CancellationToken c) => Task.FromResult("{}");
        public Task<string> ActivateSessionAsync(string s, CancellationToken c) => Task.FromResult("{}");
        public Task<string> OpenSessionAsync(string o, CancellationToken c) => Task.FromResult("{}");
        public Task<string> MessageSessionAsync(string o, CancellationToken c) => Task.FromResult("{}");
        public Task<string> CompleteModelAsync(string p, string r, CancellationToken c) => Task.FromResult("{}");
        public Task<string> SendSelfAsync(string m, CancellationToken c) => Task.FromResult("{}");
        public Task<string> AcceptIntoChannelAsync(CancellationToken c) => Task.FromResult("{}");
        public Task<string> RouteIntoChannelAsync(string o, CancellationToken c) => Task.FromResult("{}");
        public Task<string> ReplyThroughChannelAsync(string m, CancellationToken c) => Task.FromResult("{}");
        public Task<string> PollSelfAsync(CancellationToken c) => Task.FromResult("{}");
        public Task<string> ReadStateAsync(string k, CancellationToken c) => Task.FromResult("");
        public Task SetStateAsync(string k, string j, CancellationToken c) => Task.CompletedTask;
        public Task DeleteStateAsync(string k, CancellationToken c) => Task.CompletedTask;
        public void Log(string level, string message, string? detailJson) { }
        public Task<string> ListActionsAsync(CancellationToken c) => Task.FromResult("{}");
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
