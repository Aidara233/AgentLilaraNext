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
    public async Task Managers_start_in_registration_order_and_the_signal_carries_no_state_of_its_own()
    {
        var order = new List<string>();
        var events = new EventQueue(8);
        var coordinator = new LifecycleCoordinator(events, SignalLog.Open(null));
        Assert.True(coordinator.Register(new Recorder("clock", order)).Ok);
        Assert.True(coordinator.Register(new Recorder("channels", order)).Ok);
        Assert.True(coordinator.Register(new Recorder("adapters", order)).Ok);

        var started = await coordinator.StartAsync(default);

        Assert.True(started.Ok, started.Error?.Message);
        Assert.Equal(["clock", "channels", "adapters"], order);
        Assert.Equal(["clock", "channels", "adapters"], coordinator.Order);
        // One observer event once the whole tree is up.
        Assert.True(events.TryRead(out var marker));
        Assert.Equal(EventNames.LifecycleStarted, marker.Name);
    }

    [Fact]
    public async Task A_failing_manager_stops_the_chain_before_later_managers_start()
    {
        var order = new List<string>();
        var coordinator = new LifecycleCoordinator(new EventQueue(8), SignalLog.Open(null));
        Assert.True(coordinator.Register(new Recorder("channels", order)).Ok);
        Assert.True(coordinator.Register(new Recorder("adapters", order) { FailOnStart = true }).Ok);
        Assert.True(coordinator.Register(new Recorder("models", order)).Ok);

        var started = await coordinator.StartAsync(default);

        Assert.False(started.Ok);
        Assert.Equal(["channels", "adapters"], order);
    }

    [Fact]
    public async Task Start_is_idempotent_and_stop_runs_in_reverse_order()
    {
        var order = new List<string>();
        var coordinator = new LifecycleCoordinator(new EventQueue(8), SignalLog.Open(null));
        Assert.True(coordinator.Register(new Recorder("clock", order)).Ok);
        Assert.True(coordinator.Register(new Recorder("channels", order)).Ok);

        Assert.True((await coordinator.StartAsync(default)).Ok);
        Assert.True((await coordinator.StartAsync(default)).Ok);
        Assert.Equal(["clock", "channels"], order);

        Assert.True((await coordinator.StopAsync(default)).Ok);
        Assert.Equal(["clock", "channels", "channels", "clock"], order);
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
        public string Name => name;

        public ValueTask<Result> StartAsync(LifecycleSignal signal, CancellationToken cancellationToken)
        {
            order.Add(name);
            return ValueTask.FromResult(FailOnStart
                ? Result.Failure(new(ErrorCode.Unavailable, $"{name} refused to start."))
                : Result.Success());
        }

        public ValueTask<Result> StopAsync(LifecycleSignal signal, CancellationToken cancellationToken)
        {
            order.Add(name);
            return ValueTask.FromResult(Result.Success());
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
