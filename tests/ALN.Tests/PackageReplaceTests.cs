using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Graph;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// A package that has versions, and what switching between them means.
///
/// The layout is the part with the least obvious behaviour, so it is tested first and directly: a folder may hold one
/// version flat, or several beside a pointer, and the host must not guess when guessing would quietly run last week's
/// code. The switch itself is tested through the real loader, the real assembly load contexts and a real control plane,
/// because "the module changed" and "the behaviour changed" are different claims and only the second one matters.
/// </summary>
public sealed class PackageReplaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-replace-" + Guid.NewGuid().ToString("N"));

    private string PluginsRoot => Path.Combine(_root, "plugins");
    private string PackageRoot => Path.Combine(PluginsRoot, VersionedPluginFixture.PackageId);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // A collectible load context releases the assembly file only once it has actually been collected, so deleting a
        // just-unloaded package needs a nudge and a retry rather than an immediate failure.
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

    // ---- the layout, on its own -------------------------------------------------------------------------------

    [Fact]
    public void A_flat_folder_is_one_version_and_needs_no_pointer()
    {
        var root = WriteFlatPackage("1.0.0");

        var resolved = ModulePackageLayout.Resolve(root);

        Assert.True(resolved.Ok, resolved.Error?.Message);
        Assert.True(resolved.Value!.Legacy);
        Assert.Equal("1.0.0", resolved.Value.Version);
        Assert.Equal(Path.GetFullPath(root), resolved.Value.ActiveDirectory);
    }

    [Fact]
    public void One_version_and_no_pointer_is_that_version()
    {
        // "Drop it in and it runs" must not depend on a file the person has no reason to know about. With exactly one
        // candidate there is nothing to choose.
        WriteVersion("1.0.0");

        var resolved = ModulePackageLayout.Resolve(PackageRoot);

        Assert.True(resolved.Ok, resolved.Error?.Message);
        Assert.Equal("1.0.0", resolved.Value!.Version);
        Assert.False(resolved.Value.Legacy);
        Assert.EndsWith(Path.Combine("1.0.0"), resolved.Value.ActiveDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_versions_and_no_pointer_is_refused_and_names_both()
    {
        WriteVersion("1.0.0");
        WriteVersion("2.0.0");

        var resolved = ModulePackageLayout.Resolve(PackageRoot);

        Assert.False(resolved.Ok);
        // Both candidates in the message: "several versions" without saying which ones is a sentence with no fix in it.
        Assert.Contains("1.0.0", resolved.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("2.0.0", resolved.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pointer_decides_which_version_in_force()
    {
        WriteVersion("1.0.0");
        WriteVersion("2.0.0");
        Assert.True(ModulePackageLayout.WriteActive(PackageRoot, "2.0.0").Ok);

        var resolved = ModulePackageLayout.Resolve(PackageRoot);

        Assert.True(resolved.Ok, resolved.Error?.Message);
        Assert.Equal("2.0.0", resolved.Value!.Version);
        Assert.Equal("2.0.0", ModulePackageLayout.ReadActive(PackageRoot).Value);
    }

    [Fact]
    public void A_pointer_naming_a_version_that_is_not_there_is_refused()
    {
        WriteVersion("1.0.0");
        Assert.True(ModulePackageLayout.WriteActive(PackageRoot, "1.0.0").Ok);
        Directory.Delete(Path.Combine(PackageRoot, "1.0.0"), recursive: true);

        var resolved = ModulePackageLayout.Resolve(PackageRoot);

        Assert.False(resolved.Ok);
        Assert.Contains("1.0.0", resolved.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_directory_whose_manifest_disagrees_with_its_name_is_refused()
    {
        // active.json points at a directory name; plugin.json is what the code says it is. Two answers to one question
        // is how a switch loads something nobody asked for.
        WriteVersion("1.0.0", declaredVersion: "1.0.1");

        var resolved = ModulePackageLayout.Resolve(PackageRoot);

        Assert.False(resolved.Ok);
        Assert.Contains("1.0.1", resolved.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_that_disagrees_with_the_package_id_is_refused()
    {
        // The folder name is what scripts and audit records point at, so it is not decoration.
        var root = WriteFlatPackage("1.0.0", folder: "some.other.name");

        var resolved = ModulePackageLayout.Resolve(root);

        Assert.False(resolved.Ok);
        Assert.Contains(VersionedPluginFixture.PackageId, resolved.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flat_manifest_beside_a_pointer_says_both_are_there()
    {
        WriteFlatPackage("1.0.0");
        WriteVersion("2.0.0");

        var resolved = ModulePackageLayout.Resolve(PackageRoot);

        Assert.False(resolved.Ok);
        Assert.Contains(ModulePackageLayout.ActiveFileName, resolved.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_package_folder_old_manifest_name_is_reported_as_a_rename()
    {
        Directory.CreateDirectory(PackageRoot);
        File.WriteAllText(Path.Combine(PackageRoot, "module.json"), "{}");

        var resolved = ModulePackageLayout.Resolve(PackageRoot);

        Assert.False(resolved.Ok);
        Assert.Contains("plugin.json", resolved.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("Rename", resolved.Error.Message, StringComparison.Ordinal);
    }

    // ---- switching a running package --------------------------------------------------------------------------

    [Fact]
    public async Task A_switch_runs_the_new_version_and_writes_down_which_one_is_in_force()
    {
        WriteVersion("1.0.0");
        WriteVersion("2.0.0");
        Assert.True(ModulePackageLayout.WriteActive(PackageRoot, "1.0.0").Ok);
        var extensions = new SessionExtensionRegistry();
        var plane = new ControlPlane(new RecordingAudit(), new RecordingSignals());
        await using var loader = CreateLoader(extensions, plane);
        var dropped = await DroppedModules.LoadAsync(loader, PluginsRoot, default);
        var loaded = Assert.Single(dropped, outcome => outcome.Loaded);
        Assert.Equal("1.0.0", loaded.Manifest!.Version);
        Assert.Equal("one", Answer(extensions));

        var report = await loader.ReplaceAsync(VersionedPluginFixture.PackageId, "2.0.0", TimeSpan.FromSeconds(5),
            force: false, default);

        Assert.True(report.Ok, $"{report.Error?.Message} {report.Error?.Detail}");
        Assert.Equal("1.0.0", report.Value!.PreviousVersion);
        Assert.Equal("2.0.0", report.Value.CurrentVersion);
        // The code that runs is the new one, and it says so.
        Assert.Equal("two", Answer(extensions));
        // The directory is recorded, so "which build is this" is answerable without reading the pointer.
        var running = Assert.Single(loader.LoadedPackages);
        Assert.EndsWith(Path.Combine("2.0.0"), running.Directory, StringComparison.Ordinal);
        // The pointer follows the switch — but only because the switch worked. See the next test.
        Assert.Equal("2.0.0", ModulePackageLayout.ReadActive(PackageRoot).Value);

        // Both versions claim one verb id. If the host had not taken the first one back, this registration would have
        // conflicted and the switch would have failed instead.
        Assert.Single(plane.Catalog, descriptor => descriptor.Id == VersionedPluginFixture.Verb);
        var invoked = await plane.InvokeAsync(Actor.System, VersionedPluginFixture.Verb, default, default);
        Assert.True(invoked.Ok, invoked.Error?.Message);
        Assert.Equal("2.0.0", invoked.Value.GetProperty("version").GetString());
    }

    [Fact]
    public async Task A_version_that_refuses_to_start_leaves_the_old_one_running_and_the_pointer_alone()
    {
        WriteVersion("2.0.0");
        WriteVersion("3.0.0");
        Assert.True(ModulePackageLayout.WriteActive(PackageRoot, "2.0.0").Ok);
        var extensions = new SessionExtensionRegistry();
        var plane = new ControlPlane(new RecordingAudit(), new RecordingSignals());
        await using var loader = CreateLoader(extensions, plane);
        Assert.True((await loader.LoadAsync(Path.Combine(PackageRoot, "2.0.0"), ModulePackageKind.Plugin, default)).Ok);
        Assert.Equal("two", Answer(extensions));

        var report = await loader.ReplaceAsync(VersionedPluginFixture.PackageId, "3.0.0", TimeSpan.FromSeconds(5),
            force: false, default);

        Assert.False(report.Ok);
        // The old version is still what runs, and it still answers.
        Assert.Equal("2.0.0", Assert.Single(loader.LoadedPackages).Version);
        Assert.Equal("two", Answer(extensions));
        // A pointer written before the switch would have made the next start boot the version that just failed.
        Assert.Equal("2.0.0", ModulePackageLayout.ReadActive(PackageRoot).Value);
        Assert.DoesNotContain(plane.Catalog, descriptor => descriptor.Id == VersionedPluginFixture.Verb &&
            descriptor.Summary.Contains("3.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Asking_for_the_version_that_is_already_running_changes_nothing()
    {
        WriteVersion("1.0.0");
        WriteVersion("2.0.0");
        Assert.True(ModulePackageLayout.WriteActive(PackageRoot, "2.0.0").Ok);
        var extensions = new SessionExtensionRegistry();
        var plane = new ControlPlane(new RecordingAudit(), new RecordingSignals());
        await using var loader = CreateLoader(extensions, plane);
        Assert.True((await loader.LoadAsync(Path.Combine(PackageRoot, "2.0.0"), ModulePackageKind.Plugin, default)).Ok);

        var switched = await loader.ReplaceAsync(VersionedPluginFixture.PackageId, "2.0.0", TimeSpan.FromSeconds(5),
            force: false, default);

        // The loader refuses to "replace" a package with itself, and that is the right answer at this level: there is
        // nothing to switch. The action turns it into an idempotent success before it gets here.
        Assert.False(switched.Ok);
        Assert.Equal("two", Answer(extensions));
    }

    [Fact]
    public async Task Unloading_a_package_takes_back_the_verb_it_registered()
    {
        WriteVersion("1.0.0");
        var extensions = new SessionExtensionRegistry();
        var plane = new ControlPlane(new RecordingAudit(), new RecordingSignals());
        await using var loader = CreateLoader(extensions, plane);
        Assert.True((await loader.LoadAsync(Path.Combine(PackageRoot, "1.0.0"), ModulePackageKind.Plugin, default)).Ok);
        Assert.Contains(plane.Catalog, descriptor => descriptor.Id == VersionedPluginFixture.Verb);

        var unloaded = await loader.UnloadAsync(VersionedPluginFixture.PackageId, TimeSpan.FromSeconds(5), force: false, default);

        Assert.True(unloaded.Ok, unloaded.Error?.Message);
        // A verb left behind is a handler captured over an assembly load context that can then never be released, which
        // is the whole reason a replaced package stays pinned.
        Assert.DoesNotContain(plane.Catalog, descriptor => descriptor.Id == VersionedPluginFixture.Verb);
        Assert.Empty(extensions.Floating);
    }

    [Fact]
    public async Task A_replacement_whose_dependent_cannot_come_back_takes_the_new_version_out_again()
    {
        // The hard case: the new version loaded fine, and something that depends on it did not come back. Reporting
        // "failed" while the new code answers requests is the one outcome that is worse than either end of the switch.
        WriteVersion("1.0.0");
        WriteVersion("2.0.0");
        Assert.True(ModulePackageLayout.WriteActive(PackageRoot, "1.0.0").Ok);
        WritePackage(Path.Combine(PluginsRoot, DependentPluginFixture.PackageId), "1.0.0", "1.0.0",
            typeof(DependentPluginFixture).FullName!, packageId: DependentPluginFixture.PackageId,
            dependency: VersionedPluginFixture.PackageId);
        var extensions = new SessionExtensionRegistry();
        var plane = new ControlPlane(new RecordingAudit(), new RecordingSignals());
        await using var loader = CreateLoader(extensions, plane);
        var dropped = await DroppedModules.LoadAsync(loader, PluginsRoot, default);
        Assert.Equal(2, dropped.Count(outcome => outcome.Loaded));

        Environment.SetEnvironmentVariable(DependentPluginFixture.RefuseVariable, "1");
        try
        {
            var report = await loader.ReplaceAsync(VersionedPluginFixture.PackageId, "2.0.0", TimeSpan.FromSeconds(5),
                force: false, default);

            Assert.False(report.Ok);
            // Said out loud, because a half-cleaned rollback that reported nothing would look like a clean one.
            Assert.Contains("new packages removed", report.Error!.Detail ?? "", StringComparison.Ordinal);
            // The old version is what runs, and the dependent that could not come back is honestly absent rather than
            // present-but-broken.
            Assert.Equal("1.0.0", loader.LoadedPackages.Single(package => package.Id == VersionedPluginFixture.PackageId).Version);
            Assert.DoesNotContain(loader.LoadedPackages, package => package.Id == DependentPluginFixture.PackageId);
            Assert.Equal("one", Answer(extensions));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DependentPluginFixture.RefuseVariable, null);
        }
    }

    [Fact]
    public async Task A_package_that_gave_everything_back_can_be_collected()
    {
        WriteVersion("1.0.0");
        var extensions = new SessionExtensionRegistry();
        var plane = new ControlPlane(new RecordingAudit(), new RecordingSignals());
        await using var loader = CreateLoader(extensions, plane);
        Assert.True((await loader.LoadAsync(Path.Combine(PackageRoot, "1.0.0"), ModulePackageKind.Plugin, default)).Ok);
        var probe = Watch(extensions);

        var unloaded = await loader.UnloadAsync(VersionedPluginFixture.PackageId, TimeSpan.FromSeconds(5), force: false, default);

        Assert.True(unloaded.Ok, unloaded.Error?.Message);
        // The claim behind "give the verb back": nothing the host kept still reaches into the package, so its load
        // context can actually go. A verb left in the action table would keep the assembly alive, and with it the
        // locked file that makes the next version's directory impossible to clean up.
        Assert.True(await CollectedAsync(probe),
            "The unloaded package is still reachable: something the host kept is holding on to it.");
    }

    // ---- the scripts that came with the package ----------------------------------------------------------------

    [Fact]
    public async Task The_script_catalogue_reads_only_the_version_in_force()
    {
        WriteVersion("1.0.0");
        WriteVersion("2.0.0");
        WriteScript("1.0.0", "greeter", "one");
        WriteScript("2.0.0", "greeter", "two");
        Assert.True(ModulePackageLayout.WriteActive(PackageRoot, "2.0.0").Ok);
        var catalog = new JsonScriptCatalog(Path.Combine(_root, "program"), _root);

        var packages = await catalog.LoadAsync(default);

        Assert.True(packages.Ok, packages.Error?.Message);
        // One id, one body: scanning both versions would put two copies in the catalogue and let directory order decide
        // which one runs.
        var script = Assert.Single(packages.Value!, package => package.Manifest.Id == "greeter");
        Assert.Contains("two", script.Source, StringComparison.Ordinal);
        // The library is the package's id, not the folder the script happens to sit in: a switch must not rename it.
        Assert.Equal("plugin:" + VersionedPluginFixture.PackageId, script.Origin);
    }

    // ---- fixtures ----------------------------------------------------------------------------------------------

    private ModulePackageLoader CreateLoader(ISessionExtensionRegistry extensions, IControlPlane plane) =>
        new(new ModuleUnloadCoordinator(),
            new ModuleServiceRegistry(new FixtureServices(extensions, plane)),
            new AdapterTypeCatalog(),
            pluginsRoot: PluginsRoot,
            actionTable: plane as IActionTableOwner ?? throw new InvalidOperationException("The plane must own its table."));

    /// <summary>What the running version answers, read through the host rather than through a log line.</summary>
    private static string? Answer(ISessionExtensionRegistry extensions)    {
        var source = extensions.Floating.FirstOrDefault(candidate => candidate.Id == VersionedPluginFixture.SourceId);
        if (source is null) return null;
        var collected = source.CollectAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default).GetAwaiter().GetResult();
        return collected.Value?.SingleOrDefault()?.Content.Replace("version ", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A weak handle on something only the package holds. Kept out of line so no local in the caller's frame keeps the
    /// object alive — a strong reference left in a stack slot is how this kind of test lies to itself.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference Watch(ISessionExtensionRegistry extensions) =>
        new(extensions.Floating.First(source => source.Id == VersionedPluginFixture.SourceId));

    private static async Task<bool> CollectedAsync(WeakReference probe)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (!probe.IsAlive) return true;
            await Task.Delay(20);
        }
        return !probe.IsAlive;
    }

    private string WriteVersion(string version, string? declaredVersion = null)
    {
        var directory = Path.Combine(PackageRoot, version);
        WritePackage(directory, version, declaredVersion ?? version, EntryType(version));
        return directory;
    }

    private string WriteFlatPackage(string version, string? folder = null)
    {
        var root = Path.Combine(PluginsRoot, folder ?? VersionedPluginFixture.PackageId);
        WritePackage(root, version, version, EntryType(version));
        return root;
    }
    private static string EntryType(string version) => version switch
    {
        "1.0.0" => typeof(VersionedPluginV1).FullName!,
        "2.0.0" => typeof(VersionedPluginV2).FullName!,
        _ => typeof(VersionedPluginV3).FullName!
    };

    private void WritePackage(string directory, string version, string declaredVersion, string entryType,
        string? packageId = null, string? dependency = null)
    {
        Directory.CreateDirectory(directory);
        // The fixture lives in this test assembly, which is the package's code: two directories, one assembly, several
        // entry types that declare versions of one id.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ALN.Tests.dll"), Path.Combine(directory, "ALN.Tests.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(directory, ModulePackageLayout.ManifestFileName), JsonSerializer.Serialize(new
        {
            id = packageId ?? VersionedPluginFixture.PackageId,
            version = declaredVersion,
            contracts = ModuleContracts.Version,
            entryAssembly = "ALN.Tests.dll",
            entryType,
            provides = Array.Empty<object>(),
            dependencies = dependency is null
                ? Array.Empty<object>()
                : [new Dictionary<string, object> { ["id"] = dependency, ["versionRange"] = "*" }],
            reloadPolicy = "replace"
        }));
    }

    private void WriteScript(string version, string scriptId, string marker)
    {
        var directory = Path.Combine(PackageRoot, version, "scripts", scriptId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            id = scriptId,
            version = 1,
            targetType = "channel",
            mount = "manual",
            events = new[] { "channel.message.accepted" },
            entryPoint = "onEvent",
            capabilities = new[] { "channel.bound-send" },
            trustDomain = "trusted"
        }));
        File.WriteAllText(Path.Combine(directory, "main.js"),
            $"async function onEvent(event, ctx) {{ ctx.log.info('{marker}'); }}");
    }

    private sealed class FixtureServices(ISessionExtensionRegistry extensions, IControlPlane plane) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISessionExtensionRegistry) ? extensions :
            serviceType == typeof(IControlPlane) ? plane : null;
    }

    /// <summary>The plane records what it does; these tests only care that it does not refuse.</summary>
    private sealed class RecordingAudit : IAuditSink
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingSignals : ISignalLog
    {
        public SignalLevel MinLevel => SignalLevel.Debug;
        public bool IsEnabled(SignalLevel level) => true;
        public void Write(SignalRecord record) { }
        public IReadOnlyList<SignalRecord> Snapshot() => [];
    }
}
