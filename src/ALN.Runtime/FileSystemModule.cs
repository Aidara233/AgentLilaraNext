using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The file system as a module: one instance, one tool source, one whitelist.
///
/// It is a module rather than a helper because "which directories the model may touch" is a configuration decision
/// that belongs in a <c>module.json</c> a person edits, not a list compiled into the host. The kernel never reads that
/// configuration — it hands the manifest over and this class decides what it means, which is the only way the kernel
/// stays out of every module's business.
///
/// The port (<see cref="IFileSystemAccess"/>) is what enforces containment; this class only decides which of the
/// registered scopes are worth offering, and refuses a configuration that names one that does not exist rather than
/// quietly offering nothing (a typo would otherwise look like "the model has no files").
/// </summary>
[Module(TypeId, Category = "filesystem")]
public sealed class FileSystemModule(ModuleId id, FileSystemTools tools, ISessionExtensionRegistry extensions) : IKernelModule
{
    public const string TypeId = "filesystem.local";

    /// <summary>
    /// Default configuration written by the host when no <c>module.json</c> exists yet: the memory library, the folder a
    /// script is drafted in, a workspace to do chores in, and the media area to read (docs/28).
    ///
    /// Media is here in the read direction only — the port refuses writes to it whatever this file says, so offering it
    /// to the tools is safe and is what makes "look at the picture I did not show you" possible. Granting more is an
    /// edit, not a default.
    ///
    /// A data root created before media existed keeps its old file (C-03): the host says so at startup and the operator
    /// adds the name. That is the intended cost of never rewriting a person's configuration.
    /// </summary>
    public const string DefaultConfig = """{"scopes":["memory","workspace","scripts/draft","media"]}""";

    private bool _registered;

    public ModuleId Id { get; } = id;

    /// <summary>The scopes this instance offers to the model.</summary>
    public IReadOnlyList<string> Scopes => tools.Scopes;

    /// <summary>
    /// Builds the instance a <c>module.json</c> describes. Two refusals, both at startup where somebody can fix them:
    /// a config that is not JSON, and a scope that is not registered with the port.
    /// </summary>
    public static Result<IKernelModule> FromManifest(ModuleManifest manifest, IFileSystemAccess access,
        ISessionExtensionRegistry extensions,
        /// <summary>
        /// How <c>fs_read</c> recognises a picture instead of reporting "not UTF-8 text". Optional: without it the tools
        /// still read and write every text file, and opening a picture is an ordinary failure rather than a receipt.
        /// </summary>
        IMediaStore? media = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Type, TypeId, StringComparison.Ordinal))
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                $"This factory builds {TypeId}, not {manifest.Type}."));

        var scopes = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.ConfigJson))
        {
            try
            {
                using var document = JsonDocument.Parse(manifest.ConfigJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("scopes", out var declared))
                {
                    if (declared.ValueKind != JsonValueKind.Array)
                        return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                            $"{manifest.Id}: \"scopes\" must be an array of scope names."));
                    foreach (var item in declared.EnumerateArray())
                    {
                        var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name))
                            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                                $"{manifest.Id}: every entry in \"scopes\" must be a non-empty string."));
                        // Refused rather than dropped: a scope that does not exist is a typo, and a typo that silently
                        // removes an area looks exactly like a model that has nothing to read.
                        if (!access.Scopes.Contains(name, StringComparer.Ordinal))
                            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                                $"{manifest.Id}: scope \"{name}\" is not registered. Registered: {string.Join(", ", access.Scopes)}."));
                        if (!scopes.Contains(name, StringComparer.Ordinal)) scopes.Add(name);
                    }
                }
            }
            catch (JsonException ex)
            {
                return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                    $"{manifest.Id}: config is not valid JSON.", ex.Message));
            }
        }

        return Result<IKernelModule>.Success(new FileSystemModule(manifest.Id,
            new FileSystemTools(access, scopes.AsReadOnly(), media), extensions));
    }

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = extensions.Register(tools);
        if (!registered.Ok) return ValueTask.FromResult(registered);
        _registered = true;
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>Nothing to warm up: the tools answer per round, and the port is already open.</summary>
    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            extensions.Unregister(tools.Id);
            _registered = false;
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    /// <summary>A tool that failed is reported to the model by the tool itself; there is nothing to remember here.</summary>
    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}
