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
///
/// Contract version 2: this plugin registers a <see cref="IFloatingContextSource"/> and returns what it has
/// instead of writing into two writers. A source that returns its contribution can be skipped when it fails; one
/// that writes into a live request can only fail the request.
/// </summary>
public sealed class Plugin : IModulePlugin
{
    public const string PackageId = "sample.environment";
    public const string SourceId = "sample.environment";

    private ISessionExtensionRegistry? _extensions;

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
        _extensions = context.HostServices.GetService(typeof(ISessionExtensionRegistry)) as ISessionExtensionRegistry;
        if (_extensions is null)
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "This host does not expose session extensions.")));
        return ValueTask.FromResult(_extensions.Register(new EnvironmentSource()));
    }

    public ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(ModuleUnloadPreparation preparation,
        CancellationToken cancellationToken)
    {
        // Nothing in flight: this plugin answers inside CollectAsync and keeps no work queued.
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Result<ModuleUnloadReadiness>.Success(new(true)));
    }

    public ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken)
    {
        if (_extensions is not null)
        {
            _extensions.Unregister(SourceId);
            _extensions = null;
        }
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>One section, named and ordered so it cannot collide with a built-in block.</summary>
    private sealed class EnvironmentSource : IFloatingContextSource
    {
        public string Id => SourceId;

        public ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(
            RoundContextRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RoundContextContribution> sections =
            [
                new RoundContextContribution(
                    "environment",
                    "environment",
                    $"宿主运行时：{Environment.Version}（{Environment.OSVersion.Platform}，{Environment.ProcessorCount} 核）",
                    Order: 500)
            ];
            return ValueTask.FromResult(Result<IReadOnlyList<RoundContextContribution>>.Success(sections));
        }
    }
}
