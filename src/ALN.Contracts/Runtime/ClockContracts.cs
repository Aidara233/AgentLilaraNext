namespace ALN.Contracts;

public sealed record ClockPulse(Guid SubscriptionId, DateTimeOffset At);

public interface IClockManager
{
    Address Address { get; }
    ValueTask<Result<Guid>> RegisterAsync(Address target, TimeSpan interval, CancellationToken cancellationToken);
    ValueTask<Result> UnregisterAsync(Guid subscriptionId, Address target, CancellationToken cancellationToken);
    ValueTask<Result<int>> UnregisterTargetAsync(Address target, CancellationToken cancellationToken);
    ValueTask<Result> AcknowledgeAsync(Guid subscriptionId, Address target, Guid eventId, CancellationToken cancellationToken);
    bool IsRegistered(Guid subscriptionId, Address target);
    Task RunAsync(CancellationToken cancellationToken);
    ValueTask<Result<int>> PulseDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IPollingAdapter
{
    ValueTask<Result<int>> PollOnceAsync(CancellationToken cancellationToken = default);
}
