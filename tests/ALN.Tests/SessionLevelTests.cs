using System.Runtime.CompilerServices;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The one thing an operator can change while the process runs: what a Session is trusted with. It has to be both
/// changeable and immediate — a level that needs a restart is a level nobody sets — and it has to refuse to become a
/// way of promoting yourself.
/// </summary>
public sealed class SessionLevelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-level-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedModel _model = new();
    private readonly LevelRecorder _recorder = new();
    private readonly SessionExtensionRegistry _extensions = new();
    private readonly SessionManager _sessions;

    public SessionLevelTests()
    {
        Assert.True(_extensions.Register(_recorder).Ok);
        _sessions = new SessionManager(_root, _model, _extensions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static SessionSpec Spec() => new("default", "Answer briefly.", "1");

    private static Actor Operator(string id = "ops") =>
        new(id, ActorKind.User, [Actor.Any], AccessLevel.Operator);

    private static Actor Agent() => new("model", ActorKind.Model, [Actor.Any], AccessLevel.Agent);

    private static ControlPlane Plane(SessionManager sessions)
    {
        var plane = new ControlPlane(new SilentAudit(), SignalLog.Open(null));
        Assert.True(sessions.RegisterActions(plane).Ok);
        return plane;
    }

    private static JsonElement Args(string id, string level) => JsonSerializer.SerializeToElement(new { id, level });

    [Fact]
    public async Task A_session_can_be_made_on_purpose_and_an_existing_name_keeps_what_it_is()
    {
        // 2026-11-12 新增的动词。之前造一个会话只有一条路：挂一个脚本，让它 ctx.session.open。这条测试钉三件事：
        // agent 级就能造；造出来的东西真的在盘上、真的能被叫起来；同名再叫一次不重写它——"指到一个东西"不等于
        // "决定它是什么"。
        var plane = Plane(_sessions);
        var asked = JsonSerializer.SerializeToElement(new
        {
            id = "s-made",
            profile = "default",
            objective = "守着一个话题。",
            level = "agent"
        });

        var made = await plane.InvokeAsync(Agent(), SessionActions.Create, asked, default);
        Assert.True(made.Ok, made.Error?.Message);
        Assert.Equal("s-made", made.Value.GetProperty("id").GetString());
        Assert.True(made.Value.GetProperty("created").GetBoolean());
        Assert.True(File.Exists(Path.Combine(_root, "modules", "s-made", "module.json")));

        // The next turn runs at the level it was made with — the spec is the thing that was written, not a default.
        Assert.True((await _sessions.TurnAsync("s-made", Spec(), Guid.NewGuid(),
            new SessionTurnInput([new("m-1", "user", "alice", "hi", DateTimeOffset.UtcNow)]), null, default)).Ok);
        Assert.Equal(AccessLevel.Agent, Assert.Single(_recorder.Seen));

        // Same name, different objective: the stored one wins, and the answer says so instead of pretending it wrote.
        var again = await plane.InvokeAsync(Operator(), SessionActions.Create, JsonSerializer.SerializeToElement(new
        {
            id = "s-made",
            profile = "default",
            objective = "换一个目标（不该生效）。",
            level = "operator"
        }), default);
        Assert.True(again.Ok, again.Error?.Message);
        Assert.False(again.Value.GetProperty("created").GetBoolean());
        Assert.Contains("守着一个话题", again.Value.GetProperty("objective").GetString());
        Assert.Equal("L0 agent", again.Value.GetProperty("level").GetString());
    }

    [Fact]
    public async Task Creating_one_that_is_deeper_than_you_are_is_refused_by_the_same_rule_as_raising_one()
    {
        // 造一个会话是"授出一份等级"，因为它会留在那份会话上、从此每轮生效。所以它走的是同一个委派不变式，
        // 而不是一张自己的算法：把 level 交给控制面那一个点去比。
        var plane = Plane(_sessions);
        var tooDeep = await plane.InvokeAsync(Agent(), SessionActions.Create,
            JsonSerializer.SerializeToElement(new { id = "s-deep", level = "full" }), default);

        Assert.Equal(ErrorCode.Denied, tooDeep.Error!.Code);
        Assert.Contains("cannot hand out", tooDeep.Error.Message);
        // Refused before anything was written: a name with no Session behind it does not become a directory on the way.
        Assert.False(Directory.Exists(Path.Combine(_root, "modules", "s-deep")));

        // And the same request from somebody at that depth is exactly what the verb is for.
        Assert.True((await plane.InvokeAsync(Operator(), SessionActions.Create,
            JsonSerializer.SerializeToElement(new { id = "s-deep", profile = "default", level = "operator" }), default)).Ok);
        Assert.True(Directory.Exists(Path.Combine(_root, "modules", "s-deep")));
    }

    [Fact]
    public async Task A_session_made_without_a_name_gets_one_and_can_be_woken()
    {
        // 默认名字是"类型 + 新后缀"，和别的实例一样（C-01：名字不是描述，不解析）。造完就能递话，wake 真的跑一轮。
        var plane = Plane(_sessions);
        var made = await plane.InvokeAsync(Agent(), SessionActions.Create,
            JsonSerializer.SerializeToElement(new { profile = "default", objective = "没有频道也活着。" }), default);
        Assert.True(made.Ok, made.Error?.Message);

        var id = made.Value.GetProperty("id").GetString()!;
        Assert.StartsWith("session.chat#", id, StringComparison.Ordinal);
        Assert.Null(made.Value.GetProperty("channel").GetString());

        var woken = await plane.InvokeAsync(Agent(), SessionActions.Message, JsonSerializer.SerializeToElement(new
        {
            id,
            text = "你在吗",
            wake = true
        }), default);
        Assert.True(woken.Ok, woken.Error?.Message);
        Assert.Equal(AccessLevel.Agent, Assert.Single(_recorder.Seen));
    }

    [Fact]
    public async Task A_level_change_is_on_disk_and_is_carried_by_the_very_next_turn()
    {
        Assert.True((await _sessions.OpenAsync("s-1", Spec(), default)).Ok);
        var plane = Plane(_sessions);

        var raised = await plane.InvokeAsync(Operator(), SessionActions.SetLevel, Args("s-1", "operator"), default);
        Assert.True(raised.Ok, raised.Error?.Message);

        // On disk, spelled as a word: this is a file a person opens.
        var spec = await File.ReadAllTextAsync(Path.Combine(_root, "modules", "s-1", "module.json"));
        Assert.Contains("\"level\":\"operator\"", spec.Replace(" ", ""));

        // And already in force, without a restart and without a reload: the level is read when a turn is assembled.
        var turn = await _sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([new("m-1", "user", "alice", "hello", DateTimeOffset.UtcNow)]), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        Assert.Equal(AccessLevel.Operator, Assert.Single(_recorder.Seen));

        // Lowering works the same way, which matters as much: a level you cannot take back is a level you cannot give.
        var lowered = await plane.InvokeAsync(Operator(), SessionActions.SetLevel, Args("s-1", "agent"), default);
        Assert.True(lowered.Ok, lowered.Error?.Message);
        _recorder.Seen.Clear();
        Assert.True((await _sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([new("m-2", "user", "alice", "again", DateTimeOffset.UtcNow)]), null, default)).Ok);
        Assert.Equal(AccessLevel.Agent, Assert.Single(_recorder.Seen));
    }

    [Fact]
    public async Task Nobody_hands_out_a_level_above_their_own()
    {
        Assert.True((await _sessions.OpenAsync("s-2", Spec(), default)).Ok);
        var plane = Plane(_sessions);

        // 2026-11-12 改：sessions.control 降到 agent，于是"agent 调不动这个动词"不再是这里的断言。挡 agent 的
        // 换成委派不变式本身——它才是这条测试真正要钉的东西，比原来那句"这是 operator 的动词"更硬：
        // 等级可以授出，但只许授出不比自己的深的。
        var byAgent = await plane.InvokeAsync(Agent(), SessionActions.SetLevel, Args("s-2", "operator"), default);
        Assert.Equal(ErrorCode.Denied, byAgent.Error!.Code);
        Assert.Contains("cannot hand out", byAgent.Error.Message);

        // 同一级它授得出去：不变式管的是"不比自己的深"，不是"不许动"。
        Assert.True((await plane.InvokeAsync(Agent(), SessionActions.SetLevel, Args("s-2", "agent"), default)).Ok);

        // An operator cannot hand out the kernel's level — delegating upwards is the one thing the rule forbids.
        var upwards = await plane.InvokeAsync(Operator(), SessionActions.SetLevel, Args("s-2", "full"), default);
        Assert.Equal(ErrorCode.Denied, upwards.Error!.Code);
        Assert.Contains("cannot hand out", upwards.Error.Message);

        // Granting one at or below their own is exactly what they are for.
        Assert.True((await plane.InvokeAsync(Operator(), SessionActions.SetLevel, Args("s-2", "operator"), default)).Ok);

        // A name with no Session behind it is refused, and does not become a directory on the way.
        var missing = await plane.InvokeAsync(Operator(), SessionActions.SetLevel, Args("s-nope", "agent"), default);
        Assert.Equal(ErrorCode.Invalid, missing.Error!.Code);
        Assert.False(Directory.Exists(Path.Combine(_root, "modules", "s-nope")));
    }

    [Fact]
    public async Task A_level_written_into_the_file_by_hand_is_in_force_when_the_Session_is_loaded()
    {
        // The other way a person gives a Session a level: they open module.json and type it. That file is the
        // Session's own, so it wins over whatever a caller proposes — and it has to be readable spelled as a word.
        var directory = Path.Combine(_root, "modules", "s-hand");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "module.json"), """
            {
              "id": "s-hand",
              "type": "session.chat",
              "config": {"modelProfileId": "default", "objective": "Answer briefly.", "formatVersion": "1", "level": "operator"}
            }
            """);

        // The caller offers the default (agent) spec, which is what a fresh mount would carry.
        var turn = await _sessions.TurnAsync("s-hand", Spec(), Guid.NewGuid(),
            new SessionTurnInput([new("m-1", "user", "alice", "hello", DateTimeOffset.UtcNow)]), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        Assert.Equal(AccessLevel.Operator, Assert.Single(_recorder.Seen));

        // And loading it once does not quietly rewrite the level back to the caller's proposal.
        var spec = await File.ReadAllTextAsync(Path.Combine(directory, "module.json"));
        Assert.Contains("\"level\": \"operator\"", spec);
    }

    /// <summary>Remembers the level each round was assembled with. The whole point is that it is per round.</summary>
    private sealed class LevelRecorder : IFloatingContextSource
    {
        public List<AccessLevel> Seen { get; } = [];

        public string Id => "test.level-recorder";

        public ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(RoundContextRequest request,
            CancellationToken cancellationToken)
        {
            Seen.Add(request.Level);
            return ValueTask.FromResult(Result<IReadOnlyList<RoundContextContribution>>.Success([]));
        }
    }

    private sealed class SilentAudit : IAuditSink
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ScriptedModel : IModelManager
    {
        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream()));

        private static async IAsyncEnumerable<ModelEvent> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new TextDelta("ok");
            yield return new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1), null);
        }

        public Result RegisterFactory(string packageId, IModelProviderFactory factory) => Result.Success();
        public Result RemovePackageFactories(string packageId) => Result.Success();
        public IReadOnlyList<ModelProviderTypeInfo> ListTypes() => [];
        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<Result<IModelProvider>> CreateAsync(string category, string instance, string configurationJson,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public Result<IModelProvider> Get(Address provider) => Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "No providers."));
        public IReadOnlyList<ModelProviderInfo> List() => [];
        public Result RegisterProfile(ModelProfile profile) => Result.Success();
        public Result RemoveProfile(string profileId) => Result.Success();
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }
}
