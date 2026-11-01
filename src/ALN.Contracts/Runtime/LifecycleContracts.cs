namespace ALN.Contracts;

public enum LifecyclePhase { Start, Stop }

/// <summary>
/// One uniform startup/shutdown signal, handed down the tree: the core sends it to managers, a
/// manager re-sends the same shape to its children. Nothing starts because a field was set or a
/// constructor ran — it starts because this signal reached it.
/// </summary>
public sealed record LifecycleSignal(LifecyclePhase Phase, DateTimeOffset At)
{
    /// <summary>Where this copy was sent; null means "the core itself".</summary>
    public Address? Branch { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();

    public LifecycleSignal For(Address branch) => this with { Branch = branch };
}

/// <summary>
/// A manager that starts and stops its own children when the signal arrives. Implementations must
/// be idempotent: a second start is a no-op, not a second instance.
/// </summary>
public interface ILifecycleParticipant
{
    string Name { get; }
    ValueTask<Result> StartAsync(LifecycleSignal signal, CancellationToken cancellationToken);
    ValueTask<Result> StopAsync(LifecycleSignal signal, CancellationToken cancellationToken);
}

/// <summary>What happened to one participant: ok, failed, or skipped because a dependency did not start.</summary>
public sealed record LifecycleOutcome(string Name, string Outcome, string? Message = null);

/// <summary>
/// Result of starting the whole tree. Startup never aborts the world: a failed participant is
/// reported, its dependents are skipped, and everything independent is still running.
/// </summary>
public sealed record LifecycleReport(IReadOnlyList<LifecycleOutcome> Outcomes)
{
    public IEnumerable<LifecycleOutcome> Failed => Outcomes.Where(o => o.Outcome == "failed");
    public IEnumerable<LifecycleOutcome> Skipped => Outcomes.Where(o => o.Outcome == "skipped");
    public bool AllRunning => Outcomes.Count > 0 && Outcomes.All(o => o.Outcome == "ok");
}
