using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace SampleEnvironment;

/// <summary>
/// A plugin that does the smallest useful thing: it adds one floating context section to every model request.
///
/// It exists to be read as much as run. Everything a module needs is here —
/// <see cref="IModulePlugin.Manifest"/> declares what the package is, <see cref="IModulePlugin.StartAsync"/>
/// takes the one capability it needs out of the host, and
/// <see cref="IModuleUnloadParticipant.DisconnectAsync"/> gives it back. Nothing else is reachable: a plugin
/// compiles against the contracts assembly and nothing else.
/// </summary>
public sealed class Plugin : IModulePlugin
{
    public const string PackageId = "sample.environment";
    public const string ContributorId = "sample.environment";

    private IRoundContextContributorRegistry? _contributors;

    /// <summary>
    /// Must match module.json field for field — the loader compares them and refuses a package that lies about
    /// itself. The package kind is the host's judgement, so a package cannot promote itself to bundled.
    /// </summary>
    public ModulePackageManifest Manifest { get; } = new(
        PackageId,
        "1.0.0",
        ModulePackageKind.Plugin,
        Provides: [],
        Dependencies: [],
        ReloadPolicy: ModuleReloadPolicy.Replace);

    public string ParticipantId => PackageId;

    public ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken)
    {
        // Host services are looked up by contract, never by reaching into types the host owns.
        _contributors = context.HostServices.GetService(typeof(IRoundContextContributorRegistry)) as IRoundContextContributorRegistry;
        if (_contributors is null)
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "This host does not expose round-context contributors.")));
        return ValueTask.FromResult(_contributors.Register(new EnvironmentContributor()));
    }

    public ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(ModuleUnloadPreparation preparation,
        CancellationToken cancellationToken)
    {
        // Nothing in flight: this plugin answers inside ContributeAsync and keeps no work queued.
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Result<ModuleUnloadReadiness>.Success(new(true)));
    }

    public ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken)
    {
        if (_contributors is not null)
        {
            _contributors.Unregister(ContributorId);
            _contributors = null;
        }
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>One section, named and ordered so it cannot collide with a built-in block.</summary>
    private sealed class EnvironmentContributor : IRoundContextContributor
    {
        public string Id => ContributorId;

        public ValueTask ContributeAsync(RoundContextRequest request, ISessionTimelineWriter timeline,
            IRoundContextWriter roundContext, ITurnCommitSink commits, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            roundContext.Add(new RoundContextContribution(
                "environment",
                "environment",
                $"宿主运行时：{Environment.Version}（{Environment.OSVersion.Platform}，{Environment.ProcessorCount} 核）",
                Order: 500));
            return ValueTask.CompletedTask;
        }
    }
}
