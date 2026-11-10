using System.Text.Json;
using ALN.Contracts;

namespace ALN.Coin;

/// <summary>
/// What a script decided a purchase or a use did. The host applies this; the script never touches the purse itself.
/// </summary>
public sealed record CoinSettlement(
    IReadOnlyList<CoinStack> Bag,
    string? Text,
    int Used)
{
    public static readonly CoinSettlement Nothing = new([], null, 0);
}

public sealed record CoinTradeResult(string? Text, long Balance, IReadOnlyList<CoinStack> Bag, int Used);

/// <summary>
/// The market: reading the two catalogue files, and turning one purchase or one use into a change of the purse.
///
/// The shape of a trade is the interesting part. Money moves in its own transaction, the script runs <b>outside</b>
/// any transaction (a slow script must not hold the purse's lock), and then the outcome is applied in a second
/// transaction. If that last step fails the money is handed back, because "paid and got nothing because the bag was
/// full" is a bug the model would rightly read as theft.
/// </summary>
public sealed class CoinMarket(CoinLedger ledger, CoinCatalogue catalogue, ICoinScripts? scripts, int capacity = 12)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CoinCatalogue Catalogue => catalogue;

    public int Capacity => capacity;

    public async ValueTask<Result<CoinTradeResult>> BuyAsync(string sessionId, string goodId, int count,
        string actor, CancellationToken cancellationToken)
    {
        if (count is < 1 or > 99)
            return Result<CoinTradeResult>.Failure(new(ErrorCode.Invalid, $"一次买 1 到 99 件（收到 {count}）。"));

        var good = catalogue.Find(goodId);
        if (good is null)
            return Result<CoinTradeResult>.Failure(new(ErrorCode.Invalid, $"货架上没有 {goodId}。用 coin_shop 看看有什么。"));

        var before = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!before.Ok || before.Value is null) return Result<CoinTradeResult>.Failure(before.Error!);
        var purse = before.Value;

        // Stock is checked here for the message and again inside the transaction: this one is for the model, that one
        // is the one that holds.
        if (good.Stock >= 0 && purse.SoldOf(good.Id) + count > good.Stock)
            return Result<CoinTradeResult>.Failure(new(ErrorCode.Conflict, $"{good.Name} 只剩 {Math.Max(0, good.Stock - purse.SoldOf(good.Id))} 件。"));

        var price = good.Price * count;
        if (good.Price < 0)
            return Result<CoinTradeResult>.Failure(new(ErrorCode.Invalid, $"{good.Id} 的价格是负数，货架有误。"));
        if (purse.Balance < price)
            return Result<CoinTradeResult>.Failure(new(ErrorCode.Denied,
                $"还差 {price - purse.Balance}，当前 {purse.Balance}，{count} 件要 {price}。"));

        // 1. Pay. From here on the money is gone, so every path below either delivers or refunds.
        var paid = await ledger.DebitAsync(price, $"buy:{good.Id}", actor, cancellationToken).ConfigureAwait(false);
        if (!paid.Ok) return Result<CoinTradeResult>.Failure(paid.Error!);

        // 2. Ask the script what the purchase did. It runs as itself, with its own depth: a script that may move money
        //    does so through coin.credit, which needs full, which is what "high privilege" means here.
        var settled = await SettleAsync(good.Script,
            new
            {
                kind = "buy", good = good.Id, count, paid = price, balance = paid.Value!.Balance,
                bag = paid.Value.Bag, actor
            }, cancellationToken).ConfigureAwait(false);
        if (!settled.Ok) return await RefundAsync(paid.Value!, price, $"buy:{good.Id}", actor, settled.Error!, cancellationToken)
            .ConfigureAwait(false);

        // 3. Apply. Stock and bag move in the same transaction as the purchase record.
        var applied = await ledger.ChangeAsync($"settle:buy:{good.Id}", actor, state =>
        {
            if (good.Stock >= 0 && state.SoldOf(good.Id) + count > good.Stock)
                return Result<CoinState>.Failure(new(ErrorCode.Conflict, $"{good.Name} 已经卖完了。"));
            return Apply(state, settled.Value!.Bag, good.Id, count, sessionId);
        }, cancellationToken).ConfigureAwait(false);
        if (!applied.Ok)
            return await RefundAsync(paid.Value!, price, $"buy:{good.Id}", actor, applied.Error!, cancellationToken)
                .ConfigureAwait(false);

        return Result<CoinTradeResult>.Success(new CoinTradeResult(settled.Value!.Text, applied.Value!.Balance,
            settled.Value.Bag, 0));
    }

    public async ValueTask<Result<CoinTradeResult>> UseAsync(string sessionId, string itemId, int count,
        string actor, CancellationToken cancellationToken)
    {
        var item = catalogue.FindItem(itemId);
        if (item is null)
            return Result<CoinTradeResult>.Failure(new(ErrorCode.Invalid, $"背包里的东西没有 {itemId} 这种东西。"));

        var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.Ok || read.Value is null) return Result<CoinTradeResult>.Failure(read.Error!);
        var have = read.Value.CountOf(item.Id);
        var wanted = count < 1 ? 1 : count;
        if (have < wanted)
            return Result<CoinTradeResult>.Failure(new(ErrorCode.Conflict, $"只有 {have} 件 {item.Name}。"));

        var settled = await SettleAsync(item.Use,
            new { kind = "use", item = item.Id, count = wanted, balance = read.Value.Balance, bag = read.Value.Bag, actor },
            cancellationToken).ConfigureAwait(false);
        if (!settled.Ok) return Result<CoinTradeResult>.Failure(settled.Error!);

        // How many are consumed is the script's answer, not a rule: 0 (read a book), 1, or all of them.
        var consumed = settled.Value!.Used switch
        {
            < 0 => have,
            _ => Math.Min(settled.Value.Used, have)
        };

        var applied = await ledger.ChangeAsync($"settle:use:{item.Id}", actor, state =>
        {
            var bagged = Apply(state, settled.Value!.Bag, null, 0, sessionId);
            if (!bagged.Ok || bagged.Value is null) return bagged;
            return consumed == 0 ? bagged : Remove(bagged.Value, item.Id, consumed);
        }, cancellationToken).ConfigureAwait(false);
        if (!applied.Ok) return Result<CoinTradeResult>.Failure(applied.Error!);

        return Result<CoinTradeResult>.Success(new CoinTradeResult(settled.Value.Text, applied.Value!.Balance,
            settled.Value.Bag, consumed));
    }

    private async ValueTask<Result<CoinSettlement>> SettleAsync(string script, object input,
        CancellationToken cancellationToken)
    {
        if (scripts is null)
            return Result<CoinSettlement>.Failure(new(ErrorCode.Unavailable, "这个部署没有挂脚本运行时，商店用不了。"));
        var invoked = await scripts.InvokeAsync(script, JsonSerializer.Serialize(input, Json), cancellationToken)
            .ConfigureAwait(false);
        if (!invoked.Ok) return Result<CoinSettlement>.Failure(invoked.Error!);

        try
        {
            using var document = JsonDocument.Parse(invoked.Value ?? "{}");
            var root = document.RootElement;
            var bag = new List<CoinStack>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("bag", out var stacks) &&
                stacks.ValueKind == JsonValueKind.Array)
            {
                foreach (var stack in stacks.EnumerateArray())
                {
                    var item = stack.TryGetProperty("item", out var name) ? name.GetString() : null;
                    var amount = stack.TryGetProperty("count", out var number) && number.TryGetInt32(out var parsed) ? parsed : 1;
                    if (!string.IsNullOrWhiteSpace(item)) bag.Add(new CoinStack(item!, amount));
                }
            }
            var text = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("text", out var words)
                ? words.GetString()
                : null;
            var used = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("used", out var use) &&
                       use.TryGetInt32(out var useCount)
                ? useCount
                : 1;
            return Result<CoinSettlement>.Success(new CoinSettlement(bag, text, used));
        }
        catch (JsonException ex)
        {
            return Result<CoinSettlement>.Failure(new(ErrorCode.Invalid,
                $"脚本 {script} 没有返回可用的结算结果。", ex.Message));
        }
    }

    private async ValueTask<Result<CoinTradeResult>> RefundAsync(CoinState paid, long price, string reason,
        string actor, Error cause, CancellationToken cancellationToken)
    {
        var refunded = await ledger.CreditAsync(price, $"refund:{reason}", actor, cancellationToken).ConfigureAwait(false);
        var message = refunded.Ok
            ? $"{cause.Message}（钱已退回，当前 {refunded.Value!.Balance}。）"
            : $"{cause.Message}（退款也失败了：{refunded.Error?.Message}）";
        return Result<CoinTradeResult>.Failure(new(cause.Code, message));
    }

    private Result<CoinState> Apply(CoinState state, IReadOnlyList<CoinStack> delta, string? goodId, int soldCount,
        string sessionId)
    {
        var bag = state.Bag.Select(stack => stack with { }).ToList();
        foreach (var change in delta)
        {
            var index = bag.FindIndex(stack => string.Equals(stack.Item, change.Item, StringComparison.Ordinal));
            if (index < 0)
            {
                if (change.Count <= 0) continue;   // taking away what is not there is a no-op, not an error
                bag.Add(new CoinStack(change.Item, change.Count));
                continue;
            }
            var next = bag[index].Count + change.Count;
            if (next <= 0) bag.RemoveAt(index);
            else bag[index] = bag[index] with { Count = next };
        }

        if (bag.Sum(stack => stack.Count) > capacity)
            return Result<CoinState>.Failure(new(ErrorCode.Conflict, $"背包满了（上限 {capacity} 件）。"));

        var soldCounts = new Dictionary<string, int>(state.Sold, StringComparer.Ordinal);
        if (goodId is not null && soldCount > 0) soldCounts[goodId] = state.SoldOf(goodId) + soldCount;

        var purchased = new Dictionary<string, IReadOnlyDictionary<string, int>>(state.Purchased, StringComparer.Ordinal);
        if (goodId is not null && soldCount > 0)
        {
            var mine = purchased.TryGetValue(sessionId, out var previous)
                ? new Dictionary<string, int>(previous, StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal);
            mine[goodId] = (mine.TryGetValue(goodId, out var had) ? had : 0) + soldCount;
            purchased[sessionId] = mine;
        }

        return Result<CoinState>.Success(state with
        {
            Bag = bag,
            Sold = soldCounts,
            Purchased = purchased,
            Revision = state.Revision + 1
        });
    }

    private static Result<CoinState> Remove(CoinState state, string item, int consumed)
    {
        var bag = state.Bag.Select(stack => stack with { }).ToList();
        var index = bag.FindIndex(stack => string.Equals(stack.Item, item, StringComparison.Ordinal));
        if (index < 0) return Result<CoinState>.Success(state);
        var next = bag[index].Count - consumed;
        if (next <= 0) bag.RemoveAt(index);
        else bag[index] = bag[index] with { Count = next };
        return Result<CoinState>.Success(state with { Bag = bag, Revision = state.Revision + 1 });
    }
}
