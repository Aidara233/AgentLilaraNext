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
    public async Task Two_masters_may_hold_the_same_slave()
    {
        // C-16: an instance may have more than one master. It is declared once and held twice, and the phases it
        // goes through are the core's waves — so it initializes and becomes ready once, no matter how many masters
        // hold it. A master wires its edge; it does not drive its slaves through startup.
        var shared = new TestModule(new("shared"));
        var left = new TestMaster(new("left"));
        var right = new TestMaster(new("right"));
        Add(shared, "test.plain");
        Add(left, "test.master", slaves: ["shared"]);
        Add(right, "test.master", slaves: ["shared"]);

        var receipt = await Start();

        Assert.Empty(receipt.BadEdges);
        Assert.Equal(2, receipt.WiredEdges);
        Assert.Equal(new ModuleId("shared"), Assert.Single(left.Wired));
        Assert.Equal(new ModuleId("shared"), Assert.Single(right.Wired));
        Assert.Single(shared.Calls, call => call == "initialize");
        Assert.Single(shared.Calls, call => call == "ready");
        Assert.True(receipt.AllReady, Failures(receipt));
    }

    [Fact]
    public async Task A_module_declared_in_code_still_gets_its_directory_and_its_file()
    {
        // C-03: "declared in code" is not an exemption — the module gets a directory and a module.json like any
        // other, so the disk and the graph describe the same set (22 §7 I2).
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(new TestModule(new("core-1")), typeId: "test.plain").Ok);
        var receipt = await kernel.StartAsync(default);
        Assert.True(receipt.Ok, receipt.Error?.Message);

        var path = Path.Combine(_root, "modules", "core-1", "module.json");
        Assert.True(File.Exists(path));
        using var written = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("core-1", written.RootElement.GetProperty("id").GetString());
        Assert.Equal("test.plain", written.RootElement.GetProperty("type").GetString());

        // A second start over the same disk: one module with two marks is not a collision. Only a different type
        // under one id would be.
        var again = new Kernel(_root, _types);
        Assert.True(again.Declare(new TestModule(new("core-1")), typeId: "test.plain").Ok);
        var second = await again.StartAsync(default);
        Assert.True(second.Ok, second.Error?.Message);
        Assert.DoesNotContain(second.Value!.Issues, issue => issue.Path.Contains("core-1", StringComparison.Ordinal));
        Assert.Equal(ModulePhase.Ready, second.Value.Instances.Single(i => i.Id.Value == "core-1").Phase);
    }

    [Fact]
    public async Task A_master_whose_edge_failed_hears_about_it_and_the_rest_still_starts()
    {
        // The wiring phase walks the modules and builds each edge. One that cannot be built goes to that module's own
        // error hook and affects only that edge — the phase does not stop, and the module itself did not fail.
        var picky = new TestMaster(new("picky")) { Accept = (_, _, _) => Result.Failure(new(ErrorCode.Denied, "not that one")) };
        var other = new TestMaster(new("other"));
        var slave = new TestModule(new("slave-1"));
        Add(slave, "test.plain");
        Add(picky, "test.master", slaves: ["slave-1"]);
        Add(other, "test.master", slaves: ["slave-1"]);

        var receipt = await Start();

        Assert.Single(receipt.BadEdges);
        Assert.Equal(new ModuleId("picky"), receipt.BadEdges[0].Master);
        Assert.Contains(picky.Errors, error => error.Message.Contains("not that one", StringComparison.Ordinal));
        // Only that edge failed: the other master's edge is wired and nobody was demoted.
        Assert.Equal(new ModuleId("slave-1"), Assert.Single(other.Wired));
        Assert.Equal(ModulePhase.Ready, receipt.Instances.Single(instance => instance.Id.Value == "picky").Phase);
        Assert.Equal(ModulePhase.Ready, receipt.Instances.Single(instance => instance.Id.Value == "slave-1").Phase);
    }

    [Fact]
    public async Task A_module_that_needs_a_name_nobody_answers_fails_at_startup()
    {
        // C-19: modules are equal and find each other by name. A name nobody answers is a startup failure of that
        // module, not a surprise on the first message — and it does not take the others down with it.
        var needy = new Needy(new("needy"), ["somebody"]);
        var fine = new TestModule(new("fine"));
        Add(needy, "test.needy");
        Add(fine, "test.plain");

        var receipt = await Start();

        var failed = receipt.Instances.Single(instance => instance.Id.Value == "needy");
        Assert.Equal(ModulePhase.Failed, failed.Phase);
        Assert.Contains("somebody", failed.Failure ?? "", StringComparison.Ordinal);
        Assert.Contains(needy.Errors, error => error.Message.Contains("somebody", StringComparison.Ordinal));
        Assert.Equal(ModulePhase.Ready, receipt.Instances.Single(instance => instance.Id.Value == "fine").Phase);
    }

    [Fact]
    public async Task A_name_the_module_needs_only_has_to_be_answered_by_someone()
    {
        var needy = new Needy(new("needy"), ["fine"]);
        var fine = new TestModule(new("fine"));
        Add(needy, "test.needy");
        Add(fine, "test.plain");

        var receipt = await Start();

        Assert.Equal(ModulePhase.Ready, receipt.Instances.Single(instance => instance.Id.Value == "needy").Phase);
        Assert.Empty(needy.Errors);
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
        Assert.Equal(ModulePhase.Failed, receipt.Instances.Single(instance => instance.Id.Value == "broken").Phase);
        Assert.Equal(ModulePhase.Ready, receipt.Instances.Single(instance => instance.Id.Value == "healthy").Phase);
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
        Assert.Equal(ModulePhase.Ready, receipt.Instances.Single(instance => instance.Id.Value == "healthy").Phase);
    }

    [Fact]
    public async Task A_module_whose_id_says_nothing_still_works()
    {
        // "foo" carries no kind, no type and no suffix: if anything parsed the id, this module would be unreachable.
        var foo = new TestMaster(new("foo"));
        Add(new TestModule(new("bar")), "test.plain");
        Add(foo, "test.master", slaves: ["bar"]);

        var receipt = await Start();

        Assert.Equal(ModulePhase.Ready, receipt.Instances.Single(instance => instance.Id.Value == "foo").Phase);
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
        Assert.Equal(ModulePhase.Disabled, receipt.Instances.Single(instance => instance.Id.Value == "off").Phase);
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
        Assert.Equal(ModulePhase.Stopped, kernel.Instances.Single(instance => instance.Id.Value == "master").Phase);
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

        string Shape(ModuleReceipt receipt) => string.Join("|", receipt.Instances
            .OrderBy(instance => instance.Id.Value, StringComparer.Ordinal)
            .Select(instance => $"{instance.Id}:{instance.Type}:{instance.Phase}")) + $"|{receipt.WiredEdges}|{receipt.BadEdges.Count}|{receipt.Cycles.Count}";
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

    [Fact]
    public async Task A_node_the_host_declares_in_code_starts_like_any_other_and_cannot_be_declared_twice()
    {
        var declared = new TestModule(new("adapters"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(declared).Ok);
        Assert.False(kernel.Declare(new TestModule(new("adapters"))).Ok);

        var started = await kernel.StartAsync(default);

        Assert.True(started.Ok);
        Assert.Equal(ModulePhase.Ready, started.Value!.Instances.Single(instance => instance.Id.Value == "adapters").Phase);
        Assert.Equal(["initialize", "ready"], declared.Calls);
    }

    [Fact]
    public async Task A_manager_that_still_speaks_the_old_lifecycle_protocol_is_driven_by_the_kernel()
    {
        var participant = new RecordingParticipant("channels");
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(new LifecycleModule(participant), typeId: ModuleNames.ChannelManager).Ok);

        var started = await kernel.StartAsync(default);
        Assert.True(started.Ok);
        Assert.Equal(["start"], participant.Calls);

        Assert.True((await kernel.StopAsync(default)).Ok);
        Assert.Equal(["start", "stop"], participant.Calls);
    }

    [Fact]
    public async Task A_manager_that_fails_to_start_is_reported_and_does_not_take_down_the_others()
    {
        var failing = new RecordingParticipant("channels") { Fail = true };
        var healthy = new RecordingParticipant("adapters");
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(new LifecycleModule(failing)).Ok);
        Assert.True(kernel.Declare(new LifecycleModule(healthy)).Ok);

        var started = await kernel.StartAsync(default);

        Assert.True(started.Ok);
        var instances = started.Value!.Instances;
        Assert.Equal(ModulePhase.Failed, instances.Single(instance => instance.Id.Value == "channels").Phase);
        Assert.Equal(ModulePhase.Ready, instances.Single(instance => instance.Id.Value == "adapters").Phase);
        Assert.Equal(1, started.Value.Degraded);
    }

    private sealed class RecordingParticipant(string name) : ILifecycleParticipant
    {
        public string Name { get; } = name;
        public bool Fail { get; init; }
        public List<string> Calls { get; } = [];

        public ValueTask<Result> StartAsync(LifecycleSignal signal, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            return ValueTask.FromResult(Fail ? Result.Failure(new(ErrorCode.Unavailable, "cannot start")) : Result.Success());
        }

        public ValueTask<Result> StopAsync(LifecycleSignal signal, CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            return ValueTask.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task A_type_finds_capability_and_an_id_names_one_module()
    {
        // Two modules of one type, one of them called something that says nothing: an id can only answer "which
        // one", a type can only answer "who can do this" (docs/22 §3.5).
        var first = new TestModule(new("foo"));
        var second = new TestModule(new("second"));
        Add(first, "test.plain");
        Add(second, "test.plain");
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);

        Assert.Equal(2, kernel.OfType("test.plain").Count);
        Assert.Same(first, kernel.Find(new ModuleId("foo")));
        Assert.Null(kernel.Find(new ModuleId("nobody")));
        Assert.False(kernel.Require(new ModuleId("nobody")).Ok);
        // A type with two implementations cannot answer "the one": that must be an error naming both.
        var ambiguous = kernel.RequireOfType("test.plain");
        Assert.False(ambiguous.Ok);
        Assert.Equal(ErrorCode.Conflict, ambiguous.Error!.Code);
        Assert.Contains("foo", ambiguous.Error.Message, StringComparison.Ordinal);
        Assert.Contains("second", ambiguous.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_singleton_type_answers_with_its_one_module()
    {
        var only = new TestModule(new("clock"));
        Add(only, "test.clock");
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);

        Assert.Same(only, kernel.RequireOfType("test.clock").Value);
        Assert.False(kernel.RequireOfType("test.absent").Ok);
    }

    [Fact]
    public async Task A_module_added_while_the_graph_is_running_starts_against_what_is_ready_now()
    {
        Add(new TestModule(new("target")), "test.plain");
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);

        var late = new TestMaster(new("late"));
        var added = await kernel.AddAsync(late, slaves: [new ModuleSlave(new ModuleId("target"), ["ping"])],
            typeId: "test.master", cancellationToken: default);

        Assert.True(added.Ok, added.Error?.Message);
        Assert.Equal(ModulePhase.Ready, added.Value!.Instance.Phase);
        Assert.Equal(1, added.Value.WiredEdges);
        Assert.Equal([new ModuleId("target")], late.Wired);
        Assert.Contains(kernel.Instances, instance => instance.Id.Value == "late");
        // Adding the same name twice is a conflict, not a second instance.
        Assert.False((await kernel.AddAsync(new TestModule(new("late")), cancellationToken: default)).Ok);
    }

    [Fact]
    public async Task A_module_added_late_that_needs_something_missing_is_reported_not_refused()
    {
        // A slave that is not there is a bad edge, exactly like at startup: the instance exists, said what it wanted,
        // and the answer says which edge never became a call.
        Add(new TestModule(new("only")), "test.plain");
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);

        var late = new TestMaster(new("late"));
        var added = await kernel.AddAsync(late, slaves: [new ModuleSlave(new ModuleId("gone"), ["ping"])],
            typeId: "test.master", cancellationToken: default);

        Assert.True(added.Ok, added.Error?.Message);
        Assert.Equal(ModulePhase.Ready, added.Value!.Instance.Phase);
        var edge = Assert.Single(added.Value.BadEdges);
        Assert.Equal(BadEdgeReason.MissingModule, edge.Reason);
        Assert.Equal(0, added.Value.WiredEdges);
    }

    [Fact]
    public async Task A_module_added_late_that_needs_an_unready_module_does_not_start()
    {
        var broken = new TestModule(new("broken"));
        broken.Initialize = () => throw new InvalidOperationException("cannot start");
        Add(broken, "test.plain");
        Add(new TestModule(new("target")), "test.plain");
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);

        var late = new TestModule(new("late"));
        var added = await kernel.AddAsync(late, dependencies: [new ModuleId("broken")], cancellationToken: default);

        Assert.True(added.Ok, added.Error?.Message);
        Assert.Equal(ModulePhase.Failed, added.Value!.Instance.Phase);
        Assert.Contains("broken", added.Value.Instance.Failure!, StringComparison.Ordinal);
        Assert.Empty(late.Calls.Skip(1));
    }

    [Fact]
    public async Task Removing_a_node_stops_it_and_reports_the_edges_that_pointed_at_it()
    {
        var master = new TestMaster(new("master"));
        var stopped = new List<string>();
        var slave = new TestModule(new("slave"));
        slave.Stop = () => stopped.Add("slave");
        Add(slave, "test.plain");
        Add(master, "test.master", slaves: ["slave"]);
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);

        var removed = await kernel.RemoveAsync(new ModuleId("slave"), default);

        Assert.True(removed.Ok, removed.Error?.Message);
        Assert.Equal(["slave"], stopped);
        Assert.Null(kernel.Find(new ModuleId("slave")));
        Assert.DoesNotContain(kernel.Instances, instance => instance.Id.Value == "slave");
        // The master's file still lists it, so the edge is reported rather than rewritten behind its back.
        var edge = Assert.Single(removed.Value!);
        Assert.Equal(BadEdgeReason.MissingModule, edge.Reason);
        Assert.False((await kernel.RemoveAsync(new ModuleId("slave"), default)).Ok);
    }

    [Fact]
    public async Task A_module_can_be_loaded_from_its_own_file_while_the_graph_runs()
    {
        Add(new TestModule(new("thing")), "test.plain");
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);
        // A directory that appeared after startup: the file is the truth, so loading means reading it again.
        var late = new TestModule(new("later"));
        Add(late, "test.plain");

        var loaded = await kernel.LoadAsync(new ModuleId("later"), default);

        Assert.True(loaded.Ok, loaded.Error?.Message);
        Assert.Equal(ModulePhase.Ready, loaded.Value!.Instance.Phase);
        // Asking for something with no readable file is an error that says so, not an empty instance.
        Assert.False((await kernel.LoadAsync(new ModuleId("absent"), default)).Ok);
    }

    [Fact]
    public async Task Unloading_takes_it_out_of_the_graph_and_puts_its_directory_in_the_bin()
    {
        var module = new TestModule(new("thing"));
        Add(module, "test.plain");
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var directory = Path.Combine(_root, "modules", "thing");
        Assert.True(Directory.Exists(directory));

        var unloaded = await kernel.UnloadAsync(new ModuleId("thing"), archive: true, default);

        Assert.True(unloaded.Ok, unloaded.Error?.Message);
        Assert.Null(kernel.Find(new ModuleId("thing")));
        // Archived, not deleted: unloading is not a decision to forget what happened in there.
        Assert.False(Directory.Exists(directory));
        Assert.NotEmpty(Directory.GetDirectories(Path.Combine(_root, "archive", "modules")));
    }

    [Fact]
    public async Task Unloading_without_archiving_leaves_the_directory_where_it_is()
    {
        Add(new TestModule(new("thing")), "test.plain");
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var directory = Path.Combine(_root, "modules", "thing");

        Assert.True((await kernel.UnloadAsync(new ModuleId("thing"), archive: false, default)).Ok);

        Assert.True(Directory.Exists(directory));
        // Nothing was archived, so there is no bin to find - and no directory left behind by accident either.
        Assert.False(Directory.Exists(Path.Combine(_root, "archive", "modules")));
    }

    private async ValueTask<ModuleReceipt> Start(Kernel? kernel = null)
    {
        kernel ??= new Kernel(_root, _types);
        var started = await kernel.StartAsync(default);
        Assert.True(started.Ok, started.Error?.Message);
        return started.Value!;
    }

    private static string Failures(ModuleReceipt receipt) =>
        string.Join(" | ", receipt.Instances.Where(instance => instance.Phase != ModulePhase.Ready).Select(instance => $"{instance.Id}:{instance.Phase}:{instance.Failure}"));

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

    private sealed class Needy(ModuleId id, IReadOnlyList<string> requires) : TestModule(id), IRequireModules
    {
        public IReadOnlyList<string> Requires { get; } = requires;
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

    [Fact]
    public async Task An_instance_announced_by_its_manager_becomes_a_node_with_an_edge_to_it()
    {
        var manager = new TestMaster(new("ChannelManager"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(manager, typeId: ModuleNames.ChannelManager).Ok);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var host = new InstanceHost(kernel, new ModuleId("ChannelManager"));
        var instance = new ModuleId("channel.conversation#chat-1");

        var attached = await host.AttachAsync(new ModuleId("ChannelManager"), instance, "channel.conversation", ["message.accepted"], default);

        Assert.True(attached.Ok, attached.Error?.Message);
        Assert.Equal(ModulePhase.Ready, kernel.Instances.Single(candidate => candidate.Id == instance).Phase);
        Assert.Equal("channel.conversation", kernel.Instances.Single(candidate => candidate.Id == instance).Type);
        // The manager was asked to accept it, which is what makes this an edge rather than a bookmark.
        Assert.Equal([instance], manager.Wired);

        var detached = await host.DetachAsync(new ModuleId("ChannelManager"), instance, default);
        Assert.True(detached.Ok, detached.Error?.Message);
        // Detaching drops the edge and nothing else: the instance is still in the graph (C-16).
        Assert.NotNull(kernel.Find(instance));

        var forgotten = await host.ForgetAsync(new ModuleId("ChannelManager"), instance, default);
        Assert.True(forgotten.Ok, forgotten.Error?.Message);
        Assert.Null(kernel.Find(instance));
        // Forgetting twice is not an error: absent is what forgetting wanted.
        Assert.True((await host.ForgetAsync(new ModuleId("ChannelManager"), instance, default)).Ok);
    }

    [Fact]
    public async Task A_second_master_may_hold_an_instance_that_already_exists()
    {
        // C-16 at runtime: a manager announcing an instance another manager already holds gives it a second edge,
        // not a conflict — and the instance's phases are not re-run for the newcomer's sake.
        var first = new TestMaster(new("first"));
        var second = new TestMaster(new("second"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(first, typeId: "test.master").Ok);
        Assert.True(kernel.Declare(second, typeId: "test.master").Ok);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var instance = new TestModule(new("shared-1"));

        var one = await kernel.AdoptAsync(first.Id, instance, typeId: "test.plain", cancellationToken: default);
        var two = await kernel.AdoptAsync(second.Id, instance, typeId: "test.plain", cancellationToken: default);

        Assert.True(one.Ok, one.Error?.Message);
        Assert.True(two.Ok, two.Error?.Message);
        Assert.Empty(two.Value!.BadEdges);
        Assert.Equal(new ModuleId("shared-1"), Assert.Single(first.Wired));
        Assert.Equal(new ModuleId("shared-1"), Assert.Single(second.Wired));
        Assert.Single(instance.Calls, call => call == "initialize");
        Assert.Equal(ModulePhase.Ready, kernel.Instances.Single(held => held.Id == instance.Id).Phase);
    }

    [Fact]
    public async Task Two_different_objects_under_one_id_are_still_a_conflict()
    {
        // The id names one instance (C-14). A second *object* claiming it is a collision, not a second master.
        var first = new TestMaster(new("first"));
        var second = new TestMaster(new("second"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(first, typeId: "test.master").Ok);
        Assert.True(kernel.Declare(second, typeId: "test.master").Ok);
        Assert.True((await kernel.StartAsync(default)).Ok);

        Assert.True((await kernel.AdoptAsync(first.Id, new TestModule(new("dup")), typeId: "test.plain", cancellationToken: default)).Ok);
        var clash = await kernel.AdoptAsync(second.Id, new TestModule(new("dup")), typeId: "test.plain", cancellationToken: default);

        Assert.False(clash.Ok);
        Assert.Equal(ErrorCode.Conflict, clash.Error!.Code);
    }

    [Fact]
    public async Task One_master_releasing_its_edge_leaves_the_instance_alone()
    {
        // C-16 with two masters: a master holding less is not an event in the instance's life. It keeps running,
        // keeps its other edge, and is not stopped — only its constructor may take it apart.
        var first = new TestMaster(new("first"));
        var second = new TestMaster(new("second"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(first, typeId: "test.master").Ok);
        Assert.True(kernel.Declare(second, typeId: "test.master").Ok);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var instance = new TestModule(new("shared-2"));
        Assert.True((await kernel.AdoptAsync(first.Id, instance, typeId: "test.plain", cancellationToken: default)).Ok);
        Assert.True((await kernel.AdoptAsync(second.Id, instance, typeId: "test.plain", cancellationToken: default)).Ok);
        var before = instance.Calls.Count;

        var released = await kernel.ReleaseAsync(first.Id, instance.Id, default);

        Assert.True(released.Ok, released.Error?.Message);
        Assert.NotNull(kernel.Find(instance.Id));
        Assert.Equal(ModulePhase.Ready, kernel.Instances.Single(held => held.Id == instance.Id).Phase);
        Assert.Equal(before, instance.Calls.Count);     // nothing stopped, nothing re-initialized
        // Releasing again is still fine: a manager tearing down is allowed to say so twice.
        Assert.True((await kernel.ReleaseAsync(first.Id, instance.Id, default)).Ok);
    }

    [Fact]
    public async Task A_manager_only_accepts_the_instances_it_holds()
    {
        // The master judges: a Channel manager that does not have this Channel refuses the edge, and the refusal is
        // reported rather than leaving a instance nobody owns.
        var manager = new TestMaster(new("ChannelManager")) { Accept = (_, _, _) => Result.Failure(new(ErrorCode.Invalid, "not mine")) };
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(manager, typeId: ModuleNames.ChannelManager).Ok);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var host = new InstanceHost(kernel, new ModuleId("ChannelManager"));
        var instance = new ModuleId("channel.conversation#chat-1");

        var attached = await host.AttachAsync(new ModuleId("ChannelManager"), instance, "channel.conversation", null, default);

        Assert.False(attached.Ok);
        Assert.Contains("not mine", attached.Error!.Message, StringComparison.Ordinal);
        Assert.Null(kernel.Find(instance));
    }

    [Fact]
    public async Task A_node_that_reports_a_rhythm_is_attached_to_the_schedule_in_the_graph()
    {
        // "Who is on a schedule" has to be a fact the graph knows, or nobody can answer it later.
        var source = new CountingSource();
        var ticker = new TickingModule(new("adapter.file#one"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(ticker).Ok);
        Assert.True(kernel.Declare(new PassiveInstance(new ModuleId("Clock"), source), typeId: "clock").Ok);

        var receipt = await Start(kernel);

        Assert.Equal([new ModuleId("adapter.file#one")], source.Attached);
        Assert.Empty(source.DetachedIds);
        // A trigger is not an edge, so it is its own line: what a reader cannot see anywhere else is who pushes
        // whom, and at what pace.
        var rhythm = Assert.Single(receipt.Rhythms);
        Assert.Equal(new ModuleId("adapter.file#one"), rhythm.Sink);
        Assert.Equal(TimeSpan.FromMilliseconds(200), rhythm.Interval);
        Assert.Equal("Clock", rhythm.Source);
        Assert.Equal("adapter.file#one 200ms by Clock", rhythm.Line);

        Assert.True((await kernel.RemoveAsync(new ModuleId("adapter.file#one"), default)).Ok);
        Assert.Contains(new ModuleId("adapter.file#one"), source.DetachedIds);
        Assert.Empty(kernel.Rhythms);
    }

    [Fact]
    public async Task A_source_that_refuses_a_sink_leaves_no_rhythm_behind()
    {
        // The receipt is the kernel's claim about calls it actually made. A refused attach that still showed up as
        // "on a schedule" would be the one lie a reader would never catch: the schedule would just look quiet.
        var source = new CountingSource { Refuse = true };
        var signals = new RecordingSignals();
        var kernel = new Kernel(new ModuleManifestStore(_root), _types, signals);
        Assert.True(kernel.Declare(new TickingModule(new("adapter.file#one"))).Ok);
        Assert.True(kernel.Declare(new PassiveInstance(new ModuleId("Clock"), source), typeId: "clock").Ok);

        var receipt = await Start(kernel);

        Assert.Empty(receipt.Rhythms);
        Assert.Empty(kernel.Rhythms);
        // Refused, not silent: the same rule as a bad edge.
        Assert.Contains(signals.Records, record => record.Name == "trigger" && record.Level == SignalLevel.Warn);
    }

    [Fact]
    public async Task Only_sources_that_own_a_rhythm_receive_a_sink_that_asked_for_a_beat()
    {
        // A source that cannot schedule gets nothing from "I want a beat": otherwise an adapter with a clock and a
        // relay script would be triggered twice per interval, which is the double-subscription bug in another suit.
        var schedule = new CountingSource();
        var pushOnly = new CountingSource { CanScheduleOverride = false };
        var ticker = new TickingModule(new("adapter.file#one"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(ticker).Ok);
        Assert.True(kernel.Declare(new PassiveInstance(new ModuleId("Clock"), schedule), typeId: "clock").Ok);
        Assert.True(kernel.Declare(new PassiveInstance(new ModuleId("Relay"), pushOnly), typeId: "relay").Ok);

        Assert.True((await kernel.StartAsync(default)).Ok);

        Assert.Single(schedule.Attached);
        Assert.Empty(pushOnly.Attached);
    }

    [Fact]
    public async Task The_live_graph_is_what_the_receipt_said_and_not_only_a_startup_memory()
    {
        // A degraded system looks exactly like a whole one from outside, so the number has to be readable after the
        // startup log is gone — otherwise "health check" means "somebody reads the log".
        Add(new TestMaster(new("master")), "test.master", slaves: ["gone"]);
        var kernel = new Kernel(_root, _types);

        var receipt = await Start(kernel);

        Assert.Equal(receipt.Degraded, kernel.Status.Degraded);
        Assert.Equal(receipt.Instances.Count, kernel.Status.Instances);
        Assert.Equal(receipt.Ready, kernel.Status.Ready);
        var edge = Assert.Single(kernel.Status.BadEdges);
        Assert.Equal(BadEdgeReason.MissingModule, edge.Reason);
    }

    [Fact]
    public async Task An_instance_announced_before_startup_is_initialized_wired_and_counted_like_any_other_node()
    {
        // Providers are built from their files before the graph exists. If adoption only worked after startup, those
        // would be the one kind of instance the receipt never mentions — and "which instances are there" would be
        // answered differently by the receipt and by the running graph.
        var manager = new TestMaster(new("ModelManager"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(new PassiveInstance(manager.Id, manager), typeId: "manager").Ok);
        var host = new InstanceHost(kernel, manager.Id);

        var announced = await host.AttachAsync(manager.Id, new ModuleId("model.chat#one"), "model.openai-chat", null, default);

        Assert.True(announced.Ok, announced.Error?.Message);
        var receipt = await Start(kernel);
        Assert.Equal(ModulePhase.Ready, receipt.Instances.Single(instance => instance.Id.Value == "model.chat#one").Phase);
        Assert.Equal(1, receipt.WiredEdges);
        // The master judges even here: the instance is wired through it, not just written down next to it.
        Assert.Equal([new ModuleId("model.chat#one")], manager.Wired);
    }

    [Fact]
    public async Task An_instance_announced_while_the_graph_is_starting_gets_its_phases_and_is_in_the_receipt()
    {
        // A manager revives its instances in Ready — inside startup, not after it. Treating that announcement as a
        // declaration is not a small mix-up: the instance lands in no phase at all, the receipt comes up short by exactly
        // the instances that were revived, and nothing says so. (That is what the real host did when this was wrong.)
        var manager = new TestMaster(new("ChannelManager"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(manager, typeId: "manager").Ok);
        var host = new InstanceHost(kernel, manager.Id);
        manager.Ready = () => host.AttachAsync(manager.Id, new ModuleId("channel.conversation#chat"),
            "channel.conversation", ["message.accepted"], default);

        var receipt = await Start(kernel);

        var instance = receipt.Instances.Single(instance => instance.Id.Value == "channel.conversation#chat");
        Assert.Equal(ModulePhase.Ready, instance.Phase);
        Assert.Equal(1, receipt.WiredEdges);
        Assert.Equal([new ModuleId("channel.conversation#chat")], manager.Wired);
    }

    private sealed class CountingSource : ITriggerSource
    {
        public string Source => "Clock";
        public bool CanScheduleOverride { get; init; } = true;
        public bool CanSchedule => CanScheduleOverride;
        public bool Refuse { get; init; }
        public List<ModuleId> Attached { get; } = [];
        public List<ModuleId> DetachedIds { get; } = [];
        public Result Attach(ModuleId sink, ITriggerSink trigger)
        {
            if (Refuse) return Result.Failure(new(ErrorCode.Invalid, "not taking this one"));
            Attached.Add(sink);
            return Result.Success();
        }
        public Result Detach(ModuleId sink)
        {
            DetachedIds.Add(sink);
            return Result.Success();
        }
    }

    private sealed class TickingModule(ModuleId id) : IKernelModule, ITriggerSink
    {
        public ModuleId Id { get; } = id;
        public TimeSpan? PreferredInterval => TimeSpan.FromMilliseconds(200);
        public ValueTask<Result> OnTriggerAsync(TriggerSignal trigger, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
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
