namespace ALN.Contracts;

public sealed record ModuleHostPaths(string ProgramRoot, string DataRoot);

/// <summary>
/// Describes where a module package comes from. The host assigns this value from
/// the package location or release manifest; a package cannot grant itself a
/// more privileged origin by changing its own manifest.
/// </summary>
public enum ModulePackageKind
{
    Core,
    Bundled,
    Plugin
}

/// <summary>
/// Declares whether an activated package can be replaced without restarting
/// the host. Core and bundled packages are normally fixed for one host run.
/// </summary>
public enum ModuleReloadPolicy
{
    None,
    Replace
}

public sealed record ModuleDependency(string Id, string VersionRange, bool Optional = false);
public sealed record ModuleTypeProvision(string TypeId, string? ParentTypeId, bool IsAbstract = false);

public interface IModuleTypeRegistry : IModuleTypeCatalog
{
    Result RegisterPackageTypes(string packageId, IReadOnlyList<ModuleTypeProvision> types);
    Result RemovePackageTypes(string packageId);
}

/// <summary>
/// Host-validated metadata for one module package. The runtime must validate
/// package kind and reload policy against its trusted source, not trust values
/// supplied by plugin code.
/// </summary>
public sealed record ModulePackageManifest(
    string Id,
    string Version,
    ModulePackageKind PackageKind,
    IReadOnlyList<ModuleTypeProvision> Provides,
    IReadOnlyList<ModuleDependency> Dependencies,
    ModuleReloadPolicy ReloadPolicy);

/// <summary>
/// Bounded preparation phase before the core disconnects and unloads a module.
/// Deadline is absolute so all participants share one operation budget.
/// </summary>
public sealed record ModuleUnloadPreparation(Guid OperationId, DateTimeOffset Deadline, bool Replacing = false);

public interface IModuleReloadStateStore
{
    Result Store(string packageId, string stateJson);
    bool TryGet(string packageId, out string stateJson);
    void Clear(string packageId);
}

public sealed record ModuleUnloadReadiness(bool Ready, string? Detail = null);

/// <summary>
/// Implemented by lifecycle owners that must finish or report in-flight work
/// before a normal unload can proceed. Forced unload is coordinated by the
/// host and may proceed after this method fails or times out.
/// </summary>
public interface IModuleUnloadParticipant
{
    string ParticipantId { get; }

    ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(
        ModuleUnloadPreparation preparation,
        CancellationToken cancellationToken);

    /// <summary>Called when a normal unload is cancelled before disconnection.</summary>
    ValueTask<Result> ResumeAfterUnloadCancelledAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());

    /// <summary>Called only after the preparation barrier succeeds, or when forced.</summary>
    ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "Participant does not implement disconnection.")));
}

public interface IModulePlugin : IModuleUnloadParticipant
{
    ModulePackageManifest Manifest { get; }
    ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken);
}

public interface IModulePluginContext
{
    string PackageId { get; }
    IServiceProvider HostServices { get; }
    Result RegisterService(string serviceId, object service);
    Result<T> GetService<T>(string serviceId) where T : class;
}

public interface IModulePackageResourceRegistry
{
    Result RemovePackage(string packageId);
}

public interface IModuleServiceRegistry
{
    IServiceProvider HostServices { get; }
    Result RegisterService(string packageId, string serviceId, object service);
    Result<T> GetService<T>(string serviceId) where T : class;
    void RemovePackageServices(string packageId);
}

public sealed record ModuleReloadReport(
    string PackageId,
    string PreviousVersion,
    string CurrentVersion,
    IReadOnlyList<string> ReactivatedDependents);

public sealed record ModuleUnloadReport(
    string PackageId,
    bool Completed,
    bool Forced,
    IReadOnlyList<string> AffectedPackages,
    IReadOnlyList<string> PreparedParticipants,
    IReadOnlyList<string> DisconnectedParticipants,
    IReadOnlyList<string> UnreadyParticipants,
    IReadOnlyList<string> Errors);
