using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// SSH as a module: one instance, one tool source, one live configuration per Session.
///
/// It is a module for the reason every configured thing here is one: "how long a live device may hold its key", "how
/// long a command may run", "may this host open a tunnel" are decisions a person writes into a <c>module.json</c>, not
/// constants compiled into the kernel. The kernel never reads that file — it hands the manifest over and this class
/// decides what it means, which is what keeps the kernel out of every module's business.
///
/// What is *not* in the configuration: which devices exist. Live devices are created by a Session with
/// <c>ssh_init</c> from what a user said in the conversation, and saved devices (the ones that survive a restart) are
/// a later step with their own gate. A module load cannot grant a Session the ability to reach anything.
/// </summary>
[Module(TypeId, Category = "ssh")]
public sealed class SshModule(ModuleId id, SshTools tools, ISessionExtensionRegistry extensions) : IKernelModule
{
    public const string TypeId = "ssh.local";

    /// <summary>
    /// What the host writes when no <c>module.json</c> exists yet: two hours of idle before a live key is dropped, a ten
    /// minute command ceiling, 256 KiB of output, and no ceiling on file transfers. Nothing here grants anything — the
    /// file has no device list, because a device is something a user hands over in a conversation, not something a
    /// default file invents.
    /// </summary>
    public const string DefaultConfig = """
        {"idleTimeoutMinutes":120,"commandSeconds":600,"maxOutputBytes":262144,"maxTransferBytes":0,"allowTunnel":false}
        """;

    private bool _registered;

    public ModuleId Id { get; } = id;

    public SshTools Tools => tools;

    public static Result<IKernelModule> FromManifest(ModuleManifest manifest, ISshAccess access,
        ISecretStore liveKeys, ISessionExtensionRegistry extensions, IAuditSink? audit = null,
        ISignalLog? signals = null, IStateStore? savedStore = null, ISecretStore? savedKeys = null,
        IBinaryFileAccess? binaryFiles = null, TimeProvider? clock = null, IJobRegistry? jobs = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Type, TypeId, StringComparison.Ordinal))
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                $"This factory builds {TypeId}, not {manifest.Type}."));

        var options = SshOptions.Default;
        if (!string.IsNullOrWhiteSpace(manifest.ConfigJson))
        {
            try
            {
                using var document = JsonDocument.Parse(manifest.ConfigJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Result<IKernelModule>.Failure(new(ErrorCode.Invalid, $"{manifest.Id}: config must be a JSON object."));
                var root = document.RootElement;

                if (TryNumber(root, "idleTimeoutMinutes") is { } idleMinutes)
                {
                    if (idleMinutes <= 0)
                        return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                            $"{manifest.Id}: \"idleTimeoutMinutes\" must be greater than zero."));
                    options = options with { IdleTimeout = TimeSpan.FromMinutes(idleMinutes) };
                }
                if (TryNumber(root, "commandSeconds") is { } seconds)
                {
                    if (seconds <= 0)
                        return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                            $"{manifest.Id}: \"commandSeconds\" must be greater than zero."));
                    options = options with { CommandSeconds = seconds };
                }
                if (TryNumber(root, "maxOutputBytes") is { } bytes)
                {
                    if (bytes <= 0)
                        return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                            $"{manifest.Id}: \"maxOutputBytes\" must be greater than zero."));
                    options = options with { MaxOutputBytes = bytes };
                }
                // Zero (or a number the caller wants to mean "no limit") is spelled 0, not omitted: an absent key keeps
                // the default, which is already "no ceiling", while 0 says so out loud in the file a person reads.
                if (root.TryGetProperty("maxTransferBytes", out var transfer) && transfer.ValueKind == JsonValueKind.Number)
                {
                    if (!transfer.TryGetInt64(out var transferBytes) || transferBytes < 0)
                        return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                            $"{manifest.Id}: \"maxTransferBytes\" must be zero (no ceiling) or a positive byte count."));
                    options = options with { MaxTransferBytes = transferBytes };
                }
                if (root.TryGetProperty("allowTunnel", out var tunnel) && tunnel.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    options = options with { AllowTunnel = tunnel.GetBoolean() };
                if (TryNumber(root, "probeTimeoutSeconds") is { } probeSeconds)
                {
                    if (probeSeconds <= 0)
                        return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                            $"{manifest.Id}: \"probeTimeoutSeconds\" must be greater than zero."));
                    options = options with { ProbeTimeout = TimeSpan.FromSeconds(probeSeconds) };
                }
            }
            catch (JsonException ex)
            {
                return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                    $"{manifest.Id}: config is not valid JSON.", ex.Message));
            }
        }

        return Result<IKernelModule>.Success(
            new SshModule(manifest.Id,
                new SshTools(access, liveKeys, audit, signals, options, clock, savedStore, savedKeys, binaryFiles, jobs),
                extensions));
    }

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = extensions.Register(tools);
        if (!registered.Ok) return ValueTask.FromResult(registered);
        _registered = true;
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>Nothing to warm up: configurations are created by Sessions, not at startup.</summary>
    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public async ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            extensions.Unregister(tools.Id);
            _registered = false;
        }
        // Keys and sockets go with the module: nothing is left holding a secret after it stops.
        await tools.SweepAllAsync().ConfigureAwait(false);
        return Result.Success();
    }

    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    /// <summary>A failed tool is reported to the model by the tool itself; there is nothing to remember here.</summary>
    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());

    private static int? TryNumber(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;
}
