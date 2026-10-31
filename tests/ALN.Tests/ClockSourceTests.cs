using System.Text.Json;
using ALN.Adapter.File;
using ALN.Clock;
using ALN.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;

namespace ALN.Tests;

public sealed class ClockSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-clock-tests-" + Guid.NewGuid().ToString("N"));
    public ClockSourceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Pulse_is_targeted_single_flight_acknowledged_and_unregistered()
    {
        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var queue = new EventQueue(8);
        var clock = new ClockSource(queue, time);
        var target = new Address("adapter", "file", "one");
        var registration = await clock.RegisterAsync(target, TimeSpan.FromMilliseconds(200), default);
        Assert.True(registration.Ok);

        Assert.Equal(0, (await clock.PulseDueAsync(now.AddMilliseconds(199), default)).Value);
        Assert.Equal(1, (await clock.PulseDueAsync(now.AddMilliseconds(200), default)).Value);
        await using var enumerator = queue.ReadAllAsync(default).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var pulseEvent = enumerator.Current;
        Assert.Equal(EventNames.ClockPulse, pulseEvent.Name);
        Assert.Equal(clock.Address, pulseEvent.Source);
        Assert.Equal(target, pulseEvent.Target);

        Assert.Equal(0, (await clock.PulseDueAsync(now.AddSeconds(5), default)).Value);
        var pulse = JsonSerializer.Deserialize<ClockPulse>(pulseEvent.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True((await clock.AcknowledgeAsync(pulse.SubscriptionId, target, pulseEvent.EventId, default)).Ok);

        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, (await clock.PulseDueAsync(time.GetUtcNow(), default)).Value);
        Assert.True(await enumerator.MoveNextAsync());
        var nextEvent = enumerator.Current;
        Assert.True((await clock.UnregisterAsync(pulse.SubscriptionId, target, default)).Ok);
        Assert.False(clock.IsRegistered(pulse.SubscriptionId, target));
        Assert.False((await clock.AcknowledgeAsync(pulse.SubscriptionId, target, nextEvent.EventId, default)).Ok);
        Assert.Equal(0, (await clock.PulseDueAsync(time.GetUtcNow().AddSeconds(1), default)).Value);
    }

    [Fact]
    public async Task FileAdapter_registers_polling_clock_on_start_and_removes_it_on_stop()
    {
        var queue = new EventQueue(8);
        var clock = new ClockSource(queue);
        var mailbox = new FileMailbox(Path.Combine(_root, "adapter"));
        var address = new Address("adapter", "file", "clocked");
        var adapter = new FileAdapter(new(address, new("file", "clocked", AdapterDirection.Both)), mailbox, queue, clock);
        Assert.True((await adapter.StartAsync(default)).Ok);
        Assert.Equal(1, (await clock.PulseDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), default)).Value);
        await adapter.StopAsync(default);
        Assert.Equal(0, (await clock.PulseDueAsync(DateTimeOffset.UtcNow.AddSeconds(2), default)).Value);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }
}
