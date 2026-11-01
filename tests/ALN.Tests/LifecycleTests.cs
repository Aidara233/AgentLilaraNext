using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class LifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-lifecycle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Independent_managers_start_together_in_one_wave()
    {
        var events = new EventQueue(8);
        var coordinator = new LifecycleCoordinator(events, SignalLog.Open(null));
        // Both wait for each other: a sequential loop would deadlock into a failed outcome.
        var latch = new Latch(2);
        Assert.True(coordinator.Register(new Recorder("adapters", []) { Latch = latch }).Ok);
        Assert.True(coordinator.Register(new Recorder("models", []) { Latch = latch }).Ok);

        var report = await coordinator.StartAsync(default);

        Assert.True(report.AllRunning, string.Join("; ", report.Outcomes.Select(o => $"{o.Name}:{o.Outcome}")));
        Assert.Equal(["adapters", "models"], report.Outcomes.Select(o => o.Name).Order());
        Assert.True(events.TryRead(out var marker));
        Assert.Equal(EventNames.LifecycleStarted, marker.Name);
    }

    [Fact]
    public async Task A_failed_manager_only_takes_down_its_dependents()
    {
        var coordinator = new LifecycleCoordinator(new EventQueue(8), SignalLog.Open(null));
        Assert.True(coordinator.Register(new Recorder("adapters", []) { FailOnStart = true }).Ok);
        // Declares the real data dependency: a Channel is bound to an Adapter.
        Assert.True(coordinator.Register(new Recorder("channels", []), "adapters").Ok);
        // Independent of the failure, so it must still start.
        Assert.True(coordinator.Register(new Recorder("models", []), "clock").Ok);
        Assert.True(coordinator.Register(new Recorder("clock", [])).Ok);

        var report = await coordinator.StartAsync(default);

        Assert.Equal("failed", report.Outcomes.Single(o => o.Name == "adapters").Outcome);
        Assert.Equal("skipped", report.Outcomes.Single(o => o.Name == "channels").Outcome);
        Assert.Equal("ok", report.Outcomes.Single(o => o.Name == "clock").Outcome);
        Assert.Equal("ok", report.Outcomes.Single(o => o.Name == "models").Outcome);
        Assert.False(report.AllRunning);
    }

    [Fact]
    public async Task Start_is_idempotent_and_stop_runs_users_before_what_they_depend_on()
    {
        var order = new List<string>();
        var coordinator = new LifecycleCoordinator(new EventQueue(8), SignalLog.Open(null));
        Assert.True(coordinator.Register(new Recorder("adapters", order)).Ok);
        Assert.True(coordinator.Register(new Recorder("channels", order), "adapters").Ok);

        Assert.True((await coordinator.StartAsync(default)).AllRunning);
        Assert.True((await coordinator.StartAsync(default)).Outcomes.Count == 0);
        Assert.Equal(["adapters", "channels"], order);

        Assert.True((await coordinator.StopAsync(default)).Ok);
        Assert.Equal(["adapters", "channels", "channels", "adapters"], order);
        // Registration after start is a conflict, not a silent extra participant.
        Assert.False(coordinator.Register(new Recorder("late", order)).Ok);
    }

    [Fact]
    public async Task Adapter_manager_starts_stored_adapters_and_hands_the_signal_down()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapter", [new("adapter.file", "adapter")]).Ok);
        var bindings = new StubBindings();
        var factories = new AdapterFactoryRegistry();
        Assert.True(factories.Register("test.adapter", new StubFactory()).Ok);
        var specs = new FileAdapterSpecStore(Path.Combine(_root, "adapters"));
        var events = new EventQueue(16);
        var clock = new StubClock();
        var address = new Address("adapter", "file", "file-1");
        Assert.True((await specs.SaveAsync(new AdapterSpec(address, new("file", "file-1", AdapterDirection.Both)), default)).Ok);

        var manager = new AdapterManager(types, bindings, factories, specs, events, clock);
        var started = await manager.StartAsync(new LifecycleSignal(LifecyclePhase.Start, DateTimeOffset.UtcNow), default);

        Assert.True(started.Ok, started.Error?.Detail ?? started.Error?.Message);
        Assert.Equal(AdapterState.Running, Assert.Single(manager.List()).State);
        // The manager, not the host, decides that a polling adapter needs a heartbeat.
        Assert.Equal(address, Assert.Single(clock.Registered));
        // And it forwards the same signal to the child.
        Assert.True(events.TryRead(out var lifecycleEvent));
        Assert.Equal(EventNames.LifecycleStart, lifecycleEvent.Name);
        Assert.Equal(address, lifecycleEvent.Target);
    }

    private sealed class Recorder(string name, List<string> order) : ILifecycleParticipant
    {
        public bool FailOnStart { get; init; }
        /// <summary>When set, the participant waits for its peers so a wave only passes if it runs concurrently.</summary>
        public Latch? Latch { get; init; }
        public string Name => name;

        public async ValueTask<Result> StartAsync(LifecycleSignal signal, CancellationToken cancellationToken)
        {
            order.Add(name);
            // A sequential loop would time out here instead of finishing, which is the point.
            if (Latch is not null) await Latch.ArriveAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return FailOnStart
                ? Result.Failure(new(ErrorCode.Unavailable, $"{name} refused to start."))
                : Result.Success();
        }

        public ValueTask<Result> StopAsync(LifecycleSignal signal, CancellationToken cancellationToken)
        {
            order.Add(name);
            return ValueTask.FromResult(Result.Success());
        }
    }

    /// <summary>Releases all waiters only after every participant has arrived.</summary>
    private sealed class Latch(int count)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = count;
        public Task ArriveAsync()
        {
            if (Interlocked.Decrement(ref _remaining) == 0) _all.TrySetResult();
            return _all.Task;
        }
    }

    private sealed class StubFactory : IAdapterFactory
    {
        public string TypeId => "adapter.file";
        public IChatAdapter Create(AdapterSpec spec) => new StubAdapter(spec.Address);
    }

    private sealed class StubAdapter(Address address) : IChatAdapter, IPollingAdapter
    {
        public Address Address { get; } = address;
        public AdapterDescriptor Descriptor { get; } = new("file", "file-1", AdapterDirection.Both);
        public AdapterState State { get; private set; } = AdapterState.Created;
        public ValueTask<Result> StartAsync(CancellationToken cancellationToken)
        {
            State = AdapterState.Running;
            return ValueTask.FromResult(Result.Success());
        }
        public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
        {
            State = AdapterState.Stopped;
            return ValueTask.FromResult(Result.Success());
        }
        public ValueTask<Result<int>> PollOnceAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<int>.Success(0));
        public ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SendResult>.Success(new(true, "1")));
    }

    private sealed class StubClock : IClockManager
    {
        public List<Address> Registered { get; } = [];
        public Address Address { get; } = new("clock", "system", "1");
        public ValueTask<Result<Guid>> RegisterAsync(Address target, TimeSpan interval, CancellationToken cancellationToken)
        {
            Registered.Add(target);
            return ValueTask.FromResult(Result<Guid>.Success(Guid.NewGuid()));
        }
        public ValueTask<Result> UnregisterAsync(Guid subscriptionId, Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<int>> UnregisterTargetAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result<int>.Success(1));
        public ValueTask<Result> AcknowledgeAsync(Guid subscriptionId, Address target, Guid eventId, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public bool IsRegistered(Guid subscriptionId, Address target) => false;
        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask<Result<int>> PulseDueAsync(DateTimeOffset now, CancellationToken cancellationToken) => ValueTask.FromResult(Result<int>.Success(0));
    }

    private sealed class StubBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }
}
