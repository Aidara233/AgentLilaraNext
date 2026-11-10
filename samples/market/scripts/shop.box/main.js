// 盲盒：买之前你不知道里面是什么，因为决定权在这个脚本手里，不在货架上。
//
// 两个入口都是必须的：loader 会检查 entryPoint 是不是函数（typeof main === "function"），即使这个脚本
// 从不挂事件；而 call 是 coin_buy 触发的那一个。返回值会被 JSON.stringify 之后原样交给宿主。

function main() {
  // 不挂事件，但入口得在。
  return null;
}

async function box(args) {
  // 五成空盒，五成两块糖。也可能什么都没有——这是设计的一部分，不是 bug。
  if (Math.random() < 0.5) {
    return { text: "盒子是空的，只有一张过期的优惠券。", bag: [] };
  }
  return {
    text: "盒子里滚出两块糖。",
    bag: [{ item: "test-candy", count: 2 }]
  };
}
