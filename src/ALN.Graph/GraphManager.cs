using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;
using ALN.Graph.Contracts;

namespace ALN.Graph;

public sealed class GraphManager : IGraphRuntime
{
    private static readonly HashSet<string> AllowedCapabilities = new(StringComparer.Ordinal) { ScriptCapabilities.SelfAdapterSend };
    private readonly IAdapterManager _adapters;
    private readonly IModuleTypeCatalog _types;
    private readonly IScriptExecutor _executor;
    private readonly IAuditSink _audit;
    private readonly Dictionary<string, ScriptPackage> _packages = new(StringComparer.Ordinal);
    private readonly List<MountedScript> _mounted = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public GraphManager(IAdapterManager adapters, IModuleTypeCatalog types, IScriptExecutor executor, IAuditSink audit)
    { _adapters = adapters; _types = types; _executor = executor; _audit = audit; }

    public IReadOnlyList<MountedScript> Mounted
    {
        get { _gate.Wait(); try { return _mounted.ToArray(); } finally { _gate.Release(); } }
    }

    public async ValueTask<Result> LoadAsync(IEnumerable<ScriptPackage> packages, CancellationToken cancellationToken)
    {
        var incoming = packages.ToArray();
        var candidate = new Dictionary<string, ScriptPackage>(StringComparer.Ordinal);
        foreach (var package in incoming)
        {
            var m = package.Manifest;
            if (string.IsNullOrWhiteSpace(m.Id) || !candidate.TryAdd(m.Id, package) || m.Version < 1 ||
                string.IsNullOrWhiteSpace(m.TargetType) || !_types.IsKnownAbstract(m.TargetType) || m.Events is null || m.Events.Length == 0 ||
                m.Events.Any(e => e != ScriptEvents.AdapterEnvelope) || string.IsNullOrWhiteSpace(m.EntryPoint) ||
                m.MaxStatements is < 1 or > 1_000_000 || m.TimeoutMilliseconds is < 1 or > 60_000 ||
                m.MemoryLimitBytes is < 1_048_576 or > 268_435_456)
                return Result.Failure(new(ErrorCode.Invalid, $"Invalid script manifest: {m.Id}"));
            if (m.TrustDomain == TrustDomain.Restricted)
                return Result.Failure(new(ErrorCode.Denied, "Restricted scripts require the process-isolated runner, which is not part of this initial host."));
            if (m.Capabilities is null || m.Capabilities.Any(c => !AllowedCapabilities.Contains(c)))
                return Result.Failure(new(ErrorCode.Denied, $"Script {m.Id} requests an unsupported capability."));
            if (m.Mount is not (MountMode.Default or MountMode.Manual))
                return Result.Failure(new(ErrorCode.Invalid, $"Invalid mount mode: {m.Id}"));
            if (string.IsNullOrWhiteSpace(package.Source) || package.Source.Length > 256_000)
                return Result.Failure(new(ErrorCode.Invalid, $"Empty or oversized source: {m.Id}"));
            var validated = await _executor.ValidateAsync(package, cancellationToken).ConfigureAwait(false);
            if (!validated.Ok) return validated;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var targetAddresses = _adapters.List().Select(a => a.Address).ToArray();
            var mounts = new List<MountedScript>();
            foreach (var package in incoming.Where(p => p.Manifest.Mount == MountMode.Default))
            foreach (var target in targetAddresses)
                if (IsCompatible(package.Manifest.TargetType, target)) mounts.Add(new(package, target));
            _packages.Clear();
            foreach (var pair in candidate) _packages.Add(pair.Key, pair.Value);
            _mounted.Clear(); _mounted.AddRange(mounts); _loaded = true;
            return Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded) return Result.Failure(new(ErrorCode.Unavailable, "Graph scripts are not loaded."));
            if (!_types.IsConcrete(target.TypeId)) return Result.Failure(new(ErrorCode.Invalid, "Mount target must be a concrete module instance."));
            if (!_adapters.Get(target).Ok) return Result.Failure(new(ErrorCode.Invalid, $"Adapter target not found: {target}"));
            foreach (var package in _packages.Values.Where(p => p.Manifest.Mount == MountMode.Default && IsCompatible(p.Manifest.TargetType, target)))
                if (!_mounted.Any(m => m.Package.Manifest.Id == package.Manifest.Id && m.Target == target)) _mounted.Add(new(package, target));
            return Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _mounted.RemoveAll(m => m.Target == target); return Result.Success(); }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> MountAsync(string scriptId, Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_packages.TryGetValue(scriptId, out var package)) return Result.Failure(new(ErrorCode.Invalid, $"Script not found: {scriptId}"));
            if (package.Manifest.Mount != MountMode.Manual) return Result.Failure(new(ErrorCode.Denied, "Default scripts are mounted by AdapterManager lifecycle."));
            if (!IsCompatible(package.Manifest.TargetType, target) || !_adapters.Get(target).Ok)
                return Result.Failure(new(ErrorCode.Invalid, "Manual mount target is absent or incompatible."));
            if (_mounted.Any(m => m.Package.Manifest.Id == scriptId && m.Target == target))
                return Result.Failure(new(ErrorCode.Conflict, "Script is already mounted."));
            _mounted.Add(new(package, target)); return Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> UnmountAsync(string scriptId, Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = _mounted.RemoveAll(m => m.Package.Manifest.Id == scriptId && m.Target == target);
            return removed == 0 ? Result.Failure(new(ErrorCode.Invalid, "Mount not found.")) : Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> DispatchAsync(string eventName, AdapterEnvelope envelope, CancellationToken cancellationToken)
    {
        MountedScript[] handlers;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { handlers = _mounted.Where(m => m.Target == envelope.Adapter && m.Package.Manifest.Events.Contains(eventName, StringComparer.Ordinal))
                .OrderBy(m => m.Package.Manifest.Id, StringComparer.Ordinal).ToArray(); }
        finally { _gate.Release(); }

        Error? firstFailure = null;
        foreach (var mount in handlers)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(mount.Package.Manifest.TimeoutMilliseconds));
            var api = new AdapterScriptApi(_adapters, _audit, mount, envelope, deadline.Token);
            var result = await _executor.ExecuteAsync(mount.Package, mount.Target, eventName, envelope, api, deadline.Token).ConfigureAwait(false);
            if (!result.Ok) firstFailure ??= result.Error;
        }
        return firstFailure is null ? Result.Success() : Result.Failure(firstFailure);
    }

    private bool IsCompatible(string targetType, Address target) => _types.IsAssignableTo(target.TypeId, targetType);

    private sealed class AdapterScriptApi(IAdapterManager manager, IAuditSink audit, MountedScript mount, AdapterEnvelope envelope, CancellationToken ct) : IAdapterScriptApi
    {
        public async Task<string> SendAsync(string outboundMessageJson, CancellationToken cancellationToken)
        {
            if (!mount.Package.Manifest.Capabilities.Contains(ScriptCapabilities.SelfAdapterSend, StringComparer.Ordinal))
            {
                await audit.AppendAsync(new(DateTimeOffset.UtcNow, envelope.EventId, mount.Package.Manifest.Id, mount.Target,
                    ScriptCapabilities.SelfAdapterSend, "denied", "Capability not declared."), cancellationToken).ConfigureAwait(false);
                throw new UnauthorizedAccessException("self.adapter.send was not declared.");
            }
            OutboundMessage? message;
            try { message = JsonSerializer.Deserialize<OutboundMessage>(outboundMessageJson, JsonOptions); }
            catch (JsonException ex) { throw new ArgumentException("Outbound message JSON is invalid.", ex); }
            if (message is null || string.IsNullOrWhiteSpace(message.PlatformConversationId) || string.IsNullOrWhiteSpace(message.Text))
                throw new ArgumentException("Outbound message requires a conversation id and non-empty text.");
            if (!StringComparer.Ordinal.Equals(message.PlatformConversationId, envelope.PlatformConversationId))
            {
                await audit.AppendAsync(new(DateTimeOffset.UtcNow, envelope.EventId, mount.Package.Manifest.Id, mount.Target,
                    ScriptCapabilities.SelfAdapterSend, "denied", "Destination differs from source conversation."), cancellationToken).ConfigureAwait(false);
                throw new UnauthorizedAccessException("This grant only permits replying in the source conversation.");
            }
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, envelope.EventId, mount.Package.Manifest.Id, mount.Target,
                ScriptCapabilities.SelfAdapterSend, "allowed", message.PlatformConversationId), cancellationToken).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var found = manager.Get(mount.Target);
            if (!found.Ok || found.Value is null) throw new InvalidOperationException("Mounted Adapter is no longer available.");
            var sent = await found.Value.SendAsync(message, ct).ConfigureAwait(false);
            if (!sent.Ok || sent.Value is null) throw new InvalidOperationException(sent.Error?.Message ?? "Adapter send failed.");
            return JsonSerializer.Serialize(sent.Value, JsonOptions);
        }
    }
}

public sealed class JsonScriptCatalog(string programRoot, string dataRoot) : IGraphScriptCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public async ValueTask<Result<IReadOnlyList<ScriptPackage>>> LoadAsync(CancellationToken cancellationToken)
    {
        var packages = new List<ScriptPackage>();
        try
        {
            var roots = new[]
            {
                (Path.Combine(programRoot, "resources", "scripts", "builtin"), TrustDomain.Builtin),
                (Path.Combine(dataRoot, "scripts", "trusted"), TrustDomain.Trusted),
                (Path.Combine(dataRoot, "scripts", "restricted"), TrustDomain.Restricted)
            };
            foreach (var (root, expectedDomain) in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                    if (manifestBytes.Length > 64_000) return Result<IReadOnlyList<ScriptPackage>>.Failure(new(ErrorCode.Invalid, "Script manifest exceeds 64 KB."));
                    var manifest = JsonSerializer.Deserialize<ScriptFileManifest>(manifestBytes, Options);
                    if (manifest is null) return Result<IReadOnlyList<ScriptPackage>>.Failure(new(ErrorCode.Invalid, $"Invalid manifest: {manifestPath}"));
                    if (manifest.TrustDomain != expectedDomain)
                        return Result<IReadOnlyList<ScriptPackage>>.Failure(new(ErrorCode.Denied, $"Manifest trust domain does not match its catalog directory: {manifestPath}"));
                    var scriptPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "main.js");
                    var source = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
                    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
                    packages.Add(new(new ScriptManifest(manifest.Id, manifest.Version, manifest.TargetType, manifest.Mount,
                        manifest.Events, manifest.EntryPoint, manifest.Capabilities, manifest.TrustDomain,
                        manifest.MaxStatements, manifest.TimeoutMilliseconds, manifest.MemoryLimitBytes), source, fingerprint));
                }
            }
            return Result<IReadOnlyList<ScriptPackage>>.Success(packages);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<IReadOnlyList<ScriptPackage>>.Failure(new(ErrorCode.Invalid, "Could not load script catalog.", ex.Message)); }
    }
}
