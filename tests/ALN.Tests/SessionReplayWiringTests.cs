using System.Runtime.CompilerServices;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The replay rules, as a turn actually assembles its request.
///
/// <see cref="SessionReplayTests"/> pins what folding means; this pins that the loop uses it — the difference matters,
/// because the loop had been handing the whole timeline to the provider and nothing in the pure rules would notice.
/// </summary>
public sealed class SessionReplayWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-replay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const int ResultChars = 5_000;

    private static readonly ModelToolDefinition ReadShape = new("read", "Read one file.",
        """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");

    [Fact]
    public async Task A_long_session_sends_an_outline_of_old_work_and_the_full_text_of_the_newest()
    {
        const int turns = 20;
        var model = new ReplayModel(Turn());
        var read = new ReadingTool(new string('z', ResultChars));
        var sessions = new SessionManager(_root, model);
        var spec = new SessionSpec("default", "Keep reading.", "1", Tools: [ReadShape]);
        var toolbox = new ReplayToolbox(read);

        for (var turn = 0; turn < turns; turn++)
        {
            var ran = await sessions.TurnAsync("s-1", spec, Guid.NewGuid(),
                new SessionTurnInput([], null, null), toolbox, default);
            Assert.True(ran.Ok, ran.Error?.Detail ?? ran.Error?.Message);
        }

        var messages = model.Requests.Last().Messages;
        var answered = messages.Where(message => message.Role == "tool").Select(message => message.ToolCallId!).ToArray();
        var called = messages.SelectMany(message => message.ToolCalls ?? []).Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        // Same rule as at the unit level, now from the loop's own request: no result without its call.
        Assert.NotEmpty(answered);
        Assert.All(answered, id => Assert.Contains(id, called));

        // Fifteen of twenty turns are older than the window, and each of them left two results behind (the read and the
        // wait that ended the turn), so thirty of forty become outlines; the newest five turns are still whole.
        var folded = messages.Count(message => message.Content?.Contains("已折叠", StringComparison.Ordinal) == true);
        Assert.Equal((turns - SessionReplay.KeepRecentActivations) * 2, folded);
        var characters = messages.Sum(message => message.Content?.Length ?? 0);
        Assert.True(characters < turns * ResultChars / 3,
            $"{characters} characters for {turns} results of {ResultChars}: folding did not take");

        // The newest result is still readable in full: the model is in the middle of a turn, not reading history.
        Assert.Contains(messages, message => message.Content?.Length == ResultChars);

        // And the folding is a view: the timeline on disk still holds every result it ever got.
        var history = await File.ReadAllLinesAsync(Path.Combine(_root, "modules", "s-1", "history.jsonl"));
        Assert.Equal(turns, history.Count(line => line.Contains(new string('z', 64), StringComparison.Ordinal)));
        Assert.DoesNotContain(history, line => line.Contains("已折叠", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_call_that_would_overflow_the_window_is_refused_and_the_model_is_told_why()
    {
        // A turn is allowed to run as long as it needs; it is not allowed to run past the end of its own window. The
        // ceiling here is 85% of a declared 4k window (~14k characters), and one read of 30,000 characters crosses it.
        var call = 0;
        ModelEvent[][] script =
        [
            [new ToolCallDelta(0, $"call_a{call++}", "read", """{"path":"a.md"}"""), new ModelCompleted(StopReason.ToolUse, new ModelUsage(10, 10))],
            [new ToolCallDelta(0, $"call_b{call++}", "read", """{"path":"b.md"}"""), new ModelCompleted(StopReason.ToolUse, new ModelUsage(10, 10))],
            [new ToolCallDelta(0, $"call_w{call++}", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(10, 10))]
        ];
        var model = new ReplayModel(script, contextWindow: 4_000);
        var read = new ReadingTool(new string('q', 30_000));
        var sessions = new SessionManager(_root, model);
        var spec = new SessionSpec("default", "Read a lot.", "1", Tools: [ReadShape]);

        var ran = await sessions.TurnAsync("s-1", spec, Guid.NewGuid(),
            new SessionTurnInput([], null, null), new ReplayToolbox(read), default);

        Assert.True(ran.Ok, ran.Error?.Detail ?? ran.Error?.Message);
        // The first read happened, the second one did not: it would have arrived at the provider with no room to answer.
        Assert.Single(read.Calls);
        var refusal = Assert.Single(model.Requests.Last().Messages,
            message => message.Role == "tool" && message.Content?.Contains("上限", StringComparison.Ordinal) == true);
        // It says what happened and what to do, and it does not pretend the read happened.
        Assert.Contains("没有执行", refusal.Content, StringComparison.Ordinal);
        Assert.Contains("wait", refusal.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_history_whose_outline_no_longer_fits_gets_a_checkpoint_and_a_shorter_request()
    {
        // Folding cannot fix a timeline that is nothing but outline: at some point the outline itself is the request.
        // This is the case compaction exists for, and the window is small here so that it arrives in a few seconds.
        var model = new ReplayModel(Turn(), contextWindow: 1_000, summary: "## 已承诺\n无\n## 已做过\n看了一堆文件\n## 还悬着\n还没看完");
        var read = new ReadingTool("短文");
        var spec = new SessionSpec("default", "Keep reading.", "1", Tools: [ReadShape]);

        var first = await new SessionManager(_root, model).TurnAsync("s-1", spec, Guid.NewGuid(),
            new SessionTurnInput([], null, null), new ReplayToolbox(read), default);
        Assert.True(first.Ok, first.Error?.Detail ?? first.Error?.Message);

        // Sixty turns of history, written as the loop would have written them.
        var path = Path.Combine(_root, "modules", "s-1", "history.jsonl");
        var lines = new List<string>(await File.ReadAllLinesAsync(path));
        for (var turn = 0; turn < 60; turn++)
        {
            var activation = $"seeded-{turn:D2}";
            var call = new ModelToolCall($"seed_{turn}", "read", $$"""{"path":"memory/{{turn}}.md"}""");
            lines.Add(Json(new($"{activation}:assistant", activation, turn, "assistant", null, At, ToolCalls: [call])));
            lines.Add(Json(new($"{activation}:tool", activation, turn, "tool",
                $"第 {turn} 个文件的内容", At, ToolCallId: call.Id)));
        }
        await File.WriteAllLinesAsync(path, lines);

        var second = await new SessionManager(_root, model).TurnAsync("s-1", spec, Guid.NewGuid(),
            new SessionTurnInput([], null, null), new ReplayToolbox(read), default);
        Assert.True(second.Ok, second.Error?.Detail ?? second.Error?.Message);

        // A summary was asked for, and what it was shown is the old part of the timeline.
        var asked = Assert.Single(model.Summaries);
        Assert.Contains("【材料】", asked.Messages[1].Content, StringComparison.Ordinal);

        // The checkpoint is on disk as a record — the timeline is not edited, it is read from further along.
        var history = await File.ReadAllLinesAsync(path);
        var checkpoint = Deserialize(Assert.Single(history,
            line => line.Contains("\"role\":\"system\"", StringComparison.Ordinal)));
        Assert.Equal(SessionReplay.CheckpointRole, checkpoint.Role);
        Assert.False(string.IsNullOrWhiteSpace(checkpoint.CoversUpTo));
        Assert.Contains("还没看完", checkpoint.Content, StringComparison.Ordinal);

        // And the request that follows starts at the summary: the sixty seeded turns are no longer in it.
        var messages = model.Requests.Last().Messages;
        Assert.Contains(messages, message => message.Role == "system" && message.Content!.Contains("## 已承诺", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Content?.Contains("第 0 个文件的内容", StringComparison.Ordinal) == true);
        Assert.True(model.Requests.Last().Messages.Count < 10,
            $"the request still carries {model.Requests.Last().Messages.Count} messages");
    }

    [Fact]
    public async Task The_model_is_told_its_remaining_room_and_can_ask_to_fold_the_past_away()
    {
        // The scaffold's own advice is not enough: a model that cannot see its budget either ignores it until a call is
        // refused, or compacts out of nervousness on every turn. One number per turn, and a tool to act on it.
        var model = new ReplayModel(
            [
                [new ToolCallDelta(0, "call_c", "compact", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(10, 10))],
                [new ToolCallDelta(0, "call_w", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(10, 10))]
            ],
            contextWindow: 12_000,
            summary: "## 已承诺\n无\n## 已做过\n读了一堆文件\n## 还悬着\n还有没读完的");
        var spec = new SessionSpec("default", "Keep reading.", "1", Tools: [ReadShape]);
        var toolbox = new ReplayToolbox(new ReadingTool("短文"));

        Assert.True((await new SessionManager(_root, model).TurnAsync("s-1", spec, Guid.NewGuid(),
            new SessionTurnInput([], null, null), toolbox, default)).Ok);
        await SeedAsync(40);

        var ran = await new SessionManager(_root, model).TurnAsync("s-1", spec, Guid.NewGuid(),
            new SessionTurnInput([], null, null), toolbox, default);

        Assert.True(ran.Ok, ran.Error?.Detail ?? ran.Error?.Message);
        var messages = model.Requests.Last().Messages;
        // The room is stated, and stated as an estimate.
        Assert.Contains(messages, message => message.Content?.Contains("tokens（估算）", StringComparison.Ordinal) == true);
        // Asking for it worked, and the answer says what changed and what did not.
        Assert.Contains(messages, message => message.Content?.Contains("已压缩", StringComparison.Ordinal) == true);
        var history = await File.ReadAllLinesAsync(Path.Combine(_root, "modules", "s-1", "history.jsonl"));
        Assert.Single(history, line => line.Contains("\"role\":\"system\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_conversation_that_has_been_quiet_for_days_is_told_how_long()
    {
        // The model can read the clock but not the gap. "Quiet for three days" is the one moment compacting is free —
        // the prefix cache has expired anyway — and also the moment it is most likely to answer a stale question.
        var model = new ReplayModel(Turn(), contextWindow: 12_000);
        var spec = new SessionSpec("default", "Keep reading.", "1", Tools: [ReadShape]);
        var toolbox = new ReplayToolbox(new ReadingTool("短文"));
        Assert.True((await new SessionManager(_root, model).TurnAsync("s-1", spec, Guid.NewGuid(),
            new SessionTurnInput([], null, null), toolbox, default)).Ok);

        // Three days of silence, as a restart would find it.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var statePath = Path.Combine(_root, "modules", "s-1", "state.json");
        var state = System.Text.Json.JsonSerializer.Deserialize<SessionState>(await File.ReadAllTextAsync(statePath), options)!;
        await File.WriteAllTextAsync(statePath,
            System.Text.Json.JsonSerializer.Serialize(state with { LastActiveAt = DateTimeOffset.UtcNow.AddDays(-3) }, options));

        Assert.True((await new SessionManager(_root, model).TurnAsync("s-1", spec, Guid.NewGuid(),
            new SessionTurnInput([], null, null), toolbox, default)).Ok);

        var messages = model.Requests.Last().Messages;
        Assert.Contains(messages, message =>
            message.Content?.Contains("<section name=\"idle\"", StringComparison.Ordinal) == true);
        Assert.Contains(messages, message => message.Content?.Contains("3 天", StringComparison.Ordinal) == true);
    }

    /// <summary>Turns of history as the loop would have written them, so a segment exists to summarize.</summary>
    private async Task SeedAsync(int activations)
    {
        var path = Path.Combine(_root, "modules", "s-1", "history.jsonl");
        var lines = new List<string>(await File.ReadAllLinesAsync(path));
        for (var turn = 0; turn < activations; turn++)
        {
            var activation = $"seeded-{turn:D2}";
            var call = new ModelToolCall($"seed_{turn}", "read", $$"""{"path":"memory/{{turn}}.md"}""");
            lines.Add(Json(new($"{activation}:assistant", activation, turn, "assistant", null, At, ToolCalls: [call])));
            lines.Add(Json(new($"{activation}:tool", activation, turn, "tool", $"第 {turn} 个文件的内容", At, ToolCallId: call.Id)));
        }
        await File.WriteAllLinesAsync(path, lines);
    }

    private static readonly DateTimeOffset At = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static string Json(SessionHistoryEntry entry) =>
        System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private static SessionHistoryEntry Deserialize(string line) =>
        System.Text.Json.JsonSerializer.Deserialize<SessionHistoryEntry>(line,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    /// <summary>One turn's script: read a file, then stop.</summary>
    private static ModelEvent[][] Turn()
    {
        var call = 0;
        return
        [
            [new ToolCallDelta(0, $"call_r{call++}", "read", """{"path":"memory/x/block.md"}"""),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(10, 10))],
            [new ToolCallDelta(0, $"call_w{call++}", "wait", "{}"),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(10, 10))]
        ];
    }

    private sealed class ReadingTool(string result) : ISessionTool
    {
        public List<ModelToolCall> Calls { get; } = [];
        public string Name => "read";
        public bool EndsTurn => false;

        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            return ValueTask.FromResult(Result<SessionToolResult>.Success(new SessionToolResult(result, null)));
        }
    }

    private sealed class ReplayToolbox(params ISessionTool[] tools) : ISessionToolbox
    {
        public IReadOnlyList<string> Names { get; } = tools.Select(tool => tool.Name).ToArray();

        public Result<ISessionTool> Find(string name) => tools.FirstOrDefault(tool => tool.Name == name) is { } found
            ? Result<ISessionTool>.Success(found)
            : Result<ISessionTool>.Failure(new(ErrorCode.Invalid, $"No tool named {name}."));
    }

    /// <summary>A model that answers with the same steps forever and keeps every request it was given.</summary>
    private sealed class ReplayModel(ModelEvent[][] script, int? contextWindow = null, string? summary = null) : IModelManager
    {
        public List<ModelRequest> Requests { get; } = [];
        public List<ModelRequest> Summaries { get; } = [];

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            // A summarizing call is the one request with a system message and no tools. Answering it with the scripted
            // turn would make the test about the wrong thing.
            if (summary is not null && request.Tools is null &&
                request.Messages.Count > 0 && request.Messages[0].Role == "system")
            {
                Summaries.Add(request);
                return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream(
                    [new TextDelta(summary), new ModelCompleted(StopReason.EndTurn, new ModelUsage(10, 10))])));
            }
            Requests.Add(request);
            var step = script[(Requests.Count - 1) % script.Length];
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

        /// <summary>The window a Session plans against. Nothing declared means the floor, which is what most tests want.</summary>
        public IReadOnlyList<ModelProfile> ListProfiles() => contextWindow is { } declared
            ? [new ModelProfile("default", new Address("model", "stub", "one"), "stub", ContextWindow: declared)]
            : [];

        public Result RegisterProfile(ModelProfile profile) => Result.Success();
        public Result RemoveProfile(string profileId) => Result.Success();
    }
}
