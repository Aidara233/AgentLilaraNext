using System.Runtime.CompilerServices;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// The two-phase activation: the Channel hands out a signal, then — when the Session is assembling the
/// request — hands out the messages. These tests pin the parts that decide what the model sees: which
/// messages count as new, that the cursor does not hand them out twice, and that a turn with no channel
/// messages at all is a normal turn rather than a malformed one.
/// </summary>
public sealed class ChannelDeltaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-delta-" + Guid.NewGuid().ToString("N"));
    private static readonly Address Channel = new("channel", "conversation", "my-group");

    private readonly List<SqliteStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SqliteStore OpenStore()
    {
        var store = new SqliteStore(Path.Combine(_root, "aln.db"));
        _stores.Add(store);
        return store;
    }

    private (SqliteStore Store, FileChannelDeltaSource Delta) Create()
    {
        var store = OpenStore();
        return (store, new FileChannelDeltaSource(store, store));
    }

    private static StoredMessage Message(string id, string text) =>
        new(id, Channel, "group-123456", id, "alice", text, new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

    /// <summary>A whole hand-out as a turn that finished performs it: claim, then commit.</summary>
    private static async ValueTask<ChannelDelta> DeliverAsync(FileChannelDeltaSource delta, int? maxMessages = null)
    {
        var claim = await delta.ClaimDeltaAsync(Channel, maxMessages, default);
        Assert.True(claim.Ok, claim.Error?.Detail ?? claim.Error?.Message);
        var committed = await claim.Value!.Commit.CommitAsync(default);
        Assert.True(committed.Ok, committed.Error?.Detail ?? committed.Error?.Message);
        return claim.Value.Delta;
    }

    [Fact]
    public async Task A_delta_is_everything_since_the_last_hand_out_and_never_twice()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "first"), default)).Ok);
        Assert.True((await store.AppendAsync(Message("m2", "second"), default)).Ok);

        var first = await DeliverAsync(delta);
        Assert.Equal(["first", "second"], first.Messages.Select(m => m.Text));

        // Nothing new since: an empty delta is a normal answer, not a failure.
        Assert.True((await DeliverAsync(delta)).IsEmpty);

        // A message that arrives later belongs to the next hand-out only.
        Assert.True((await store.AppendAsync(Message("m3", "third"), default)).Ok);
        Assert.Equal(["third"], (await DeliverAsync(delta)).Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task Reading_alone_moves_nothing_so_a_turn_that_died_can_be_asked_again()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "说的一句话"), default)).Ok);

        // Two reads, no commit: this is what a request that fails before the turn is written down looks like.
        // Before the claim existed, the first read spent the message and the second came back empty — which is how
        // a person's message disappeared for good when a provider answered 422.
        Assert.Equal(["说的一句话"], (await delta.ClaimDeltaAsync(Channel, null, default)).Value!.Delta.Messages.Select(m => m.Text));
        var retried = await delta.ClaimDeltaAsync(Channel, null, default);
        Assert.Equal(["说的一句话"], retried.Value!.Delta.Messages.Select(m => m.Text));

        // Committing is what makes it delivered, and it is idempotent: a second commit changes nothing.
        Assert.True((await retried.Value.Commit.CommitAsync(default)).Ok);
        Assert.True((await retried.Value.Commit.CommitAsync(default)).Ok);
        Assert.True((await delta.ClaimDeltaAsync(Channel, null, default)).Value!.Delta.IsEmpty);
    }

    [Fact]
    public async Task A_commit_never_moves_the_cursor_backwards()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "one"), default)).Ok);
        var old = await delta.ClaimDeltaAsync(Channel, null, default);

        // A message arrives, a later turn reads both and commits, and only then does the late commit land.
        Assert.True((await store.AppendAsync(Message("m2", "two"), default)).Ok);
        var newer = await delta.ClaimDeltaAsync(Channel, null, default);
        Assert.Equal(["one", "two"], newer.Value!.Delta.Messages.Select(m => m.Text));
        Assert.True((await newer.Value.Commit.CommitAsync(default)).Ok);

        Assert.True((await old.Value!.Commit.CommitAsync(default)).Ok);
        // The newer commit survives: a stale one must not un-read a message somebody already saw.
        Assert.True((await delta.ClaimDeltaAsync(Channel, null, default)).Value!.Delta.IsEmpty);
    }

    [Fact]
    public async Task The_cursor_survives_a_restart_so_a_new_session_does_not_re_read_the_conversation()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "before restart"), default)).Ok);
        Assert.Single((await DeliverAsync(delta)).Messages);

        // A fresh instance on the same root is what a restart looks like.
        var restarted = new FileChannelDeltaSource(store, OpenStore());
        Assert.True((await DeliverAsync(restarted)).IsEmpty);
    }

    [Fact]
    public async Task A_corrupt_cursor_replays_instead_of_skipping()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "hello"), default)).Ok);
        Assert.Single((await DeliverAsync(delta)).Messages);

        // Replaying is visible and the Session timeline deduplicates by message id; skipping would silently
        // lose what people said, which is worse.
        var corrupt = await store.WriteAsync("channel.delta", Channel.ToString(), "{ not json", default);
        Assert.True(corrupt.Ok, corrupt.Error?.Message);
        Assert.Equal(["hello"], (await DeliverAsync(delta)).Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task The_contributor_turns_the_delta_into_timeline_entries()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "在吗"), default)).Ok);
        var contributors = new RoundContextContributorRegistry();
        Assert.True(contributors.Register(new ChannelDeltaContributor(delta, new StubChannels(maxMessages: null))).Ok);
        var model = new ScriptedModel();
        var sessions = new SessionManager(Path.Combine(_root, "sessions"), model, contributors);
        var binding = new ChannelBinding(new("adapter", "qq", "qq-1"), "group-123456");
        var spec = new SessionSpec("default", "Answer briefly.", "1");

        var turn = await sessions.TurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding),
            null, default);

        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        var request = Assert.Single(model.Requests);
        var user = request.Messages.FirstOrDefault(m => m.Content?.Contains("<channel_context>", StringComparison.Ordinal) == true);
        Assert.NotNull(user);
        // Same XML shape as before: the delta did not invent a new format, it just arrived later.
        Assert.Contains("<channel_messages shown=", user!.Content, StringComparison.Ordinal);
        Assert.Contains("sender=\"alice\"", user.Content, StringComparison.Ordinal);
        Assert.Contains("在吗", user.Content, StringComparison.Ordinal);

        // Second activation with nothing new still runs, and the block is not empty.
        var again = await sessions.TurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding),
            null, default);
        Assert.True(again.Ok, again.Error?.Detail ?? again.Error?.Message);
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task A_window_caps_what_one_turn_carries_and_reports_the_rest_as_omitted()
    {
        var (store, delta) = Create();
        for (var index = 1; index <= 5; index++)
            Assert.True((await store.AppendAsync(Message($"m{index}", $"msg {index}"), default)).Ok);

        var windowed = await DeliverAsync(delta, maxMessages: 2);
        // The newest two survive; the older three are gone from this turn but not from the conversation.
        Assert.Equal(["msg 4", "msg 5"], windowed.Messages.Select(m => m.Text));
        Assert.Equal(3, windowed.Omitted);

        // Elided messages are delivered, not deferred: otherwise the same head would be re-elided forever.
        var next = await DeliverAsync(delta, maxMessages: 2);
        Assert.True(next.IsEmpty);
        Assert.Equal(0, next.Omitted);
    }

    [Fact]
    public async Task A_long_conversation_is_carried_as_its_recent_tail_and_says_how_much_it_left_out()
    {
        var (store, delta) = Create();
        const int total = SessionManager.RecentChannelMessages + 5;
        for (var index = 1; index <= total; index++)
        {
            // Distinct timestamps: the timeline is ordered by time, and equal ones fall back to id order.
            var at = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero).AddMinutes(index);
            Assert.True((await store.AppendAsync(new StoredMessage($"m{index}", Channel, "group-123456", $"m{index}",
                "alice", index == 1 ? "the very first thing" : $"msg {index}", at), default)).Ok);
        }

        var contributors = new RoundContextContributorRegistry();
        Assert.True(contributors.Register(new ChannelDeltaContributor(delta, new StubChannels(maxMessages: null))).Ok);
        var model = new ScriptedModel();
        var sessions = new SessionManager(Path.Combine(_root, "sessions"), model, contributors);
        var spec = new SessionSpec("default", "Answer briefly.", "1");

        var turn = await sessions.TurnAsync("s-1", spec, Guid.NewGuid(),
            new SessionTurnInput([], Channel, new ChannelBinding(new("adapter", "qq", "qq-1"), "group-123456")), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var user = Assert.Single(model.Requests).Messages
            .First(m => m.Content?.Contains("<channel_messages", StringComparison.Ordinal) == true).Content!;
        // The block states its own size, so the model can tell a short conversation from a trimmed one.
        Assert.Contains($"shown=\"{SessionManager.RecentChannelMessages}\" total=\"{total}\"", user, StringComparison.Ordinal);
        Assert.Contains("<omitted count=\"5\">", user, StringComparison.Ordinal);
        Assert.Contains("msg 6", user, StringComparison.Ordinal);
        Assert.DoesNotContain("the very first thing", user, StringComparison.Ordinal);
        Assert.DoesNotContain("msg 5", user, StringComparison.Ordinal);

        // The trimmed messages are still in the session: the window is a rendering choice, not a deletion.
        Assert.Contains("the very first thing",
            await File.ReadAllTextAsync(Path.Combine(_root, "sessions", "s-1", "history.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_does_not_re_read_the_timeline_it_already_has()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "第一句"), default)).Ok);
        var contributors = new RoundContextContributorRegistry();
        Assert.True(contributors.Register(new ChannelDeltaContributor(delta, new StubChannels(maxMessages: null))).Ok);
        var model = new ScriptedModel();
        var sessions = new SessionManager(Path.Combine(_root, "sessions"), model, contributors);
        var spec = new SessionSpec("default", "Answer briefly.", "1");
        var binding = new ChannelBinding(new("adapter", "qq", "qq-1"), "group-123456");

        var first = await sessions.TurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding), null, default);
        Assert.True(first.Ok, first.Error?.Detail ?? first.Error?.Message);

        // A well-formed entry written behind the manager's back: a process that re-read the file on every commit
        // would pick it up. The Session keeps its context in memory, so this must stay invisible — and because
        // commits append rather than rewrite, it must still be in the file afterwards.
        var historyPath = Path.Combine(_root, "sessions", "s-1", "history.jsonl");
        await File.AppendAllTextAsync(historyPath,
            JsonSerializer.Serialize(new SessionHistoryEntry("ghost", "ghost", 9, "user", "从外部写进来的条目",
                new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero))) + "\n");

        var second = await sessions.TurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding), null, default);
        Assert.True(second.Ok, second.Error?.Detail ?? second.Error?.Message);
        var request = model.Requests.Last();
        Assert.DoesNotContain(request.Messages, message => message.Content?.Contains("从外部写进来的条目", StringComparison.Ordinal) == true);
        // Not vacuous: what the Session does remember is still carried, so this is a test about *where* it reads
        // from, not about a timeline that went missing.
        Assert.Contains(request.Messages, message => message.Content?.Contains("第一句", StringComparison.Ordinal) == true);
        Assert.Contains("ghost", await File.ReadAllTextAsync(historyPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_commit_that_died_halfway_is_finished_when_the_session_is_next_loaded()
    {
        var (_, delta) = Create();
        var contributors = new RoundContextContributorRegistry();
        Assert.True(contributors.Register(new ChannelDeltaContributor(delta, new StubChannels(maxMessages: null))).Ok);
        var model = new ScriptedModel();
        var root = Path.Combine(_root, "sessions");
        var spec = new SessionSpec("default", "Answer briefly.", "1");
        var binding = new ChannelBinding(new("adapter", "qq", "qq-1"), "group-123456");
        var manager = new SessionManager(root, model, contributors);
        var first = await manager.TurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding), null, default);
        Assert.True(first.Ok, first.Error?.Detail ?? first.Error?.Message);

        // What a crash between "breadcrumb written" and "entries appended" leaves behind: state is still at the old
        // revision, the entry is not in the file, and commit.pending.json says what was about to happen.
        var directory = Path.Combine(root, "s-1");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var state = JsonSerializer.Deserialize<SessionState>(await File.ReadAllTextAsync(Path.Combine(directory, "state.json")), options)!;
        var pending = new
        {
            state = state with { Revision = state.Revision + 1 },
            entries = new[]
            {
                new { id = "crash-1", activationId = "crash", stepNumber = 7, role = "user", content = "半途提交的条目",
                    at = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero) }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "commit.pending.json"), JsonSerializer.Serialize(pending, options));

        // A fresh manager is what a restart looks like: the next turn has to finish that commit before it runs.
        var restarted = new SessionManager(root, model, contributors);
        var next = await restarted.TurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding), null, default);
        Assert.True(next.Ok, next.Error?.Detail ?? next.Error?.Message);
        // The entry id is ASCII; the text itself is written escaped, as everything in this file is.
        Assert.Contains("crash-1", await File.ReadAllTextAsync(Path.Combine(directory, "history.jsonl")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "commit.pending.json")));
        var recovered = JsonSerializer.Deserialize<SessionState>(await File.ReadAllTextAsync(Path.Combine(directory, "state.json")), options)!;
        Assert.True(recovered.Revision >= state.Revision + 1);
    }

    [Fact]
    public async Task A_non_positive_window_is_rejected_rather_than_treated_as_unlimited()
    {
        var (_, delta) = Create();
        var zero = await delta.ClaimDeltaAsync(Channel, maxMessages: 0, default);
        Assert.False(zero.Ok);
        Assert.Equal(ErrorCode.Invalid, zero.Error!.Code);
    }

    [Fact]
    public async Task A_turn_that_failed_leaves_the_message_pending_for_the_next_one()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "周四开始冻结客户"), default)).Ok);
        var contributors = new RoundContextContributorRegistry();
        Assert.True(contributors.Register(new ChannelDeltaContributor(delta, new StubChannels(maxMessages: null))).Ok);
        var model = new ScriptedModel { Failures = 1 };
        var sessions = new SessionManager(Path.Combine(_root, "sessions"), model, contributors);
        var spec = new SessionSpec("default", "Answer briefly.", "1");
        var binding = new ChannelBinding(new("adapter", "qq", "qq-1"), "group-123456");

        // The provider fails on the first turn (the real run's 422). Before the claim existed, the cursor had
        // already moved past this message and the next turn never saw it again.
        var failed = await sessions.TurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding), null, default);
        Assert.False(failed.Ok);

        var second = await sessions.TurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding), null, default);
        Assert.True(second.Ok, second.Error?.Detail ?? second.Error?.Message);

        var user = model.Requests.Last().Messages
            .First(m => m.Content?.Contains("<channel_messages", StringComparison.Ordinal) == true).Content!;
        // Delivered again — and exactly once: the timeline collapses the copy by platform message id, so the model
        // reads the sentence one time even though two turns claimed it.
        Assert.Equal(1, CountOf(user, "周四开始冻结客户"));

        // Now that a turn finished, the cursor has moved and nothing is pending.
        Assert.True((await delta.ClaimDeltaAsync(Channel, null, default)).Value!.Delta.IsEmpty);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private sealed class StubChannels(int? maxMessages) : IChannelManager
    {
        public Result<ChannelInfo> Get(Address address) => Result<ChannelInfo>.Success(
            new ChannelInfo(address, "test", [new ChannelBinding(new("adapter", "qq", "qq-1"), "group-123456")], true, ChannelTypes.Conversation, maxMessages));

        public IReadOnlyList<ChannelInfo> List() => [];
        public bool IsBound(Address channel, Address adapter, string platformConversationId) => true;
        public Result<ChannelInfo> FindBound(Address adapter, string platformConversationId) =>
            Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "none"));
        public ValueTask<Result<ChannelInfo>> CreateAsync(ChannelSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result> StartAsync(Address address, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DestroyAsync(Address channel, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<int>> AcceptAsync(AdapterEnvelope envelope, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result> CompleteAcceptAsync(Address channel, Guid messageId, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> RecordReplyAsync(Address channel, Guid answeredMessageId, string platformConversationId, string text, string? platformMessageId, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<int>> ReplayPendingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result<int>.Success(0));
    }

    private sealed class ScriptedModel : IModelManager
    {
        public List<ModelRequest> Requests { get; } = [];

        /// <summary>How many of the next calls fail the way a provider does, before any text arrives.</summary>
        public int Failures { get; set; }

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Failures > 0) { Failures--; return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Failing())); }
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream()));
        }

        private static async IAsyncEnumerable<ModelEvent> Failing([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ModelFailed(new Error(ErrorCode.Unavailable, "OpenAI Chat returned HTTP 422.", null, false));
        }

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
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }
}
