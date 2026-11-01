namespace ALN.Contracts;

/// <summary>Severity gate for the signal log. Audit never uses this scale.</summary>
public enum SignalLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// One dispatch byproduct: an edge that was actually traversed, or a capability that was
/// actually invoked. Correlation is structural — <see cref="StepId"/> is the root event id,
/// shared by everything the activation triggers.
/// </summary>
public sealed record SignalRecord
{
    public long Sequence { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public SignalLevel Level { get; init; } = SignalLevel.Info;
    /// <summary>Coarse bucket: dispatch, capability, session, model, channel, queue.</summary>
    public required string Group { get; init; }
    /// <summary>Edge id for dispatch, capability name for calls, event name for intake.</summary>
    public required string Name { get; init; }
    /// <summary>Root event id of the activation that produced this record.</summary>
    public Guid StepId { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    /// <summary>How the hop happened: emit, call, session.turn, model.request.</summary>
    public string Mechanism { get; init; } = "";
    /// <summary>ok, failed, denied or timeout. Never "probably ok".</summary>
    public string Outcome { get; init; } = "ok";
    public double DurationMs { get; init; }
    public IReadOnlyDictionary<string, string>? Detail { get; init; }
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
