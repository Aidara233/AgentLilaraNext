using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;

namespace ALN.Tests;

/// <summary>
/// Loading a module into this process: the thing "modules can be embedded" actually means. Every test here
/// builds a real package directory on disk and hands it to the real loader, because a plugin that only works
/// when the test fakes the loader is not evidence of anything.
/// </summary>
public sealed class ModulePluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-plugin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // A collectible load context releases the assembly file only once it has actually been collected, so
        // deleting a just-unloaded package needs a nudge and a retry rather than an immediate failure.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public async Task A_dropped_in_plugin_loads_is_visible_and_unloads_cleanly()
    {
        var contributors = new RoundContextContributorRegistry();
        var packageDirectory = WriteSamplePackage("sample.environment");
        await using var loader = CreateLoader(contributors);

        var loaded = await loader.LoadAsync(packageDirectory, ModulePackageKind.Plugin, default);
        Assert.True(loaded.Ok, $"{loaded.Error?.Message} {loaded.Error?.Detail}");
        Assert.Equal("sample.environment", loaded.Value!.Id);
        Assert.Equal(ModulePackageKind.Plugin, loaded.Value.PackageKind);

        // What it did is observable through the host, not through a log line.
        Assert.Contains(contributors.Snapshot(), contributor => contributor.Id == "sample.environment");
        Assert.Contains(loader.Loaded, manifest => manifest.Id == "sample.environment");

        // The contributor it registered actually answers a request.
        var roundContext = new CapturingWriter();
        await contributors.Snapshot().Single(c => c.Id == "sample.environment")
            .ContributeAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), new NullTimeline(), roundContext,
                new NullCommits(), default);
        Assert.Contains(roundContext.Contributions, contribution => contribution.Section == "environment");

        var unloaded = await loader.UnloadAsync("sample.environment", TimeSpan.FromSeconds(5), force: false, default);
        Assert.True(unloaded.Ok, unloaded.Error?.Message);
        Assert.DoesNotContain(contributors.Snapshot(), contributor => contributor.Id == "sample.environment");
    }

    [Fact]
    public async Task A_package_built_against_another_contract_version_is_refused_with_a_reason()
    {
        var packageDirectory = WriteSamplePackage("sample.environment", contracts: "999");
        await using var loader = CreateLoader(new RoundContextContributorRegistry());

        var loaded = await loader.LoadAsync(packageDirectory, ModulePackageKind.Plugin, default);

        Assert.False(loaded.Ok);
        Assert.Contains("999", loaded.Error!.Message, StringComparison.Ordinal);
        Assert.Contains(ModuleContracts.Version, loaded.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_package_is_skipped_and_the_good_one_still_loads()
    {
        var good = WriteSamplePackage("sample.environment");
        var broken = Path.Combine(_root, "modules", "broken");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, "plugin.json"), "{ this is not json");
        await using var loader = CreateLoader(new RoundContextContributorRegistry());

        var outcomes = await DroppedModules.LoadAsync(loader, Path.Combine(_root, "modules"), default);

        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes.Single(outcome => outcome.Directory == good).Loaded);
        var failed = outcomes.Single(outcome => outcome.Directory == broken);
        Assert.False(failed.Loaded);
        Assert.NotNull(failed.Failure);
        // The host keeps going with what did load: a third-party package must not be able to stop startup.
        Assert.Contains(loader.Loaded, manifest => manifest.Id == "sample.environment");
    }

    [Fact]
    public async Task The_same_package_id_cannot_be_loaded_twice()
    {
        var packageDirectory = WriteSamplePackage("sample.environment");
        await using var loader = CreateLoader(new RoundContextContributorRegistry());
        Assert.True((await loader.LoadAsync(packageDirectory, ModulePackageKind.Plugin, default)).Ok);

        var again = await loader.LoadAsync(packageDirectory, ModulePackageKind.Plugin, default);
        Assert.Equal(ErrorCode.Conflict, again.Error!.Code);
    }

    [Fact]
    public async Task A_plugin_can_ship_scripts_and_they_are_read_from_its_folder()
    {
        WriteSamplePluginFolder();
        var catalog = new JsonScriptCatalog(Path.Combine(_root, "program"), _root);

        var packages = await catalog.LoadAsync(default);

        Assert.True(packages.Ok, packages.Error?.Message);
        var script = Assert.Single(packages.Value!, package => package.Manifest.Id == "sample.clock");
        // Dropping the plugin in is the trust decision, so its scripts are trusted — and they say where they came from.
        Assert.Equal(TrustDomain.Trusted, script.Manifest.TrustDomain);
        Assert.Equal("plugin:sample.environment", script.Origin);
    }

    [Fact]
    public async Task A_shipped_script_cannot_claim_to_be_builtin()
    {
        var folder = WriteSamplePluginFolder();
        var manifestPath = Path.Combine(folder, "scripts", "sample.clock", "manifest.json");
        await File.WriteAllTextAsync(manifestPath,
            (await File.ReadAllTextAsync(manifestPath)).Replace("\"trusted\"", "\"builtin\""));
        var catalog = new JsonScriptCatalog(Path.Combine(_root, "program"), _root);

        var packages = await catalog.LoadAsync(default);

        Assert.False(packages.Ok);
        Assert.Equal(ErrorCode.Denied, packages.Error!.Code);
        Assert.Contains("sample.clock", packages.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_scripts_with_one_id_are_refused_with_both_paths()
    {
        WriteSamplePluginFolder();
        // A hand-written script with the same id as the one the plugin ships.
        var trusted = Path.Combine(_root, "scripts", "trusted", "mine");
        Directory.CreateDirectory(trusted);
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "plugins", "sample.environment", "scripts", "sample.clock")))
            File.Copy(file, Path.Combine(trusted, Path.GetFileName(file)));
        var catalog = new JsonScriptCatalog(Path.Combine(_root, "program"), _root);

        var packages = await catalog.LoadAsync(default);

        Assert.Equal(ErrorCode.Conflict, packages.Error!.Code);
        Assert.Contains("sample.clock", packages.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>The plugin folder as it ships: manifest beside the scripts (the assembly is not needed here).</summary>
    private string WriteSamplePluginFolder()
    {
        var source = Path.Combine(RepositoryRoot(), "samples", "plugins", "sample.environment");
        var target = Path.Combine(_root, "plugins", "sample.environment");
        Directory.CreateDirectory(target);
        File.Copy(Path.Combine(source, "plugin.json"), Path.Combine(target, "plugin.json"));
        CopyTree(Path.Combine(source, "scripts"), Path.Combine(target, "scripts"));
        return target;
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    /// <summary>Writes a package directory the way an operator would: plugin.json beside the built assembly.</summary>
    private string WriteSamplePackage(string name, string? contracts = null)
    {
        var directory = Path.Combine(_root, "modules", name);
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "SampleEnvironment.*"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        var manifest = File.ReadAllText(Path.Combine(RepositoryRoot(), "samples", "plugins", name, "plugin.json"));
        if (contracts is not null) manifest = manifest.Replace("\"contracts\": \"1\"", $"\"contracts\": \"{contracts}\"");
        File.WriteAllText(Path.Combine(directory, "plugin.json"), manifest);
        return directory;
    }

    private ModulePackageLoader CreateLoader(IRoundContextContributorRegistry contributors) =>
        new(new ModuleUnloadCoordinator(),
            new ModuleServiceRegistry(new SingleServiceProvider(typeof(IRoundContextContributorRegistry), contributors)),
            new AdapterTypeCatalog());

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ALN.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class SingleServiceProvider(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type requested) => requested == serviceType ? instance : null;
    }

    private sealed class CapturingWriter : IRoundContextWriter
    {
        public List<RoundContextContribution> Contributions { get; } = [];
        public void Add(RoundContextContribution contribution) => Contributions.Add(contribution);
    }

    private sealed class NullTimeline : ISessionTimelineWriter
    {
        public void Add(SessionTimelineContribution contribution) { }
    }

    private sealed class NullCommits : ITurnCommitSink
    {
        public void Add(ITurnCommit commit) { }
    }
}
