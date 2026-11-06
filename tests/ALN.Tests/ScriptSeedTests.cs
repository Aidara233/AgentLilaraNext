using ALN.Contracts;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// Seeding is the only moment the host writes a script, and it happens once per script. Two properties make it
/// safe to do that on every start: it never rewrites what is already there, and what it writes is what the
/// catalogue accepts — a copy the catalogue refuses would be worse than no copy, because the host exits on it.
/// </summary>
public sealed class ScriptSeedTests : IDisposable
{
    private const string IntakeManifest = """
    {
      "id": "adapter.channel-intake", "version": 3, "targetType": "adapter", "mount": "default",
      "events": ["adapter.envelope"], "entryPoint": "onEvent", "capabilities": ["channel.accept"],
      "trustDomain": "builtin", "maxStatements": 10000, "timeoutMilliseconds": 1000,
      "memoryLimitBytes": 16777216
    }
    """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-script-seed-" + Guid.NewGuid().ToString("N"));

    public ScriptSeedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string ProgramRoot => Path.Combine(_root, "program");
    private string DataRoot => Path.Combine(_root, "data");
    private string TrustedCopy(string id) => Path.Combine(DataRoot, "scripts", "trusted", id);

    private void Ship(string id, string manifest, string body)
    {
        var folder = Path.Combine(ProgramRoot, "resources", "scripts", "builtin", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(folder, "main.js"), body);
    }

    private void WriteUserScript(string folder, string id, string body)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), $$"""
        {
          "id": "{{id}}", "version": 1, "targetType": "channel", "mount": "manual",
          "events": ["channel.message.accepted"], "entryPoint": "onEvent",
          "capabilities": ["channel.bound-send"], "trustDomain": "trusted"
        }
        """);
        File.WriteAllText(Path.Combine(folder, "main.js"), body);
    }

    [Fact]
    public async Task A_shipped_script_is_copied_out_as_a_trusted_script()
    {
        Ship("adapter.channel-intake", IntakeManifest, "async function onEvent() { }");

        var report = await new ScriptSeed(ProgramRoot, DataRoot).CopyMissingAsync(default);

        Assert.Equal(["adapter.channel-intake"], report.Copied);
        Assert.Empty(report.Kept);
        Assert.Empty(report.Failed);
        // The body is the shipped body, byte for byte.
        Assert.Equal("async function onEvent() { }",
            await File.ReadAllTextAsync(Path.Combine(TrustedCopy("adapter.channel-intake"), "main.js")));
        // The manifest is not: "builtin" means "shipped, and nobody here may change it", which stops being true
        // the moment a person owns the copy — and a manifest claiming otherwise is what the catalogue refuses.
        var manifest = await File.ReadAllTextAsync(Path.Combine(TrustedCopy("adapter.channel-intake"), "manifest.json"));
        Assert.Contains("\"trusted\"", manifest);
        Assert.DoesNotContain("builtin", manifest);
        // Everything else survives the rewrite: the copy is the same script, not a stub.
        Assert.Contains("\"channel.accept\"", manifest);
        Assert.Contains("\"adapter.envelope\"", manifest);
    }

    [Fact]
    public async Task A_second_start_leaves_an_edited_copy_alone_even_when_the_shipped_one_moved_on()
    {
        Ship("adapter.channel-intake", IntakeManifest, "async function onEvent() { }");
        var seed = new ScriptSeed(ProgramRoot, DataRoot);
        await seed.CopyMissingAsync(default);

        var mine = Path.Combine(TrustedCopy("adapter.channel-intake"), "main.js");
        await File.WriteAllTextAsync(mine, "async function onEvent() { /* mine */ }");
        // An upgrade ships a new version and a new body. The copy must still win.
        Ship("adapter.channel-intake", IntakeManifest.Replace("\"version\": 3", "\"version\": 4"), "async function onEvent() { /* newer */ }");

        var second = await seed.CopyMissingAsync(default);

        Assert.Empty(second.Copied);
        Assert.Equal(["adapter.channel-intake"], second.Kept);
        Assert.Contains("mine", await File.ReadAllTextAsync(mine));
    }

    [Fact]
    public async Task The_copied_script_replaces_the_shipped_one_instead_of_colliding_with_it()
    {
        Ship("adapter.channel-intake", IntakeManifest, "shipped();");
        await new ScriptSeed(ProgramRoot, DataRoot).CopyMissingAsync(default);
        // The operator edits the copy. "The copy is the one that runs" has to mean exactly this.
        await File.WriteAllTextAsync(Path.Combine(TrustedCopy("adapter.channel-intake"), "main.js"), "edited();");

        var catalogue = await new JsonScriptCatalog(ProgramRoot, DataRoot).LoadAsync(default);

        Assert.True(catalogue.Ok, catalogue.Error?.Message);
        var intake = Assert.Single(catalogue.Value!, package => package.Manifest.Id == "adapter.channel-intake");
        Assert.Equal("edited();", intake.Source);
        Assert.Equal("user", intake.Origin);
        Assert.Equal(TrustDomain.Trusted, intake.Manifest.TrustDomain);
    }

    [Fact]
    public async Task Two_operator_copies_of_one_id_are_still_refused()
    {
        // Nothing was seeded: both copies are the operator's own, and two answers to one name is the ambiguity
        // the catalogue exists to refuse rather than resolve.
        WriteUserScript(Path.Combine(DataRoot, "scripts", "trusted", "a", "chat"), "chat", "a();");
        WriteUserScript(Path.Combine(DataRoot, "scripts", "trusted", "b", "chat"), "chat", "b();");

        var catalog = new JsonScriptCatalog(ProgramRoot, DataRoot);
        var catalogue = await catalog.LoadAsync(default);

        // Two operator copies of one name: the first wins, the second is reported. Not a catalogue failure — a host
        // that will not start because a draft was copied twice is worse than one that says which copy it used.
        Assert.True(catalogue.Ok, catalogue.Error?.Message);
        Assert.Single(catalogue.Value!, package => package.Manifest.Id == "chat");
        Assert.Single(catalog.LastIssues);
    }

    [Fact]
    public async Task A_plugin_cannot_take_a_name_a_shipped_script_already_uses()
    {
        Ship("adapter.channel-intake", IntakeManifest, "shipped();");
        WriteUserScript(Path.Combine(DataRoot, "plugins", "someone.plugin", "scripts", "adapter.channel-intake"),
            "adapter.channel-intake", "plugin();");

        var catalog = new JsonScriptCatalog(ProgramRoot, DataRoot);
        var catalogue = await catalog.LoadAsync(default);

        // Only the operator's own tree may replace a shipped script: dropping a plugin in is a trust decision about
        // code, not a licence to take over a name the shipped behaviour answers to. The shipped one keeps the name and
        // the plugin's copy is reported instead of being loaded over it.
        Assert.True(catalogue.Ok, catalogue.Error?.Message);
        var intake = Assert.Single(catalogue.Value!, package => package.Manifest.Id == "adapter.channel-intake");
        Assert.Equal("bundled", intake.Origin);
        Assert.Contains(catalog.LastIssues, issue => issue.Reason.Contains("Duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_shipped_folder_is_not_a_failure()
    {
        var report = await new ScriptSeed(ProgramRoot, DataRoot).CopyMissingAsync(default);

        Assert.Empty(report.Copied);
        Assert.Empty(report.Failed);
        Assert.False(Directory.Exists(Path.Combine(DataRoot, "scripts")));
    }
}
