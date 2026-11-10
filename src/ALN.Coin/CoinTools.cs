using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Coin;

/// <summary>
/// What the model can do with the purse: look, shop, buy, use.
///
/// All five are <see cref="AccessLevel.Agent"/> and four of them are free — asking the price must not cost anything,
/// or deciding anything becomes impossible. Buying and using are marked "acts externally": their answer is what the
/// model <i>got</i>, not something it said.
///
/// The Session id is bound per round, not per instance: one source serves every Session, and a purchase is recorded
/// against the Session that made it.
/// </summary>
public sealed class CoinToolSource(CoinLedger ledger, CoinMarket market) : ISessionToolSource
{
    public const string Wallet = "coin_wallet";
    public const string Shop = "coin_shop";
    public const string Bag = "coin_bag";
    public const string Buy = "coin_buy";
    public const string Use = "coin_use";

    public string Id => "coin.tools";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ModelToolDefinition WalletDefinition = new(Wallet,
        "看看 Lilara 自己的钱包里还有多少硬币，以及背包里有什么。免费，随时可以看。",
        """{"type":"object","properties":{}}""");

    private static readonly ModelToolDefinition ShopDefinition = new(Shop,
        "看货架上有什么、多少钱、买得起否。免费。买什么会得到什么由每件商品自己的脚本决定——可能什么都不给。",
        """{"type":"object","properties":{}}""");

    private static readonly ModelToolDefinition BagDefinition = new(Bag,
        "看背包里每件东西是什么、有几件。免费。",
        """{"type":"object","properties":{}}""");

    private static readonly ModelToolDefinition BuyDefinition = new(Buy,
        "花硬币买一件商品。先扣钱再跑商品脚本；脚本决定你最后得到什么（可能什么都没有，也可能比付的多）。"
        + "余额不足不会扣款，会告诉你还差多少。返回扣后的余额。",
        """{"type":"object","properties":{"id":{"type":"string","description":"商品 id，见 coin_shop"},"count":{"type":"integer","description":"买几件，默认 1"}},"required":["id"]}""");

    private static readonly ModelToolDefinition UseDefinition = new(Use,
        "使用背包里的一件东西。消耗几件由这件东西自己的脚本决定：可能不消耗，可能消耗一件，也可能全消耗掉。",
        """{"type":"object","properties":{"id":{"type":"string","description":"物品 id，见 coin_bag"},"count":{"type":"integer","description":"用几件，默认 1"}},"required":["id"]}""");

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        var session = string.IsNullOrWhiteSpace(request.SessionId) ? "session" : request.SessionId;
        IReadOnlyList<SessionToolOffer> offers =
        [
            new(new Tool(Wallet, (call, ct) => ShowWalletAsync(ct)), WalletDefinition,
                ActsExternally: false),
            new(new Tool(Shop, (call, ct) => ShowShopAsync(ct)), ShopDefinition,
                ActsExternally: false),
            new(new Tool(Bag, (call, ct) => ShowBagAsync(ct)), BagDefinition,
                ActsExternally: false),
            new(new Tool(Buy, (call, ct) => BuyAsync(call, session, ct)), BuyDefinition),
            new(new Tool(Use, (call, ct) => UseAsync(call, session, ct)), UseDefinition)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private async ValueTask<Result<SessionToolResult>> ShowWalletAsync(CancellationToken cancellationToken)
    {
        var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.Ok || read.Value is null) return Failure(read.Error!);
        return Answer(JsonSerializer.Serialize(new
        {
            balance = read.Value.Balance,
            revision = read.Value.Revision,
            capacity = market.Capacity,
            used = read.Value.Bag.Sum(stack => stack.Count),
            bag = read.Value.Bag.Select(stack => new { item = stack.Item, count = stack.Count })
        }, Json));
    }

    private async ValueTask<Result<SessionToolResult>> ShowShopAsync(CancellationToken cancellationToken)
    {
        var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.Ok || read.Value is null) return Failure(read.Error!);
        return Answer(JsonSerializer.Serialize(new
        {
            balance = read.Value.Balance,
            goods = market.Catalogue.Goods.Select(good => new
            {
                id = good.Id,
                name = good.Name,
                price = good.Price,
                left = good.Stock < 0 ? (int?)null : Math.Max(0, good.Stock - read.Value.SoldOf(good.Id)),
                affordable = read.Value.Balance >= good.Price
            })
        }, Json));
    }

    private async ValueTask<Result<SessionToolResult>> ShowBagAsync(CancellationToken cancellationToken)
    {
        var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.Ok || read.Value is null) return Failure(read.Error!);
        return Answer(JsonSerializer.Serialize(new
        {
            capacity = market.Capacity,
            items = read.Value.Bag.Select(stack => new
            {
                item = stack.Item,
                count = stack.Count,
                name = market.Catalogue.FindItem(stack.Item)?.Name
            })
        }, Json));
    }

    private async ValueTask<Result<SessionToolResult>> BuyAsync(ModelToolCall call, string sessionId,
        CancellationToken cancellationToken)
    {
        var arguments = Parse(call.ArgumentsJson);
        if (arguments is null) return Failure(new(ErrorCode.Invalid, "参数不是 JSON。"));
        var id = arguments.Value.TryGetProperty("id", out var named) ? named.GetString() : null;
        var count = arguments.Value.TryGetProperty("count", out var many) && many.TryGetInt32(out var parsed) ? parsed : 1;
        if (string.IsNullOrWhiteSpace(id)) return Failure(new(ErrorCode.Invalid, "要说买哪一件：id。"));

        var trade = await market.BuyAsync(sessionId, id!, count, $"model:{sessionId}", cancellationToken, call.Id)
            .ConfigureAwait(false);
        if (!trade.Ok || trade.Value is null) return Failure(trade.Error!);
        return Answer(JsonSerializer.Serialize(new
        {
            text = trade.Value.Text,
            balance = trade.Value.Balance,
            bag = trade.Value.Bag.Select(stack => new { item = stack.Item, count = stack.Count })
        }, Json));
    }

    private async ValueTask<Result<SessionToolResult>> UseAsync(ModelToolCall call, string sessionId,
        CancellationToken cancellationToken)
    {
        var arguments = Parse(call.ArgumentsJson);
        if (arguments is null) return Failure(new(ErrorCode.Invalid, "参数不是 JSON。"));
        var id = arguments.Value.TryGetProperty("id", out var named) ? named.GetString() : null;
        var count = arguments.Value.TryGetProperty("count", out var many) && many.TryGetInt32(out var parsed) ? parsed : 1;
        if (string.IsNullOrWhiteSpace(id)) return Failure(new(ErrorCode.Invalid, "要说用哪一件：id。"));

        var trade = await market.UseAsync(sessionId, id!, count, $"model:{sessionId}", cancellationToken, call.Id)
            .ConfigureAwait(false);
        if (!trade.Ok || trade.Value is null) return Failure(trade.Error!);
        return Answer(JsonSerializer.Serialize(new
        {
            text = trade.Value.Text,
            consumed = trade.Value.Used,
            balance = trade.Value.Balance,
            // "consumed 0" is legal (a book you read), so say it: otherwise the model assumes the thing is gone.
            note = trade.Value.Used == 0 ? "没有消耗，东西还在包里。" : null
        }, Json));
    }

    private static Result<SessionToolResult> Answer(string content) =>
        Result<SessionToolResult>.Success(new SessionToolResult(content, null));

    /// <summary>
    /// Refusals come back as a tool result, not as a failed call: the model must read "还差 20" and decide, and a
    /// failed call is what it retries blindly.
    /// </summary>
    private static Result<SessionToolResult> Failure(Error error) =>
        Result<SessionToolResult>.Success(new SessionToolResult(
            JsonSerializer.Serialize(new { error = error.Message }, Json), null));

    private static JsonElement? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private sealed class Tool(
        string name,
        Func<ModelToolCall, CancellationToken, ValueTask<Result<SessionToolResult>>> body) : ISessionTool
    {
        public string Name => name;

        public bool EndsTurn => false;

        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken) =>
            body(call, cancellationToken);
    }
}
