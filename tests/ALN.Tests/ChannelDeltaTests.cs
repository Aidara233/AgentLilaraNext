using System.Runtime.CompilerServices;
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

    [Fact]
    public async Task A_delta_is_everything_since_the_last_hand_out_and_never_twice()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "first"), default)).Ok);
        Assert.True((await store.AppendAsync(Message("m2", "second"), default)).Ok);

        var first = await delta.TakeDeltaAsync(Channel, null, default);
        Assert.True(first.Ok, first.Error?.Detail ?? first.Error?.Message);
        Assert.Equal(["first", "second"], first.Value!.Messages.Select(m => m.Text));

        // Nothing new since: an empty delta is a normal answer, not a failure.
        var second = await delta.TakeDeltaAsync(Channel, null, default);
        Assert.True(second.Ok);
        Assert.True(second.Value!.IsEmpty);

        // A message that arrives later belongs to the next hand-out only.
        Assert.True((await store.AppendAsync(Message("m3", "third"), default)).Ok);
        var third = await delta.TakeDeltaAsync(Channel, null, default);
        Assert.Equal(["third"], third.Value!.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task The_cursor_survives_a_restart_so_a_new_session_does_not_re_read_the_conversation()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "before restart"), default)).Ok);
        Assert.Single((await delta.TakeDeltaAsync(Channel, null, default)).Value!.Messages);

        // A fresh instance on the same root is what a restart looks like.
        var restarted = new FileChannelDeltaSource(store, OpenStore());
        var afterRestart = await restarted.TakeDeltaAsync(Channel, null, default);
        Assert.True(afterRestart.Ok);
        Assert.True(afterRestart.Value!.IsEmpty);
    }

    [Fact]
    public async Task A_corrupt_cursor_replays_instead_of_skipping()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "hello"), default)).Ok);
        Assert.Single((await delta.TakeDeltaAsync(Channel, null, default)).Value!.Messages);

        // Replaying is visible and the Session timeline deduplicates by message id; skipping would silently
        // lose what people said, which is worse.
        var corrupt = await store.WriteAsync("channel.delta", Channel.Instance!, "{ not json", default);
        Assert.True(corrupt.Ok, corrupt.Error?.Message);
        var replayed = await delta.TakeDeltaAsync(Channel, null, default);
        Assert.True(replayed.Ok);
        Assert.Equal(["hello"], replayed.Value!.Messages.Select(m => m.Text));
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
        var spec = new SessionSpec("default", "Answer briefly.", "You are a test agent.", "1");

        var turn = await sessions.RunTurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding),
            null, default);

        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        var request = Assert.Single(model.Requests);
        var user = request.Messages.FirstOrDefault(m => m.Content?.Contains("<channel_context>", StringComparison.Ordinal) == true);
        Assert.NotNull(user);
        // Same XML shape as before: the delta did not invent a new format, it just arrived later.
        Assert.Contains("<channel_messages>", user!.Content, StringComparison.Ordinal);
        Assert.Contains("sender=\"alice\"", user.Content, StringComparison.Ordinal);
        Assert.Contains("在吗", user.Content, StringComparison.Ordinal);

        // Second activation with nothing new still runs, and the block is not empty.
        var again = await sessions.RunTurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding),
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

        var windowed = await delta.TakeDeltaAsync(Channel, maxMessages: 2, default);
        Assert.True(windowed.Ok, windowed.Error?.Message);
        // The newest two survive; the older three are gone from this turn but not from the conversation.
        Assert.Equal(["msg 4", "msg 5"], windowed.Value!.Messages.Select(m => m.Text));
        Assert.Equal(3, windowed.Value.Omitted);

        // Elided messages are consumed, not deferred: otherwise the same head would be re-elided forever.
        var next = await delta.TakeDeltaAsync(Channel, maxMessages: 2, default);
        Assert.True(next.Ok);
        Assert.True(next.Value!.IsEmpty);
        Assert.Equal(0, next.Value.Omitted);
    }

    [Fact]
    public async Task A_non_positive_window_is_rejected_rather_than_treated_as_unlimited()
    {
        var (_, delta) = Create();
        var zero = await delta.TakeDeltaAsync(Channel, maxMessages: 0, default);
        Assert.False(zero.Ok);
        Assert.Equal(ErrorCode.Invalid, zero.Error!.Code);
    }

    private sealed class StubChannels(int? maxMessages) : IChannelManager
    {
        public Result<ChannelInfo> Get(Address address) => Result<ChannelInfo>.Success(
            new ChannelInfo(address, "test", [new ChannelBinding(new("adapter", "qq", "qq-1"), "group-123456")], true, maxMessages));

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
        public ValueTask<Result> RecordReplyAsync(Address channel, Guid answeredMessageId, string platformConversationId, string text, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<int>> ReplayPendingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result<int>.Success(0));
    }

    private sealed class ScriptedModel : IModelManager
    {
        public List<ModelRequest> Requests { get; } = [];

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream()));
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
