using ALN.Adapter.File;
using ALN.Clock;
using ALN.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;

namespace ALN.Tests;

public sealed class ClockTriggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-clock-tests-" + Guid.NewGuid().ToString("N"));
    public ClockTriggerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_sink_that_reports_a_rhythm_is_triggered_once_per_interval()
    {
        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var clock = new ClockSource(new EventQueue(8), time);
        var sink = new CountingSink(TimeSpan.FromMilliseconds(200));
        Assert.True(clock.Attach(new ModuleId("adapter.file#one"), sink).Ok);

        // Not yet: the beat is one interval away.
        time.Advance(TimeSpan.FromMilliseconds(199));
        Assert.Equal(0, (await clock.PulseDueAsync(time.GetUtcNow(), default)).Value);
        Assert.Empty(sink.Triggers);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, (await clock.PulseDueAsync(time.GetUtcNow(), default)).Value);
        var trigger = Assert.Single(sink.Triggers);
        Assert.Equal(TriggerReason.Schedule, trigger.Reason);
        Assert.Equal(ClockSource.SourceName, trigger.Source);

        // The next beat was scheduled before the call, from the clock's own time: no drift, no double fire.
        time.Advance(TimeSpan.FromMilliseconds(199));
        Assert.Equal(0, (await clock.PulseDueAsync(time.GetUtcNow(), default)).Value);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, (await clock.PulseDueAsync(time.GetUtcNow(), default)).Value);
    }

    [Fact]
    public async Task A_sink_without_a_rhythm_is_accepted_but_never_scheduled()
    {
        var clock = new ClockSource(new EventQueue(8), new MutableTimeProvider(DateTimeOffset.UtcNow));
        var sink = new CountingSink(null);

        // "No rhythm of mine" is an answer, not a refusal: the sink wants to be pushed, not scheduled.
        Assert.True(clock.Attach(new ModuleId("thing#one"), sink).Ok);
        Assert.Equal(0, clock.BeatCount);
        Assert.Equal(0, (await clock.PulseDueAsync(DateTimeOffset.UtcNow.AddDays(1), default)).Value);
        Assert.Empty(sink.Triggers);
    }

    [Fact]
    public async Task A_sink_that_fails_one_beat_is_not_dropped_and_the_others_still_fire()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var clock = new ClockSource(new EventQueue(8), time);
        var broken = new CountingSink(TimeSpan.FromMilliseconds(200)) { Fail = true };
        var healthy = new CountingSink(TimeSpan.FromMilliseconds(200));
        Assert.True(clock.Attach(new ModuleId("broken"), broken).Ok);
        Assert.True(clock.Attach(new ModuleId("healthy"), healthy).Ok);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, (await clock.PulseDueAsync(time.GetUtcNow(), default)).Value);

        Assert.Single(broken.Triggers);
        Assert.Single(healthy.Triggers);
        Assert.Equal(2, clock.BeatCount);
    }

    [Fact]
    public async Task A_file_adapter_is_a_trigger_sink_and_needs_nobody_to_register_it()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var clock = new ClockSource(new EventQueue(8), time);
        var mailbox = new FileMailbox(Path.Combine(_root, "adapter"));
        var adapter = new FileAdapter(
            new(new Address("adapter", "file", "clocked"), new("file", "clocked", AdapterDirection.Both), "adapter.file"),
            mailbox, new EventQueue(8));

        // The adapter declares its own rhythm; the clock is told nothing about adapters in general.
        Assert.Equal(TimeSpan.FromMilliseconds(200), adapter.PreferredInterval);
        Assert.True(clock.Attach(new ModuleId(adapter.Address.ToString()), adapter).Ok);
        var inbox = Path.Combine(mailbox.Root, "inbox", "chat-1");
        Directory.CreateDirectory(inbox);
        await File.WriteAllTextAsync(Path.Combine(inbox, "one.ready.json"), """{"text":"hi"}""");
        Assert.True((await adapter.StartAsync(default)).Ok);

        time.Advance(TimeSpan.FromSeconds(1));
        await clock.PulseDueAsync(time.GetUtcNow(), default);

        // The trigger turned into a poll without anybody wiring a script: that is the whole point of the abstraction.
        Assert.False(File.Exists(Path.Combine(inbox, "one.ready.json")));
        await adapter.StopAsync(default);
    }

    [Fact]
    public void A_rhythm_outside_the_scheduler_range_is_refused()
    {
        var clock = new ClockSource(new EventQueue(8), new MutableTimeProvider(DateTimeOffset.UtcNow));
        Assert.False(clock.Attach(new ModuleId("too-fast"), new CountingSink(TimeSpan.FromMilliseconds(1))).Ok);
        Assert.False(clock.Attach(new ModuleId("too-slow"), new CountingSink(TimeSpan.FromDays(2))).Ok);
    }

    private sealed class CountingSink(TimeSpan? interval) : ITriggerSink
    {
        public TimeSpan? PreferredInterval { get; } = interval;
        public bool Fail { get; init; }
        public List<TriggerSignal> Triggers { get; } = [];

        public ValueTask<Result> OnTriggerAsync(TriggerSignal trigger, CancellationToken cancellationToken)
        {
            Triggers.Add(trigger);
            return ValueTask.FromResult(Fail ? Result.Failure(new(ErrorCode.Unavailable, "this beat failed")) : Result.Success());
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }
}
