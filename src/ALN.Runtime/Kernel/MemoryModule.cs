using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// Memory as a module: one instance, two ways into a round (docs/22, C2).
///
/// It exists to stop memory being a piece of the session manager. Memory has its own storage, its own lifetime and
/// its own failures; the only thing it needs from the rest of the system is a place to say "here is a line for this
/// round" and "here are the tools". Both of those are the session extension point, which is the core's, so this
/// module talks to the core and to nothing else — no other module holds it, and it holds none.
///
/// Registering in Initialize (not in a constructor) is what makes the graph honest: before Initialize nothing is
/// allowed to be visible to anything else.
/// </summary>
public sealed class MemoryModule(IMemoryStore store, ISessionExtensionRegistry extensions, TimeProvider? timeProvider = null)
    : IKernelModule
{
    public const string TypeId = "memory.store";

    private readonly MemoryContextSource _floating = new(store);
    private readonly MemoryToolSource _tools = new(store, timeProvider);
    private bool _registered;

    public ModuleId Id { get; } = new(ModuleNames.Memory);

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var floating = extensions.Register(_floating);
        if (!floating.Ok) return ValueTask.FromResult(floating);
        var tools = extensions.Register(_tools);
        if (!tools.Ok)
        {
            // Half a module is worse than none: a floating line that names recall while recall is missing is a
            // promise to the model that nothing can keep.
            extensions.Unregister(_floating.Id);
            return ValueTask.FromResult(tools);
        }
        _registered = true;
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>Nothing to start: the sources answer per round, so there is no state to warm up.</summary>
    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            extensions.Unregister(_floating.Id);
            extensions.Unregister(_tools.Id);
            _registered = false;
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    /// <summary>A store that is down is worth knowing about, and nothing more: the next round asks again.</summary>
    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Result.Success());
    }
}
