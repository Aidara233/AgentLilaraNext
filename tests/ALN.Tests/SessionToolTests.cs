using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private class ScriptedModel(params ModelEvent[][] steps) : IModelManager
    {
        public List<ModelRequest> Requests { get; } = [];

        public virtual ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
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

        var turn = await sessions.TurnAsync("s-1", Spec(SpeakShape), Guid.NewGuid(),
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

        Assert.True((await sessions.TurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], null, null),
            new Toolbox(speak), default)).Ok);
        // A second turn is where the bug this pins showed up: the step that carried the tool call had been left out
        // of the request, so its result arrived answering nothing — a 400 from every OpenAI-compatible provider.
        Assert.True((await sessions.TurnAsync("s-1", spec, Guid.NewGuid(), new SessionTurnInput([], null, null),
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

        var turn = await sessions.TurnAsync("s-1", Spec(SpeakShape), Guid.NewGuid(),
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

        var turn = await sessions.TurnAsync("s-1", Spec(SpeakShape), Guid.NewGuid(),
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

        var turn = await sessions.TurnAsync("s-1", Spec(SpeakShape), Guid.NewGuid(),
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

        var turn = await sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([], null, null), new Toolbox(new RecordingTool("speak", false, null)), default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var history = await File.ReadAllTextAsync(Path.Combine(directory, "history.jsonl"));
        Assert.Contains("\"toolCallId\":\"call-9\"", history, StringComparison.Ordinal);
        Assert.Contains("interrupted", history, StringComparison.Ordinal);
    }

    // ---- what "wait" declares -------------------------------------------------------------------

    private async Task<System.Text.Json.JsonElement> StateAsync(string sessionId = "s-1")
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_root, "sessions", sessionId, "state.json")));
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Wait_with_a_deadline_records_when_to_wake_and_whether_to_release()
    {
        var model = new ScriptedModel(
            [new ToolCallDelta(0, "call-2", "wait", """{"timeout":3600,"release":true}"""),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = Manager(model);
        var before = DateTimeOffset.UtcNow;

        var turn = await sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([], null, null), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        // The declaration is state, not a timer: this is the only thing a sweep will ever read, so a restart
        // cannot lose the promise.
        var state = await StateAsync();
        Assert.True(state.GetProperty("waitRelease").GetBoolean());
        Assert.InRange(state.GetProperty("waitUntil").GetDateTimeOffset(),
            before.AddSeconds(3595), DateTimeOffset.UtcNow.AddSeconds(3605));
    }

    [Fact]
    public async Task Somebody_writing_cancels_a_pending_release()
    {
        var model = new ScriptedModel(
            [new ToolCallDelta(0, "call-2", "wait", """{"timeout":3600,"release":true}"""),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))],
            [new TextDelta("在的。"), new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1))]);
        var sessions = Manager(model);

        Assert.True((await sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([], null, null), null, default)).Ok);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, (await StateAsync()).GetProperty("waitUntil").ValueKind);

        // The declaration said "release me if nobody comes". A second activation is somebody coming.
        Assert.True((await sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([], null, null), null, default)).Ok);

        var state = await StateAsync();
        Assert.True(!state.TryGetProperty("waitUntil", out var cleared) ||
                    cleared.ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.False(state.GetProperty("waitRelease").GetBoolean());
    }

    [Theory]
    [InlineData("""{"release":true}""", "release needs a timeout")]
    [InlineData("""{"timeout":0}""", "between 1 and")]
    [InlineData("""{"timeout":60,"unit":"minutes"}""", "no argument named unit")]
    public async Task Wait_refuses_arguments_it_does_not_understand_and_the_turn_carries_on(string arguments, string expected)
    {
        var model = new ScriptedModel(
            [new ToolCallDelta(0, "call-2", "wait", arguments), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))],
            [new ToolCallDelta(0, "call-3", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = Manager(model);

        var turn = await sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([], null, null), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        // The refusal comes back as a tool result the model can read, and the turn does not end on it: a typo must
        // not be recorded as a successful stop, with the error nobody reads.
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(model.Requests[1].Messages, message => message.Role == "tool" &&
            message.Content?.Contains(expected, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_call_that_keeps_being_refused_stops_the_turn_instead_of_looping_forever()
    {
        // The scripted model repeats its last step, so this is a model that will never correct itself.
        var model = new ScriptedModel(
            [new ToolCallDelta(0, "call-2", "wait", """{"release":true}"""),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = Manager(model);

        var turn = await sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([], null, null), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        // Same brake as a model talking to nobody: what is counted is rounds without progress, not calls.
        Assert.Equal(SessionManager.TextOnlyNudgeLimit + 1, model.Requests.Count);
    }

    // ---- the deadline sweep ---------------------------------------------------------------------

    private sealed class RecordingDeadlineHandler(Func<SessionSummary, bool>? fail = null) : ISessionDeadlineHandler
    {
        public List<string> Released { get; } = [];
        public List<string> Woken { get; } = [];

        public ValueTask<Result> ReleaseAsync(SessionSummary session, CancellationToken cancellationToken)
        {
            if (fail?.Invoke(session) == true) return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "Refused.")));
            Released.Add(session.Id);
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> WakeAsync(SessionSummary session, DateTimeOffset dueAt, CancellationToken cancellationToken)
        {
            if (fail?.Invoke(session) == true) return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "Refused.")));
            Woken.Add(session.Id);
            return ValueTask.FromResult(Result.Success());
        }
    }

    /// <summary>A model that stays inside its call until the test lets it go, so "the Session is busy" is a fact.</summary>
    private sealed class HeldModel : ScriptedModel
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HeldModel() : base([]) { }

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public override ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId,
            ModelRequest request, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream(cancellationToken)));
        }

        private async IAsyncEnumerable<ModelEvent> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _release.Task.WaitAsync(cancellationToken);
            await Task.Yield();
            yield return new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1), null);
        }
    }

    private async Task<SessionManager> WaitingAsync(ScriptedModel model, string sessionId)
    {
        var sessions = new SessionManager(Path.Combine(_root, "sweep-" + Guid.NewGuid().ToString("N")), model);
        var turn = await sessions.TurnAsync(sessionId, Spec(), Guid.NewGuid(),
            new SessionTurnInput([], null, null), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        return sessions;
    }

    private static ScriptedModel WaitingModel(string waitArguments) => new(
        [new ToolCallDelta(0, "call-2", "wait", waitArguments),
         new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);

    [Fact]
    public async Task The_sweep_releases_a_session_whose_deadline_passed()
    {
        var sessions = await WaitingAsync(WaitingModel("""{"timeout":60,"release":true}"""), "s-1");
        var handler = new RecordingDeadlineHandler();

        var swept = await sessions.SweepAsync(DateTimeOffset.UtcNow.AddSeconds(61), handler, default);

        Assert.True(swept.Ok, swept.Error?.Message);
        Assert.Equal(1, swept.Value!.Due);
        Assert.Equal(1, swept.Value.Released);
        Assert.Equal(new[] { "s-1" }, handler.Released);
        Assert.Empty(handler.Woken);
    }

    [Fact]
    public async Task A_deadline_in_the_future_is_left_alone()
    {
        var sessions = await WaitingAsync(WaitingModel("""{"timeout":600,"release":true}"""), "s-1");
        var handler = new RecordingDeadlineHandler();

        var swept = await sessions.SweepAsync(DateTimeOffset.UtcNow, handler, default);

        Assert.True(swept.Ok, swept.Error?.Message);
        Assert.Equal(0, swept.Value!.Due);
        Assert.Empty(handler.Released);
    }

    [Fact]
    public async Task A_due_deadline_wakes_the_session_into_an_empty_room_and_says_so()
    {
        var model = new ScriptedModel([new TextDelta("ok"), new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1))]);
        var sessions = new SessionManager(Path.Combine(_root, "wake"), model);
        Assert.True((await sessions.OpenAsync("s-wake", Spec(), default)).Ok);

        var woken = await sessions.ActivateAsync("s-wake", ActivationReason.DeadlineDue,
            DateTimeOffset.UtcNow.AddMinutes(-1), default);

        Assert.True(woken.Ok, woken.Error?.Detail ?? woken.Error?.Message);
        // The model was told the truth: nobody spoke, and this is the Session's own deadline. Without that note it
        // invents a message to answer, and answers that.
        var note = Assert.Single(model.Requests[^1].Messages, message => message.Content?.Contains("没有任何人说话") == true);
        Assert.Equal("user", note.Role);
        Assert.Contains("等待到期", note.Content!, StringComparison.Ordinal);
        // On disk too: the turn after this one must still know why it happened.
        var lines = await File.ReadAllLinesAsync(Path.Combine(_root, "wake", "s-wake", "history.jsonl"));
        var firstTurn = JsonSerializer.Deserialize<SessionHistoryEntry>(lines[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains("没有任何人说话", firstTurn.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wake_up_that_arrives_while_the_session_is_busy_is_skipped_not_queued()
    {
        var model = new HeldModel();
        var sessions = new SessionManager(Path.Combine(_root, "wake-busy"), model);
        var opened = await sessions.OpenAsync("s-busy", Spec(), default);
        Assert.True(opened.Ok, opened.Error?.Message);

        // Start a turn and wait until it is inside the model call: it now holds the Session, gate and all.
        var running = opened.Value!.RunTurnAsync(Guid.NewGuid(), new SessionTurnInput([], null, null), null, default);
        await model.Entered;

        var woken = await sessions.ActivateAsync("s-busy", ActivationReason.DeadlineDue, DateTimeOffset.UtcNow, default);

        // Seven queued wake-ups become seven "you woke up" notes and nothing to say, so this one is dropped — and
        // the caller can tell the difference between "dropped because busy" and "failed".
        Assert.False(woken.Ok);
        Assert.Equal(ErrorCode.Conflict, woken.Error!.Code);
        Assert.True(woken.Error.Retryable);

        model.Release();
        Assert.True((await running).Ok);
    }

    [Fact]
    public async Task A_deadline_without_release_wakes_the_session_instead()
    {
        var sessions = await WaitingAsync(WaitingModel("""{"timeout":60}"""), "s-1");
        var handler = new RecordingDeadlineHandler();

        var swept = await sessions.SweepAsync(DateTimeOffset.UtcNow.AddSeconds(61), handler, default);

        Assert.Equal(1, swept.Value!.Woken);
        Assert.Empty(handler.Released);
        Assert.Equal(new[] { "s-1" }, handler.Woken);
    }

    [Fact]
    public async Task A_host_that_cannot_act_on_a_deadline_says_so_instead_of_dropping_it()
    {
        var sessions = await WaitingAsync(WaitingModel("""{"timeout":60,"release":true}"""), "s-1");

        var swept = await sessions.SweepAsync(DateTimeOffset.UtcNow.AddSeconds(61), handler: null, default);

        // A promise with no keeper must be visible, not quietly satisfied by doing nothing.
        Assert.Equal(1, swept.Value!.Due);
        Assert.Equal(1, swept.Value.Unhandled);
        Assert.Equal(0, swept.Value.Released);
    }

    [Fact]
    public async Task One_session_that_refuses_to_go_does_not_stop_the_others()
    {
        // Two Sessions in one root: a refusal on the first must not take the second down with it.
        var sessions = new SessionManager(Path.Combine(_root, "sweep-shared"), WaitingModel("""{"timeout":60,"release":true}"""));
        Assert.True((await sessions.TurnAsync("s-1", Spec(), Guid.NewGuid(),
            new SessionTurnInput([], null, null), null, default)).Ok);
        Assert.True((await sessions.TurnAsync("s-2", Spec(), Guid.NewGuid(),
            new SessionTurnInput([], null, null), null, default)).Ok);

        var refusesFirst = new RecordingDeadlineHandler(session => session.Id == "s-1");
        var swept = await sessions.SweepAsync(DateTimeOffset.UtcNow.AddSeconds(61), refusesFirst, default);

        Assert.Equal(2, swept.Value!.Due);
        Assert.Equal(1, swept.Value.Failed);
        Assert.Equal(1, swept.Value.Released);
        Assert.Equal(new[] { "s-2" }, refusesFirst.Released);
    }

    [Fact]
    public async Task The_sweep_drives_itself_on_an_interval_until_cancelled()
    {
        var sessions = await WaitingAsync(WaitingModel("""{"timeout":1,"release":true}"""), "s-1");
        var handler = new RecordingDeadlineHandler();
        using var stop = new CancellationTokenSource();

        var loop = sessions.RunDeadlineSweepAsync(handler, TimeProvider.System, TimeSpan.FromMilliseconds(50), stop.Token);
        var giveUp = DateTimeOffset.UtcNow.AddSeconds(10);
        while (handler.Released.Count == 0 && DateTimeOffset.UtcNow < giveUp) await Task.Delay(20);
        stop.Cancel();
        await loop;

        Assert.Equal(new[] { "s-1" }, handler.Released);
    }
}
