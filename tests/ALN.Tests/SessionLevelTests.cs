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

        // An agent cannot raise a Session at all: the verb itself is an operator's.
        var byAgent = await plane.InvokeAsync(Agent(), SessionActions.SetLevel, Args("s-2", "operator"), default);
        Assert.Equal(ErrorCode.Denied, byAgent.Error!.Code);
        Assert.Contains("needs L1 operator", byAgent.Error.Message);

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
