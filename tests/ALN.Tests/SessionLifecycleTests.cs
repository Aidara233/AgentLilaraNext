using System.Runtime.CompilerServices;
using System.Text.Json;
using ALN.Channel;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// A Session is the only thing in the data root that the host mints by itself — a Channel's script asks for
/// one and a hash becomes a directory. So it is also the only thing that can pile up unseen, and these tests
/// pin the four facts that make that pile manageable: who a directory belongs to, when it last spoke, that a
/// destroyed Channel takes its own with it, and that none of it is looked at until somebody asks.
/// </summary>
public sealed class SessionLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-session-life-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Address Chat = new("channel", "conversation", "chat-1");
    private static readonly Address Ghost = new("channel", "conversation", "ghost");
    private static readonly ChannelBinding FileBinding = new(new("adapter", "file", "file-1"), "chat-1");
    private static readonly SessionSpec Spec = new("default", "Answer briefly.", "You are a test agent.", "1");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SessionManager Sessions() => new(Path.Combine(_root, "sessions"), new ScriptedModel());

    private static SessionTurnInput Input(Address? channel, string text) => new(
        channel is null ? [] : [new($"m-{Guid.NewGuid():N}", "user", "alice", text, DateTimeOffset.UtcNow)],
        channel,
        channel is null ? null : FileBinding);

    private SessionState StoredState(string sessionId) => JsonSerializer.Deserialize<SessionState>(
        File.ReadAllBytes(Path.Combine(_root, "sessions", sessionId, "state.json")), Json)!;

    [Fact]
    public async Task A_session_records_which_channel_it_serves_and_when_it_last_spoke()
    {
        // Two instances over one root is what a restart looks like: nothing about a Session lives in the object.
        var first = Sessions();
        var turn = await first.RunTurnAsync("s-one", Spec, Guid.NewGuid(), Input(Chat, "hello"), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var afterFirst = StoredState("s-one");
        // The id is a hash, so this line is the only way a person can tell whose directory this is.
        Assert.Equal("channel.conversation#chat-1", afterFirst.Channel);
        Assert.NotNull(afterFirst.LastActiveAt);

        var spokeAt = afterFirst.LastActiveAt;
        await Task.Delay(20);
        var second = Sessions();
        var again = await second.RunTurnAsync("s-one", Spec, Guid.NewGuid(), Input(Chat, "still here"), null, default);
        Assert.True(again.Ok, again.Error?.Detail ?? again.Error?.Message);

        var afterSecond = StoredState("s-one");
        Assert.Equal("channel.conversation#chat-1", afterSecond.Channel);
        Assert.True(afterSecond.LastActiveAt > spokeAt, "a completed turn must move lastActiveAt forward");
    }

    [Fact]
    public async Task A_destroyed_channel_takes_its_own_sessions_and_leaves_the_rest()
    {
        var sessions = Sessions();
        Assert.True((await sessions.RunTurnAsync("s-chat", Spec, Guid.NewGuid(), Input(Chat, "hi"), null, default)).Ok);
        Assert.True((await sessions.RunTurnAsync("s-ghost", Spec, Guid.NewGuid(), Input(Ghost, "hi"), null, default)).Ok);

        var channels = await ChannelsServing(sessions, Chat);
        var destroyed = await channels.DestroyAsync(Chat, default);
        Assert.True(destroyed.Ok, destroyed.Error?.Detail ?? destroyed.Error?.Message);

        Assert.False(Directory.Exists(Path.Combine(_root, "sessions", "s-chat")));
        // Somebody else's conversation is not collateral.
        Assert.True(Directory.Exists(Path.Combine(_root, "sessions", "s-ghost")));

        // And the Channel really is gone, so a re-created one starts from an empty session instead of a
        // spec it can never match.
        Assert.False(channels.Get(Chat).Ok);
    }

    [Fact]
    public async Task Collection_offers_every_kind_of_dead_session_and_a_dry_run_deletes_nothing()
    {
        var sessions = Sessions();
        Assert.True((await sessions.RunTurnAsync("s-live", Spec, Guid.NewGuid(), Input(Chat, "hi"), null, default)).Ok);
        Assert.True((await sessions.RunTurnAsync("s-orphan", Spec, Guid.NewGuid(), Input(Ghost, "hi"), null, default)).Ok);
        // No Channel at all: written by a caller that walked a Session straight through the manager.
        Assert.True((await sessions.RunTurnAsync("s-unowned", Spec, Guid.NewGuid(), Input(null, "hi"), null, default)).Ok);
        Assert.True((await sessions.RunTurnAsync("s-stale", Spec, Guid.NewGuid(), Input(Chat, "hi"), null, default)).Ok);

        // Backdate one as "nobody has spoken to this since spring".
        var stale = StoredState("s-stale");
        await File.WriteAllBytesAsync(Path.Combine(_root, "sessions", "s-stale", "state.json"),
            JsonSerializer.SerializeToUtf8Bytes(stale with { LastActiveAt = DateTimeOffset.UtcNow.AddDays(-90) }, Json));

        var dryRun = await sessions.PruneAsync(new([Chat.ToString()], DryRun: true, IdleDays: 30), default);
        Assert.True(dryRun.Ok, dryRun.Error?.Detail ?? dryRun.Error?.Message);
        var offered = dryRun.Value!.Items.ToDictionary(item => item.Id, item => item.Reason, StringComparer.Ordinal);
        Assert.Equal(SessionPruneReasons.ChannelGone, offered["s-orphan"]);
        Assert.Equal(SessionPruneReasons.ChannelUnknown, offered["s-unowned"]);
        Assert.Equal(SessionPruneReasons.Idle, offered["s-stale"]);
        Assert.DoesNotContain("s-live", offered.Keys);
        // Looking costs nothing: every directory is still there and every byte is accounted for.
        Assert.Equal(0, dryRun.Value.Removed);
        Assert.All(offered.Keys, id => Assert.True(Directory.Exists(Path.Combine(_root, "sessions", id))));
        Assert.All(dryRun.Value.Items, item => Assert.True(item.Bytes > 0));

        var sweep = await sessions.PruneAsync(new([Chat.ToString()], DryRun: false, IdleDays: 30), default);
        Assert.True(sweep.Ok, sweep.Error?.Detail ?? sweep.Error?.Message);
        Assert.Equal(3, sweep.Value!.Removed);
        Assert.True(Directory.Exists(Path.Combine(_root, "sessions", "s-live")));
        Assert.Equal(["s-live"], (await sessions.ListAsync(default)).Value!.Select(session => session.Id));

        // The listing says what a keep-or-delete decision needs, and nothing else.
        var live = Assert.Single((await sessions.ListAsync(default)).Value!);
        Assert.Equal("channel.conversation#chat-1", live.Channel);
        Assert.NotNull(live.LastActiveAt);
        Assert.True(live.Bytes > 0);
    }

    [Fact]
    public async Task A_spec_that_no_longer_matches_is_marked_instead_of_failing_forever_unexplained()
    {
        var sessions = Sessions();
        Assert.True((await sessions.RunTurnAsync("s-stuck", Spec, Guid.NewGuid(), Input(Chat, "hi"), null, default)).Ok);

        // The script was edited: same Channel, same id, different spec. The stored spec is immutable, so this
        // caller can never be served again — and now the directory says so.
        var changed = Spec with { SystemPrompt = "You are a different agent." };
        var refused = await sessions.RunTurnAsync("s-stuck", changed, Guid.NewGuid(), Input(Chat, "hello again"), null, default);
        Assert.Equal(ErrorCode.Conflict, refused.Error!.Code);

        var offered = await sessions.PruneAsync(new([Chat.ToString()], DryRun: true, IdleDays: 0), default);
        var stuck = Assert.Single(offered.Value!.Items);
        Assert.Equal("s-stuck", stuck.Id);
        Assert.Equal(SessionPruneReasons.SpecSuperseded, stuck.Reason);

        // Reverting the script revives it, and the first turn that works again clears the note: the marker is
        // evidence of a failure, not a sentence.
        Assert.True((await sessions.RunTurnAsync("s-stuck", Spec, Guid.NewGuid(), Input(Chat, "back"), null, default)).Ok);
        Assert.Empty((await sessions.PruneAsync(new([Chat.ToString()], DryRun: true, IdleDays: 0), default)).Value!.Items);
    }

    [Fact]
    public async Task Building_the_manager_and_running_an_unrelated_turn_never_walks_the_session_tree()
    {
        var ghosts = Enumerable.Range(0, 50).Select(index => WriteGhostSession(index)).ToArray();
        var before = ghosts.ToDictionary(dir => dir, Snapshot, StringComparer.Ordinal);

        var sessions = Sessions();
        var turn = await sessions.RunTurnAsync("s-fresh", Spec, Guid.NewGuid(), Input(Chat, "hi"), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        // Nothing walked them, repaired them, migrated them or rewrote their metadata. Reads are not observable
        // without a filesystem hook, so what is pinned here is the observable half: laziness means a forgotten
        // Session costs nothing at startup and nothing during somebody else's turn.
        foreach (var ghost in ghosts)
            Assert.Equal(before[ghost], Snapshot(ghost));

        var directories = Directory.GetDirectories(Path.Combine(_root, "sessions"));
        Assert.Equal(ghosts.Length + 1, directories.Length);
        Assert.Equal("channel.conversation#chat-1", StoredState("s-fresh").Channel);
    }

    /// <summary>A Session directory as an older run would have left it: real files, a recorded Channel.</summary>
    private string WriteGhostSession(int index)
    {
        var directory = Path.Combine(_root, "sessions", $"s-ghost{index}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "spec.json"), "{\"objective\":\"ghost\",\"formatVersion\":\"1\"}");
        File.WriteAllText(Path.Combine(directory, "state.json"),
            "{\"status\":0,\"stepNumber\":3,\"revision\":3,\"locked\":false,\"usage\":{\"inputTokens\":1,\"outputTokens\":1}," +
            "\"schemaVersion\":2,\"channel\":\"channel.conversation#ghost\"}");
        File.WriteAllText(Path.Combine(directory, "history.jsonl"), "{\"id\":\"ghost\"}\n");
        return directory;
    }

    /// <summary>The observable shape of a directory: which files, how big, and when it was last written.</summary>
    private static (string Files, long Bytes, DateTime Written) Snapshot(string directory)
    {
        var files = Directory.GetFiles(directory);
        return (string.Join('|', files.Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal)),
            files.Sum(file => new FileInfo(file).Length),
            files.Max(File.GetLastWriteTimeUtc));
    }

    /// <summary>A Channel that exists, so destroying it is a real destroy and not a "not found".</summary>
    private static async Task<ChannelManager> ChannelsServing(ISessionManager sessions, Address channel)
    {
        var channels = new ChannelManager(new StubAdapters(), new EventQueue(8), new StubBindings(), sessions: sessions);
        var created = await channels.CreateAsync(new(channel, channel.Instance!, [FileBinding]), default);
        Assert.True(created.Ok, created.Error?.Detail ?? created.Error?.Message);
        return channels;
    }

    private sealed class StubAdapters : IAdapterManager
    {
        public Result<IChatAdapter> Get(Address address) => Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, "No adapters in this test."));
        public IReadOnlyList<AdapterInfo> List() => [];
        public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, Func<AdapterSpec, IChatAdapter> factory,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result<IReadOnlyList<AdapterInfo>>> SnapshotByTypeAsync(string concreteTypeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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

    private sealed class StubBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }

    private sealed class ScriptedModel : IModelManager
    {
        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream()));

        private static async IAsyncEnumerable<ModelEvent> Stream([EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        public IReadOnlyList<ModelProfile> ListProfiles() => [new("default", new("model", "openai-chat", "default"), "test-model")];
    }
}
