using System.Text.Json;
using ALN.Contracts;
using ALN.Graph;

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
    public async Task The_loader_reads_the_sample_manifests()
    {
        // The one thing the other tests cannot answer: are the manifest *field names* right? A script is only sold if
        // the loader resolves it into a callable package, and that resolution is where "call", "trustDomain" and
        // "mount" have to mean something.
        var root = Directory.CreateTempSubdirectory("coin-samples-").FullName;
        try
        {
            var programRoot = Path.Combine(root, "program");
            var dataRoot = Path.Combine(root, "data");
            var trusted = Path.Combine(dataRoot, "scripts", "trusted");
            Directory.CreateDirectory(programRoot);
            Copy(Path.Combine(Samples(), "scripts"), trusted);

            var catalog = new JsonScriptCatalog(programRoot, dataRoot);
            var loaded = await catalog.LoadAsync(default);

            Assert.True(loaded.Ok, loaded.Error?.Message);
            var box = Assert.Single(loaded.Value!, package => package.Manifest.Id == "shop.box");
            Assert.Equal("box", box.Manifest.Call);
            Assert.Equal(AccessLevel.Full, box.Level);
            // The catalogue resolves files, not tool definitions: Tool is null here and is filled in later, when the
            // graph loads the package and asks the script describe(). That is why every sample script has one —
            // GraphManager.InvokeToolAsync refuses a package whose Tool is still null, so without describe() the shelf
            // would look fine and every purchase would fail.
            Assert.Null(box.Tool);
            Assert.Equal("box", box.Manifest.Call);
            // scripts/trusted is a person's folder, so the package is Full — that is what lets its script credit the
            // purse through coin.credit.
            Assert.Equal(AccessLevel.Full, box.Level);
            // Every sample script must resolve, not just the box: a manifest the loader drops is a product that sells
            // nothing, and the shelf would silently shrink. Counted rather than hard-coded, so adding a script to the
            // samples does not quietly turn this into a test of an old number.
            Assert.Equal(
                Directory.EnumerateDirectories(Path.Combine(Samples(), "scripts")).Count(),
                loaded.Value!.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
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
            // WhyRejected refuses a script that declares no event at all, even one whose only job is to settle a
            // purchase — so "events": [] is not a valid manifest, it is a script that never loads.
            Assert.NotEmpty(manifest.GetProperty("events").EnumerateArray());
            Assert.True(File.Exists(Path.Combine(folder, "main.js")), $"{name} has no main.js");
        }
    }
}
