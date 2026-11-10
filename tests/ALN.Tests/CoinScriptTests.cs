using ALN.Contracts;
using ALN.Coin;
using ALN.Graph.Contracts;
using ALN.Scripting.Jint;

namespace ALN.Tests;

/// <summary>
/// One purchase, end to end, with a <b>real</b> script engine: the purse talks to Jint, the script decides what a box
/// contains, and the answer comes back as the settlement the purse applies.
///
/// This is the test the first cut could not have: <see cref="CoinTests"/> stubs the script host, so it proves the
/// purse is consistent but not that the contract between the purse and a script is real. Here the script is JS.
/// </summary>
public sealed class CoinScriptTests
{
    private const string Box = """
        function main() { return null; }
        async function box(args) {
          if (Math.random() < 0.5) return { text: "空的。", bag: [] };
          return { text: "两块糖。", bag: [{ item: "test-candy", count: 2 }] };
        }
        """;

    private static CoinCatalogue Catalogue => new(
        [new CoinGood("box", "盲盒", 30, "shop.box")],
        [new CoinItem("test-candy", "糖", "use.candy")],
        []);

    [Fact]
    public async Task A_real_script_decides_what_a_box_contains()
    {
        var scripts = new RealScripts(Box);
        var ledger = new CoinLedger(new MemoryStore(), new MemoryAtomic());
        var market = new CoinMarket(ledger, Catalogue, scripts);
        await ledger.CreditAsync(100, "topup", "test", default);

        var bought = await market.BuyAsync("s-1", "box", 1, "model:s-1", default, "call-1");

        Assert.True(bought.Ok, bought.Error?.Message);
        Assert.Equal(70, bought.Value!.Balance);          // the price does not depend on what was inside
        var state = (await ledger.ReadAsync(default)).Value!;
        // 50/50 in the script: either nothing or two. Both are legal; anything else means the contract is not real.
        Assert.True(state.CountOf("test-candy") is 0 or 2, $"got {state.CountOf("test-candy")}");
        Assert.Equal(1, state.SoldOf("box"));
    }

    [Fact]
    public async Task A_script_that_throws_is_a_failed_purchase_and_the_money_comes_back()
    {
        var scripts = new RealScripts("""
            function main() { return null; }
            async function box(args) { throw new Error("柜台后面着火了"); }
            """);
        var ledger = new CoinLedger(new MemoryStore(), new MemoryAtomic());
        var market = new CoinMarket(ledger, Catalogue, scripts);
        await ledger.CreditAsync(100, "topup", "test", default);

        var bought = await market.BuyAsync("s-1", "box", 1, "model:s-1", default, "call-1");

        Assert.False(bought.Ok);
        Assert.Equal(100, (await ledger.ReadAsync(default)).Value!.Balance);
    }

    /// <summary>A real Jint executor behind the narrow port the purse uses.</summary>
    private sealed class RealScripts(string source) : ICoinScripts
    {
        private static readonly Address Target = new("channel", "test", "one");
        private readonly JintScriptExecutor _executor = new();

        private ScriptPackage Package() => new(
            new ScriptManifest("shop.box", 1, "channel", MountMode.Manual, [], "main", [],
                TrustDomain.Trusted, Call: "box", Tool: new ScriptToolSpec("shop.box", "盲盒", "{}")),
            source);

        public bool CanRun(string script) => string.Equals(script, "shop.box", StringComparison.Ordinal);

        public ValueTask<Result<string>> InvokeAsync(string script, string argumentsJson,
            CancellationToken cancellationToken) =>
            _executor.InvokeAsync(Package(), Target, argumentsJson, new NoApi(), cancellationToken);
    }

    /// <summary>The script uses none of it; a settlement that reached for the world would be a different design.</summary>
    private sealed class NoApi : IScriptApi
    {
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
        public Task<string> CallActionAsync(string a, string j, CancellationToken c) => Task.FromResult("{}");
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
