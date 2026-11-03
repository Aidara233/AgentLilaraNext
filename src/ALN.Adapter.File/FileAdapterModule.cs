using System.Text.Json;
using ALN.Contracts;

namespace ALN.Adapter.File;

/// <summary>Runtime package entry point for the FileAdapter implementation.</summary>
public sealed class FileAdapterModule : IModulePlugin
{
    public const string PackageId = "aln.adapter.file";
    private static readonly ModulePackageManifest PackageManifest = new(
        PackageId,
        "1.0.0",
        ModulePackageKind.Plugin,
        [new ModuleTypeProvision("adapter.file", "adapter")],
        [new ModuleDependency("aln.bundled.runtime", "*")],
        ModuleReloadPolicy.Replace);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private IAdapterFactoryRegistry? _factories;
    private IAdapterManager? _adapters;
    private IClockManager? _clock;
    private IModuleReloadStateStore? _reloadState;
    private FileAdapterFactory? _factory;
    private bool _started;
    private bool _storedStateForPendingReplacement;

    public string ParticipantId => PackageId;
    public ModulePackageManifest Manifest => PackageManifest;

    public async ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mailbox = GetHostService<IFileMailbox>(context);
        var events = GetHostService<IEventSink>(context);
        var acks = context.HostServices.GetService(typeof(ALN.Contracts.IInboundAcks)) as ALN.Contracts.IInboundAcks;
        var clockResult = context.GetService<IClockManager>("clock.manager");
        if (!clockResult.Ok || clockResult.Value is null)
            return Result.Failure(clockResult.Error ?? new(ErrorCode.Unavailable, "Clock service is not available."));
        _clock = clockResult.Value;
        _adapters = GetHostService<IAdapterManager>(context);
        _factories = GetHostService<IAdapterFactoryRegistry>(context);
        _reloadState = GetHostService<IModuleReloadStateStore>(context);
        _factory = new FileAdapterFactory(mailbox, events, _clock, acks);
        var registered = _factories.Register(PackageId, _factory);
        if (!registered.Ok) return registered;
        _started = true;

        if (_reloadState.TryGet(PackageId, out var stateJson))
        {
            AdapterInfo[] snapshots;
            try { snapshots = JsonSerializer.Deserialize<AdapterInfo[]>(stateJson, JsonOptions) ?? []; }
            catch (JsonException ex) { return Result.Failure(new(ErrorCode.Invalid, "Saved Adapter replacement state is invalid.", ex.Message)); }
            foreach (var snapshot in snapshots)
            {
                // The type travels in the snapshot: after a reload the manager has forgotten the instance, so
                // asking it would answer nothing, and reading the address would be guessing.
                if (string.IsNullOrWhiteSpace(snapshot.TypeId))
                    return Result.Failure(new(ErrorCode.Invalid, $"Saved Adapter replacement state has no type for {snapshot.Address}."));
                var unregistered = await _clock.UnregisterTargetAsync(snapshot.Address, cancellationToken).ConfigureAwait(false);
                if (!unregistered.Ok) return Result.Failure(unregistered.Error!);
                var created = await _adapters.CreateAsync(new(snapshot.Address, snapshot.Descriptor, snapshot.TypeId), cancellationToken).ConfigureAwait(false);
                if (!created.Ok) return Result.Failure(created.Error!);
                if (snapshot.State == AdapterState.Running)
                {
                    var started = await _adapters.StartAsync(snapshot.Address, cancellationToken).ConfigureAwait(false);
                    if (!started.Ok) return started;
                }
                else if (snapshot.State == AdapterState.Stopped)
                {
                    var stopped = await _adapters.StopAsync(snapshot.Address, cancellationToken).ConfigureAwait(false);
                    if (!stopped.Ok) return stopped;
                }
            }
        }
        return Result.Success();
    }

    public async ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(
        ModuleUnloadPreparation preparation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started || _factories is null)
            return Result<ModuleUnloadReadiness>.Success(new(true));
        var draining = _factories.BeginDrain(PackageId);
        if (!draining.Ok) return Result<ModuleUnloadReadiness>.Failure(draining.Error!);

        if (preparation.Replacing && !_reloadState!.TryGet(PackageId, out _))
        {
            var snapshot = await _adapters!.SnapshotByTypeAsync("adapter.file", cancellationToken).ConfigureAwait(false);
            if (!snapshot.Ok || snapshot.Value is null)
                return Result<ModuleUnloadReadiness>.Failure(snapshot.Error ?? new(ErrorCode.Internal, "Could not snapshot active Adapters."));
            var stored = _reloadState!.Store(PackageId, JsonSerializer.Serialize(snapshot.Value, JsonOptions));
            if (!stored.Ok) return Result<ModuleUnloadReadiness>.Failure(stored.Error!);
            _storedStateForPendingReplacement = true;
        }
        return Result<ModuleUnloadReadiness>.Success(new(true));
    }

    public ValueTask<Result> ResumeAfterUnloadCancelledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_storedStateForPendingReplacement)
        {
            _reloadState?.Clear(PackageId);
            _storedStateForPendingReplacement = false;
        }
        return ValueTask.FromResult(!_started || _factories is null ? Result.Success() : _factories.Resume(PackageId));
    }

    public async ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken)
    {
        if (!_started) return Result.Success();
        Result destroyed;
        if (forced)
        {
            var snapshot = await _adapters!.SnapshotByTypeAsync("adapter.file", cancellationToken).ConfigureAwait(false);
            if (!snapshot.Ok || snapshot.Value is null)
                return Result.Failure(snapshot.Error ?? new(ErrorCode.Internal, "Could not enumerate Adapters for forced disconnection."));
            foreach (var adapter in snapshot.Value)
            {
                var unregistered = await _clock!.UnregisterTargetAsync(adapter.Address, cancellationToken).ConfigureAwait(false);
                if (!unregistered.Ok) return Result.Failure(unregistered.Error!);
            }
            destroyed = await _adapters.ForceDestroyByTypeAsync("adapter.file", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            destroyed = await _adapters!.DestroyByTypeAsync("adapter.file", cancellationToken).ConfigureAwait(false);
        }
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

    private sealed class FileAdapterFactory(IFileMailbox mailbox, IEventSink events, IClockManager clock, IInboundAcks? acks) : IAdapterFactory
    {
        public string TypeId => "adapter.file";
        public IChatAdapter Create(AdapterSpec spec) => new FileAdapter(spec, mailbox, events, clock, acks);
    }
}
