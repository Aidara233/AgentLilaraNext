namespace ALN.Contracts;

public sealed record ExecutionRecord(Guid ExecutionId, string ScriptId, Address Target,
    string EventName, bool Succeeded, string? Error, DateTimeOffset StartedAt, DateTimeOffset FinishedAt)
{
    public Guid EventId { get; init; }
    public string Fingerprint { get; init; } = "";
}
public interface IExecutionJournal
{
    void Record(ExecutionRecord record);
    IReadOnlyList<ExecutionRecord> Snapshot();
}
public sealed record AuditRecord(DateTimeOffset At, Guid EventId, string Actor, Address Target,
    string Action, string Outcome, string? Detail = null);
public interface IAuditSink
{
    ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken);
}
