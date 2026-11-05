using ALN.Contracts;

namespace ALN.Clock;

/// <summary>
/// The clock, as a trigger source: every interval it triggers every sink that asked for a rhythm.
///
/// It used to be a subscription registry that adapters called into by address ("register me, I am a polling
/// adapter"), which had two problems: the clock knew about adapter addresses, and the knowledge that a heartbeat was
/// needed lived in two places at once (the adapter's own registration and the manager's). It is the other way round
/// now — a sink declares its rhythm by reporting <see cref="ITriggerSink.PreferredInterval"/>, and whoever holds a
/// clock attaches what wants a beat. The clock holds no addresses and knows no adapter; it would work just as well
/// for a module that wants to flush every minute.
/// </summary>
[Module(ClockId, Category = "clock", Role = ModuleRole.Manager, Singleton = true)]
public sealed class ClockSource : IClockManager, ITriggerSource, IModule
{
    public const string ClockId = ModuleNames.Clock;
    public const string SourceName = "Clock";

    public ModuleId Id { get; } = new(ClockId);

    private static readonly TimeSpan SchedulerResolution = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan LongestInterval = TimeSpan.FromDays(1);
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<ModuleId, Beat> _beats = new();
    private readonly object _gate = new();

    public ClockSource(IEventSink events, TimeProvider? timeProvider = null, Address? address = null)
    {
        _ = events;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Address = address ?? new Address("clock", "system", "1");
        // No check against the name: an id is a name, and a name that happens to start with something else is
        // still a legal clock address. What can be checked here is that it names something at all.
        if (!Address.IsInstance) throw new ArgumentException("Clock source requires a concrete clock address.", nameof(address));
    }

    public Address Address { get; }

    public string Source => SourceName;

    /// <summary>How many sinks are on a rhythm right now. Reported, never used to decide anything.</summary>
    public int BeatCount
    {
        get { lock (_gate) return _beats.Count; }
    }

    public Result Attach(ModuleId sink, ITriggerSink trigger)
    {
        if (trigger is null) return Result.Failure(new(ErrorCode.Invalid, "A trigger sink is required."));
        if (sink.IsEmpty) return Result.Failure(new(ErrorCode.Invalid, "A trigger sink needs an id."));
        if (trigger.PreferredInterval is not { } interval)
        {
            // No rhythm of its own: it wants to be pushed, not scheduled. That is an answer, not a refusal — and it
            // is why this succeeds without holding on to anything.
            return Result.Success();
        }
        if (interval < SchedulerResolution || interval > LongestInterval)
            return Result.Failure(new(ErrorCode.Invalid,
                $"Sink {sink} asked for {interval}, outside {SchedulerResolution}..{LongestInterval}."));

        lock (_gate) _beats[sink] = new(sink, trigger, interval, _timeProvider.GetUtcNow() + interval);
        return Result.Success();
    }

    public Result Detach(ModuleId sink)
    {
        lock (_gate) _beats.Remove(sink);
        // Absent is what detaching wants: a sink that went away twice is not a problem to report.
        return Result.Success();
    }

    public Task RunAsync(CancellationToken cancellationToken) => RunCoreAsync(cancellationToken);

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SchedulerResolution, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await PulseDueAsync(_timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One scheduler step: every sink whose beat is due gets one trigger, at most once per step.
    ///
    /// The next beat is scheduled from the clock's own time, not from the argument — a caller that passes a future
    /// instant would otherwise push every later beat into that future, and "pulse one second from now" twice in a row
    /// would find the second one not due. It is also scheduled <b>before</b> the call, so a slow sink cannot make the
    /// clock drift and a sink that fails cannot stop the others: one failure is one failure.
    /// </summary>
    public async ValueTask<Result<int>> PulseDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Beat[] due;
        lock (_gate)
        {
            due = _beats.Values.Where(beat => beat.NextDue <= now).ToArray();
            var nowForSchedule = _timeProvider.GetUtcNow();
            foreach (var beat in due) _beats[beat.Sink] = beat with { NextDue = nowForSchedule + beat.Interval };
        }

        var fired = 0;
        foreach (var beat in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fired++;
            try
            {
                await beat.Trigger
                    .OnTriggerAsync(new TriggerSignal(TriggerReason.Schedule, SourceName, now), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // A sink that fails one beat is not unregistered: the next beat may be the one that works, and a
                // clock that silently dropped a module would be worse than one that keeps knocking.
            }
        }
        return Result<int>.Success(fired);
    }

    private sealed record Beat(ModuleId Sink, ITriggerSink Trigger, TimeSpan Interval, DateTimeOffset NextDue);
}
