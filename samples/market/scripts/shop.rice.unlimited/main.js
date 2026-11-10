// 免费续饭：选一个频道，今日免费。
//
// 硬币已经由 coin_buy 扣过了。这里**不把饭补进米缸**——那会让今天的每一口都先记成真实消耗，再把它们补回来，
// 中间那段时间米缸的数字是假的。开一张免单，扣的时候就知道不用扣，米缸一动不动。
//
// "哪个频道"来自 coin_buy 的结算参数：工具层知道这句话是从哪个频道说的，市场层不知道，所以要一路传下来。

function main() {
  return null;
}

function describe() {
  return { name: "shop.rice.unlimited", description: "免费续饭的结算脚本", parametersJson: "{\"type\":\"object\",\"properties\":{}}", access: "full" };
}

async function buy(args, ctx) {
  var channel = args && args.channel ? String(args.channel) : "";
  if (!channel) {
    // 这不是"随便挑一个"的商品：卖的就是"某个频道今天不用管饭"。不知道是哪个频道，宁可让 coin_buy 退钱，
    // 也不要猜——给错频道等于给了一件别人买的东西。
    throw new Error("免费续饭要在频道里买：结算时没有拿到频道地址。");
  }

  var granted = await ctx.actions.call("rice.waive", {
    unlimited: true,
    channel: channel,
    // 不传 until：服务端默认到今日 24:00（本地时区），"今天免费"和"到午夜"是同一个承诺。
    reason: "免费续饭：" + channel
  });
  if (!granted || granted.ok !== true) {
    throw new Error("开免单失败：" + JSON.stringify((granted && granted.error) || granted));
  }

  return {
    text: "这个频道今日免单：" + channel + "（到今日 24:00，不限轮数）。",
    bag: []
  };
}
