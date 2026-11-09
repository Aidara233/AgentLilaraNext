using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Tests;

/// <summary>
/// One package, three versions, in one assembly.
///
/// A version switch has to be tested with real plugins: the loader refuses a package whose entry type declares a
/// different manifest than its <c>plugin.json</c>, so "the same code, relabelled" is not something the host would ever
/// accept. Three types that declare three versions of one id are the smallest honest fixture — separate
/// <see cref="IModulePlugin"/> implementations, loaded from separate directories, and the only thing that makes them
/// "the same package" is the id they all claim.
///
/// Each version answers the round context with its own name, and each registers a verb under the same id: if the host
/// did not take the first version's verb back, the second version's registration would conflict and the switch would
/// fail — which is the failure this fixture exists to make visible.
/// </summary>
public abstract class VersionedPluginFixture : IModulePlugin
{
    public const string PackageId = "fixture.versioned";
    public const string SourceId = "fixture.versioned";
    public const string Verb = "fixture.versioned.verb";

    private ISessionExtensionRegistry? _extensions;
    private IControlPlane? _plane;

    protected abstract string Version { get; }

    /// <summary>What this version answers with, so a test can tell which body is running.</summary>
    protected abstract string Answer { get; }

    public ModulePackageManifest Manifest => new(PackageId, Version, ModulePackageKind.Plugin,
        Provides: [], Dependencies: [], ReloadPolicy: ModuleReloadPolicy.Replace);

    public string ParticipantId => PackageId + ":" + Version;

    public virtual ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _extensions = context.HostServices.GetService(typeof(ISessionExtensionRegistry)) as ISessionExtensionRegistry;
        _plane = context.HostServices.GetService(typeof(IControlPlane)) as IControlPlane;
        if (_extensions is not null)
        {
            var registered = _extensions.Register(new Source(Answer));
            if (!registered.Ok) return ValueTask.FromResult(registered);
        }

        // The same verb id in every version: the second registration only succeeds if the first was taken back.
        var verb = _plane?.Register(
            new ActionDescriptor(Verb, $"{PackageId} answers from {Version}.", Capabilities.SystemRead, false),
            (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ActionArgs.JsonOf(new { version = Version }))));
        return ValueTask.FromResult(verb ?? Result.Success());
    }

    public ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(ModuleUnloadPreparation preparation,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<ModuleUnloadReadiness>.Success(new(true)));

    public ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken)
    {
        if (_extensions is not null)
        {
            _extensions.Unregister(SourceId);
            _extensions = null;
        }
        _plane = null;
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>One section whose text is the version answering, which is the whole observable difference.</summary>
    private sealed class Source(string answer) : IFloatingContextSource
    {
        public string Id => SourceId;

        public ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(RoundContextRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RoundContextContribution> sections =
                [new RoundContextContribution("fixture", "fixture", $"version {answer}", Order: 900)];
            return ValueTask.FromResult(Result<IReadOnlyList<RoundContextContribution>>.Success(sections));
        }
    }
}

public sealed class VersionedPluginV1 : VersionedPluginFixture
{
    protected override string Version => "1.0.0";
    protected override string Answer => "one";
}

public sealed class VersionedPluginV2 : VersionedPluginFixture
{
    protected override string Version => "2.0.0";
    protected override string Answer => "two";
}

/// <summary>A version that refuses to start, which is the only way to test that a failed switch leaves the old one running.</summary>
public sealed class VersionedPluginV3 : VersionedPluginFixture
{
    protected override string Version => "3.0.0";
    protected override string Answer => "three";

    public override ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "This version refuses to start on purpose.")));
}

/// <summary>
/// A package that depends on <see cref="VersionedPluginFixture"/> — the fixture that makes a replacement have a
/// dependent, which is the only way to reach the rollback path where the new version loaded and something else did not
/// come back.
///
/// Whether it starts is read from an environment variable rather than a static field: the loader gives each package its
/// own assembly load context, so a static flag set by the test would be a different field from the one the plugin reads.
/// </summary>
public sealed class DependentPluginFixture : IModulePlugin
{
    public const string PackageId = "fixture.dependent";
    public const string RefuseVariable = "ALN_TEST_REFUSE_DEPENDENT_START";

    public ModulePackageManifest Manifest => new(PackageId, "1.0.0", ModulePackageKind.Plugin,
        Provides: [], Dependencies: [new(VersionedPluginFixture.PackageId, "*")], ReloadPolicy: ModuleReloadPolicy.Replace);

    public string ParticipantId => PackageId;

    public ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Environment.GetEnvironmentVariable(RefuseVariable) is { Length: > 0 }
            ? Result.Failure(new(ErrorCode.Unavailable, "This dependent refuses to come back on purpose."))
            : Result.Success());

    public ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(ModuleUnloadPreparation preparation,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<ModuleUnloadReadiness>.Success(new(true)));

    public ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}
