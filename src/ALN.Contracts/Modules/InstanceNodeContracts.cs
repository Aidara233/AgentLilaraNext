using ALN.Contracts;

namespace ALN.Contracts;

/// <summary>
/// Where a manager puts the instances it holds so that the rest of the system can see them (docs/23 §4 step 1).
///
/// Until now "this manager holds this instance" existed only as a dictionary inside the manager: the catalogue could
/// ask about it, but the graph could not, so a mount pointing at an instance pointed at something that was not a
/// node. This port is the smallest thing that fixes it — the manager announces the instance, the graph draws the edge.
///
/// It says nothing about lifecycle. The manager creates, starts and stops its instances and will keep doing so;
/// what it gains here is that the relation is a fact somebody else can read.
/// </summary>
public interface IInstanceNodeHost
{
    /// <summary>Announces an instance the manager now holds. False when the graph cannot take it (reported by the caller).</summary>
    ValueTask<Result> AttachAsync(ModuleId master, ModuleId instance, string typeId,
        IReadOnlyList<string>? kinds, CancellationToken cancellationToken);

    /// <summary>Announces that the manager no longer holds it. Absent is not an error: a manager may be tearing down.</summary>
    ValueTask<Result> DetachAsync(ModuleId master, ModuleId instance, CancellationToken cancellationToken);
}

/// <summary>
/// An instance seen from the graph: a node with a name and two no-op lifecycle methods.
///
/// The no-ops are the point, not an omission. The manager owns this instance's lifecycle and calling back into it
/// from here would give two owners to one thing. This node exists so that an edge can point at it.
/// </summary>
public sealed class InstanceNode(ModuleId id) : IKernelModule
{
    public ModuleId Id { get; } = id;

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
}

/// <summary>
/// A manager that can be told where to announce its instances. Separate from <see cref="IChannelManager"/> because a
/// test fake must not have to know that a graph exists; the host wires it once, right after startup.
/// </summary>
public interface IInstanceNodeSink
{
    void UseInstanceNodes(IInstanceNodeHost nodes);
}
