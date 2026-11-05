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

    [Fact]
    public async Task A_node_the_host_declares_in_code_starts_like_any_other_and_cannot_be_declared_twice()
    {
        var declared = new TestModule(new("adapters"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(declared).Ok);
        Assert.False(kernel.Declare(new TestModule(new("adapters"))).Ok);

        var started = await kernel.StartAsync(default);

        Assert.True(started.Ok);
        Assert.Equal(ModulePhase.Ready, started.Value!.Nodes.Single(node => node.Id.Value == "adapters").Phase);
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
        var nodes = started.Value!.Nodes;
        Assert.Equal(ModulePhase.Failed, nodes.Single(node => node.Id.Value == "channels").Phase);
        Assert.Equal(ModulePhase.Ready, nodes.Single(node => node.Id.Value == "adapters").Phase);
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
        Assert.Equal(ModulePhase.Ready, added.Value!.Node.Phase);
        Assert.Equal(1, added.Value.WiredEdges);
        Assert.Equal([new ModuleId("target")], late.Wired);
        Assert.Contains(kernel.Nodes, node => node.Id.Value == "late");
        // Adding the same name twice is a conflict, not a second node.
        Assert.False((await kernel.AddAsync(new TestModule(new("late")), cancellationToken: default)).Ok);
    }

    [Fact]
    public async Task A_module_added_late_that_needs_something_missing_is_reported_not_refused()
    {
        // A slave that is not there is a bad edge, exactly like at startup: the node exists, said what it wanted,
        // and the answer says which edge never became a call.
        Add(new TestModule(new("only")), "test.plain");
        var kernel = new Kernel(_root, _types);
        Assert.True((await kernel.StartAsync(default)).Ok);

        var late = new TestMaster(new("late"));
        var added = await kernel.AddAsync(late, slaves: [new ModuleSlave(new ModuleId("gone"), ["ping"])],
            typeId: "test.master", cancellationToken: default);

        Assert.True(added.Ok, added.Error?.Message);
        Assert.Equal(ModulePhase.Ready, added.Value!.Node.Phase);
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
        Assert.Equal(ModulePhase.Failed, added.Value!.Node.Phase);
        Assert.Contains("broken", added.Value.Node.Failure!, StringComparison.Ordinal);
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
        Assert.DoesNotContain(kernel.Nodes, node => node.Id.Value == "slave");
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
        Assert.Equal(ModulePhase.Ready, loaded.Value!.Node.Phase);
        // Asking for something with no readable file is an error that says so, not an empty node.
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

    [Fact]
    public async Task An_instance_announced_by_its_manager_becomes_a_node_with_an_edge_to_it()
    {
        var manager = new TestMaster(new("ChannelManager"));
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(manager, typeId: ModuleNames.ChannelManager).Ok);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var host = new InstanceNodeHost(kernel, new ModuleId("ChannelManager"));
        var instance = new ModuleId("channel.conversation#chat-1");

        var attached = await host.AttachAsync(new ModuleId("ChannelManager"), instance, "channel.conversation", ["message.accepted"], default);

        Assert.True(attached.Ok, attached.Error?.Message);
        Assert.Equal(ModulePhase.Ready, kernel.Nodes.Single(node => node.Id == instance).Phase);
        Assert.Equal("channel.conversation", kernel.Nodes.Single(node => node.Id == instance).Type);
        // The manager was asked to accept it, which is what makes this an edge rather than a bookmark.
        Assert.Equal([instance], manager.Wired);

        var detached = await host.DetachAsync(new ModuleId("ChannelManager"), instance, default);
        Assert.True(detached.Ok, detached.Error?.Message);
        Assert.Null(kernel.Find(instance));
        // Detaching twice is not an error: absent is what detaching wanted.
        Assert.True((await host.DetachAsync(new ModuleId("ChannelManager"), instance, default)).Ok);
    }

    [Fact]
    public async Task A_manager_only_accepts_the_instances_it_holds()
    {
        // The master judges: a Channel manager that does not have this Channel refuses the edge, and the refusal is
        // reported rather than leaving a node nobody owns.
        var manager = new TestMaster(new("ChannelManager")) { Accept = (_, _, _) => Result.Failure(new(ErrorCode.Invalid, "not mine")) };
        var kernel = new Kernel(_root, _types);
        Assert.True(kernel.Declare(manager, typeId: ModuleNames.ChannelManager).Ok);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var host = new InstanceNodeHost(kernel, new ModuleId("ChannelManager"));
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
        Assert.True(kernel.Declare(new PassiveNode(new ModuleId("Clock"), source), typeId: "clock").Ok);

        Assert.True((await kernel.StartAsync(default)).Ok);

        Assert.Equal([new ModuleId("adapter.file#one")], source.Attached);
        Assert.Empty(source.DetachedIds);
        Assert.True((await kernel.RemoveAsync(new ModuleId("adapter.file#one"), default)).Ok);
        Assert.Contains(new ModuleId("adapter.file#one"), source.DetachedIds);
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
        Assert.True(kernel.Declare(new PassiveNode(new ModuleId("Clock"), schedule), typeId: "clock").Ok);
        Assert.True(kernel.Declare(new PassiveNode(new ModuleId("Relay"), pushOnly), typeId: "relay").Ok);

        Assert.True((await kernel.StartAsync(default)).Ok);

        Assert.Single(schedule.Attached);
        Assert.Empty(pushOnly.Attached);
    }

    private sealed class CountingSource : ITriggerSource
    {
        public string Source => "Clock";
        public bool CanScheduleOverride { get; init; } = true;
        public bool CanSchedule => CanScheduleOverride;
        public List<ModuleId> Attached { get; } = [];
        public List<ModuleId> DetachedIds { get; } = [];
        public Result Attach(ModuleId sink, ITriggerSink trigger)
        {
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
