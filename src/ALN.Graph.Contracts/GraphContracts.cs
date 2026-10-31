using System.Text.Json.Nodes;
using ALN.Contracts;

namespace ALN.Graph.Contracts;

public enum MountMode { Default, Manual }
public enum TrustDomain { Builtin, Trusted, Restricted }

public sealed record ScriptManifest(
    string Id,
    int Version,
    string TargetType,
    MountMode Mount,
    string[] Events,
    string EntryPoint,
    string[] Capabilities,
    TrustDomain TrustDomain = TrustDomain.Trusted,
    int MaxStatements = 10_000,
    int TimeoutMilliseconds = 1_000,
    long MemoryLimitBytes = 16 * 1024 * 1024);

public sealed record ScriptPackage(ScriptManifest Manifest, string Source, string Fingerprint = "");
public sealed record MountedScript(ScriptPackage Package, Address Target);

public interface IGraphRuntime : IInstanceBindings
{
    ValueTask<Result> LoadAsync(IEnumerable<ScriptPackage> packages, CancellationToken cancellationToken);
    ValueTask<Result> MountAsync(string scriptId, Address target, CancellationToken cancellationToken);
    ValueTask<Result> UnmountAsync(string scriptId, Address target, CancellationToken cancellationToken);
    IReadOnlyList<MountedScript> Mounted { get; }
    ValueTask<Result> DispatchAsync(string eventName, AdapterEnvelope envelope, CancellationToken cancellationToken);
}

public interface IScriptExecutor
{
    ValueTask<Result> ValidateAsync(ScriptPackage package, CancellationToken cancellationToken);
    ValueTask<Result> ExecuteAsync(ScriptPackage package, Address target, string eventName,
        AdapterEnvelope envelope, IAdapterScriptApi adapter, CancellationToken cancellationToken);
}

public interface IAdapterScriptApi
{
    Task<string> SendAsync(string outboundMessageJson, CancellationToken cancellationToken);
}

public interface IGraphScriptCatalog
{
    ValueTask<Result<IReadOnlyList<ScriptPackage>>> LoadAsync(CancellationToken cancellationToken);
}

public sealed record ScriptContext(Address Target, IAdapterScriptApi Adapter);
public sealed record ScriptFileManifest(
    string Id, int Version, string TargetType, MountMode Mount, string[] Events,
    string EntryPoint, string[] Capabilities, TrustDomain TrustDomain,
    int MaxStatements = 10_000, int TimeoutMilliseconds = 1_000, long MemoryLimitBytes = 16 * 1024 * 1024);
public static class ScriptEvents
{
    public const string AdapterEnvelope = "adapter.envelope";
}
public static class ScriptCapabilities
{
    public const string SelfAdapterSend = "self.adapter.send";
}
