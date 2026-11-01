namespace ALN.Contracts;

public sealed record AuditRecord(DateTimeOffset At, Guid EventId, string Actor, Address Target,
    string Action, string Outcome, string? Detail = null);
public interface IAuditSink
{
    ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken);
}
