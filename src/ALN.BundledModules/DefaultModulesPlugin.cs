using ALN.Channel;
using ALN.Clock;
using ALN.Contracts;
using ALN.Contracts.Archive;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;
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
        [new ModuleTypeProvision("channel.conversation", "channel")],
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
            var journal = HostService<ISignalLog>(context);
            var audit = HostService<IAuditSink>(context);
            var paths = HostService<ModuleHostPaths>(context);
            var sessions = HostService<ISessionManager>(context);
            var catalog = HostService<ICatalog>(context);

            var clock = new ClockSource(events);
            // The bin, if this host loaded one. Everything that can pile up registers here; the archive package
            // owns the path and the naming, this code only says what it has to offer.
            var archive = context.GetService<IArchiveRegistry>(ArchiveService.Registry);
            var registry = archive.Ok ? archive.Value : null;
            // Both managers own state that has to survive a restart: the Channel's wish to run, and the
            // graph's mount rules. Neither invents a path — the host says where, they say what.
            var state = context.HostServices.GetService(typeof(IStateStore)) as IStateStore;
            var report = context.HostServices.GetService(typeof(IRestoreReporter)) as IRestoreReporter;
            var atomic = context.HostServices.GetService(typeof(IAtomicWork)) as IAtomicWork;
            var queue = context.HostServices.GetService(typeof(IQueuedMessageStore)) as IQueuedMessageStore;
            // Media is the one store two components need: the graph (speak's image blocks) and the Channel (a picture that
            // arrived with a message). Read here, before either is built, because passing it to one and assuming the
            // other would find it by name is exactly the bug this line replaced: a ChannelManager constructed without
            // it answers every attachment with "this host has no media store; they were dropped" — a note, not a failure,
            // so nothing ever said so. An inbound picture then cost a download and left no file.
            var media = context.HostServices.GetService(typeof(IMediaStore)) as IMediaStore;
            // Said at startup, where somebody reads it. Without this, a host that forgot to register a media store drops
            // every attachment with a note written after the receipt has already been printed — which is how a picture
            // can cost a download, land nowhere, and leave nothing behind but a model saying it cannot see pictures.
            if (media is null)
                report?.Note("channels", "this host has no media store: an attachment that arrives with a message is "
                    + "dropped, and speak cannot send a picture");
            var channels = new ChannelManager(events, bindings, catalog,
                context.HostServices.GetService(typeof(IChannelSpecStore)) as IChannelSpecStore,
                context.HostServices.GetService(typeof(IMessageStore)) as IMessageStore,
                context.HostServices.GetService(typeof(IParameterRegistry)) as IParameterRegistry,
                state, report, atomic, queue, registry, types: types,
                // Optional for the graph's sake below, required in practice for anything that carries a picture.
                media: media,
                // Optional: a host without an identity table still routes messages, they simply arrive from strangers.
                people: context.HostServices.GetService(typeof(IPersonDirectory)) as IPersonDirectory);
            // The channel verbs are the Channel module's, so it registers them: the table is handed out, not filled in
            // from outside. Failing here is a startup failure — a verb that silently did not exist would only be
            // discovered by whoever tried to use it.
            var channelActions = channels.RegisterActions(HostService<IControlPlane>(context));
            if (!channelActions.Ok) return ValueTask.FromResult(channelActions);
            var executor = new JintScriptExecutor();
            var graph = new GraphManager(catalog, types, executor, audit, journal, state, report, media);
            if (bindings is IInstanceBindingsProxy proxy) proxy.Bind(graph);
            // The mount verbs are the graph's: it owns the rules, and it owns what a scope may name.
            var graphActions = graph.RegisterActions(HostService<IControlPlane>(context));
            if (!graphActions.Ok) return ValueTask.FromResult(graphActions);

            // Sessions know better than anyone which of their directories stopped being useful; the archive
            // only knows how to take things away. That is the whole point of a source.
            if (registry is not null)
            {
                var source = registry.Register(new SessionArchiveSource(sessions, channels));
                if (!source.Ok) return ValueTask.FromResult(source);
            }

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
