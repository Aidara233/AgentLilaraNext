using System.Text.Json;
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
    public async Task A_directory_whose_type_the_host_builds_is_skipped_instead_of_reported()
    {
        // Every instance has its own module.json too (docs/22 §2), and its type belongs to a manager. Reporting those
        // as "unknown module type" would put one false line per adapter, per channel and per provider into the
        // receipt — a receipt nobody can read is the same as no receipt.
        Write("channel.conversation#chat", """{ "id": "channel.conversation#chat", "type": "channel.conversation" }""");
        Write("mystery", """{ "id": "mystery", "type": "not.a.type" }""");

        var scan = await new ModuleManifestStore(_root).ScanAsync(_types,
            typeId => typeId == "channel.conversation", default);

        Assert.Empty(scan.Manifests);
        var issue = Assert.Single(scan.Issues);
        Assert.Contains("not.a.type", issue.Message, StringComparison.Ordinal);

        // Without the answer, both are unknown — the distinction is the host's, and the scan does not invent it.
        var blind = await new ModuleManifestStore(_root).ScanAsync(_types, default);
        Assert.Equal(2, blind.Issues.Count);
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

    [Fact]
    public async Task A_manifest_the_host_writes_reads_back_with_its_configuration()
    {
        // The chain a module's configuration actually travels: written by the host, scanned by the kernel, parsed by the
        // module. Testing only the last leg let a mismatch between the first two ship — the file said "configJson" (an
        // escaped string) while the scan looked for "config", so the module loaded with an empty configuration and
        // quietly offered nothing.
        var store = new ModuleManifestStore(_root);
        var manifest = new ModuleManifest(new ModuleId("filesystem.local"), "filesystem.local", Enabled: true,
            ConfigJson: """{"scopes":["memory"]}""");
        Assert.True((await store.WriteIfMissingAsync(manifest, default)).Ok);

        var text = await File.ReadAllTextAsync(Path.Combine(_root, "modules", "filesystem.local", "module.json"));
        Assert.Contains("\"config\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("configJson", text, StringComparison.Ordinal);

        var scan = await store.ScanAsync(types: null, default);
        var scanned = Assert.Single(scan.Manifests);
        // Compared as JSON, not as text: the file is written indented, and "the same configuration" is a fact about
        // what it means, not about its whitespace.
        using var config = JsonDocument.Parse(scanned.ConfigJson!);
        Assert.Equal("memory", config.RootElement.GetProperty("scopes")[0].GetString());
    }

    [Fact]
    public async Task A_file_with_the_older_escaped_spelling_still_yields_its_configuration()
    {
        // Files written by the version that produced {"configJson":"…"} exist on disk. Refusing to read them would mean
        // asking somebody to delete their own configuration to get a module working again.
        Write("filesystem.local",
            """{ "id": "filesystem.local", "type": "filesystem.local", "enabled": true, "configJson": "{\"scopes\":[\"memory\"]}" }""");

        var scan = await new ModuleManifestStore(_root).ScanAsync(types: null, default);
        var scanned = Assert.Single(scan.Manifests);
        using var config = JsonDocument.Parse(scanned.ConfigJson!);
        Assert.Equal("memory", config.RootElement.GetProperty("scopes")[0].GetString());
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
