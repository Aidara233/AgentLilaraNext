using ALN.Contracts;
using ALN.Contracts.Archive;

namespace ALN.Archive;

/// <summary>
/// The archive as a module package. It starts no threads and does no work at startup: the registry itself is
/// created by the host early, so any component can register what it has to offer without caring about module
/// startup order. This package publishes it under a service id — and is where the script-facing surface will
/// live when scripts are allowed to look inside the bin.
/// </summary>
public sealed class ArchiveModule : IModulePlugin
{
    public const string PackageId = "aln.archive";

    private static readonly ModulePackageManifest PackageManifest = new(
        PackageId,
        "1.0.0",
        ModulePackageKind.Bundled,
        [],
        [],
        ModuleReloadPolicy.None);

    public string ParticipantId => PackageId;
    public ModulePackageManifest Manifest => PackageManifest;

    public async ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.HostServices.GetService(typeof(ModuleHostPaths)) is not ModuleHostPaths paths)
            return Result.Failure(new(ErrorCode.Unavailable, "The host did not provide its data root."));

        // The bin is created here, by the package that owns it — the host only asked for the package. That is
        // why the registry lives behind a service id: any component can register what it has to offer without
        // holding on to this type, and without caring which package got loaded first.
        var archive = new ArchiveManager(paths.DataRoot);
        var registered = context.RegisterService(ArchiveManager.ServiceId, archive);
        if (!registered.Ok) return registered;

        // Say what is in the bin, the way every other component says what it recovered: a bin nobody can see
        // is indistinguishable from data that vanished.
        var archived = await archive.ArchivedAsync(null, cancellationToken).ConfigureAwait(false);
        if (archived.Ok && archived.Value is { Count: > 0 } items &&
            context.HostServices.GetService(typeof(IRestoreReporter)) is IRestoreReporter report)
            report.Note("archive", $"{items.Count} item(s) in the bin, {items.Sum(item => item.Bytes)} byte(s); " +
                                   $"see archive.list, put one back with archive.restore");
        return Result.Success();
    }

    public ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(
        ModuleUnloadPreparation preparation,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<ModuleUnloadReadiness>.Failure(
            new(ErrorCode.Denied, "Bundled modules are fixed for the host release.")));

    public ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}
