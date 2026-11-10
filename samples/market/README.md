# 样例市场

一份能直接用的最小市场：四件商品、三个物品、七个结算脚本。

## 怎么用

```
<dataRoot>/market/goods.json          ← 复制这里的同名片
<dataRoot>/market/items.json
<dataRoot>/scripts/trusted/shop.box/  ← 整个文件夹复制过去
<dataRoot>/scripts/trusted/shop.air/
<dataRoot>/scripts/trusted/shop.brick/
<dataRoot>/scripts/trusted/shop.rare/
<dataRoot>/scripts/trusted/use.candy/
<dataRoot>/scripts/trusted/use.brick/
<dataRoot>/scripts/trusted/use.note/
```

然后 `//reload`（或重启）。启动时的 `market:` 行会逐条告诉你哪件没上架、为什么——**没有脚本的商品不上架**，而不是“买了没反应”。

钱从 `//coin topup <数量>` 来（要 `full`：人手写 `identify/<id>/info.json` 里的 `"level": "full"`）。

## 货架

| 商品 | 价 | 脚本 | 买到什么 |
|---|---|---|---|
| `test-air` | 0 | `shop.air` | 什么也没有 |
| `test-brick` | 1 | `shop.brick` | 一块砖 |
| `test-box` | 30 | `shop.box` | 五成空盒，五成两块糖 |
| `test-rare` | 100 | `shop.rare` | 一张便签（库存 1） |

物品：`test-candy`（用掉一件）、`test-brick`（用不掉）、`test-note`（不消耗，只是读）。

## 脚本的契约

```js
function main() { return null; }                    // 事件入口：loader 会检查它是不是函数，哪怕你不挂事件
async function box(args, ctx) {                     // manifest 的 "call" 指向这里
  return { text: "给人看的一句话", bag: [{ item: "test-candy", count: 2 }], used: 1 };
}
```

- `bag`：`count` 为正进包、为负出包。
- `used`：只在使用时有意义——`0` 不消耗、`1` 消耗一件、`-1` 全部。
- **返回值里没有余额**。脚本要给钱（备用资金、返利）就调 `ctx.actions.call("coin.credit", { amount, reason })`，能力名 `coin.control` 未登记 ⇒ `full`，只有 `trusted` / `builtin` 目录里的脚本能调。
- 载荷里有 `{ kind, good|item, count, paid, balance, bag, actor }`。

> 这份 manifest 是按 `ScriptFileManifest` 的字段写的，**没有经过 loader 实跑**（`docs/33 §7.2`）。第一次复制过去时请看一眼启动日志：`market:` 行没说话，就说明七件都认出来了。
