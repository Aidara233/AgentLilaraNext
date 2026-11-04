using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class ModuleScaffolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-scaffold-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleScaffolder _scaffolder;

    public ModuleScaffolderTests()
    {
        Directory.CreateDirectory(_root);
        _scaffolder = new ModuleScaffolder(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_declared_submodule_is_written_when_it_is_missing_and_only_then()
    {
        var created = await _scaffolder.EnsureAsync([new ModuleSubmodule("memory.store#main", "memory.store")], default);

        Assert.True(created.Ok);
        var id = Assert.Single(created.Value!.Created);
        Assert.Equal("memory.store#main", id.Value);
        var path = Path.Combine(_root, "modules", "memory.store#main", "module.json");
        Assert.True(File.Exists(path));

        // And it is a file the kernel can read back.
        var scan = await new ModuleManifestStore(_root).ScanAsync(null, default);
        var manifest = Assert.Single(scan.Manifests);
        Assert.Equal("memory.store", manifest.Type);
    }

    [Fact]
    public async Task An_existing_module_file_is_never_overwritten()
    {
        // The whole point: a package that redeclares a submodule on upgrade must not undo somebody's configuration.
        var directory = Path.Combine(_root, "modules", "thing#one");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "module.json");
        await File.WriteAllTextAsync(path, """{ "id": "thing#one", "type": "test.thing", "config": { "window": 40 } }""");

        var created = await _scaffolder.EnsureAsync([new ModuleSubmodule("thing#one", "test.thing")], default);

        Assert.True(created.Ok);
        Assert.Empty(created.Value!.Created);
        Assert.Contains("\"window\": 40", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declaration_that_is_not_a_legal_name_is_reported_and_the_rest_still_written()
    {
        var created = await _scaffolder.EnsureAsync(
            [new ModuleSubmodule("../outside", "memory.store"), new ModuleSubmodule("thing#one", "test.thing")],
            default);

        Assert.True(created.Ok);
        Assert.Equal("thing#one", Assert.Single(created.Value!.Created).Value);
        Assert.Single(created.Value.Issues);
    }
}
