using ALN.Contracts;

namespace ALN.Adapter.Qq;

/// <summary>
/// Runtime package entry point for the QQ adapter. It registers a factory and gets out of the way: the
/// per-instance work happens in <see cref="QqAdapter"/>, and instances are created by the manager from
/// the stored specs, not here.
/// </summary>
public sealed class QqAdapterModule : IModulePlugin
{
    public const string PackageId = "aln.adapter.qq";
    private const string TypeId = "adapter.qq";

    private static readonly ModulePackageManifest PackageManifest = new(
        PackageId,
        "1.0.0",
        ModulePackageKind.Plugin,
        [new ModuleTypeProvision(TypeId, "adapter")],
        [new ModuleDependency("aln.bundled.runtime", "*")],
        ModuleReloadPolicy.Replace);

    private IAdapterFactoryRegistry? _factories;
    private IAdapterManager? _adapters;
    private bool _started;

    public string ParticipantId => PackageId;
    public ModulePackageManifest Manifest => PackageManifest;

    public ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var events = GetHostService<IEventSink>(context);
        var loggers = GetHostService<IModuleLoggerFactory>(context);
        var logger = loggers.For(PackageId);
        _adapters = GetHostService<IAdapterManager>(context);
        _factories = GetHostService<IAdapterFactoryRegistry>(context);
        // Optional on purpose: without them the adapter still works, it just cannot promise a message that
        // arrived will survive a crash, and nothing acknowledges on its behalf.
        var acks = context.HostServices.GetService(typeof(IInboundAcks)) as IInboundAcks;
        var queue = context.HostServices.GetService(typeof(IQueuedMessageStore)) as IQueuedMessageStore;
        // Optional: it is how a picture the model wants to send becomes bytes on the wire. Without it the adapter still
        // sends every message, and a picture part arrives as the sentence that named it (docs/28 §6).
        var files = context.HostServices.GetService(typeof(IBinaryFileAccess)) as IBinaryFileAccess;
        // And, when the host opened one, a URL a bridge can fetch instead of base64 in a frame: the difference between
        // "works for a small picture on this machine" and "works for a photo from a container over there".
        var urls = context.HostServices.GetService(typeof(IMediaUrlSource)) as IMediaUrlSource;

        var registered = _factories.Register(PackageId, new QqAdapterFactory(events, logger, acks, queue, files, urls));
        if (!registered.Ok) return ValueTask.FromResult(registered);
        // The QQ verbs this package wraps by name (docs/16 §10.1). Registered here rather than by the host: the verbs are
        // this platform's, the schemas are this platform's dialect, and the package is the one place that can change them
        // without a host release — which is exactly why adapters.raw exists for the rest (docs/16 §10). The loader takes
        // them back when the package goes, so an unloaded package leaves no names behind.
        if (context.HostServices.GetService(typeof(IControlPlane)) is IControlPlane plane)
        {
            var actions = QqOperations.Register(plane, _adapters);
            if (!actions.Ok) return ValueTask.FromResult(actions);
        }
        _started = true;
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(ModuleUnloadPreparation preparation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started || _factories is null) return ValueTask.FromResult(Result<ModuleUnloadReadiness>.Success(new(true)));
        var draining = _factories.BeginDrain(PackageId);
        return ValueTask.FromResult(draining.Ok
            ? Result<ModuleUnloadReadiness>.Success(new(true))
            : Result<ModuleUnloadReadiness>.Failure(draining.Error!));
    }

    public ValueTask<Result> ResumeAfterUnloadCancelledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(!_started || _factories is null ? Result.Success() : _factories.Resume(PackageId));
    }

    public async ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken)
    {
        if (!_started) return Result.Success();
        var destroyed = forced
            ? await _adapters!.ForceDestroyByTypeAsync(TypeId, cancellationToken).ConfigureAwait(false)
            : await _adapters!.DestroyByTypeAsync(TypeId, cancellationToken).ConfigureAwait(false);
        if (!destroyed.Ok) return destroyed;
        var removed = _factories!.RemovePackage(PackageId);
        if (removed.Ok) _started = false;
        return removed;
    }

    private static T GetHostService<T>(IModulePluginContext context) where T : class
    {
        var service = context.HostServices.GetService(typeof(T));
        return service as T ?? throw new InvalidOperationException($"Required host service is missing: {typeof(T).FullName}");
    }

    private sealed class QqAdapterFactory(IEventSink events, IModuleLogger logger, IInboundAcks? acks,
        IQueuedMessageStore? queue, IBinaryFileAccess? files, IMediaUrlSource? urls) : IAdapterFactory
    {
        public string TypeId => "adapter.qq";

        /// <summary>
        /// Creation reads the instance settings, so a spec with a missing or malformed endpoint fails
        /// here — at startup, with a message naming the file — instead of at first send.
        /// </summary>
        public IChatAdapter Create(AdapterSpec spec) => new QqAdapter(spec, events, logger, acks, queue, files, urls);
    }}
