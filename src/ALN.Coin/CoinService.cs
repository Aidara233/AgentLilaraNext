using System.Text;
using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Coin;

/// <summary>
/// The purse as the round sees it: one line, rebuilt every round, free to read.
///
/// Without it the model has to spend a tool call to learn its budget — which means it can never "save up", and the
/// thing the purse exists to measure (managing a resource) becomes guessing. Reading is not spending: a floating block
/// may be rendered more than once, so nothing here may have an effect.
/// </summary>
public sealed class CoinContextSource(CoinLedger ledger, CoinMarket market) : IFloatingContextSource
{
    public string Id => "coin.context";

    public async ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        // A purse nobody can read is not worth a broken round: the model simply does not see the line this round.
        if (!read.Ok || read.Value is null) return Result<IReadOnlyList<RoundContextContribution>>.Success([]);

        var state = read.Value;
        var bag = state.Bag.Count == 0
            ? "背包是空的"
            : "背包 " + string.Join('、', state.Bag.Select(stack => $"{stack.Item}×{stack.Count}"));
        var used = state.Bag.Sum(stack => stack.Count);
        var line = $"钱包：{state.Balance} 枚 · {bag}（{used}/{market.Capacity}）";

        return Result<IReadOnlyList<RoundContextContribution>>.Success(
            [new RoundContextContribution("coin.wallet", "coin", line)]);
    }
}

/// <summary>
/// The human side: a <c>coin</c> subtree. Looking is <see cref="AccessLevel.Agent"/>; buying, using and topping up
/// are <see cref="AccessLevel.Full"/>, because spending Lilara's money and printing more of it are kernel-grade acts.
///
/// <c>//coin</c> on its own is a namespace, so it answers with what it has — and a shallower person only sees the
/// sub-instructions he could actually run.
/// </summary>
public static class CoinService
{
    public static Result Install(ISessionExtensionRegistry extensions, ICommandRegistry commands, IControlPlane plane,
        CoinLedger ledger, CoinMarket market)
    {
        var verbs = CoinActions.Register(plane, ledger);
        if (!verbs.Ok) return verbs;
        var tools = extensions.Register(new CoinToolSource(ledger, market));
        if (!tools.Ok) return tools;
        var context = extensions.Register(new CoinContextSource(ledger, market));
        if (!context.Ok) return context;

        CommandNode[] nodes =
        [
            new("coin", "钱包与商店（//coin <子指令>）", AccessLevel.Agent),
            new("coin.wallet", "余额与背包", AccessLevel.Agent,
                Body: async (context, cancellationToken) =>
                {
                    var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (!read.Ok || read.Value is null)
                        return Result<string>.Failure(read.Error ?? new(ErrorCode.Unavailable, "读不到钱包。"));
                    var state = read.Value;
                    var bag = new StringBuilder();
                    bag.Append($"余额：{state.Balance} 枚（第 {state.Revision} 版）");
                    bag.Append(state.Bag.Count == 0
                        ? "\n背包：空"
                        : "\n背包 " + state.Bag.Sum(stack => stack.Count) + "/" + market.Capacity + "："
                          + string.Join('、', state.Bag.Select(stack => $"{stack.Item}×{stack.Count}")));
                    return Result<string>.Success(bag.ToString());
                }),
            new("coin.shop", "货架", AccessLevel.Agent, Body: async (context, cancellationToken) =>
            {
                var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
                var balance = read.Ok && read.Value is not null ? read.Value.Balance : 0;
                var lines = new List<string> { $"货架（你有 {balance} 枚）：" };
                foreach (var good in market.Catalogue.Goods)
                {
                    var left = good.Stock < 0
                        ? "不限"
                        : $"剩 {Math.Max(0, good.Stock - (read.Value?.SoldOf(good.Id) ?? 0))}";
                    lines.Add($"  {good.Id}  {good.Price} 枚  {good.Name}（{left}）");
                }
                if (market.Catalogue.Goods.Count == 0) lines.Add("  空的。");
                return Result<string>.Success(string.Join('\n', lines));
            }),
            new("coin.bag", "背包", AccessLevel.Agent, Body: async (context, cancellationToken) =>
            {
                var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!read.Ok || read.Value is null)
                    return Result<string>.Failure(read.Error ?? new(ErrorCode.Unavailable, "读不到钱包。"));
                var state = read.Value;
                if (state.Bag.Count == 0) return Result<string>.Success("背包是空的。");
                var lines = state.Bag
                    .Select(stack =>
                    {
                        var item = market.Catalogue.FindItem(stack.Item);
                        return $"  {stack.Item}×{stack.Count}  {item?.Name ?? "（没有说明）"}";
                    })
                    .ToList();
                return Result<string>.Success(
                    $"背包 {state.Bag.Sum(s => s.Count)}/{market.Capacity}：\n" + string.Join('\n', lines));
            }),
            new("coin.buy", "买一件（要 full）", AccessLevel.Full,
                Usage: "//coin buy <商品 id> [件数]",
                Body: async (context, cancellationToken) =>
                {
                    if (context.Arguments.Count == 0)
                        return Result<string>.Failure(new(ErrorCode.Invalid, "用法：//coin buy <商品 id> [件数]"));
                    var count = context.Arguments.Count > 1 && int.TryParse(context.Arguments[1], out var many) ? many : 1;
                    var trade = await market.BuyAsync($"command:{context.Person?.Id ?? "unknown"}",
                        context.Arguments[0], count, context.Actor.Id, cancellationToken).ConfigureAwait(false);
                    if (!trade.Ok || trade.Value is null)
                        return Result<string>.Failure(trade.Error ?? new(ErrorCode.Unavailable, "没买成。"));
                    return Result<string>.Success(
                        (trade.Value.Text ?? "（脚本没有留下话）") + $"\n余额：{trade.Value.Balance} 枚");
                }),
            new("coin.use", "用一件（要 full）", AccessLevel.Full,
                Usage: "//coin use <物品 id> [件数]",
                Body: async (context, cancellationToken) =>
                {
                    if (context.Arguments.Count == 0)
                        return Result<string>.Failure(new(ErrorCode.Invalid, "用法：//coin use <物品 id> [件数]"));
                    var count = context.Arguments.Count > 1 && int.TryParse(context.Arguments[1], out var many) ? many : 1;
                    var trade = await market.UseAsync($"command:{context.Person?.Id ?? "unknown"}",
                        context.Arguments[0], count, context.Actor.Id, cancellationToken).ConfigureAwait(false);
                    if (!trade.Ok || trade.Value is null)
                        return Result<string>.Failure(trade.Error ?? new(ErrorCode.Unavailable, "没用成。"));
                    return Result<string>.Success(
                        (trade.Value.Text ?? "（脚本没有留下话）") + $"\n消耗了 {trade.Value.Used} 件，余额 {trade.Value.Balance} 枚");
                }),
            new("coin.topup", "充钱（要 full）", AccessLevel.Full, Usage: "//coin topup <数量>",
                Body: async (context, cancellationToken) =>
                {
                    if (context.Arguments.Count == 0 || !long.TryParse(context.Arguments[0], out var amount))
                        return Result<string>.Failure(new(ErrorCode.Invalid, "用法：//coin topup <数量>"));
                    var credited = await ledger.CreditAsync(amount, "topup", context.Actor.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (!credited.Ok || credited.Value is null)
                        return Result<string>.Failure(credited.Error ?? new(ErrorCode.Unavailable, "没充上。"));
                    return Result<string>.Success($"存了 {amount} 枚，现在有 {credited.Value.Balance} 枚。");
                })
        ];

        foreach (var node in nodes)
        {
            var registered = commands.Register(node);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }
}
