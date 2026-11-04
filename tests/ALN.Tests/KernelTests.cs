using System.Text.Json;
using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class KernelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-kernel-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleFactoryRegistry _types = new();
    private readonly Dictionary<string, TestModule> _instances = new(StringComparer.Ordinal);

    public KernelTests() => Directory.CreateDirectory(Path.Combine(_root, "modules"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Nothing_is_wired_before_every_module_has_finished_initializing()
    {
        var slow = new TestModule(new("slow"));
        var slowFinished = false;
        slow.Initialize = async () =>
        {
            await Task.Delay(60);
            slowFinished = true;
            return Result.Success();
        };
        var master = new TestMaster(new("master"));
        // The master judges every slave it is offered; here it only records what it could see at that moment.
        var sawSlowFinish = false;
        master.Accept = (_, _, _) =>
        {
            sawSlowFinish = slowFinished;
            return Result.Success();
        };
        Add(slow, "test.plain");
        Add(master, "test.master", slaves: ["slow"]);

        var receipt = await Start();

        Assert.True(sawSlowFinish, "Wire ran before the slow module finished initializing.");
        Assert.True(receipt.AllReady, Failures(receipt));
    }

    [Fact]
    public async Task Modules_that_hold_each_other_are_ready_at_the_same_time_and_the_cycle_is_reported()
    {
        // Neither can be ready before the other, so the only order that works is "both at once". A latch makes
        // that a test instead of a coincidence: sequential startup would hang and fail on the timeout.
        var latch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        async ValueTask<Result> MeetAsync()
        {
            if (Interlocked.Increment(ref arrived) == 2) latch.TrySetResult();
            await latch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return Result.Success();
        }
        var a = new TestMaster(new("a")) { Ready = MeetAsync };
        var b = new TestMaster(new("b")) { Ready = MeetAsync };
        Add(a, "test.master", slaves: ["b"]);
        Add(b, "test.master", slaves: ["a"]);

        var receipt = await Start();

        var cycle = Assert.Single(receipt.Cycles);
        Assert.Equal(2, cycle.Members.Count);
        Assert.True(receipt.AllReady, Failures(receipt));
    }

    [Fact]
    public async Task A_startup_with_broken_edges_completes_and_reports_each_one()
    {
        var missing = new TestMaster(new("missing"));
        var refused = new TestMaster(new("refused")) { Accept = (_, _, _) => Result.Failure(new(ErrorCode.Invalid, "needs IChannelSlave")) };
        var plain = new TestModule(new("plain"));
        var target = new TestModule(new("target"));
        Add(target, "test.plain");
        Add(missing, "test.master", slaves: ["nobody"]);
        Add(refused, "test.master", slaves: ["target"]);
        // A type that declares a slave but does not implement IModuleWiring: nobody can be asked, so it is dropped.
        Add(plain, "test.plain", slaves: ["missing"]);
        Add(new TestModule(new("other")), "test.other");

        var receipt = await Start();

        Assert.Equal(3, receipt.BadEdges.Count);
        Assert.Contains(receipt.BadEdges, edge => edge.Reason == BadEdgeReason.MissingModule && edge.Slave.Value == "nobody");
        Assert.Contains(receipt.BadEdges, edge => edge.Reason == BadEdgeReason.RejectedByMaster && edge.Master.Value == "refused");
        Assert.Contains(receipt.BadEdges, edge => edge.Reason == BadEdgeReason.MasterDoesNotWire && edge.Master.Value == "plain");
        Assert.Equal(0, receipt.WiredEdges);
        // A dropped edge is reported, not fatal: every module is still up.
        Assert.True(receipt.AllReady, Failures(receipt));
        Assert.Equal(3, receipt.Degraded);
    }

    [Fact]
    public async Task A_module_that_throws_goes_through_its_own_error_hook_and_the_others_still_start()
    {
        var broken = new TestModule(new("broken"));
        broken.Initialize = () => throw new InvalidOperationException("cannot open its file");
        var healthy = new TestModule(new("healthy"));
        Add(broken, "test.plain");
        Add(healthy, "test.plain");

        var receipt = await Start();

        var error = Assert.Single(broken.Errors);
        Assert.Equal(nameof(IKernelModule.InitializeAsync), error.Method);
        Assert.Contains("cannot open its file", error.Message, StringComparison.Ordinal);
        Assert.Equal(ModulePhase.Failed, receipt.Nodes.Single(node => node.Id.Value == "broken").Phase);
        Assert.Equal(ModulePhase.Ready, receipt.Nodes.Single(node => node.Id.Value == "healthy").Phase);
        Assert.False(receipt.AllReady);
    }

    [Fact]
    public async Task An_error_hook_that_throws_too_is_swallowed_and_recorded_once()
    {
        var broken = new TestModule(new("broken"));
        broken.Initialize = () => throw new InvalidOperationException("first failure");
        broken.OnError = _ => throw new InvalidOperationException("hook is broken too");
        var healthy = new TestModule(new("healthy"));
        Add(broken, "test.plain");
        Add(healthy, "test.plain");
        var signals = new RecordingSignals();
        var kernel = new Kernel(_root, _types, signals);

        var receipt = await Start(kernel);

        // Two records: the failure, and the hook that failed to receive it. Nothing propagates further.
        Assert.Equal(2, signals.Records.Count);
        Assert.All(signals.Records, record => Assert.Equal(SignalLevel.Error, record.Level));
        Assert.Equal(ModulePhase.Ready, receipt.Nodes.Single(node => node.Id.Value == "healthy").Phase);
    }

    [Fact]
    public async Task A_module_whose_id_says_nothing_still_works()
    {
        // "foo" carries no kind, no type and no suffix: if anything parsed the id, this module would be unreachable.
        var foo = new TestMaster(new("foo"));
        Add(new TestModule(new("bar")), "test.plain");
        Add(foo, "test.master", slaves: ["bar"]);

        var receipt = await Start();

        Assert.Equal(ModulePhase.Ready, receipt.Nodes.Single(node => node.Id.Value == "foo").Phase);
        Assert.Equal(1, receipt.WiredEdges);
        Assert.Equal([new ModuleId("bar")], foo.Wired);
    }

    [Fact]
    public async Task A_disabled_module_is_not_started_and_its_master_is_told_at_wire_time()
    {
        var off = new TestModule(new("off"));
        var master = new TestMaster(new("master"));
        Add(off, "test.plain", enabled: false);
        Add(master, "test.master", slaves: ["off"]);

        var receipt = await Start();

        Assert.Empty(off.Calls);
        Assert.Equal(ModulePhase.Disabled, receipt.Nodes.Single(node => node.Id.Value == "off").Phase);
        var edge = Assert.Single(receipt.BadEdges);
        Assert.Equal(BadEdgeReason.DisabledModule, edge.Reason);
    }

    [Fact]
    public async Task Stop_runs_users_before_what_they_use()
    {
        var master = new TestMaster(new("master"));
        var slave = new TestModule(new("slave"));
        Add(slave, "test.plain");
        Add(master, "test.master", slaves: ["slave"]);
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var order = new List<string>();
        master.Stop = () => order.Add("master");
        slave.Stop = () => order.Add("slave");

        Assert.True((await kernel.StopAsync(default)).Ok);

        Assert.Equal(["master", "slave"], order);
        Assert.Equal(ModulePhase.Stopped, kernel.Nodes.Single(node => node.Id.Value == "master").Phase);
    }

    [Fact]
    public async Task Two_startups_of_the_same_disk_produce_the_same_receipt()
    {
        // Restarting is not a repair step (docs/22 §7 I3): what the disk says has to be what the second start does.
        Add(new TestModule(new("plain")), "test.plain");
        Add(new TestMaster(new("master")), "test.master", slaves: ["plain", "nobody"]);
        var kernel = new Kernel(_root, _types);

        var first = await Start(kernel);
        var second = await Start(kernel);

        string Shape(ModuleReceipt receipt) => string.Join("|", receipt.Nodes
            .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
            .Select(node => $"{node.Id}:{node.Type}:{node.Phase}")) + $"|{receipt.WiredEdges}|{receipt.BadEdges.Count}|{receipt.Cycles.Count}";
        Assert.Equal(Shape(first), Shape(second));
    }

    [Fact]
    public async Task Abort_does_not_wait_for_a_module_that_will_not_return()
    {
        var stuck = new TestModule(new("stuck"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stuck.Abort = async token =>
        {
            await gate.Task.WaitAsync(token);
            return Result.Success();
        };
        Add(stuck, "test.plain");
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await kernel.AbortAsync(default);
        started.Stop();

        // Abort promises that everyone was told, not that everyone finished (docs/22 §4).
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"Abort waited {started.Elapsed}.");
        Assert.Contains("abort", stuck.Calls);
        gate.TrySetResult();
    }

    private async ValueTask<ModuleReceipt> Start(Kernel? kernel = null)
    {
        kernel ??= new Kernel(_root, _types);
        var started = await kernel.StartAsync(default);
        Assert.True(started.Ok, started.Error?.Message);
        return started.Value!;
    }

    private static string Failures(ModuleReceipt receipt) =>
        string.Join(" | ", receipt.Nodes.Where(node => node.Phase != ModulePhase.Ready).Select(node => $"{node.Id}:{node.Phase}:{node.Failure}"));

    /// <summary>Writes the module's own file and makes its type resolvable. Slaves are given by id.</summary>
    private void Add(TestModule module, string typeId, IReadOnlyList<string>? slaves = null, bool enabled = true)
    {
        _instances[module.Id.Value] = module;
        if (!_types.IsRegistered(typeId))
            Assert.True(_types.Register(typeId, manifest => Result<IKernelModule>.Success(_instances[manifest.Id.Value])).Ok);

        var slavesJson = slaves is null
            ? ""
            : ", \"slaves\": [" + string.Join(",", slaves.Select(id => $"{{ \"id\": {JsonSerializer.Serialize(id)} }}")) + "]";
        var json = $"{{ \"id\": {JsonSerializer.Serialize(module.Id.Value)}, \"type\": {JsonSerializer.Serialize(typeId)}, " +
                   $"\"enabled\": {(enabled ? "true" : "false")}{slavesJson} }}";
        var directory = Path.Combine(_root, "modules", module.Id.Value);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "module.json"), json);
    }

    private class TestModule(ModuleId id) : IKernelModule
    {
        public ModuleId Id { get; } = id;
        public List<string> Calls { get; } = [];
        public List<ModuleError> Errors { get; } = [];
        public Func<ValueTask<Result>>? Initialize { get; set; }
        public Func<ValueTask<Result>>? Ready { get; set; }
        public Action? Stop { get; set; }
        public Func<CancellationToken, ValueTask<Result>>? Abort { get; set; }
        public Func<ModuleError, ValueTask<Result>>? OnError { get; set; }

        public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
        {
            Calls.Add("initialize");
            return Initialize?.Invoke() ?? ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken)
        {
            Calls.Add("ready");
            return Ready?.Invoke() ?? ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            Stop?.Invoke();
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> AbortAsync(CancellationToken cancellationToken)
        {
            Calls.Add("abort");
            return Abort?.Invoke(cancellationToken) ?? ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken)
        {
            Errors.Add(error);
            return OnError?.Invoke(error) ?? ValueTask.FromResult(Result.Success());
        }
    }

    private sealed class TestMaster(ModuleId id) : TestModule(id), IModuleWiring
    {
        public List<ModuleId> Wired { get; } = [];
        public Func<ModuleId, IReadOnlyList<string>, IKernelModule, Result>? Accept { get; set; }

        public Result WireSlave(ModuleId slaveId, IReadOnlyList<string> kinds, IKernelModule instance)
        {
            var accepted = Accept?.Invoke(slaveId, kinds, instance) ?? Result.Success();
            if (accepted.Ok) Wired.Add(slaveId);
            return accepted;
        }
    }

    private sealed class RecordingSignals : ISignalLog
    {
        public List<SignalRecord> Records { get; } = [];
        public SignalLevel MinLevel => SignalLevel.Debug;
        public bool IsEnabled(SignalLevel level) => true;
        public void Write(SignalRecord record) => Records.Add(record);
        public IReadOnlyList<SignalRecord> Snapshot() => Records;
    }
}
