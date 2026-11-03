using System.Text.Json;
using ALN.Contracts;

namespace ALN.Clock;

[Module(ClockId, Category = "clock", Role = ModuleRole.Manager, Singleton = true)]
public sealed class ClockSource : IClockManager, IModule
{
    public const string ClockId = ModuleNames.Clock;
    public ModuleId Id { get; } = new(ClockId);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SchedulerResolution = TimeSpan.FromMilliseconds(20);
    private readonly IEventSink _events;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, Subscription> _subscriptions = new();
    private readonly object _gate = new();

    public ClockSource(IEventSink events, TimeProvider? timeProvider = null, Address? address = null)
    {
        _events = events;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Address = address ?? new Address("clock", "system", "1");
        // No check against the name: an id is a name, and a name that happens to start with something else is
        // still a legal clock address. What can be checked here is that it names something at all.
        if (!Address.IsInstance) throw new ArgumentException("Clock source requires a concrete clock address.", nameof(address));
    }
    public Address Address { get; }

    public ValueTask<Result<Guid>> RegisterAsync(Address target, TimeSpan interval, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!target.IsInstance) return ValueTask.FromResult(Result<Guid>.Failure(new(ErrorCode.Invalid, "Clock target must be a concrete module instance.")));
        if (interval < SchedulerResolution || interval > TimeSpan.FromDays(1))
            return ValueTask.FromResult(Result<Guid>.Failure(new(ErrorCode.Invalid, "Clock interval must be between scheduler resolution and 1 day.")));
        var id = Guid.NewGuid();
        lock (_gate) _subscriptions.Add(id, new(id, target, interval, _timeProvider.GetUtcNow() + interval, null));
        return ValueTask.FromResult(Result<Guid>.Success(id));
    }

    public ValueTask<Result> UnregisterAsync(Guid subscriptionId, Address target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscriptionId, out var subscription) || subscription.Target != target)
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, "Clock subscription not found for target.")));
            _subscriptions.Remove(subscriptionId);
            return ValueTask.FromResult(Result.Success());
        }
    }

    public ValueTask<Result<int>> UnregisterTargetAsync(Address target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var ids = _subscriptions.Values.Where(s => s.Target == target).Select(s => s.Id).ToArray();
            foreach (var id in ids) _subscriptions.Remove(id);
            return ValueTask.FromResult(Result<int>.Success(ids.Length));
        }
    }

    public ValueTask<Result> AcknowledgeAsync(Guid subscriptionId, Address target, Guid eventId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscriptionId, out var subscription) || subscription.Target != target || subscription.PendingEventId != eventId)
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, "Clock pulse is stale or not registered for target.")));
            _subscriptions[subscriptionId] = subscription with { NextDue = _timeProvider.GetUtcNow() + subscription.Interval, PendingEventId = null };
            return ValueTask.FromResult(Result.Success());
        }
    }

    public bool IsRegistered(Guid subscriptionId, Address target)
    { lock (_gate) return _subscriptions.TryGetValue(subscriptionId, out var subscription) && subscription.Target == target; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SchedulerResolution, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await PulseDueAsync(_timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<int>> PulseDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Subscription[] due;
        lock (_gate)
        {
            var dueNow = _subscriptions.Values.Where(s => s.PendingEventId is null && s.NextDue <= now).ToArray();
            due = dueNow.Select(s => s with { PendingEventId = Guid.NewGuid() }).ToArray();
            foreach (var subscription in due) _subscriptions[subscription.Id] = subscription;
        }
        var emitted = 0;
        foreach (var subscription in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eventId = subscription.PendingEventId!.Value;
            var pulse = new ClockPulse(subscription.Id, now);
            var routed = new RoutedEvent(EventNames.ClockPulse, Address, eventId, JsonSerializer.Serialize(pulse, JsonOptions), subscription.Target);
            var result = await _events.PublishAsync(routed, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                lock (_gate)
                    if (_subscriptions.TryGetValue(subscription.Id, out var current) && current.PendingEventId == eventId)
                        _subscriptions[subscription.Id] = current with { PendingEventId = null, NextDue = now + current.Interval };
                return Result<int>.Failure(result.Error!);
            }
            emitted++;
        }
        return Result<int>.Success(emitted);
    }

    private sealed record Subscription(Guid Id, Address Target, TimeSpan Interval, DateTimeOffset NextDue, Guid? PendingEventId);
}
