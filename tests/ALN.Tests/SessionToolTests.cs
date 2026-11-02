using System.Runtime.CompilerServices;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The turn loop with tools: speaking is a tool, stopping is a tool, and a step that calls neither has said
/// nothing to anybody. These pin the three things that make that safe — text never sneaks out, a turn ends only
/// because a tool said so, and a tool round is on disk all at once or not at all.
/// </summary>
public sealed class SessionToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-tools-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly ModelToolDefinition SpeakShape = new("speak", "Say something.",
        """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");

    private static ModelToolCall Speak(string text) => new("call-1", "speak", $$"""{"text":{{System.Text.Json.JsonSerializer.Serialize(text)}}}""");
    private static ModelToolCall Wait() => new("call-2", "wait", "{}");

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

    /// <summary>A toolbox with one tool: whatever the test needs it to do.</summary>
    private sealed class Toolbox(params ISessionTool[] tools) : ISessionToolbox
    {
        public IReadOnlyList<string> Names { get; } = tools.Select(tool => tool.Name).ToArray();

        public Result<ISessionTool> Find(string name) => tools.FirstOrDefault(tool => tool.Name == name) is { } found
            ? Result<ISessionTool>.Success(found)
            : Result<ISessionTool>.Failure(new(ErrorCode.Invalid, $"No tool named {name}."));
    }

    private sealed class RecordingTool(string name, bool endsTurn, string? spoken) : ISessionTool
    {
        public List<ModelToolCall> Calls { get; } = [];
        public string Name => name;
        public bool EndsTurn => endsTurn;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            return ValueTask.FromResult(Result<SessionToolResult>.Success(new SessionToolResult("""{"ok":true}""", spoken)));
        }
    }

    private static SessionSpec Spec(params ModelToolDefinition[] tools) =>
        new("default", "Answer briefly.", "1", Tools: tools);

    private SessionManager Manager(IModelManager model) => new(Path.Combine(_root, "sessions"), model);

    [Fact]
    public async Task Text_that_was_never_spoken_does_not_reach_the_conversation_and_the_turn_is_nudged()
    {
        var model = new ScriptedModel(
            [new TextDelta("I should answer this."), new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1))],
            [new TextDelta("Still talking to nobody."), new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1))],
            [new TextDelta("Third time."), new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1))],
            [new TextDelta("Fourth time."), new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1))]);
        var speak = new RecordingTool("speak", endsTurn: false, spoken: null);
        var sessions = Manager(model);

        var turn = await sessions.RunTurnAsync("s-1", Spec(SpeakShape), Guid.NewGuid(),
            new SessionTurnInput([], null, null), new Toolbox(speak), default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        // The brake is a brake on talking, not on working: it stops after the given number of tool-less steps.
        Assert.Equal(SessionManager.TextOnlyNudgeLimit + 1, model.Requests.Count);
        var history = await File.ReadAllLinesAsync(Path.Combine(_root, "sessions", "s-1", "history.jsonl"));
        // Kept in the timeline as the assistant's own record …
        Assert.Contains(history, line => line.Contains("I should answer this.", StringComparison.Ordinal));
        // … but never as something the conversation saw.
        Assert.DoesNotContain(history, line => line.Contains("\"channelMessages\":[{", StringComparison.Ordinal));
        // And the nudge is request-only: it is in the request, never in the journal.
        Assert.Contains(model.Requests[1].Messages, message => message.Content?.Contains("speak", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(history, line => line.Contains("<framework>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_turn_sends_a_request_where_every_tool_result_answers_a_call_it_contains()
    {
        var model = new ScriptedModel(
            [new TextDelta("先想想怎么说。"), new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1))],
            [new ToolCallDelta(0, "call-1", "speak", """{"text":"在的"}"""),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))],
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var speak = new RecordingTool("speak", endsTurn: false, spoken: "在的");
        var sessions = Manager(model);
        var spec = Spec(SpeakShape);

        Assert.True((await sessions.RunTurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], null, null),
            new Toolbox(speak), default)).Ok);
        // A second turn is where the bug this pins showed up: the step that carried the tool call had been left out
        // of the request, so its result arrived answering nothing — a 400 from every OpenAI-compatible provider.
        Assert.True((await sessions.RunTurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], null, null),
            new Toolbox(speak), default)).Ok);

        var messages = model.Requests.Last().Messages;
        var called = messages.SelectMany(message => message.ToolCalls ?? []).Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        var answered = messages.Where(message => message.Role == "tool").Select(message => message.ToolCallId!).ToArray();
        Assert.NotEmpty(answered);
        Assert.All(answered, id => Assert.Contains(id, called));
        // And each answer comes after the step that made the call, with nothing but results in between.
        foreach (var (message, index) in messages.Select((message, index) => (message, index)))
        {
            if (message.Role != "tool") continue;
            var owner = messages.Take(index).Select((candidate, at) => (candidate, at))
                .Last(item => (item.candidate.ToolCalls ?? []).Any(call => call.Id == message.ToolCallId));
            Assert.All(messages.Skip(owner.at + 1).Take(index - owner.at - 1),
                inBetween => Assert.Equal("tool", inBetween.Role));
        }
        // The scratch step is not replayed, but the words the tool sent are still there — through the channel view.
        Assert.DoesNotContain(messages, message => message.Content?.Contains("先想想怎么说", StringComparison.Ordinal) == true);
        Assert.Contains(messages, message => message.Content?.Contains("在的", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_turn_ends_because_wait_says_so_and_a_silent_model_says_nothing()
    {
        var model = new ScriptedModel(
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var speak = new RecordingTool("speak", endsTurn: false, spoken: null);
        var sessions = Manager(model);

        var turn = await sessions.RunTurnAsync("s-1", Spec(SpeakShape), Guid.NewGuid(),
            new SessionTurnInput([], null, null), new Toolbox(speak), default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        Assert.Single(model.Requests);
        Assert.Equal("", turn.Value!.Text);
        Assert.Empty(speak.Calls);
    }

    [Fact]
    public async Task What_a_tool_spoke_becomes_the_assistant_own_words_in_the_timeline()
    {
        var model = new ScriptedModel(
            [new ToolCallDelta(0, "call-1", "speak", """{"text":"在的，说吧。"}"""),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))],
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var speak = new RecordingTool("speak", endsTurn: false, spoken: "在的，说吧。");
        var sessions = Manager(model);

        var turn = await sessions.RunTurnAsync("s-1", Spec(SpeakShape), Guid.NewGuid(),
            new SessionTurnInput([], null, null), new Toolbox(speak), default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        Assert.Single(speak.Calls);

        // The conversation's own view carries it: without this the model would lose its own words the moment it
        // stopped using "the turn's text" as its reply.
        var entries = (await File.ReadAllLinesAsync(Path.Combine(_root, "sessions", "s-1", "history.jsonl")))
            .Where(line => line.Length > 0)
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<SessionHistoryEntry>(line,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!)
            .ToArray();
        var spoken = entries.SelectMany(entry => entry.ChannelMessages ?? []).ToArray();
        Assert.Contains(spoken, message => message.Source == "assistant" && message.Content == "在的，说吧。");
    }

    [Fact]
    public async Task A_tool_round_is_one_commit_so_a_call_never_exists_without_its_result()
    {
        // Two calls in one round: two results and the step, all at once.
        var model = new ScriptedModel(
            [new ToolCallDelta(0, "call-1", "speak", """{"text":"一"}"""),
             new ToolCallDelta(1, "call-2", "speak", """{"text":"二"}"""),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))],
            [new ToolCallDelta(0, "call-3", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var speak = new RecordingTool("speak", endsTurn: false, spoken: "一");
        var sessions = Manager(model);

        var turn = await sessions.RunTurnAsync("s-1", Spec(SpeakShape), Guid.NewGuid(),
            new SessionTurnInput([], null, null), new Toolbox(speak), default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var lines = (await File.ReadAllLinesAsync(Path.Combine(_root, "sessions", "s-1", "history.jsonl")))
            .Where(line => line.Length > 0).ToArray();
        Assert.Contains(lines, line => line.Contains(":tool:1:call-1", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(":tool:1:call-2", StringComparison.Ordinal));

        // One revision per round, not one per entry — the commit is the round. (Plus the activation entry that
        // opened the turn, which is its own commit by design.)
        var state = System.Text.Json.JsonSerializer.Deserialize<SessionState>(
            await File.ReadAllTextAsync(Path.Combine(_root, "sessions", "s-1", "state.json")),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(2, state.StepNumber);
        Assert.Equal(state.StepNumber + 1, state.Revision);
    }

    [Fact]
    public async Task A_half_written_tool_round_from_an_older_build_is_finished_instead_of_jamming_the_session()
    {
        var model = new ScriptedModel(
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = Manager(model);
        var directory = Path.Combine(_root, "sessions", "s-1");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "spec.json"),
            """{"modelProfileId":"default","objective":"Answer briefly.","formatVersion":"1"}""");
        await File.WriteAllTextAsync(Path.Combine(directory, "state.json"),
            """{"status":1,"stepNumber":1,"revision":2,"locked":false,"usage":{"inputTokens":1,"outputTokens":1},"schemaVersion":2}""");
        // The shape an older build left behind: the call was written, its result never was.
        await File.WriteAllTextAsync(Path.Combine(directory, "history.jsonl"),
            """{"id":"a:user","activationId":"a","stepNumber":1,"role":"user","content":"hi","at":"2026-01-01T00:00:00+00:00"}""" + "\n" +
            """{"id":"a:assistant:1","activationId":"a","stepNumber":1,"role":"assistant","content":"","at":"2026-01-01T00:00:01+00:00","toolCalls":[{"id":"call-9","name":"speak","argumentsJson":"{\"text\":\"x\"}"}]}""" + "\n");

        var turn = await sessions.RunTurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([], null, null), new Toolbox(new RecordingTool("speak", false, null)), default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var history = await File.ReadAllTextAsync(Path.Combine(directory, "history.jsonl"));
        Assert.Contains("\"toolCallId\":\"call-9\"", history, StringComparison.Ordinal);
        Assert.Contains("interrupted", history, StringComparison.Ordinal);
    }
}
