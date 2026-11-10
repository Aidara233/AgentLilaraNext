// 自热米饭：今日多 500 轮白饭，到 24:00 作废。
//
// 它开的不是"免费"，是一份额度：扣满 500 次之后，照常从米缸里吃。之所以两者是一回事，是因为每轮只吃 1 口——
// "今天多 500 口"和"今天前 500 轮不扣"说的是同一件事，而后者是米缸真能表达的那种说法。
//
// 用不完不退款，是设计的一部分：它卖的是"今天不用管饭"，不是"买 500 口存在那里"。真想存着，去买一盆。

function main() {
  return null;
}

function describe() {
  return { name: "shop.rice.instant", description: "自热米饭的结算脚本", parametersJson: "{\"type\":\"object\",\"properties\":{}}", access: "full" };
}

async function buy(args, ctx) {
  // 买几份就是几份：一份 500 轮，三份 1500 轮。不乘它就会收三份钱、只给一份额度。
  var count = args && args.count ? Number(args.count) : 1;
  if (!(count > 0)) {
    count = 1;
  }
  var bites = 500 * count;

  var granted = await ctx.actions.call("rice.waive", {
    bites: bites,
    // 不传 until：服务端默认到今日 24:00（本地时区）。
    reason: "自热米饭：" + bites + " 轮"
  });
  if (!granted || granted.ok !== true) {
    throw new Error("开免单失败：" + JSON.stringify((granted && granted.error) || granted));
  }

  return {
    text: "今日多出 " + bites + " 轮白饭（到今日 24:00 作废，用不完不退款）。",
    bag: []
  };
}
