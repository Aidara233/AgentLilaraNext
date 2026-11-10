using System.Collections.Concurrent;
using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The pump's one job that is worth a test: <b>who waits for whom</b>.
///
/// It used to run one event at a time, and that turned a forty-second compaction inside one conversation into a host
/// that stopped answering everywhere — the lifecycle events, the other conversations, and the instructions queued
/// behind it. The fix is a lane per address: within a lane strictly in order, across lanes never waiting on each other.
/// These tests are the two halves of that sentence, plus the two ways a naive version breaks.
/// </summary>
public sealed class EventPumpTests
{
    private static readonly Address Group = new("channel", "conversation", "group");
    private static readonly Address Direct = new("channel", "conversation", "direct");
    private static readonly Address Adapter = new("adapter", "qq", "one");

    private static RoutedEvent Event(string name, Address source, Address? target = null) =>
        new(name, source, Guid.NewGuid(), "{}", target);

    /// <summary>
    /// Runs the pump until <paramref name="stop"/> says so, then drains.
    ///
    /// The cancellation is a test fence, not part of the assertion: a dispatch that never returns must not turn a
    /// failing test into a hung run.
    /// </summary>
    private static async Task RunAsync(EventQueue queue, Func<RoutedEvent, CancellationToken, ValueTask<Result>> dispatch,
        Func<bool> stop)
    {
        using var fence = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pump = new EventPump(queue, dispatch, NullSignalLog.Instance);
        var running = pump.RunAsync(fence.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!stop() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        queue.Complete();
        try { await running; }
        catch (OperationCanceledException) { /* the fence fired; the assertions say whether that mattered */ }
    }

    [Fact]
    public async Task Events_for_one_address_run_in_the_order_they_were_queued()
    {
        var queue = new EventQueue(64);
        var seen = new ConcurrentQueue<string>();
        var count = 0;
        for (var index = 0; index < 5; index++)
            await queue.PublishAsync(Event($"e{index}", Group), default);

        await RunAsync(queue, async (routedEvent, cancellationToken) =>
        {
            // Slow enough that an unordered pump would finish out of order rather than by luck.
            await Task.Delay(5, cancellationToken);
            seen.Enqueue(routedEvent.Name);
            Interlocked.Increment(ref count);
            return Result.Success();
        }, () => Volatile.Read(ref count) == 5);

        Assert.Equal(["e0", "e1", "e2", "e3", "e4"], seen.ToArray());
    }

    [Fact]
    public async Task One_conversation_slow_does_not_hold_up_another()
    {
        // The failure this is about: a Channel's turn waits for a model, and while it waits nothing else happens.
        // Asserted by finishing order rather than by "did it run at all": if the pump were back to one at a time, the
        // quick event could not overtake the slow one, whatever the delays were.
        var queue = new EventQueue(64);
        var order = new ConcurrentQueue<string>();

        await queue.PublishAsync(Event("slow", Group), default);
        await queue.PublishAsync(Event("quick", Direct), default);

        await RunAsync(queue, async (routedEvent, cancellationToken) =>
        {
            if (routedEvent.Name == "slow") await Task.Delay(200, cancellationToken);
            order.Enqueue(routedEvent.Name);
            return Result.Success();
        }, () => order.Count == 2);

        Assert.Equal(["quick", "slow"], order.ToArray());
    }

    [Fact]
    public async Task An_instruction_on_the_adapter_lane_does_not_queue_behind_a_conversation()
    {
        // This is why instructions got their head start for free: an instruction arrives as an Adapter envelope, so its
        // lane is the Adapter, never the Channel whose turn happens to be running.
        var queue = new EventQueue(64);
        var order = new ConcurrentQueue<string>();

        await queue.PublishAsync(Event(EventNames.ChannelMessageAccepted, Group), default);
        await queue.PublishAsync(Event(EventNames.AdapterEnvelope, Adapter), default);

        await RunAsync(queue, async (routedEvent, cancellationToken) =>
        {
            if (routedEvent.Name == EventNames.ChannelMessageAccepted) await Task.Delay(200, cancellationToken);
            order.Enqueue(routedEvent.Name);
            return Result.Success();
        }, () => order.Count == 2);

        Assert.Equal([EventNames.AdapterEnvelope, EventNames.ChannelMessageAccepted], order.ToArray());
    }

    [Fact]
    public async Task A_failed_event_does_not_stop_the_rest_of_its_lane()
    {
        var queue = new EventQueue(64);
        var after = new TaskCompletionSource();

        await queue.PublishAsync(Event("boom", Group), default);
        await queue.PublishAsync(Event("after", Group), default);

        await RunAsync(queue, (routedEvent, cancellationToken) =>
        {
            if (routedEvent.Name == "boom") throw new InvalidOperationException("dispatch is supposed to return a Result");
            after.TrySetResult();
            return ValueTask.FromResult(Result.Success());
        }, () => after.Task.IsCompleted);

        Assert.True(after.Task.IsCompleted, "one crashing event silenced its whole lane");
    }

    [Fact]
    public async Task Nothing_is_left_running_when_the_pump_returns()
    {
        // A half-finished round leaves a Channel row nobody answers and a mailbox file nobody acknowledges, and both are
        // replayed on the next boot — so draining is part of the promise, not tidiness.
        var queue = new EventQueue(64);
        var finished = 0;
        for (var index = 0; index < 6; index++)
            await queue.PublishAsync(Event($"e{index}", index % 2 == 0 ? Group : Direct), default);

        var press = new CancellationTokenSource();
        var pump = new EventPump(queue, async (routedEvent, cancellationToken) =>
        {
            await Task.Delay(20, cancellationToken);
            Interlocked.Increment(ref finished);
            return Result.Success();
        }, NullSignalLog.Instance);
        var running = pump.RunAsync(press.Token);
        queue.Complete();
        await running;

        Assert.Equal(6, Volatile.Read(ref finished));
        press.Dispose();
    }
}
