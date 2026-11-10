using System.Text.Json;
using ALN.Commands;
using ALN.Contracts;

namespace ALN.Tests;

/// <summary>
/// Instructions, tested where they are decided: the table, the walk from text to leaf, the depth, and the one look the
/// pump takes before dispatch. Sending the reply is not exercised here — that is one Adapter call, and it is verified
/// on a real Adapter rather than faked into looking correct.
/// </summary>
public sealed class CommandTests
{
    private static readonly Address Adapter = new("adapter", "qq", "main");
    private const string Conversation = "group-1";

    private readonly RecordingSignals _signals = new();
    private readonly RecordingAudit _audit = new();

    private CommandRegistry Registry(CommandOptions? options = null) =>
        new(options ?? new CommandOptions(), _signals, _audit);

    private static Person Xiaolin => new("xiaolin", "小林", Level: AccessLevel.Operator);

    private CommandInvocation From(string text, Person? person, string? messageId = null) =>
        new(text, Adapter, Conversation,
            // A fresh platform id per call: deduplication is tested explicitly, and every other test would fail in a
            // way that looks like a parsing bug if two different instructions shared an id.
            messageId ?? "m-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), person, "1234567", "群名片");

    // ---- the tree ------------------------------------------------------------------------------

    [Fact]
    public async Task A_leaf_answers_and_a_namespace_lists_what_it_has()
    {
        var registry = Registry();
        // 'order' is registered first as what it is — a namespace with something to say — and only then does the
        // child appear. A namespace's own depth decides who can see the subtree, so it has to be reachable by the
        // people who may run the children.
        Assert.True(registry.Register(new CommandNode("order", "下单", AccessLevel.Agent)).Ok);
        Assert.True(registry.Register(new CommandNode("order.add", "下一单", AccessLevel.Agent,
            Body: (context, _) => ValueTask.FromResult(Result<string>.Success($"收到 {string.Join('|', context.Arguments)}"))))
            .Ok);

        // The same path as a runnable command is refused: a leaf cannot also be a namespace.
        var shadow = registry.Register(new CommandNode("order", "下单", AccessLevel.Agent,
            Body: (_, _) => ValueTask.FromResult(Result<string>.Success("no"))));
        Assert.False(shadow.Ok);
        Assert.Equal(ErrorCode.Conflict, shadow.Error!.Code);

        var bare = await registry.InvokeAsync(From("//order", null), default);
        Assert.True(bare.Ok, bare.Error?.Message);
        Assert.Contains("order.add", bare.Value!.Reply);

        // Quoted runs stay one argument: "两个 苹果" is one thing a person said, not two words.
        var added = await registry.InvokeAsync(From("//order add \"两个 苹果\"", null), default);
        Assert.True(added.Ok, added.Error?.Message);
        Assert.Equal("收到 两个 苹果", added.Value!.Reply);
    }

    [Fact]
    public void A_path_is_either_a_leaf_or_a_namespace()
    {
        var registry = Registry();
        Assert.True(registry.Register(new CommandNode("say", "说话", AccessLevel.Agent,
            Body: (_, _) => ValueTask.FromResult(Result<string>.Success("ok")))).Ok);

        // 'say' is runnable, so nothing may hang below it.
        var under = registry.Register(new CommandNode("say.more", "子指令", AccessLevel.Agent,
            Body: (_, _) => ValueTask.FromResult(Result<string>.Success("ok"))));
        Assert.False(under.Ok);
        Assert.Equal(ErrorCode.Conflict, under.Error!.Code);

        // And the other direction: a namespace cannot become runnable behind an existing child.
        Assert.True(registry.Register(new CommandNode("shop.item", "一件商品", AccessLevel.Agent,
            Body: (_, _) => ValueTask.FromResult(Result<string>.Success("ok")))).Ok);
        var shadow = registry.Register(new CommandNode("shop", "商店", AccessLevel.Agent,
            Body: (_, _) => ValueTask.FromResult(Result<string>.Success("ok"))));
        Assert.False(shadow.Ok);
        Assert.Equal(ErrorCode.Conflict, shadow.Error!.Code);
    }

    [Fact]
    public async Task An_unknown_path_is_an_error_and_not_an_apology()
    {
        var registry = Registry();
        Assert.True(registry.Register(new CommandNode("order.add", "下一单", AccessLevel.Agent,
            Body: (_, _) => ValueTask.FromResult(Result<string>.Success("ok")))).Ok);

        var missing = await registry.InvokeAsync(From("//foo", null), default);
        Assert.False(missing.Ok);
        Assert.Equal(ErrorCode.Invalid, missing.Error!.Code);
        Assert.Contains("没有这条指令", missing.Error.Message);

        // A word under a namespace that exists gets a useful answer: what it does have.
        var wrong = await registry.InvokeAsync(From("//order zzz", null), default);
        Assert.False(wrong.Ok);
        Assert.Contains("order", wrong.Error!.Message);
        Assert.Contains("add", wrong.Error!.Message);

        // Both refusals are recorded: a person who typed the prefix and got nowhere is a thing that happened.
        Assert.Equal(2, _signals.Count("invalid"));
    }

    [Fact]
    public async Task Unregister_takes_back_exactly_one_owner()
    {
        var registry = Registry();
        Assert.True(registry.Register(new CommandNode("mine", "我的", AccessLevel.Agent, Owner: "script:s1",
            Body: (_, _) => ValueTask.FromResult(Result<string>.Success("ok")))).Ok);
        Assert.True(registry.Register(new CommandNode("yours", "别人的", AccessLevel.Agent, Owner: "script:s2",
            Body: (_, _) => ValueTask.FromResult(Result<string>.Success("ok")))).Ok);

        Assert.True(registry.Unregister("script:s1").Ok);
        Assert.True(registry.Unregister("script:s1").Ok); // unload is not a promise

        Assert.False((await registry.InvokeAsync(From("//mine", null), default)).Ok);
        Assert.True((await registry.InvokeAsync(From("//yours", null), default)).Ok);
    }

    // ---- depth ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_strangers_depth_is_the_floor_and_operator_instructions_are_out_of_reach()
    {
        var registry = Registry();
        Assert.True(BuiltinCommands.Register(registry, new RecordingPlane()).Ok);

        var stranger = await registry.InvokeAsync(From("//reload", null), default);
        Assert.False(stranger.Ok);
        Assert.Equal(ErrorCode.Denied, stranger.Error!.Code);
        Assert.Contains("L1 operator", stranger.Error.Message);

        // Refused and recorded: a denial is a thing that happened.
        Assert.Equal(1, _signals.Count("denied"));
        Assert.Contains(_audit.Records, record => record.Actor == "command:unknown" && record.Outcome == "denied");

        // The same words from somebody with a card at operator run.
        var plane = new RecordingPlane();
        var deeper = Registry();
        Assert.True(BuiltinCommands.Register(deeper, plane).Ok);
        var allowed = await deeper.InvokeAsync(From("//reload", Xiaolin), default);
        Assert.True(allowed.Ok, allowed.Error?.Message);
        Assert.Equal("scripts.reload", plane.LastAction);
    }

    [Fact]
    public void Help_lists_only_what_the_caller_could_run()
    {
        var registry = Registry();
        Assert.True(BuiltinCommands.Register(registry, new RecordingPlane()).Ok);

        var forStranger = registry.Render(AccessLevel.Agent);
        Assert.Contains("//help", forStranger);
        Assert.Contains("//whoami", forStranger);
        Assert.DoesNotContain("//reload", forStranger);

        var forOperator = registry.Render(AccessLevel.Operator);
        Assert.Contains("//reload", forOperator);
    }

    [Fact]
    public async Task Who_am_I_says_which_card_and_which_depth()
    {
        var registry = Registry();
        Assert.True(BuiltinCommands.Register(registry, new RecordingPlane()).Ok);

        var known = await registry.InvokeAsync(From("//whoami", Xiaolin), default);
        Assert.True(known.Ok, known.Error?.Message);
        Assert.Contains("小林", known.Value!.Reply);
        Assert.Contains("L1 operator", known.Value.Reply);

        var stranger = await registry.InvokeAsync(From("//whoami", null), default);
        Assert.True(stranger.Ok);
        Assert.Contains("未登记", stranger.Value!.Reply);
        // The instruction that says who you are must also say where that answer comes from.
        Assert.Contains("identify/", stranger.Value.Reply);
    }

    // ---- arriving twice, and arriving too often ------------------------------------------------

    [Fact]
    public async Task The_same_platform_message_does_not_run_twice()
    {
        var registry = Registry();
        var ran = 0;
        Assert.True(registry.Register(new CommandNode("tick", "计数", AccessLevel.Agent,
            Body: (_, _) => ValueTask.FromResult(Result<string>.Success($"第 {++ran} 次")))).Ok);

        var first = await registry.InvokeAsync(From("//tick", null, "same-id"), default);
        var second = await registry.InvokeAsync(From("//tick", null, "same-id"), default);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal("第 1 次", first.Value!.Reply);
        Assert.Equal("duplicate", second.Value!.Outcome);
        Assert.Null(second.Value.Reply);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Over_the_limit_is_dropped_in_silence()
    {
        var registry = Registry(new CommandOptions(PerSenderPerMinute: 1));
        Assert.True(registry.Register(new CommandNode("ping", "测一下", AccessLevel.Agent,
            Body: (_, _) => ValueTask.FromResult(Result<string>.Success("pong")))).Ok);

        var first = await registry.InvokeAsync(From("//ping", null, "a"), default);
        var second = await registry.InvokeAsync(From("//ping", null, "b"), default);

        Assert.True(first.Ok);
        Assert.Equal("pong", first.Value!.Reply);
        Assert.True(second.Ok);
        Assert.Null(second.Value!.Reply);
        Assert.Equal("throttled", second.Value.Outcome);
        Assert.Equal(1, _signals.Count("throttled"));
    }

    // ---- the prefix, and the one look before dispatch -----------------------------------------

    [Fact]
    public void The_prefix_is_a_decision_and_two_slashes_are_the_default()
    {
        Assert.True(Registry().LooksLike("//help"));
        Assert.False(Registry().LooksLike("/help"));
        // A URL is not an instruction: it does not start with the prefix.
        Assert.False(Registry().LooksLike("https://example.com/a"));
        // Leading space is tolerated: a phone keyboard adds it, and refusing an instruction over it teaches nothing.
        Assert.True(Registry().LooksLike("  //help"));

        var bang = Registry(new CommandOptions(Prefix: "!!"));
        Assert.True(bang.LooksLike("!!help"));
        Assert.False(bang.LooksLike("//help"));
    }

    [Fact]
    public async Task The_look_before_dispatch_passes_what_is_not_an_instruction()
    {
        var registry = Registry();
        Assert.True(BuiltinCommands.Register(registry, new RecordingPlane()).Ok);
        var interceptor = new CommandInterceptor(registry, new RecordingAdapters(), new StubPeople(null));

        var ordinary = Event(Adapter, "大家好啊");
        var passed = await interceptor.InterceptAsync(ordinary, default);
        Assert.True(passed.Ok);
        Assert.Equal(EnvelopeDisposition.Pass, passed.Value);

        var instruction = Event(Adapter, "//help");
        var looked = await interceptor.InterceptAsync(instruction, default);
        // No Adapter answers in this stub, so the reply cannot be sent: the message was still taken out of the stream,
        // and the failure is reported rather than swallowed.
        Assert.False(looked.Ok);
        Assert.Equal(ErrorCode.Unavailable, looked.Error!.Code);
    }

    [Fact]
    public async Task A_payload_that_names_another_adapter_is_refused()
    {
        var registry = Registry();
        var interceptor = new CommandInterceptor(registry, new RecordingAdapters(), new StubPeople(null));
        // The event came from one Adapter, the payload says another: answering it would let a message be sent through
        // an Adapter it did not come from — the same check the accept verb makes, for the same reason.
        var forged = Event(Adapter, new Address("adapter", "qq", "backup"), "//help");
        var refused = await interceptor.InterceptAsync(forged, default);
        Assert.False(refused.Ok);
        Assert.Contains("does not match", refused.Error!.Message);
    }

    private static RoutedEvent Event(Address source, string text) => Event(source, source, text);

    private static RoutedEvent Event(Address source, Address payloadAdapter, string text)
    {
        var envelope = new AdapterEnvelope(payloadAdapter, "qq", Conversation, "m-" + Guid.NewGuid().ToString("N"), text,
            DateTimeOffset.UtcNow, Sender: "1234567", SenderName: "群名片");
        return new RoutedEvent(EventNames.AdapterEnvelope, source, envelope.EventId,
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    // ---- stubs ---------------------------------------------------------------------------------

    private sealed class StubPeople(Person? person) : IPersonDirectory
    {
        public Person? Find(Address adapter, string? account) =>
            person is not null && adapter == Adapter && account == "1234567" ? person : null;

        public Person? FindById(string? id) => person is not null && id == person.Id ? person : null;

        public IReadOnlyList<Person> List() => person is null ? [] : [person];

        public Person? Self => null;
    }

    private sealed class RecordingAdapters : IAdapterManager
    {
        public Result<IChatAdapter> Get(Address address) =>
            Result<IChatAdapter>.Failure(new(ErrorCode.Unavailable, "No Adapter answers in this test."));

        public IReadOnlyList<AdapterInfo> List() => [];
        public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, Func<AdapterSpec, IChatAdapter> factory,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result<IReadOnlyList<AdapterInfo>>> SnapshotByTypeAsync(string concreteTypeId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result> DestroyByTypeAsync(string concreteTypeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> ForceDestroyByTypeAsync(string concreteTypeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StartAsync(Address address, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DestroyAsync(Address address, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
    }

    private sealed class RecordingPlane : IControlPlane
    {
        private static readonly JsonElement Ok = JsonSerializer.SerializeToElement(new { version = "test" });

        public string? LastAction { get; private set; }

        public IReadOnlyList<ActionDescriptor> Catalog => [];

        public Result Register(ActionDescriptor descriptor, ActionHandler handler) => Result.Success();

        public Result Register(ActionDescriptor descriptor, AuthorizedActionHandler handler) => Result.Success();

        public ValueTask<Result<JsonElement>> InvokeAsync(Actor actor, string action, JsonElement arguments,
            CancellationToken cancellationToken)
        {
            LastAction = action;
            return ValueTask.FromResult(Result<JsonElement>.Success(Ok));
        }
    }

    private sealed class RecordingSignals : ISignalLog
    {
        private readonly List<SignalRecord> _records = [];

        public SignalLevel MinLevel => SignalLevel.Info;

        public bool IsEnabled(SignalLevel level) => true;

        public void Write(SignalRecord record) => _records.Add(record);

        public IReadOnlyList<SignalRecord> Snapshot() => _records;

        public int Count(string outcome) => _records.Count(record => record.Outcome == outcome);
    }

    private sealed class RecordingAudit : IAuditSink
    {
        private readonly List<AuditRecord> _records = [];

        public IReadOnlyList<AuditRecord> Records => _records;

        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            _records.Add(record);
            return ValueTask.CompletedTask;
        }
    }
}
