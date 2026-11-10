using System.Text.Json;
using ALN.Contracts;

namespace ALN.Coin;

/// <summary>
/// The two catalogue files: <c>&lt;dataRoot&gt;/market/goods.json</c> and <c>items.json</c>.
///
/// A good whose script cannot be called is <b>dropped from the shelf</b> and reported, not sold-and-doing-nothing:
/// "I paid and nothing happened" is exactly the lesson this system must not teach by accident. The reverse mistake —
/// selling nothing on purpose — is a matter for the script, not for the loader.
/// </summary>
public static class CoinCatalogueFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CoinCatalogue Load(string dataRoot, ICoinScripts? scripts)
    {
        var issues = new List<string>();
        var goods = ReadGoods(Path.Combine(dataRoot, "market", "goods.json"), issues);
        var items = ReadItems(Path.Combine(dataRoot, "market", "items.json"), issues);

        if (scripts is null)
        {
            issues.Add("没有脚本运行时：商品仍上架，但买的时候会失败。");
            return new CoinCatalogue(goods, items, issues);
        }

        var shelf = new List<CoinGood>();
        foreach (var good in goods)
        {
            if (good.Price < 0) { issues.Add($"{good.Id}: 价格是负数，不上架。"); continue; }
            if (!scripts.CanRun(good.Script))
            {
                issues.Add($"{good.Id}: 脚本 {good.Script} 不可调用，不上架（图里已加载：{string.Join(", ", scripts.Loaded)}）。");
                continue;
            }
            shelf.Add(good);
        }

        var bag = new List<CoinItem>();
        foreach (var item in items)
        {
            if (!scripts.CanRun(item.Use))
            {
                issues.Add($"{item.Id}: 脚本 {item.Use} 不可调用，这件东西不会进背包（图里已加载：{string.Join(", ", scripts.Loaded)}）。");
                continue;
            }
            bag.Add(item);
        }

        return new CoinCatalogue(shelf, bag, issues);
    }

    private static IReadOnlyList<CoinGood> ReadGoods(string path, List<string> issues)
    {
        if (!File.Exists(path)) { issues.Add($"没有 {path}：货架是空的。"); return []; }
        try
        {
            var file = JsonSerializer.Deserialize<GoodsFile>(File.ReadAllText(path), Json);
            return file?.Goods ?? [];
        }
        catch (JsonException ex)
        {
            issues.Add($"{path}: 不是可读的 JSON（{ex.Message}），货架是空的。");
            return [];
        }
    }

    private static IReadOnlyList<CoinItem> ReadItems(string path, List<string> issues)
    {
        if (!File.Exists(path)) { issues.Add($"没有 {path}：背包里的东西没有说明。"); return []; }
        try
        {
            var file = JsonSerializer.Deserialize<ItemsFile>(File.ReadAllText(path), Json);
            return file?.Items ?? [];
        }
        catch (JsonException ex)
        {
            issues.Add($"{path}: 不是可读的 JSON（{ex.Message}）。");
            return [];
        }
    }

    private sealed record GoodsFile(IReadOnlyList<CoinGood> Goods);

    private sealed record ItemsFile(IReadOnlyList<CoinItem> Items);
}
