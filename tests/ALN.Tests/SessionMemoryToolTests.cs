using System.Runtime.CompilerServices;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// Memory as the model uses it: <c>remember</c>, <c>recall</c>, <c>forget</c>.
///
/// The claims worth pinning are the ones that would quietly stop being true: memory belongs to the conversation
/// rather than to the Session that happened to write it, a conclusion cannot be filed without the words it was
/// drawn from, and the tools survive the one turn where nobody speaks — which is exactly the turn a Session runs
/// to think.
/// </summary>
public sealed class SessionMemoryToolTests : IDisposable
{
    private static readonly Address Channel = new("channel", "conversation", "chat-1");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-memtools-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;
    private readonly SessionManager _sessions;

    public SessionMemoryToolTests()
    {
        _store = new SqliteStore(Path.Combine(_root, "aln.db"));
        _sessions = new SessionManager(Path.Combine(_root, "sessions"), new ScriptedModel([]), Extensions(_store));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static void Turned(Result<SessionTurnResult> result) =>
        Assert.True(result.Ok, result.Error?.Detail ?? result.Error?.Message);

    private static SessionSpec Spec() => new("default", "记住该记的，其余别废话。", "1");

    private static ModelToolCall Call(string id, string name, object arguments) =>
        new(id, name, JsonSerializer.Serialize(arguments));

    private static ModelEvent[] Calling(ModelToolCall call) =>
        [new ToolCallDelta(0, call.Id, call.Name, call.ArgumentsJson),
         new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))];

    private static ModelEvent[] Waiting() =>
        [new ToolCallDelta(0, "wait-1", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))];

    /// <summary>A scripted model: one canned step per call, so the loop's decisions are the only variable.</summary>
    private sealed class ScriptedModel(params ModelEvent[][] steps) : IModelManager
    {
        public List<ModelRequest> Requests { get; } = [];

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var step = steps[Math.Min(Requests.Count - 1, steps.Length - 1)];
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream(step)));
        }

        private static async IAsyncEnumerable<ModelEvent> Stream(ModelEvent[] events,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var item in events) yield return item;
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

    /// <summary>One managed conversation: its own store, its own session directory.</summary>
    private SessionManager Manager(IModelManager model, IMemoryStore? memory) =>
        new(Path.Combine(_root, "sessions-" + Guid.NewGuid().ToString("N")[..8]), model,
            memory is null ? null : Extensions(memory));

    /// <summary>The memory module, as a host wires it: one module, two sources.</summary>
    private static SessionExtensionRegistry Extensions(IMemoryStore memory)
    {
        var extensions = new SessionExtensionRegistry();
        Assert.True(extensions.Register(new MemoryContextSource(memory)).Ok);
        Assert.True(extensions.Register(new MemoryToolSource(memory)).Ok);
        return extensions;
    }

    /// <summary>
    /// What one tool answered, as the last request carries it. Read from the last request rather than the first,
    /// because history is replayed: the same answer appears in every later request, and this asks "what did the
    /// model end up holding" once.
    /// </summary>
    private static List<string> ToolResults(ScriptedModel model, string tool) =>
        model.Requests.Count == 0
            ? []
            : model.Requests[^1].Messages
                .Where(message => message.Role == "tool" && message.Name == tool)
                .Select(message => message.Content ?? "")
                .ToList();

    [Fact]
    public async Task Memory_is_offered_only_when_the_host_has_somewhere_to_put_it()
    {
        // No store: the tools are simply not offered, and a model that calls one anyway is told by the turn rather
        // than by a silent no-op — a model that believes it remembered something is worse than one that knows it
        // could not.
        var without = new ScriptedModel(Calling(Call("c1", "remember", new { text = "在的", kind = "fragment" })), Waiting());
        var refused = await Manager(without, null).TurnAsync("s-1", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default);
        Assert.False(refused.Ok);
        Assert.Contains("not offered", refused.Error!.Message);
        var bare = without.Requests[0].Tools!;
        Assert.Contains(bare, tool => tool.Name == "wait");
        Assert.DoesNotContain(bare, tool => tool.Name == "remember");

        var with = new ScriptedModel(Calling(Call("c1", "remember", new { text = "在的", kind = "fragment" })), Waiting());
        Turned(await Manager(with, _store).TurnAsync("s-2", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default));
        var offered = with.Requests[0].Tools!;
        Assert.Contains(offered, tool => tool.Name == "remember");
        Assert.Contains(offered, tool => tool.Name == "recall");
        Assert.Contains(offered, tool => tool.Name == "forget");
        // Every offered tool has a schema the provider will accept as an object.
        foreach (var tool in offered)
            using (var schema = JsonDocument.Parse(tool.ParametersJson))
                Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
    }

    [Fact]
    public async Task A_later_session_serving_the_same_conversation_reads_what_was_written()
    {
        // Two different Session ids for one Channel: the point of scoping memory to the conversation is that the
        // second one — the Session that exists after the first was archived — must not start from nothing.
        var writer = new ScriptedModel(
            Calling(Call("c1", "remember", new { text = "部署改到周五了", kind = "note", evidence = "他说：那就周五吧，周四是客户冻结。" })),
            Waiting());
        Turned(await Manager(writer, _store).TurnAsync("s-first", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default));

        // A different Session id, and it still finds it: the scope is the conversation, so replacing the Session
        // (archive, restart, a new one opened for the same Channel) does not lose anything.
        var search = await _store.SearchAsync(Channel.ToString(), "部署", 20, DateTimeOffset.UtcNow, default);
        Assert.Single(search.Value!);
        Assert.Equal("部署改到周五了", search.Value![0].Record.Text);
        Assert.Equal("他说：那就周五吧，周四是客户冻结。", search.Value[0].Record.Evidence);
        Assert.Equal("s-first", search.Value[0].Record.Source);
    }

    [Fact]
    public async Task Recall_returns_the_evidence_and_the_counts_so_the_model_can_see_the_gap()
    {
        for (var i = 0; i < 12; i++)
            await _store.AddAsync(new MemoryDraft(Channel.ToString(), MemoryKind.Fragment, $"第{i}条碎片", At: DateTimeOffset.UtcNow), default);

        var model = new ScriptedModel(
            Calling(Call("c1", "recall", new { query = "碎片" })),
            Calling(Call("wait-1", "wait", new { })));
        var turn = await Manager(model, _store).TurnAsync("s-1", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var answer = ToolResults(model, "recall").Single();
        Assert.Contains("碎片", answer);
        Assert.Contains("碎片 12 条", answer);
        // Twelve fragments and no notes is the state that needs consolidating, and the model is the only one who
        // can do it — so the hint has to reach it here rather than live in a background job.
        Assert.Contains("整理成一两条 note", answer);
    }

    [Fact]
    public async Task A_note_without_evidence_is_refused_and_nothing_is_written()
    {
        var model = new ScriptedModel(
            Calling(Call("c1", "remember", new { text = "客户会冻结，所以改周五", kind = "note" })),
            Waiting());
        var turn = await Manager(model, _store).TurnAsync("s-1", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default);
        Assert.True(turn.Ok);

        Assert.Contains("evidence", ToolResults(model, "remember").Single());
        Assert.Equal(0, (await _store.CountEntriesAsync(Channel.ToString(), default)).Value!.Total);

        // A kind it does not know is refused rather than read as "fragment": filing a conclusion as chatter is the
        // one mistake this table exists to prevent.
        var typo = new ScriptedModel(Calling(Call("c1", "remember", new { text = "x", kind = "nöte" })), Waiting());
        Turned(await Manager(typo, _store).TurnAsync("s-2", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default));
        Assert.Contains("remember.kind must be", ToolResults(typo, "remember").Single());
    }

    [Fact]
    public async Task A_session_that_woke_itself_up_can_still_recall_with_no_toolbox_and_no_messages()
    {
        await _store.AddAsync(new MemoryDraft(Channel.ToString(), MemoryKind.Note, "周五下午部署",
            Evidence: "他说：周五下午吧。", At: DateTimeOffset.UtcNow), default);

        var model = new ScriptedModel(
            Calling(Call("c1", "recall", new { query = "部署" })),
            Calling(Call("wait-1", "wait", new { })));
        // No tools and no messages: the turn is not about anybody, it is the Session's own deadline coming due.
        var turn = await Manager(model, _store).TurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([], Channel, WokenAt: DateTimeOffset.UtcNow), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var answer = ToolResults(model, "recall").Single();
        Assert.Contains("周五下午部署", answer);
        Assert.Contains("证据：他说：周五下午吧。", answer);
    }

    [Fact]
    public async Task The_floating_line_says_what_is_stored_even_when_the_model_never_asks()
    {
        // The hole this closes: memory as a tool only exists for a model that thinks to call it, so a model that
        // never recalls cannot know there is anything to recall. One line per round makes the store visible.
        var model = new ScriptedModel(Waiting());
        var contributors = new SessionExtensionRegistry();
        Assert.True(contributors.Register(new MemoryContextSource(_store)).Ok);
        Assert.True(contributors.Register(new MemoryToolSource(_store)).Ok);
        var sessions = new SessionManager(Path.Combine(_root, "sessions-floating"), model, contributors);
        await _store.AddAsync(new MemoryDraft(Channel.ToString(), MemoryKind.Note, "周五下午部署",
            Evidence: "他说：周五下午吧。", At: DateTimeOffset.UtcNow), default);

        Turned(await sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default));

        var block = model.Requests[0].Messages.Single(message => message.Role == "user" &&
            message.Content?.Contains("<floating_context>", StringComparison.Ordinal) == true).Content!;
        Assert.Contains("name=\"memory\"", block);
        Assert.Contains("周五下午部署", block);
        Assert.Contains("recall", block);
        // The evidence is not repeated here: recall is where a conclusion is checked, and the floating line is a
        // reminder that there is something to check, not the whole store.
        Assert.DoesNotContain("他说：周五下午吧。", block);
    }

    [Fact]
    public async Task An_empty_store_contributes_no_line_at_all()
    {
        var model = new ScriptedModel(Waiting());
        var contributors = new SessionExtensionRegistry();
        Assert.True(contributors.Register(new MemoryContextSource(_store)).Ok);
        Assert.True(contributors.Register(new MemoryToolSource(_store)).Ok);
        var sessions = new SessionManager(Path.Combine(_root, "sessions-empty"), model, contributors);

        Turned(await sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default));

        // "You remember nothing" on every round is noise, and noise is what makes a real reminder invisible.
        var carriers = model.Requests[0].Messages.Where(message => message.Content?.Contains("<floating_context>", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(carriers, message => message.Content!.Contains("name=\"memory\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeating_a_fragment_tells_the_model_it_was_already_written()
    {
        var first = new ScriptedModel(Calling(Call("c1", "remember", new { text = "周五部署", kind = "fragment" })), Waiting());
        Turned(await Manager(first, _store).TurnAsync("s-1", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default));
        Assert.Contains("记下了", ToolResults(first, "remember").Single());

        var second = new ScriptedModel(
            Calling(Call("c1", "remember", new { text = "  周五部署 ", kind = "fragment" })),
            Waiting());
        Turned(await Manager(second, _store).TurnAsync("s-2", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default));

        var answer = ToolResults(second, "remember").Single();
        // The model is told it was a repeat: that is the only way "stop writing this every turn" can happen, and
        // the only way "he has said this three times" survives.
        Assert.Contains("已经记过了", answer);
        Assert.Contains("第 2 次", answer);
        Assert.Equal(1, (await _store.CountEntriesAsync(Channel.ToString(), default)).Value!.Total);
    }

    [Fact]
    public async Task Forget_drops_one_entry_and_says_what_is_left()
    {
        var written = (await _store.AddAsync(new MemoryDraft(Channel.ToString(), MemoryKind.Fragment, "要丢掉的",
            At: DateTimeOffset.UtcNow), default)).Value!.Record;

        var model = new ScriptedModel(
            Calling(Call("c1", "forget", new { id = written.Id })),
            Calling(Call("wait-1", "wait", new { })));
        var turn = await Manager(model, _store).TurnAsync("s-1", Spec(), Guid.NewGuid(), new SessionTurnInput([], Channel), null, default);
        Assert.True(turn.Ok);

        Assert.Contains(written.Id, ToolResults(model, "forget").Single());
        Assert.Equal(0, (await _store.CountEntriesAsync(Channel.ToString(), default)).Value!.Total);

        // Forgetting what is already gone is not an error: consolidation deletes in bulk and must not trip on a
        // fragment that an earlier pass already absorbed.
        Assert.True((await _store.ForgetAsync(Channel.ToString(), written.Id, default)).Ok);
    }
}
