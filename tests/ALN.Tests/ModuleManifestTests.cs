using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class ModuleManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-manifests-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleFactoryRegistry _types = new();

    public ModuleManifestTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "modules"));
        Assert.True(_types.Register("test.thing", manifest => Result<IKernelModule>.Success(new StubModule(manifest.Id))).Ok);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_module_is_read_from_its_own_directory_and_its_config_is_not_parsed()
    {
        Write("thing#one", """
            {
              "id": "thing#one",
              "type": "test.thing",
              "config": { "window": 40, "nested": { "free": "form" } },
              "slaves": [ { "id": "thing#two", "kinds": ["message.accepted"], "requireAll": false } ],
              "dependencies": ["clock.system"]
            }
            """);

        var scan = await new ModuleManifestStore(_root).ScanAsync(_types, default);

        var manifest = Assert.Single(scan.Manifests);
        Assert.Empty(scan.Issues);
        Assert.Equal(new ModuleId("thing#one"), manifest.Id);
        Assert.Equal("test.thing", manifest.Type);
        Assert.True(manifest.Enabled);
        var slave = Assert.Single(manifest.Slaves!);
        Assert.Equal(new ModuleId("thing#two"), slave.Id);
        Assert.Equal(["message.accepted"], slave.Kinds);
        Assert.False(slave.RequireAll);
        Assert.Equal([new ModuleId("clock.system")], manifest.Dependencies);
        // The kernel does not understand "window": it hands the text over and forgets it.
        Assert.Contains("\"window\"", manifest.ConfigJson, StringComparison.Ordinal);
        Assert.Contains("\"free\"", manifest.ConfigJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_enabled_flag_means_enabled()
    {
        Write("thing#one", """{ "id": "thing#one", "type": "test.thing" }""");

        var manifest = Assert.Single((await new ModuleManifestStore(_root).ScanAsync(_types, default)).Manifests);

        Assert.True(manifest.Enabled);
        Assert.Null(manifest.ConfigJson);
        Assert.Empty(manifest.Slaves!);
        Assert.Empty(manifest.Dependencies!);
    }

    [Fact]
    public async Task One_broken_manifest_costs_one_module_and_says_why_for_each()
    {
        Write("thing#ok", """{ "id": "thing#ok", "type": "test.thing" }""");
        Write("thing#noid", """{ "type": "test.thing" }""");
        Write("thing#badjson", """{ "id": "thing#badjson", "type": """);
        Write("thing#unknowntype", """{ "id": "thing#unknowntype", "type": "nobody.implements.this" }""");
        Write("thing#illegal", """{ "id": "thing/../out", "type": "test.thing" }""");
        Write("thing#mismatch", """{ "id": "thing#other", "type": "test.thing" }""");
        Directory.CreateDirectory(Path.Combine(_root, "modules", "empty-dir"));

        var scan = await new ModuleManifestStore(_root).ScanAsync(_types, default);

        // The healthy one still loads: refusing the whole root would cost every other module.
        Assert.Equal(new ModuleId("thing#ok"), Assert.Single(scan.Manifests).Id);
        Assert.Equal(6, scan.Issues.Count);
        Assert.Contains(scan.Issues, issue => issue.Message.Contains("needs both", StringComparison.Ordinal));
        Assert.Contains(scan.Issues, issue => issue.Message.Contains("Not valid JSON", StringComparison.Ordinal));
        Assert.Contains(scan.Issues, issue => issue.Message.Contains("Unknown module type", StringComparison.Ordinal));
        Assert.Contains(scan.Issues, issue => issue.Message.Contains("Illegal module id", StringComparison.Ordinal));
        Assert.Contains(scan.Issues, issue => issue.Message.Contains("does not match its directory", StringComparison.Ordinal));
        Assert.Contains(scan.Issues, issue => issue.Message.Contains("Not a module", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_directory_whose_case_differs_from_its_id_is_refused_rather_than_guessed()
    {
        // Ids are compared case-insensitively (the file systems are), so a directory spelled differently from the
        // id inside it is the one duplicate shape this platform can produce: both files claim the same module.
        Write("thing#one", """{ "id": "thing#one", "type": "test.thing" }""");
        await File.WriteAllTextAsync(Path.Combine(_root, "modules", "thing#one", "module.json"),
            """{ "id": "thing#ONE", "type": "test.thing" }""");

        var scan = await new ModuleManifestStore(_root).ScanAsync(_types, default);

        // One of the two spellings wins, the other is reported: silence here would mean a module that exists but
        // answers to nobody.
        var issue = Assert.Single(scan.Issues);
        Assert.Contains("does not match its directory", issue.Message, StringComparison.Ordinal);
        Assert.Empty(scan.Manifests);
    }

    [Fact]
    public async Task An_empty_or_missing_modules_directory_is_not_an_error()
    {
        var empty = await new ModuleManifestStore(_root).ScanAsync(_types, default);
        Assert.Empty(empty.Manifests);
        Assert.Empty(empty.Issues);

        var missing = await new ModuleManifestStore(Path.Combine(_root, "nothing-here")).ScanAsync(_types, default);
        Assert.Empty(missing.Manifests);
        Assert.Empty(missing.Issues);
    }

    [Fact]
    public async Task A_factory_that_builds_another_name_is_refused()
    {
        Write("thing#one", """{ "id": "thing#one", "type": "test.thing" }""");
        var scan = await new ModuleManifestStore(_root).ScanAsync(_types, default);
        var manifest = Assert.Single(scan.Manifests);

        var registry = new ModuleFactoryRegistry();
        Assert.True(registry.Register("test.thing", _ => Result<IKernelModule>.Success(new StubModule(new ModuleId("something.else")))).Ok);
        var created = registry.Create(manifest);

        Assert.False(created.Ok);
        Assert.Contains("instead of", created.Error!.Message, StringComparison.Ordinal);
    }

    private void Write(string id, string json)
    {
        var directory = Path.Combine(_root, "modules", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "module.json"), json);
    }

    private sealed class StubModule(ModuleId id) : IKernelModule
    {
        public ModuleId Id { get; } = id;
        public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }
}
