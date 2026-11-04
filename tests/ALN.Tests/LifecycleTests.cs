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
    public async Task The_manager_answers_what_an_instance_is_from_its_spec_not_from_its_name()
    {
        // An address is a name. Here the name says "whatever" and the spec says "adapter.file"; the manager must
        // answer the spec, because the moment it starts reading names, renaming an instance changes what it is.
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapter", [new("adapter.file", "adapter")]).Ok);
        var factories = new AdapterFactoryRegistry();
        Assert.True(factories.Register("test.adapter", new StubFactory()).Ok);
        var address = new Address("adapter", "whatever", "one");
        var manager = new AdapterManager(types, new StubBindings(), factories, new FileAdapterSpecStore(Path.Combine(_root, "adapters")));

        var created = await manager.CreateAsync(new AdapterSpec(address, new("file", "file-1", AdapterDirection.Both), "adapter.file"), default);

        Assert.True(created.Ok, created.Error?.Detail ?? created.Error?.Message);
        Assert.Equal("adapter.file", manager.TypeOf(address));
        Assert.Equal("adapter.file", Assert.Single(manager.List()).TypeId);
        // Filtering by type keeps working: it asks the row, not the address.
        Assert.Single((await manager.SnapshotByTypeAsync("adapter.file", default)).Value!);
        Assert.Empty((await manager.SnapshotByTypeAsync("adapter.whatever", default)).Value!);
    }

    [Fact]
    public async Task A_live_adapter_is_findable_by_name_and_stops_being_findable_once_it_is_gone()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapter", [new("adapter.file", "adapter")]).Ok);
        var factories = new AdapterFactoryRegistry();
        Assert.True(factories.Register("test.adapter", new StubFactory()).Ok);
        var catalog = new ModuleCatalog();
        var manager = new AdapterManager(types, new StubBindings(), factories, new FileAdapterSpecStore(Path.Combine(_root, "adapters")));
        Assert.True(catalog.Attach(manager).Ok);
        Assert.True(catalog.Observe(manager).Ok);
        var address = new Address("adapter", "file", "file-1");

        Assert.True((await manager.CreateAsync(new(address, new("file", "file-1", AdapterDirection.Both), "adapter.file"), default)).Ok);

        // Reachable by the name it carries, and listed as an instance of the type its spec named.
        var found = catalog.Find(new ModuleId(address.ToString()));
        Assert.NotNull(found);
        Assert.Equal(address, Assert.IsAssignableFrom<IChatAdapter>(found).Address);
        var row = Assert.Single(catalog.List(typeId: "adapter.file"));
        Assert.Equal(ModuleRole.Instance, row.Role);
        Assert.Equal("adapter", row.Category);

        Assert.True((await manager.DestroyAsync(address, default)).Ok);

        // Nothing had to be un-registered: the owner stopped holding it, so the catalog stopped finding it.
        Assert.Null(catalog.Find(new ModuleId(address.ToString())));
        Assert.Empty(catalog.List(typeId: "adapter.file"));
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
        Assert.True((await specs.SaveAsync(new AdapterSpec(address, new("file", "file-1", AdapterDirection.Both), "adapter.file"), default)).Ok);

        var manager = new AdapterManager(types, bindings, factories, specs, events, clock);
        var started = await manager.StartAsync(new LifecycleSignal(LifecyclePhase.Start, DateTimeOffset.UtcNow), default);

        Assert.True(started.Ok, started.Error?.Detail ?? started.Error?.Message);
        Assert.Equal(AdapterState.Running, Assert.Single(manager.List()).State);
        // The manager does not decide that a polling adapter needs a heartbeat: it used to, on top of the one the
        // Adapter already makes for itself, and the address then got two pulses per interval.
        Assert.Empty(clock.Registered);
        // And it forwards the same signal to the child.
        Assert.True(events.TryRead(out var lifecycleEvent));
        Assert.Equal(EventNames.LifecycleStart, lifecycleEvent.Name);
        Assert.Equal(address, lifecycleEvent.Target);
    }

    [Fact]
    public async Task A_polling_adapter_is_subscribed_to_the_clock_once_and_unsubscribed_after_it_stops()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapter", [new("adapter.file", "adapter")]).Ok);
        var clock = new StubClock();
        var factories = new AdapterFactoryRegistry();
        Assert.True(factories.Register("test.adapter", new StubFactory(clock, clock.Order)).Ok);
        var specs = new FileAdapterSpecStore(Path.Combine(_root, "adapters"));
        var address = new Address("adapter", "file", "file-1");
        Assert.True((await specs.SaveAsync(new AdapterSpec(address, new("file", "file-1", AdapterDirection.Both), "adapter.file"), default)).Ok);

        var manager = new AdapterManager(types, new StubBindings(), factories, specs, new EventQueue(16), clock);
        Assert.True((await manager.StartAsync(new LifecycleSignal(LifecyclePhase.Start, DateTimeOffset.UtcNow), default)).Ok);
        Assert.Equal(address, Assert.Single(clock.Registered));

        var stopped = await manager.StopAsync(new LifecycleSignal(LifecyclePhase.Stop, DateTimeOffset.UtcNow), default);
        Assert.True(stopped.Ok, stopped.Error?.Detail ?? stopped.Error?.Message);
        Assert.Equal(AdapterState.Stopped, Assert.Single(manager.List()).State);
        // The Adapter stops itself before the manager sweeps the clock: sweeping first made the Adapter's own
        // unregistration fail, and an Adapter that reports "could not unregister" never reaches its stopped state.
        Assert.Equal(["clock.register", "adapter.stop", "clock.unregister"], clock.Order);
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

    private sealed class StubFactory(IClockManager? clock = null, List<string>? order = null) : IAdapterFactory
    {
        public string TypeId => "adapter.file";
        public IChatAdapter Create(AdapterSpec spec) => new StubAdapter(spec.Address, clock, order);
    }

    private sealed class StubAdapter(Address address, IClockManager? clock = null, List<string>? order = null) : IChatAdapter, IPollingAdapter
    {
        public Address Address { get; } = address;
        public ModuleId Id => new(Address.ToString());
        public AdapterDescriptor Descriptor { get; } = new("file", "file-1", AdapterDirection.Both);
        public AdapterState State { get; private set; } = AdapterState.Created;
        public async ValueTask<Result> StartAsync(CancellationToken cancellationToken)
        {
            State = AdapterState.Running;
            // The Adapter owns its own heartbeat, the way a transport knows its own interval.
            if (clock is not null)
                await clock.RegisterAsync(Address, TimeSpan.FromMilliseconds(200), cancellationToken);
            return Result.Success();
        }
        public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
        {
            order?.Add("adapter.stop");
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
        public List<string> Order { get; } = [];
        public Address Address { get; } = new("clock", "system", "1");
        public ValueTask<Result<Guid>> RegisterAsync(Address target, TimeSpan interval, CancellationToken cancellationToken)
        {
            Registered.Add(target);
            Order.Add("clock.register");
            return ValueTask.FromResult(Result<Guid>.Success(Guid.NewGuid()));
        }
        public ValueTask<Result> UnregisterAsync(Guid subscriptionId, Address target, CancellationToken cancellationToken)
        { Order.Add("clock.unregister.subscription"); return ValueTask.FromResult(Result.Success()); }
        public ValueTask<Result<int>> UnregisterTargetAsync(Address target, CancellationToken cancellationToken)
        { Order.Add("clock.unregister"); return ValueTask.FromResult(Result<int>.Success(1)); }
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
