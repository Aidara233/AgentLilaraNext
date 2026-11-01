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

        var registered = _factories.Register(PackageId, new QqAdapterFactory(events, logger, acks, queue));
        if (!registered.Ok) return ValueTask.FromResult(registered);
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

    private sealed class QqAdapterFactory(IEventSink events, IModuleLogger logger, IInboundAcks? acks, IQueuedMessageStore? queue) : IAdapterFactory
    {
        public string TypeId => "adapter.qq";

        /// <summary>
        /// Creation reads the instance settings, so a spec with a missing or malformed endpoint fails
        /// here — at startup, with a message naming the file — instead of at first send.
        /// </summary>
        public IChatAdapter Create(AdapterSpec spec) => new QqAdapter(spec, events, logger, acks, queue);
    }}
