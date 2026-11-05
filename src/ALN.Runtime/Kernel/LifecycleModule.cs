using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Turns something that already speaks the older <see cref="ILifecycleParticipant"/> protocol into a kernel instance.
///
/// It exists so that the managers do not have to be rewritten in one commit to be driven by the kernel. What it
/// deliberately does <b>not</b> do is invent work: a manager's own startup happens in the Start signal it already
/// understands, so <see cref="InitializeAsync"/> is a no-op. The day a manager needs to build something before
/// anything is wired, it implements <see cref="IKernelModule"/> itself and this adapter disappears from its path.
///
/// The id is a parameter because a manager's participant name ("channels") is not the name the rest of the process
/// calls it by (<see cref="ModuleNames.ChannelManager"/>), and an instance edge has to name the same instance the
/// receipt shows.
/// </summary>
public sealed class LifecycleModule(ILifecycleParticipant participant, ModuleId? id = null) : IKernelModule, IModuleWiring
{
    public ModuleId Id { get; } = id ?? new ModuleId(participant.Name);

    /// <summary>
    /// The manager behind this instance, when it is the manager itself. A manager that holds instances announces them
    /// through the graph, and the graph asks the master to accept each one — the master here is the manager, not
    /// this adapter.
    /// </summary>
    public Result WireSlave(ModuleId slaveId, IReadOnlyList<string> kinds, IKernelModule instance) =>
        participant is IModuleWiring wiring
            ? wiring.WireSlave(slaveId, kinds, instance)
            : Result.Failure(new(ErrorCode.Invalid, $"{Id} does not hold instances."));

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) =>
        participant.StartAsync(new LifecycleSignal(LifecyclePhase.Start, DateTimeOffset.UtcNow), cancellationToken);

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken) =>
        participant.StopAsync(new LifecycleSignal(LifecyclePhase.Stop, DateTimeOffset.UtcNow), cancellationToken);

    /// <summary>
    /// Abort is the same call: there is no cheaper way to tell a manager to stop, and pretending otherwise would
    /// mean this adapter promising a kind of stop the manager cannot perform.
    /// </summary>
    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) =>
        participant.StopAsync(new LifecycleSignal(LifecyclePhase.Stop, DateTimeOffset.UtcNow), cancellationToken);

    /// <summary>A manager that can say something about its own failure gets the chance; the rest are recorded by the kernel.</summary>
    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) =>
        participant is IModuleErrorHandler handler
            ? handler.OnErrorAsync(error, cancellationToken)
            : ValueTask.FromResult(Result.Success());
}

/// <summary>Optional: a manager that wants to react when the kernel records a failure that happened inside it.</summary>
public interface IModuleErrorHandler
{
    ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken);
}
