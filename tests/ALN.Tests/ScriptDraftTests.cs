using ALN.Contracts;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The draft folder is where a script is written before it runs (docs/25). Three properties carry the whole design: the
/// model can write there, it cannot write into the folder the catalogue loads, and a broken draft cannot stop the host.
/// </summary>
public sealed class ScriptDraftTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-script-draft-" + Guid.NewGuid().ToString("N"));

    public ScriptDraftTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string ProgramRoot => Path.Combine(_root, "program");
    private string DataRoot => Path.Combine(_root, "data");

    private LocalFileSystemAccess Access() => new(DataRoot, [FileScopes.Memory, FileScopes.ScriptsDraft]);

    [Fact]
    public async Task The_model_can_write_a_draft_and_cannot_write_the_folder_that_runs()
    {
        var access = Access();

        var draft = await access.WriteAsync(FileScopes.ScriptsDraft, "say-hello/manifest.json", ScriptSeed.ExampleManifest, default);
        Assert.True(draft.Ok, draft.Error?.Message);
        Assert.True(File.Exists(Path.Combine(DataRoot, "scripts", "draft", "say-hello", "manifest.json")));

        // The same content one directory over: refused, because that folder is not a scope this caller was given.
        var live = await access.WriteAsync("scripts/trusted", "say-hello/manifest.json", ScriptSeed.ExampleManifest, default);
        Assert.False(live.Ok);
        Assert.Equal(ErrorCode.Denied, live.Error!.Code);

        // And a draft cannot climb out of its own scope.
        var escape = await access.WriteAsync(FileScopes.ScriptsDraft, "../trusted/say-hello/manifest.json", "{}", default);
        Assert.False(escape.Ok);
        Assert.Equal(ErrorCode.Invalid, escape.Error!.Code);
    }

    [Fact]
    public async Task A_draft_written_with_the_shipped_tools_is_what_the_catalogue_loads()
    {
        var access = Access();
        Assert.True((await access.WriteAsync(FileScopes.ScriptsDraft, "say-hello/manifest.json", ScriptSeed.ExampleManifest, default)).Ok);
        Assert.True((await access.WriteAsync(FileScopes.ScriptsDraft, "say-hello/main.js", ScriptSeed.ExampleSource, default)).Ok);

        var catalog = new JsonScriptCatalog(ProgramRoot, DataRoot);
        var loaded = await catalog.LoadAsync(default);

        // The example in the guide is the example the host accepts: a guide that has drifted teaches a shape that is
        // refused, which is worse than having no guide.
        Assert.True(loaded.Ok, loaded.Error?.Message);
        var script = Assert.Single(loaded.Value!, package => package.Manifest.Id == "channel.say-hello");
        Assert.Equal("draft", script.Origin);
        Assert.Equal(TrustDomain.Trusted, script.Manifest.TrustDomain);
        Assert.Empty(catalog.LastIssues);
    }

    [Fact]
    public async Task The_guide_is_written_where_the_same_tools_can_read_it_and_kept_current()
    {
        var seed = new ScriptSeed(ProgramRoot, DataRoot);

        Assert.True(await seed.EnsureDraftGuideAsync(default));
        // Nothing changed, so nothing is written: a start that rewrote it every time would make every launch a
        // potential change of what the reader is taught.
        Assert.False(await seed.EnsureDraftGuideAsync(default));

        var guide = await Access().ReadAsync(FileScopes.ScriptsDraft, ScriptSeed.GuideFileName, default);
        Assert.True(guide.Ok, guide.Error?.Message);
        Assert.NotNull(guide.Value);
        Assert.Contains("manifest.json", guide.Value!.Text, StringComparison.Ordinal);
        // It is a guide a model can act on, not a stub: the capability table and the tool names are what keep it from
        // guessing, and the tools are the names it can actually call.
        Assert.Contains("channel.accept", guide.Value.Text, StringComparison.Ordinal);
        Assert.Contains("scripts_reload", guide.Value.Text, StringComparison.Ordinal);
        Assert.Contains("scripts_mount", guide.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_guide_from_an_older_build_is_replaced_and_the_old_one_is_kept()
    {
        // The shape every existing deployment is in: a guide written by a build that did not have today's features.
        // Left alone it teaches a program that no longer exists, which is worse than teaching nothing.
        var draft = Path.Combine(DataRoot, "scripts", "draft");
        Directory.CreateDirectory(draft);
        var path = Path.Combine(draft, ScriptSeed.GuideFileName);
        await File.WriteAllTextAsync(path, "an older guide, with no ctx.actions in it\n");

        var seed = new ScriptSeed(ProgramRoot, DataRoot);
        Assert.True(await seed.EnsureDraftGuideAsync(default));

        var current = await File.ReadAllTextAsync(path);
        Assert.Equal(ScriptSeed.DraftGuide, current);
        Assert.Contains("ctx.actions", current, StringComparison.Ordinal);

        // Nothing is destroyed to keep it true: the replaced one is one file away.
        var previous = await File.ReadAllTextAsync(Path.Combine(draft, ScriptSeed.GuidePreviousFileName));
        Assert.Contains("an older guide", previous, StringComparison.Ordinal);

        // And a second start leaves both alone.
        Assert.False(await seed.EnsureDraftGuideAsync(default));
        Assert.Equal(current, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_script_in_the_live_folder_is_loaded_as_live()
    {
        var folder = Path.Combine(DataRoot, "scripts", "live", "say-hello");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), ScriptSeed.ExampleManifest);
        await File.WriteAllTextAsync(Path.Combine(folder, "main.js"), ScriptSeed.ExampleSource);

        var loaded = await new JsonScriptCatalog(ProgramRoot, DataRoot).LoadAsync(default);

        Assert.True(loaded.Ok, loaded.Error?.Message);
        var script = Assert.Single(loaded.Value!, package => package.Manifest.Id == "channel.say-hello");
        // The folder is the answer to "is it in effect", so loading has to keep saying which one it was.
        Assert.Equal("live", script.Origin);
    }

    [Fact]
    public async Task A_broken_draft_is_reported_and_does_not_stop_the_load()
    {
        Directory.CreateDirectory(Path.Combine(DataRoot, "scripts", "draft", "half"));
        // A folder with a manifest but no id.
        var noId = Path.Combine(DataRoot, "scripts", "draft", "no-id");
        Directory.CreateDirectory(noId);
        await File.WriteAllTextAsync(Path.Combine(noId, "manifest.json"), """{ "version": 1 }""");
        await File.WriteAllTextAsync(Path.Combine(noId, "main.js"), "async function onEvent() {}");
        // Right shape, wrong trust domain: a person's copy claiming to be shipped.
        var pretending = Path.Combine(DataRoot, "scripts", "draft", "pretending");
        Directory.CreateDirectory(pretending);
        await File.WriteAllTextAsync(Path.Combine(pretending, "manifest.json"),
            ScriptSeed.ExampleManifest.Replace("\"trusted\"", "\"builtin\""));
        await File.WriteAllTextAsync(Path.Combine(pretending, "main.js"), ScriptSeed.ExampleSource);

        var catalog = new JsonScriptCatalog(ProgramRoot, DataRoot);
        var loaded = await catalog.LoadAsync(default);

        // Loading what it can is the point: a host that will not start because a draft is half-written is a host that a
        // draft can stop.
        Assert.True(loaded.Ok, loaded.Error?.Message);
        Assert.Empty(loaded.Value!);
        Assert.Equal(2, catalog.LastIssues.Count);
        Assert.Contains(catalog.LastIssues, issue => issue.Reason.Contains("needs an \"id\"", StringComparison.Ordinal));
        Assert.Contains(catalog.LastIssues, issue => issue.Reason.Contains("trustDomain", StringComparison.Ordinal));
    }
}
