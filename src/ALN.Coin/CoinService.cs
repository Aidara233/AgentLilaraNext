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
        CoinLedger ledger, CoinMarket market, TimeProvider? time = null, CoinNotice? notice = null)
    {
        var verbs = CoinActions.Register(plane, ledger);
        if (!verbs.Ok) return verbs;
        var tools = extensions.Register(new CoinToolSource(ledger, market));
        if (!tools.Ok) return tools;
        var context = extensions.Register(new CoinContextSource(ledger, market));
        if (!context.Ok) return context;
        // One coin a day from a person. Nothing else has a clock, because nothing else here cares what day it is.
        var gifts = new CoinGifts(ledger, time);

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
                    // Who gave today, and only when somebody did: a line that says "今天 0 人" on every check is noise
                    // where the number that matters should be.
                    var today = CoinGifts.DayOf((time ?? TimeProvider.System).GetLocalNow());
                    var givers = state.GiversOn(today);
                    if (givers > 0) bag.Append($"\n今天有 {givers} 人投过硬币");
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
                }),
            // The one way a person adds money: no depth needed beyond being on the identity table, and nothing at all for
            // somebody who is not. The check is `Person is null`, not a level — a stranger arrives as agent, so a level
            // cannot tell the two apart and using one here would let every stranger in.
            new("coin.toss", "给 Lilara 一枚硬币：每人每天一枚，只认登记过的人", AccessLevel.Agent,
                Body: async (context, cancellationToken) =>
                {
                    if (context.Person is not { } person)
                        return Result<string>.Failure(new(ErrorCode.Denied,
                            "投币只认人物表里的人：让管这件事的人在 identify/ 里给你写一张卡。"));

                    var given = await gifts.GiveAsync(person, context.Actor.Id, cancellationToken).ConfigureAwait(false);
                    if (!given.Ok || given.Value is null)
                        return Result<string>.Failure(given.Error ?? new(ErrorCode.Unavailable, "没投进去。"));

                    // The coin is in the purse from here on, so telling the conversation is best effort: a Channel nobody
                    // bound or a Session busy with a turn must not turn a taken coin into a failed instruction (the
                    // notice logs its own reason). It goes before the reply because the reply is sent by the interceptor
                    // once this body returns, and "she has already noticed" is the shape a person expects.
                    if (notice is not null)
                        await notice.SendAsync(context.Adapter, context.PlatformConversationId,
                            $"{person.Name}（{person.Id}）投了一枚硬币给你：今天第 {given.Value.GiversToday} 枚，"
                            + $"钱包里现在有 {given.Value.Balance} 枚。", cancellationToken).ConfigureAwait(false);

                    return Result<string>.Success(
                        $"收下了 {CoinGifts.CoinsPerDay} 枚硬币，Lilara 现在有 {given.Value.Balance} 枚"
                        + $"（今天是第 {given.Value.GiversToday} 位）。明天再来。");
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
