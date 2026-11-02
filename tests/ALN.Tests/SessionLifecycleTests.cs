using System.Runtime.CompilerServices;
using System.Text.Json;
using ALN.Archive;
using ALN.Channel;
using ALN.Contracts;
using ALN.Contracts.Archive;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// A Session is the only thing the host mints by itself — a Channel's script asks for one and a hash becomes a
/// directory — so it is also the only thing that can pile up unseen. These tests pin what makes that pile
/// manageable: a directory says whose it is and when it last spoke, and the bin can take it away and give it
/// back without either operation ever overwriting or merging anything.
/// </summary>
public sealed class SessionLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-session-life-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Address Chat = new("channel", "conversation", "chat-1");
    private static readonly Address Ghost = new("channel", "conversation", "ghost");
    private static readonly ChannelBinding FileBinding = new(new("adapter", "file", "file-1"), "chat-1");
    private static readonly SessionSpec Spec = new("default", "Answer briefly.", "1");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SessionManager Sessions() => new(Path.Combine(_root, "sessions"), new ScriptedModel());

    private ArchiveManager Bin() => new(_root);

    private string SessionPath(string sessionId) => Path.Combine(_root, "sessions", sessionId);

    private static SessionTurnInput Input(Address? channel, string text) => new(
        channel is null ? [] : [new($"m-{Guid.NewGuid():N}", "user", "alice", text, DateTimeOffset.UtcNow)],
        channel,
        channel is null ? null : FileBinding);

    private SessionState StoredState(string sessionId) => JsonSerializer.Deserialize<SessionState>(
        File.ReadAllBytes(Path.Combine(SessionPath(sessionId), "state.json")), Json)!;

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

    /// <summary>
    /// Both halves of "the engine and the bin agree on what an id means": the bin's entry name is its own, and
    /// the source's id travels with it so a restore still knows where it belongs.
    /// </summary>
    [Fact]
    public async Task The_bin_takes_a_session_away_and_gives_it_back()
    {
        var sessions = Sessions();
        // Ghost's Channel does not exist here, so the Session is offered for the real reason (channel-gone)
        // rather than because a test wanted it to be.
        Assert.True((await sessions.RunTurnAsync("s-one", Spec, Guid.NewGuid(), Input(Ghost, "hello"), null, default)).Ok);
        var before = StoredState("s-one");
        var bin = Bin();
        var channels = await ChannelsServing(sessions, bin, Chat);
        Assert.True(bin.Register(new SessionArchiveSource(sessions, channels)).Ok);

        // Dry run first: it says what would happen and does nothing.
        var dry = await bin.ArchiveAsync(ArchiveKinds.Session, null, dryRun: true, default);
        Assert.True(dry.Ok, dry.Error?.Detail ?? dry.Error?.Message);
        var offered = Assert.Single(dry.Value!.Items);
        Assert.Equal("s-one", offered.Id);
        Assert.False(offered.Done);
        Assert.True(Directory.Exists(SessionPath("s-one")));

        var moved = await bin.ArchiveAsync(ArchiveKinds.Session, null, dryRun: false, default);
        Assert.True(moved.Ok, moved.Error?.Detail ?? moved.Error?.Message);
        Assert.True(Assert.Single(moved.Value!.Items).Done);
        // Gone from the living side: the engine's own list says so, and so does the filesystem.
        Assert.Empty((await sessions.ListAsync(default)).Value!);
        Assert.False(Directory.Exists(SessionPath("s-one")));

        var archived = Assert.Single((await bin.ArchivedAsync(ArchiveKinds.Session, default)).Value!);
        Assert.Equal("s-one", archived.Id);
        Assert.Equal("s-one", archived.SourceId);
        Assert.True(archived.Bytes > 0);
        // The material really moved: the archived copy has the history that was committed just before.
        var archivedState = JsonSerializer.Deserialize<SessionState>(
            File.ReadAllBytes(Path.Combine(archived.Path, ArchiveLayout.Payload, "state.json")), Json)!;
        Assert.Equal(before.StepNumber, archivedState.StepNumber);
        // The manifest says where it came from, so a person can put it back by hand if the database is gone.
        Assert.Equal("sessions/s-one", archived.Note!["origin"]);

        var restored = await bin.RestoreAsync(ArchiveKinds.Session, ["s-one"], dryRun: false, default);
        Assert.True(restored.Ok, restored.Error?.Detail ?? restored.Error?.Message);
        Assert.True(Assert.Single(restored.Value!.Items).Done);
        Assert.True(Directory.Exists(SessionPath("s-one")));
        Assert.Empty((await bin.ArchivedAsync(ArchiveKinds.Session, default)).Value!);
        Assert.Equal("channel.conversation#ghost", Assert.Single((await sessions.ListAsync(default)).Value!).Channel);

        // And it is a working Session again, not just a directory: the engine picks the history up where it was.
        var resumed = await sessions.RunTurnAsync("s-one", Spec, Guid.NewGuid(), Input(Ghost, "back again"), null, default);
        Assert.True(resumed.Ok, resumed.Error?.Detail ?? resumed.Error?.Message);
        Assert.True(resumed.Value!.StepNumber > before.StepNumber);
    }

    [Fact]
    public async Task Candidates_cover_the_four_ways_a_session_stops_being_useful()
    {
        var sessions = Sessions();
        Assert.True((await sessions.RunTurnAsync("s-live", Spec, Guid.NewGuid(), Input(Chat, "hi"), null, default)).Ok);
        Assert.True((await sessions.RunTurnAsync("s-orphan", Spec, Guid.NewGuid(), Input(Ghost, "hi"), null, default)).Ok);
        // No Channel at all: written by a caller that ran a Session straight through the manager.
        Assert.True((await sessions.RunTurnAsync("s-unowned", Spec, Guid.NewGuid(), Input(null, "hi"), null, default)).Ok);
        Assert.True((await sessions.RunTurnAsync("s-stale", Spec, Guid.NewGuid(), Input(Chat, "hi"), null, default)).Ok);

        // Backdate one as "nobody has spoken to this since spring".
        var stale = StoredState("s-stale");
        await File.WriteAllBytesAsync(Path.Combine(SessionPath("s-stale"), "state.json"),
            JsonSerializer.SerializeToUtf8Bytes(stale with { LastActiveAt = DateTimeOffset.UtcNow.AddDays(-90) }, Json));

        var bin = Bin();
        var channels = await ChannelsServing(sessions, bin, Chat);
        Assert.True(bin.Register(new SessionArchiveSource(sessions, channels, idleDays: 30)).Ok);

        var candidates = await bin.CandidatesAsync(ArchiveKinds.Session, default);
        Assert.True(candidates.Ok, candidates.Error?.Detail ?? candidates.Error?.Message);
        var offered = candidates.Value!.ToDictionary(candidate => candidate.Id, candidate => candidate.Reason, StringComparer.Ordinal);
        Assert.Equal(SessionArchiveReasons.ChannelGone, offered["s-orphan"]);
        Assert.Equal(SessionArchiveReasons.ChannelUnknown, offered["s-unowned"]);
        Assert.Equal(SessionArchiveReasons.Idle, offered["s-stale"]);
        Assert.DoesNotContain("s-live", offered.Keys);
        Assert.All(candidates.Value!, candidate => Assert.True(candidate.Bytes > 0));

        // A spec that changed can never match again: the refusal leaves a note, and the note is a reason.
        var changed = Spec with { Objective = "Answer differently." };
        var refused = await sessions.RunTurnAsync("s-live", changed, Guid.NewGuid(), Input(Chat, "again"), null, default);
        Assert.Equal(ErrorCode.Conflict, refused.Error!.Code);
        var afterRefusal = await bin.CandidatesAsync(ArchiveKinds.Session, default);
        Assert.Equal(SessionArchiveReasons.SpecSuperseded,
            afterRefusal.Value!.Single(candidate => candidate.Id == "s-live").Reason);
    }

    [Fact]
    public async Task A_destroyed_channel_puts_its_own_sessions_in_the_bin()
    {
        var sessions = Sessions();
        Assert.True((await sessions.RunTurnAsync("s-chat", Spec, Guid.NewGuid(), Input(Chat, "hi"), null, default)).Ok);
        Assert.True((await sessions.RunTurnAsync("s-ghost", Spec, Guid.NewGuid(), Input(Ghost, "hi"), null, default)).Ok);

        var bin = Bin();
        var channels = await ChannelsServing(sessions, bin, Chat);
        Assert.True(bin.Register(new SessionArchiveSource(sessions, channels)).Ok);

        var destroyed = await channels.DestroyAsync(Chat, default);
        Assert.True(destroyed.Ok, destroyed.Error?.Detail ?? destroyed.Error?.Message);

        // Out of service, not out of existence: the Channel's Session is in the bin...
        Assert.False(Directory.Exists(SessionPath("s-chat")));
        Assert.Equal("s-chat", Assert.Single((await bin.ArchivedAsync(ArchiveKinds.Session, default)).Value!).Id);
        // ...and somebody else's conversation was never touched.
        Assert.True(Directory.Exists(SessionPath("s-ghost")));
        Assert.False(channels.Get(Chat).Ok);
    }

    [Fact]
    public async Task Two_entries_for_one_id_never_overwrite_each_other()
    {
        var sessions = Sessions();
        var bin = Bin();
        var channels = await ChannelsServing(sessions, bin, Chat);
        Assert.True(bin.Register(new SessionArchiveSource(sessions, channels)).Ok);

        Assert.True((await sessions.RunTurnAsync("s-twice", Spec, Guid.NewGuid(), Input(Ghost, "first life"), null, default)).Ok);
        var firstBytes = StoredState("s-twice").StepNumber;
        Assert.True((await bin.ArchiveAsync(ArchiveKinds.Session, null, dryRun: false, default)).Ok);

        // The same id exists again (a Channel's script does not know about the bin) and goes away again.
        Assert.True((await sessions.RunTurnAsync("s-twice", Spec, Guid.NewGuid(), Input(Ghost, "second life"), null, default)).Ok);
        Assert.True((await bin.ArchiveAsync(ArchiveKinds.Session, null, dryRun: false, default)).Ok);

        var entries = (await bin.ArchivedAsync(ArchiveKinds.Session, default)).Value!;
        Assert.Equal(2, entries.Count);
        // Two addresses, one source id: the older entry is still there to be restored on its own.
        Assert.Equal(2, entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, entry => Assert.Equal("s-twice", entry.SourceId));
        Assert.Contains(entries, entry => entry.Id != "s-twice");
        Assert.True(firstBytes > 0);
    }

    [Fact]
    public async Task Restoring_into_a_rebuilt_session_is_refused_instead_of_merging_two_histories()
    {
        var sessions = Sessions();
        var bin = Bin();
        var channels = await ChannelsServing(sessions, bin, Chat);
        Assert.True(bin.Register(new SessionArchiveSource(sessions, channels)).Ok);

        Assert.True((await sessions.RunTurnAsync("s-twice", Spec, Guid.NewGuid(), Input(Ghost, "first life"), null, default)).Ok);
        Assert.True((await bin.ArchiveAsync(ArchiveKinds.Session, null, dryRun: false, default)).Ok);
        Assert.True((await sessions.RunTurnAsync("s-twice", Spec, Guid.NewGuid(), Input(Ghost, "second life"), null, default)).Ok);

        var refused = await bin.RestoreAsync(ArchiveKinds.Session, ["s-twice"], dryRun: false, default);
        var outcome = Assert.Single(refused.Value!.Items);
        Assert.False(outcome.Done);
        Assert.Contains("merge", outcome.Message, StringComparison.OrdinalIgnoreCase);
        // The entry stays in the bin: a refused restore is not a lost one.
        Assert.Single((await bin.ArchivedAsync(ArchiveKinds.Session, default)).Value!);
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

    [Fact]
    public async Task An_explicit_id_can_be_archived_even_when_the_source_did_not_offer_it()
    {
        // A conversation that is broken (its stored spec no longer reads) is not "garbage" by any checkable rule,
        // yet putting it away is exactly what an operator needs. A bin that refuses that is only a garbage list.
        var sessions = Sessions();
        Assert.True((await sessions.RunTurnAsync("s-live", Spec, Guid.NewGuid(), Input(Chat, "hi"), null, default)).Ok);
        var bin = Bin();
        var channels = await ChannelsServing(sessions, bin, Chat);
        Assert.True(bin.Register(new SessionArchiveSource(sessions, channels)).Ok);

        // Offered by nobody: the Channel exists and the Session is fresh.
        Assert.Empty((await bin.CandidatesAsync(ArchiveKinds.Session, default)).Value!);

        var taken = await bin.ArchiveAsync(ArchiveKinds.Session, ["s-live"], dryRun: false, default);
        Assert.True(taken.Ok, taken.Error?.Detail ?? taken.Error?.Message);
        Assert.True(Assert.Single(taken.Value!.Items).Done);
        Assert.False(Directory.Exists(SessionPath("s-live")));
        Assert.Equal("requested", Assert.Single((await bin.ArchivedAsync(ArchiveKinds.Session, default)).Value!).Reason);

        // An id the source does not own is refused by the source, and the outcome says so.
        var missing = await bin.ArchiveAsync(ArchiveKinds.Session, ["s-not-mine"], dryRun: false, default);
        var outcome = Assert.Single(missing.Value!.Items);
        Assert.False(outcome.Done);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message));
    }

    /// <summary>A Session directory as an older run would have left it: real files, a recorded Channel.</summary>
    private string WriteGhostSession(int index)
    {
        var directory = SessionPath($"s-ghost{index}");
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
    private static async Task<ChannelManager> ChannelsServing(ISessionManager sessions, IArchiveRegistry archive, Address channel)
    {
        var channels = new ChannelManager(new StubAdapters(), new EventQueue(8), new StubBindings(), sessions: sessions,
            archive: archive);
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
