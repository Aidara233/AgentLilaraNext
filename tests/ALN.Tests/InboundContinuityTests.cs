using System.Runtime.CompilerServices;
using ALN.Adapter.Qq;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Storage.Sqlite;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The two things that keep a message from being lost when nobody is looking: the QQ inbound journal (a
/// forward WebSocket never replays) and the model profile a session falls back to when its spec names none.
/// </summary>
public sealed class InboundContinuityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-inbound-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A fresh connection each time: two of them in one test is what "before and after a restart" is.</summary>
    private SqliteStore OpenStore()
    {
        var store = new SqliteStore(Path.Combine(_root, "aln.db"));
        _stores.Add(store);
        return store;
    }

    [Fact]
    public async Task A_journaled_message_can_be_read_back_and_forgetting_it_removes_it()
    {
        var journal = NewJournal(OpenStore(), out _);
        var envelope = Envelope("m-1", "朋友来了");

        Assert.True((await journal.RememberAsync(envelope, default)).Ok);
        Assert.True((await journal.RememberAsync(Envelope("m-2", "第二条"), default)).Ok);

        var read = await journal.ReadAsync(default);
        Assert.Equal(["m-1", "m-2"], read.Value!.Select(entry => entry.PlatformMessageId!).ToArray());

        Assert.True((await journal.ForgetAsync(envelope.EventId, default)).Ok);
        Assert.Equal(["m-2"], (await journal.ReadAsync(default)).Value!.Select(entry => entry.PlatformMessageId!).ToArray());
    }

    [Fact]
    public async Task The_journal_is_bounded_and_gives_up_on_the_oldest_first()
    {
        var journal = new InboundJournal(OpenStore(), new CollectingLogger(), Owner, capacity: 3);
        for (var index = 0; index < 5; index++)
            Assert.True((await journal.RememberAsync(Envelope($"m-{index}", "x"), default)).Ok);

        var read = await journal.ReadAsync(default);
        // The two that waited longest were dropped, and not silently: the logger got a warning.
        Assert.Equal(["m-2", "m-3", "m-4"], read.Value!.Select(entry => entry.PlatformMessageId!).ToArray());
    }

    [Fact]
    public async Task An_unreadable_journal_starts_over_instead_of_refusing_to_boot()
    {
        var store = OpenStore();
        var written = await store.EnqueueAsync(InboundJournal.QueueName, Owner, new QueuedMessage("bad-row", null, "group-1", null, "x", DateTimeOffset.UtcNow, "{ this is not json"), null, default);
        Assert.True(written.Ok, written.Error?.Message);
        var logger = new CollectingLogger();
        var journal = new InboundJournal(store, logger, Owner);

        var read = await journal.ReadAsync(default);
        Assert.True(read.Ok, read.Error?.Message);
        Assert.Empty(read.Value!);
        Assert.Contains(logger.Warnings, warning => warning.Contains("unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_state_store_the_journal_is_honest_about_being_ephemeral()
    {
        var journal = new InboundJournal(null, new CollectingLogger(), Owner);
        Assert.False(journal.IsDurable);
        Assert.True((await journal.RememberAsync(Envelope("m-1", "x"), default)).Ok);
        Assert.Empty((await journal.ReadAsync(default)).Value!);
    }

    [Fact]
    public async Task A_session_that_names_no_profile_uses_default_and_says_so()
    {
        var model = new ProfileAwareModel("alpha", "default", "beta");
        var sessions = new SessionManager(Path.Combine(_root, "sessions"), model);
        var turn = await sessions.RunTurnAsync("s-chat-1", new SessionSpec("", "Answer briefly.", "1"),
            Guid.NewGuid(), new SessionTurnInput([new("m-1", "user", "alice", "hello", DateTimeOffset.UtcNow)],
                new("channel", "conversation", "chat-1"), new(new("adapter", "file", "file-1"), "chat-1")), null, default);

        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        // "default" wins over the alphabetically first one, and the provider actually used it.
        Assert.Equal("default", Assert.Single(model.UsedProfiles));
    }

    [Fact]
    public async Task With_no_default_profile_the_first_by_id_is_chosen_deterministically()
    {
        var model = new ProfileAwareModel("beta", "alpha");
        var sessions = new SessionManager(Path.Combine(_root, "sessions"), model);
        var turn = await sessions.RunTurnAsync("s-chat-2", new SessionSpec("", "Answer briefly.", "1"),
            Guid.NewGuid(), new SessionTurnInput([new("m-1", "user", "alice", "hello", DateTimeOffset.UtcNow)],
                new("channel", "conversation", "chat-2"), new(new("adapter", "file", "file-1"), "chat-2")), null, default);

        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        Assert.Equal("alpha", Assert.Single(model.UsedProfiles));
    }

    [Fact]
    public async Task A_spec_with_no_profile_and_no_profiles_configured_fails_with_a_reason()
    {
        var sessions = new SessionManager(Path.Combine(_root, "sessions"), new ProfileAwareModel());
        var turn = await sessions.RunTurnAsync("s-chat-3", new SessionSpec("", "Answer briefly.", "1"),
            Guid.NewGuid(), new SessionTurnInput([new("m-1", "user", "alice", "hello", DateTimeOffset.UtcNow)],
                new("channel", "conversation", "chat-3"), new(new("adapter", "file", "file-1"), "chat-3")), null, default);

        Assert.False(turn.Ok);
        Assert.Contains("no profile", turn.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_qq_request_timeout_is_a_runtime_parameter()
    {
        var settings = new Dictionary<string, string> { ["endpoint"] = "ws://127.0.0.1:1" };
        var adapter = new QqAdapter(new(new("adapter", "qq", "1"), new("qq", "acc", AdapterDirection.Both), "adapter.qq", settings),
            new EventQueue(8), new CollectingLogger());

        Assert.Equal("10", adapter.ReadParameter(QqAdapter.TimeoutParameter));
        Assert.True((await adapter.ApplyParameterAsync(QqAdapter.TimeoutParameter, "30", default)).Ok);
        Assert.Equal("30", adapter.ReadParameter(QqAdapter.TimeoutParameter));
        // Out of range is refused and the old value stays.
        Assert.Equal(ErrorCode.Invalid, (await adapter.ApplyParameterAsync(QqAdapter.TimeoutParameter, "0", default)).Error!.Code);
        Assert.Equal("30", adapter.ReadParameter(QqAdapter.TimeoutParameter));
    }

    private static InboundJournal NewJournal(IQueuedMessageStore store, out CollectingLogger logger)
    {
        logger = new CollectingLogger();
        return new InboundJournal(store, logger, Owner);
    }

    private static AdapterEnvelope Envelope(string platformMessageId, string text) => new(
        new("adapter", "qq", "1"), "qq", "group-123456", platformMessageId, text, DateTimeOffset.UtcNow, "20000", false);

    private static readonly Address Owner = new("adapter", "qq", "1");

    private sealed class CollectingLogger : IModuleLogger
    {
        public List<string> Warnings { get; } = [];
        public void Info(string message, IReadOnlyDictionary<string, string>? detail = null) { }
        public void Warn(string message, IReadOnlyDictionary<string, string>? detail = null) => Warnings.Add(message);
        public void Error(string message, IReadOnlyDictionary<string, string>? detail = null) { }
    }

    private sealed class ProfileAwareModel(params string[] profiles) : IModelManager
    {
        public List<string> UsedProfiles { get; } = [];

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            UsedProfiles.Add(profileId);
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

        public IReadOnlyList<ModelProfile> ListProfiles() =>
            profiles.Select(id => new ModelProfile(id, new("model", "openai-chat", id), id)).ToArray();
    }
}
