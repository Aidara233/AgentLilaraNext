using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace Aln.Documents;

/// <summary>
/// Word, Excel, PowerPoint and PDF as something a Session can read and write.
///
/// <b>Why a plugin and not a bundled module.</b> Everything here feeds bytes written by somebody else to a parser,
/// and a parser handed unfamiliar bytes is the most crash-prone thing in a process. A plugin runs in its own
/// collectable load context with its own private dependencies: a package that throws takes itself down, not the host,
/// and a version that turns out to mangle documents is replaced with <c>packages.replace</c> instead of a release.
///
/// <b>What it does not own.</b> Where a file came from is not decided here. A model reaches this plugin with a
/// <c>scope</c> and a relative path — the same pair <c>fs_read</c> takes — and the host's byte port decides whether
/// that scope may hold bytes and whether the path is inside it. A file that arrived from a platform is put where the
/// model asked for it by <c>files_get</c> first; this plugin never reaches a platform, a URL, or a conversation.
/// </summary>
public sealed class DocumentsPlugin : IModulePlugin
{
    public const string PackageId = "aln.documents";

    /// <summary>
    /// Must match <c>plugin.json</c> field for field: the loader compares them and refuses a package that lies about
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

    private ISessionExtensionRegistry? _extensions;

    public ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken)
    {
        // Looked up by contract, never by reaching into types the host owns. Both are required: a host without the
        // byte port would leave every tool answering "no such file" forever, which is the failure mode this project
        // keeps paying for — a capability that is quietly absent instead of loudly missing.
        var extensions = context.HostServices.GetService(typeof(ISessionExtensionRegistry)) as ISessionExtensionRegistry;
        if (extensions is null)
            return ValueTask.FromResult(Result.Failure(
                new(ErrorCode.Unavailable, "This host does not expose session extensions.")));

        var files = context.HostServices.GetService(typeof(IBinaryFileAccess)) as IBinaryFileAccess;
        if (files is null)
            return ValueTask.FromResult(Result.Failure(
                new(ErrorCode.Unavailable, "This host does not expose a binary file port, so no document can be read.")));

        var registered = extensions.Register(new DocumentTools(files));
        if (!registered.Ok) return ValueTask.FromResult(registered);

        _extensions = extensions;
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>Nothing is in flight: every tool answers inside its own call and keeps no work queued.</summary>
    public ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(ModuleUnloadPreparation preparation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Result<ModuleUnloadReadiness>.Success(new(true)));
    }

    public ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken)
    {
        _extensions?.Unregister(DocumentTools.SourceId);
        _extensions = null;
        return ValueTask.FromResult(Result.Success());
    }
}
