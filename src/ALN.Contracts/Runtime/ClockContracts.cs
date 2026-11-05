namespace ALN.Contracts;

/// <summary>
/// The scheduler: it knows how often to look, not who it is looking at.
///
/// What used to be here — <c>RegisterAsync(Address, interval)</c>, <c>UnregisterAsync</c>,
/// <c>UnregisterTargetAsync</c>, <c>AcknowledgeAsync</c>, <c>IsRegistered</c> — is gone on purpose: those methods made
/// the clock a registry of adapter addresses, so "this adapter needs a heartbeat" lived in the clock rather than in
/// the adapter. A sink now declares its rhythm by implementing <see cref="ITriggerSink"/>, and the clock is one
/// <see cref="ITriggerSource"/> among several possible ones.
///
/// There is no pulse record either: a beat is a method call on the sink (<see cref="TriggerSignal"/>), not an event
/// anything else can subscribe to. The record that used to be here was what a routed <c>clock.pulse</c> carried.
/// </summary>
public interface IClockManager
{
    Address Address { get; }

    /// <summary>Runs the scheduler until cancelled. A failure inside one beat never stops the loop.</summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Fires every beat that is due at <paramref name="now"/>. Returns how many were fired.</summary>
    ValueTask<Result<int>> PulseDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
