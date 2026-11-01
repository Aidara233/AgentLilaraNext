using System.Runtime.CompilerServices;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (FileMessageStore Store, FileChannelDeltaSource Delta) Create()
    {
        var store = new FileMessageStore(Path.Combine(_root, "messages"));
        return (store, new FileChannelDeltaSource(store, Path.Combine(_root, "messages")));
    }

    private static StoredMessage Message(string id, string text) =>
        new(id, Channel, "group-123456", id, "alice", text, new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_delta_is_everything_since_the_last_hand_out_and_never_twice()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "first"), default)).Ok);
        Assert.True((await store.AppendAsync(Message("m2", "second"), default)).Ok);

        var first = await delta.TakeDeltaAsync(Channel, default);
        Assert.True(first.Ok, first.Error?.Message);
        Assert.Equal(["first", "second"], first.Value!.Messages.Select(m => m.Text));

        // Nothing new since: an empty delta is a normal answer, not a failure.
        var second = await delta.TakeDeltaAsync(Channel, default);
        Assert.True(second.Ok);
        Assert.True(second.Value!.IsEmpty);

        // A message that arrives later belongs to the next hand-out only.
        Assert.True((await store.AppendAsync(Message("m3", "third"), default)).Ok);
        var third = await delta.TakeDeltaAsync(Channel, default);
        Assert.Equal(["third"], third.Value!.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task The_cursor_survives_a_restart_so_a_new_session_does_not_re_read_the_conversation()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "before restart"), default)).Ok);
        Assert.Equal(1, (await delta.TakeDeltaAsync(Channel, default)).Value!.Messages.Count);

        // A fresh instance on the same root is what a restart looks like.
        var restarted = new FileChannelDeltaSource(store, Path.Combine(_root, "messages"));
        var afterRestart = await restarted.TakeDeltaAsync(Channel, default);
        Assert.True(afterRestart.Ok);
        Assert.True(afterRestart.Value!.IsEmpty);
    }

    [Fact]
    public async Task A_corrupt_cursor_replays_instead_of_skipping()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "hello"), default)).Ok);
        Assert.Equal(1, (await delta.TakeDeltaAsync(Channel, default)).Value!.Messages.Count);

        // Replaying is visible and the Session timeline deduplicates by message id; skipping would silently
        // lose what people said, which is worse.
        await File.WriteAllTextAsync(Path.Combine(_root, "messages", "my-group", "cursor.json"), "{ not json");
        var replayed = await delta.TakeDeltaAsync(Channel, default);
        Assert.True(replayed.Ok);
        Assert.Equal(["hello"], replayed.Value!.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task The_contributor_turns_the_delta_into_timeline_entries()
    {
        var (store, delta) = Create();
        Assert.True((await store.AppendAsync(Message("m1", "在吗"), default)).Ok);
        var contributors = new RoundContextContributorRegistry();
        Assert.True(contributors.Register(new ChannelDeltaContributor(delta)).Ok);
        var model = new ScriptedModel();
        var sessions = new SessionManager(Path.Combine(_root, "sessions"), model, contributors);
        var binding = new ChannelBinding(new("adapter", "qq", "qq-1"), "group-123456");
        var spec = new SessionSpec("default", "Answer briefly.", "You are a test agent.", "1");

        var turn = await sessions.RunTurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], Channel, binding),
            null, default);

        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        var request = Assert.Single(model.Requests);
        var user = request.Messages.FirstOrDefault(m => m.Content.Contains("<channel_context>", StringComparison.Ordinal));
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
