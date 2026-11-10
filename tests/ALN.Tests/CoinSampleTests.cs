using System.Text.Json;
using ALN.Contracts;

namespace ALN.Tests;

/// <summary>
/// The sample market has to be self-consistent, or "copy this folder and you have a shop" is a lie: a good whose
/// script is missing is dropped from the shelf, so one stale id in goods.json silently shrinks the shelf to nothing.
///
/// This is the check the loader cannot do for us — it validates against the graph, and here there is no graph. What it
/// can do is check what a person would check: every id in the two catalogue files has a folder, and every manifest
/// says the things the loader will look for.
/// </summary>
public sealed class CoinSampleTests
{
    private static string Samples()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ALN.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var market = Path.Combine(directory!.FullName, "samples", "market");
        Assert.True(Directory.Exists(market), $"no sample market at {market}");
        return market;
    }

    private static JsonElement Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    [Fact]
    public void Every_good_and_item_points_at_a_script_that_is_here()
    {
        var market = Samples();
        var scripts = Directory.EnumerateDirectories(Path.Combine(market, "scripts"))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        var goods = Read(Path.Combine(market, "goods.json")).GetProperty("goods").EnumerateArray()
            .Select(good => (Id: good.GetProperty("id").GetString(), Script: good.GetProperty("script").GetString()))
            .ToList();
        var items = Read(Path.Combine(market, "items.json")).GetProperty("items").EnumerateArray()
            .Select(item => (Id: item.GetProperty("id").GetString(), Script: item.GetProperty("use").GetString()))
            .ToList();

        Assert.NotEmpty(goods);
        Assert.NotEmpty(items);
        foreach (var (id, script) in goods.Concat(items))
            Assert.True(scripts.Contains(script!), $"{id} points at '{script}', and there is no such folder under scripts/");
    }

    [Fact]
    public void Every_manifest_says_what_the_loader_looks_for()
    {
        var market = Samples();
        foreach (var folder in Directory.EnumerateDirectories(Path.Combine(market, "scripts")).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(folder);
            var manifest = Read(Path.Combine(folder, "manifest.json"));

            // The id has to match the folder's script name — the catalogue keys on the manifest, a person reads the
            // folder, and a mismatch is how "I copied it but it sells nothing" happens.
            Assert.Equal(name, manifest.GetProperty("id").GetString());
            // entryPoint must be a function or the loader refuses the package, even for a script that never answers
            // events; call is the one the purse invokes.
            Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("entryPoint").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("call").GetString()));
            Assert.True(File.Exists(Path.Combine(folder, "main.js")), $"{name} has no main.js");
        }
    }
}
