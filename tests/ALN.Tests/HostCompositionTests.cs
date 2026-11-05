using System.Text.Json;
using ALN.Contracts;

using ALN.Runtime;
using ALN.Runtime.Hosting;

namespace ALN.Tests;

/// <summary>
/// The host's own declaration table, tested where it can be tested: without a data directory, without a plugin, and
/// without starting a process. What it pins down is the shape — five modules, five ids, five types, all on the graph
/// and ready — which is exactly what a hand-written list silently loses when somebody edits one of five copies.
/// </summary>
public sealed class HostCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-host-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleFactoryRegistry _factories = new();

    public HostCompositionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_core_table_declares_every_module_the_host_builds_itself()
    {
        var kernel = new Kernel(_root, _factories);
        var modules = new[]
        {
            new DeclaredModule(new Stub(new("adapters")), "adapters", "Adapter manager"),
            new DeclaredModule(new Stub(new("channels")), "channels", "Channel manager"),
            new DeclaredModule(new Stub(new("memory")), "memory", "Memory"),
            new DeclaredModule(new Stub(new("models")), "models", "Model manager"),
            new DeclaredModule(new Stub(new("sessions")), "sessions", "Session manager")
        };

        var declared = HostComposition.Declare(kernel, modules);

        Assert.True(declared.Ok, declared.Error?.Message);
        // Declared, not yet in the graph: declaration is collected and turned into instances when startup runs.
        var started = await kernel.StartAsync(default);
        Assert.True(started.Ok, started.Error?.Message);
        Assert.Equal(modules.Length, kernel.Instances.Count);
        Assert.All(modules, module => Assert.Contains(kernel.Instances, instance => instance.Id.Value == module.Module.Id.Value));
        Assert.All(modules, module => Assert.Equal(ModulePhase.Ready,
            kernel.Instances.Single(instance => instance.Id.Value == module.Module.Id.Value).Phase));
    }

    [Fact]
    public async Task A_table_that_names_one_id_twice_says_which_entry_failed()
    {
        // The five copies this replaced each had their own message. One table means one message — but it has to say
        // which entry it is about, or the operator is back to guessing.
        var kernel = new Kernel(_root, _factories);
        var modules = new[]
        {
            new DeclaredModule(new Stub(new("adapters")), "adapters", "Adapter manager"),
            new DeclaredModule(new Stub(new("adapters")), "adapters", "Adapter manager again")
        };

        var declared = HostComposition.Declare(kernel, modules);

        Assert.False(declared.Ok);
        Assert.Contains("Adapter manager again", declared.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_catalog_table_attaches_the_modules_and_announces_their_owners()
    {
        // Two tables, two loops: what the catalog lists, and which of those own instances it must not copy. Both are
        // answered from one place, and a failure names the module that caused it.
        var catalog = new ModuleCatalog();
        var alpha = new Stub(new("alpha"));
        var beta = new Stub(new("beta"));

        var registered = HostComposition.Register(catalog, [alpha, beta], [(IModuleIndex)alpha]);

        Assert.True(registered.Ok, registered.Error?.Message);
        Assert.Equal(2, catalog.Count);
        Assert.NotNull(catalog.Find(new("alpha")));
        Assert.NotNull(catalog.Find(new("beta")));
    }

    [Fact]
    public async Task A_catalog_table_that_names_one_id_twice_reports_the_collision()
    {
        var catalog = new ModuleCatalog();
        var again = new Stub(new("alpha"));

        var registered = HostComposition.Register(catalog, [new Stub(new("alpha")), again], []);

        Assert.False(registered.Ok);
        Assert.Contains("alpha", registered.Error!.Message, StringComparison.Ordinal);
    }

    [Module("stub", Category = "stub", Role = ModuleRole.Manager)]
    private sealed class Stub(ModuleId id) : IKernelModule, IModule, IModuleIndex
    {
        public ModuleId Id { get; } = id;

        public string Category => "stub";

        public IReadOnlyList<CatalogEntry> Entries() => [];

        public LocatedModule? Locate(ModuleId located) => null;

        public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> StopAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
    }
}
