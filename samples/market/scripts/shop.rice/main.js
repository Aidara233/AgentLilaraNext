// 买饭：硬币已经由 coin_buy 扣过了，这里负责把饭补进米缸。
//
// "rice.control" 在等级表里没有登记，所以它是 full —— 只有 trusted / builtin 目录里的脚本声明得起，
// 这正是"脚本能印饭、模型不能"的那道闸。

function main() {
  return null;
}

function describe() {
  return { name: "shop.rice", description: "买饭的结算脚本", parametersJson: "{\"type\":\"object\",\"properties\":{}}", access: "full" };
}

async function buy(args, ctx) {
  await ctx.actions.call("rice.add", { bites: 100, reason: "买饭" });
  return { text: "补了一碗白饭（100 口）。", bag: [] };
}
