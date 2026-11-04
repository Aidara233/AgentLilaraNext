using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Turns something that already speaks the older <see cref="ILifecycleParticipant"/> protocol into a kernel node.
///
/// It exists so that the managers do not have to be rewritten in one commit to be driven by the kernel. What it
/// deliberately does <b>not</b> do is invent work: a manager's own startup happens in the Start signal it already
/// understands, so <see cref="InitializeAsync"/> is a no-op. The day a manager needs to build something before
/// anything is wired, it implements <see cref="IKernelModule"/> itself and this adapter disappears from its path.
/// </summary>
public sealed class LifecycleModule(ILifecycleParticipant participant) : IKernelModule
{
    public ModuleId Id { get; } = new(participant.Name);

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
