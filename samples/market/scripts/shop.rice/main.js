// 买饭：硬币已经由 coin_buy 扣过了，这里负责把饭补进米缸。
//
// "rice.control" 在等级表里没有登记，所以它是 full —— 只有 trusted / builtin 目录里的脚本声明得起，
// 这正是"脚本能印饭、模型不能"的那道闸。
//
// 一份补多少口，看的是货架上的 good id，不是价格：价格是生意，口数是规格，两件事分开改。
// 存货以"口"计，一碗 100 口（RiceUnits.BitesPerBowl），每轮吃 1 口 —— 所以 100 口也正好是 100 轮。

function main() {
  return null;
}

function describe() {
  return { name: "shop.rice", description: "白饭套餐的结算脚本", parametersJson: "{\"type\":\"object\",\"properties\":{}}", access: "full" };
}

async function buy(args, ctx) {
  // 一张价目表。放在函数里而不是顶层，是为了让它只活这一次调用；同时它也是这份脚本认识的
  // 全部 good id —— 货架上多一个套餐而这里没跟上，会在下面那个 throw 里显形，而不是静默地按一碗算。
  var menu = {
    "rice-bowl":   { bites: 100,  unit: "碗" },
    "rice-basin":  { bites: 500,  unit: "盆" },
    "rice-pot":    { bites: 2000, unit: "锅" },
    "rice-bucket": { bites: 5000, unit: "桶" }
  };

  var good = args && args.good ? String(args.good) : "";
  var entry = menu[good];
  if (!entry) {
    // 宁可炸掉也不要按一碗算：结算失败 coin_buy 会退款，猜错则是扣了钱少给饭。
    throw new Error("shop.rice 不认识货架上的 " + good);
  }

  // count 是"买几件"：买 3 盆就是 1500 口。不乘它就会收三份钱、补一份饭。
  var count = args && args.count ? Number(args.count) : 1;
  if (!(count > 0)) {
    count = 1;
  }

  var bites = entry.bites * count;
  await ctx.actions.call("rice.add", { bites: bites, reason: "买饭：" + good + "x" + count });

  return {
    text: "补了 " + count + " " + entry.unit + "白饭，共 " + bites + " 口（约 " + (bites / 100) + " 碗，还能跑 " + bites + " 轮）。",
    bag: []
  };
}
