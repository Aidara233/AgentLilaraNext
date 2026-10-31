using ALN.Channel;
using ALN.Clock;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Scripting.Jint;

namespace ALN.BundledModules;

/// <summary>Fixed-for-release module implementations shipped with the host.</summary>
public sealed class DefaultModulesPlugin : IModulePlugin
{
    public const string PackageId = "aln.bundled.runtime";
    private static readonly ModulePackageManifest PackageManifest = new(
        PackageId,
        "1.0.0",
        ModulePackageKind.Bundled,
        [new ModuleTypeProvision("channel.logical", "channel")],
        [],
        ModuleReloadPolicy.None);

    public string ParticipantId => PackageId;
    public ModulePackageManifest Manifest => PackageManifest;

    public ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var adapters = HostService<IAdapterManager>(context);
            var events = HostService<IEventSink>(context);
            var types = HostService<IModuleTypeCatalog>(context);
            var bindings = HostService<IInstanceBindings>(context);
            var journal = HostService<IExecutionJournal>(context);
            var audit = HostService<IAuditSink>(context);
            var paths = HostService<ModuleHostPaths>(context);
            var models = HostService<IModelManager>(context);
            var sessions = HostService<ISessionManager>(context);

            var clock = new ClockSource(events);
            var channels = new ChannelManager(adapters, events, bindings);
            var executor = new JintScriptExecutor(journal);
            var graph = new GraphManager(adapters, channels, clock, types, executor, audit, models, sessions);
            if (bindings is IInstanceBindingsProxy proxy) proxy.Bind(graph);

            foreach (var (id, service) in new (string, object)[]
            {
                ("clock.manager", clock),
                ("channel.manager", channels),
                ("script.executor", executor),
                ("graph.runtime", graph),
                ("graph.script-catalog", new JsonScriptCatalog(paths.ProgramRoot, paths.DataRoot))
            })
            {
                var registered = context.RegisterService(id, service);
                if (!registered.Ok) return ValueTask.FromResult(registered);
            }
            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Internal, "Bundled module initialization failed.", ex.Message)));
        }
    }

    public ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(
        ModuleUnloadPreparation preparation,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<ModuleUnloadReadiness>.Failure(new(ErrorCode.Denied, "Bundled modules are fixed for the host release.")));

    public ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());

    private static T HostService<T>(IModulePluginContext context) where T : class =>
        context.HostServices.GetService(typeof(T)) as T
        ?? throw new InvalidOperationException($"Required host service is missing: {typeof(T).FullName}");
}
