using System.Text.Json;
using ALN.Adapter.File;
using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The platform's own operations, passed through — the verbs we never wrapped.
///
/// One action, and one decision per verb: an instance lists the verbs it will carry and the level each may be called at,
/// and nothing else is reachable. That shape is the point: the host does not need to know OneBot's feature list, and the
/// model gets the bridge's own capabilities without anybody hand-writing a wrapper per verb.
/// </summary>
public sealed class RawAdapterActionTests
{
    /// <summary>An adapter that claims to pass things through, and remembers what it was asked.</summary>
    private sealed class RawFileAdapter(AdapterSpec spec) : IChatAdapter, IRawAdapter
    {
        public List<(string Action, string Parameters)> Calls { get; } = [];

        public AdapterDescriptor Descriptor { get; } = spec.Descriptor;
        public AdapterState State => AdapterState.Running;
        public Address Address { get; } = spec.Address;
        public ModuleId Id => Address.ToModuleId();

        public ValueTask<Result<JsonElement>> RawAsync(string action, JsonElement parameters, CancellationToken ct)
        {
            Calls.Add((action, parameters.GetRawText()));
            return ValueTask.FromResult(Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new
            {
                ok = true,
                retcode = 0,
                data = new[] { new { group_id = 12345, group_name = "测试群" } }
            })));
        }

        public ValueTask<Result> StartAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> InitializeAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> ReadyAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> AbortAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken ct) =>
            ValueTask.FromResult(Result<SendResult>.Success(new SendResult(true, "m-1")));
        public ValueTask<Result<int>> PollAsync(CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(0));
    }

    private static (ControlPlane Plane, AdapterManager Manager) SetUp()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapters", [new("adapter.file", "adapter")]).Ok);
        var manager = new AdapterManager(types, new NoBindings());
        var plane = new ControlPlane(new SilentAudit(), SignalLog.Open(null));
        Assert.True(manager.RegisterActions(plane).Ok);
        return (plane, manager);
    }

    private static Actor At(AccessLevel level, string id) => new(id, ActorKind.Model, [Actor.Any], level);

    private static JsonElement Args(string address, string action) =>
        JsonSerializer.SerializeToElement(new { address, action, @params = new { } });

    private static AdapterSpec Spec(IReadOnlyDictionary<string, AccessLevel>? raw) => new(
        new Address("adapter", "file", "test-1"), new AdapterDescriptor("file", "acc", AdapterDirection.Both),
        "adapter.file", RawActions: raw);

    [Fact]
    public async Task An_unlisted_verb_sits_at_the_door_level_which_is_full_by_default()
    {
        // The raw path is a convenience, so its default is the deepest level: a bridge that gains a verb tomorrow works
        // without us or the config changing. That also means the *only* way a configuration moves is down.
        var (plane, manager) = SetUp();
        var spec = Spec(null);
        var adapter = new RawFileAdapter(spec);
        Assert.True((await manager.CreateAsync(spec, _ => adapter, default)).Ok);

        foreach (var level in new[] { AccessLevel.Agent, AccessLevel.Operator })
        {
            var refused = await plane.InvokeAsync(At(level, "someone"), AdapterActions.Raw,
                Args("adapter.file#test-1", "set_group_ban"), default);
            Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
            Assert.Contains("rawAccess", refused.Error.Message);
        }
        Assert.Empty(adapter.Calls);

        var allowed = await plane.InvokeAsync(At(AccessLevel.Full, "kernel"), AdapterActions.Raw,
            Args("adapter.file#test-1", "set_group_ban"), default);
        Assert.True(allowed.Ok, allowed.Error?.Message);
        Assert.Equal("set_group_ban", Assert.Single(adapter.Calls).Action);
    }

    [Fact]
    public async Task An_instance_can_lower_the_door_and_name_exceptions_without_listing_every_verb()
    {
        // The shape that does not rot: one number for the whole door, and lines only for the exceptions somebody cares
        // about. A verb nobody wrote works at the door's level — that is what "we never wrapped it" has to mean.
        var (plane, manager) = SetUp();
        var spec = new AdapterSpec(
            new Address("adapter", "file", "test-1"), new AdapterDescriptor("file", "acc", AdapterDirection.Both),
            "adapter.file", RawAccess: AccessLevel.Agent,
            RawActions: new Dictionary<string, AccessLevel> { ["set_group_ban"] = AccessLevel.Full });
        var adapter = new RawFileAdapter(spec);
        Assert.True((await manager.CreateAsync(spec, _ => adapter, default)).Ok);

        // Unlisted: the door's level, so the model may call it.
        var open = await plane.InvokeAsync(At(AccessLevel.Agent, "model"), AdapterActions.Raw,
            Args("adapter.file#test-1", "get_group_list"), default);
        Assert.True(open.Ok, open.Error?.Message);

        // Named: its own level, so this one stays a person's.
        var kept = await plane.InvokeAsync(At(AccessLevel.Agent, "model"), AdapterActions.Raw,
            Args("adapter.file#test-1", "set_group_ban"), default);
        Assert.Equal(ErrorCode.Denied, kept.Error!.Code);
        Assert.Single(adapter.Calls);
    }

    [Fact]
    public async Task A_listed_verb_is_callable_at_the_level_it_was_listed_with_and_the_answer_is_the_platform_answer()
    {
        var (plane, manager) = SetUp();
        var spec = Spec(new Dictionary<string, AccessLevel> { ["get_group_list"] = AccessLevel.Agent });
        var adapter = new RawFileAdapter(spec);
        Assert.True((await manager.CreateAsync(spec, _ => adapter, default)).Ok);

        var listed = await plane.InvokeAsync(At(AccessLevel.Agent, "model"), AdapterActions.Raw,
            Args("adapter.file#test-1", "get_group_list"), default);

        Assert.True(listed.Ok, listed.Error?.Message);
        // The platform's own data, including the retcode, because that is where a bridge says why.
        Assert.Equal(0, listed.Value.GetProperty("retcode").GetInt32());
        Assert.Equal("测试群", listed.Value.GetProperty("data")[0].GetProperty("group_name").GetString());
        Assert.Equal("get_group_list", Assert.Single(adapter.Calls).Action);
    }

    [Fact]
    public async Task An_adapter_that_passes_nothing_through_says_so_instead_of_pretending()
    {
        var (plane, manager) = SetUp();

        var missing = await plane.InvokeAsync(At(AccessLevel.Full, "kernel"), AdapterActions.Raw,
            Args("adapter.file#nope", "get_group_list"), default);
        Assert.Equal(ErrorCode.Invalid, missing.Error!.Code);
        Assert.Contains("No adapter answers", missing.Error.Message);

        // A real adapter whose platform has no verbs beyond what we model. Listed or not, there is nothing to pass through,
        // and the answer says that instead of "denied" — the difference between "you may not" and "there is no door here".
        var spec = Spec(new Dictionary<string, AccessLevel> { ["get_group_list"] = AccessLevel.Agent });
        var plain = new PlainAdapter(spec);
        var created = await manager.CreateAsync(spec, _ => plain, default);
        Assert.True(created.Ok, created.Error?.Message);

        var unsupported = await plane.InvokeAsync(At(AccessLevel.Agent, "model"), AdapterActions.Raw,
            Args("adapter.file#test-1", "get_group_list"), default);
        Assert.Equal(ErrorCode.Invalid, unsupported.Error!.Code);
        Assert.Contains("does not pass raw operations through", unsupported.Error.Message);
    }

    [Fact]
    public async Task With_no_list_and_no_door_level_an_agent_caller_gets_nothing()
    {
        // Silence is still the strict reading for anyone below full: the default door is full, so a model finds it closed
        // until somebody lowers the door to its level.
        var (plane, manager) = SetUp();
        var bare = Spec(null);
        var adapter = new RawFileAdapter(bare);
        Assert.True((await manager.CreateAsync(bare, _ => adapter, default)).Ok);

        var refused = await plane.InvokeAsync(At(AccessLevel.Agent, "model"), AdapterActions.Raw,
            Args("adapter.file#test-1", "get_group_list"), default);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Empty(adapter.Calls);
    }

    /// <summary>An adapter that implements nothing extra — the shape a platform with no raw verbs has.</summary>
    private sealed class PlainAdapter(AdapterSpec spec) : IChatAdapter
    {
        public AdapterDescriptor Descriptor { get; } = spec.Descriptor;
        public AdapterState State => AdapterState.Running;
        public Address Address { get; } = spec.Address;
        public ModuleId Id => Address.ToModuleId();
        public ValueTask<Result> StartAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> InitializeAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> ReadyAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> AbortAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken ct) =>
            ValueTask.FromResult(Result<SendResult>.Success(new SendResult(true, "m-1")));
        public ValueTask<Result<int>> PollAsync(CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(0));
    }

    private sealed class NoBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) => ValueTask.FromResult(Result.Success());
    }

    private sealed class SilentAudit : IAuditSink
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
