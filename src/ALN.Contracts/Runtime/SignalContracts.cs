namespace ALN.Contracts;

/// <summary>Severity gate for the signal log. Audit never uses this scale.</summary>
public enum SignalLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// One dispatch byproduct: an edge that was actually traversed, or a capability that was
/// actually invoked, or a module's own note about what it did.
/// </summary>
public sealed record SignalRecord
{
    public long Sequence { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public SignalLevel Level { get; init; } = SignalLevel.Info;
    /// <summary>Identity of this record. Allocated on entry so children can point at it.</summary>
    public Guid Id { get; init; }
    /// <summary>Enclosing record; null only for records with no ambient dispatch (a new lane).</summary>
    public Guid? ParentId { get; init; }
    /// <summary>Coarse bucket: queue, dispatch, capability, session, model, module, channel.</summary>
    public required string Group { get; init; }
    /// <summary>Edge id for dispatch, capability name for calls, module id for point logs.</summary>
    public required string Name { get; init; }
    /// <summary>Root event id of the activation that produced this record.</summary>
    public Guid StepId { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    /// <summary>How the hop happened: emit, call, session.turn, model.request, log.</summary>
    public string Mechanism { get; init; } = "";
    /// <summary>ok, failed, denied, timeout or truncated. Never "probably ok".</summary>
    public string Outcome { get; init; } = "ok";
    public double DurationMs { get; init; }
    public IReadOnlyDictionary<string, string>? Detail { get; init; }
}

/// <summary>
/// Ambient dispatch position. Every record written while a scope is open attaches to it, so
/// module notes, capability calls and model requests all land on the same tree as the edge
/// that caused them instead of becoming a second, unlinkable stream.
/// </summary>
public static class SignalScope
{
    private sealed record Frame(Guid Id, Guid StepId);
    private static readonly AsyncLocal<Frame?> Current = new();

    public static Guid? CurrentId => Current.Value?.Id;
    public static Guid? StepId => Current.Value?.StepId;

    /// <summary>Opens a scope for one boundary crossing. Dispose closes it and restores the parent.</summary>
    public static IDisposable Enter(Guid id, Guid stepId)
    {
        var previous = Current.Value;
        Current.Value = new Frame(id, stepId);
        return new Restore(previous);
    }

    private sealed class Restore(Frame? previous) : IDisposable
    {
        private Frame? _previous = previous;
        public void Dispose() => Current.Value = _previous;
    }
}

/// <summary>
/// Signal log sink. Writes are asynchronous and lossy by design: they must never throw into
/// dispatch, never block it, and never carry security accountability (that is the audit sink).
/// </summary>
public interface ISignalLog
{
    SignalLevel MinLevel { get; }
    bool IsEnabled(SignalLevel level);
    void Write(SignalRecord record);
    IReadOnlyList<SignalRecord> Snapshot();
}

/// <summary>Log that records nothing; used where a log is optional (tests, memory-only hosts).</summary>
public sealed class NullSignalLog : ISignalLog
{
    public static readonly NullSignalLog Instance = new();
    public SignalLevel MinLevel => SignalLevel.Error;
    public bool IsEnabled(SignalLevel level) => false;
    public void Write(SignalRecord record) { }
    public IReadOnlyList<SignalRecord> Snapshot() => [];
}

/// <summary>
/// Module-facing point log: business narration, not graph edges. Three levels only —
/// there is no Debug here, because "log everything then grep" is how a log becomes unreadable.
/// </summary>
public interface IModuleLogger
{
    void Info(string message, IReadOnlyDictionary<string, string>? detail = null);
    void Warn(string message, IReadOnlyDictionary<string, string>? detail = null);
    void Error(string message, IReadOnlyDictionary<string, string>? detail = null);
}

public interface IModuleLoggerFactory
{
    IModuleLogger For(string moduleId);
}
