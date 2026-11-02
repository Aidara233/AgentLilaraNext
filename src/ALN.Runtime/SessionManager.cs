using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Prompts;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>Runs one serialized model step per activation and persists completed turns.</summary>
public sealed class SessionManager : ISessionManager
{
    private const int StateSchemaVersion = 2;
    private const long MaxStoredHistoryBytes = 64 * 1024 * 1024;

    /// <summary>
    /// How many channel messages one request may carry. The channel timeline is re-rendered in full on every
    /// turn, so without a window the cost of a conversation grows with the square of its length — and every
    /// old message is re-stated, which invites the model to answer what was already answered.
    /// </summary>
    public const int RecentChannelMessages = 40;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly string _legacyRoot;
    private readonly IModelManager _models;
    private readonly ISignalLog _signalLog;
    private readonly IRoundContextContributorRegistry _roundContextContributors;
    private readonly IPromptLibrary? _prompts;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionRecord> _loaded = new(StringComparer.Ordinal);

    public SessionManager(string dataRoot, IModelManager models, IRoundContextContributorRegistry? roundContextContributors = null,
        ISignalLog? signalLog = null, IPromptLibrary? prompts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(models);
        _root = Path.GetFullPath(dataRoot);
        _legacyRoot = Path.Combine(_root, "chat");
        _models = models;
        _roundContextContributors = roundContextContributors ?? new RoundContextContributorRegistry();
        _signalLog = signalLog ?? NullSignalLog.Instance;
        _prompts = prompts;
    }

    public async ValueTask<Result<SessionTurnResult>> RunTurnAsync(
        string sessionId, SessionSpec spec, Guid activationId, SessionTurnInput input,
        ISessionToolbox? tools, CancellationToken cancellationToken)
    {
        var recordId = Guid.NewGuid();
        var parentId = SignalScope.CurrentId;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (spec is null) return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Session specification is incomplete."));
        // A spec that names no profile is not incomplete: it means "whatever the host has". Deciding it here
        // keeps the choice visible — the signal log, the stored session and the error all show the real id.
        if (string.IsNullOrWhiteSpace(spec.ModelProfileId))
        {
            var resolved = ResolveProfile(spec);
            if (!resolved.Ok) return Result<SessionTurnResult>.Failure(resolved.Error!);
            spec = resolved.Value!;
        }
        try
        {
            Result<SessionTurnResult> result;
            using (SignalScope.Enter(recordId, activationId))
                result = await RunTurnCoreAsync(sessionId, spec, activationId, input, tools, cancellationToken).ConfigureAwait(false);
            RecordTurn(sessionId, spec, activationId, result, started, recordId, parentId);
            return result;
        }
        catch (OperationCanceledException)
        {
            _signalLog.Write(new()
            {
                Id = recordId, ParentId = parentId,
                Group = "session", Name = sessionId, StepId = activationId,
                From = "session", To = $"model:{spec.ModelProfileId}", Mechanism = "session.turn",
                Outcome = "timeout", Level = SignalLevel.Warn,
                DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
            throw;
        }
    }

    /// <summary>
    /// Picks the profile a spec left open: "default" when it exists, otherwise the first by id. Deterministic
    /// on purpose — the same data root must resolve to the same provider every time, and the guess is said
    /// out loud in the signal log rather than buried.
    /// </summary>
    private Result<SessionSpec> ResolveProfile(SessionSpec spec)
    {
        var profiles = _models.ListProfiles();
        if (profiles.Count == 0)
            return Result<SessionSpec>.Failure(new(ErrorCode.Unavailable,
                "Session spec names no model profile and no profile is registered."));
        var chosen = profiles.FirstOrDefault(profile => string.Equals(profile.Id, "default", StringComparison.Ordinal))
                     ?? profiles.OrderBy(profile => profile.Id, StringComparer.Ordinal).First();
        if (profiles.Count > 1)
            _signalLog.Write(new()
            {
                Id = Guid.NewGuid(),
                Group = "session",
                Name = "profile",
                Mechanism = "session.resolve",
                Outcome = "guessed",
                Level = SignalLevel.Warn,
                Detail = new Dictionary<string, string>
                {
                    ["chosen"] = chosen.Id,
                    ["available"] = string.Join(",", profiles.Select(profile => profile.Id))
                }
            });
        return Result<SessionSpec>.Success(spec with { ModelProfileId = chosen.Id });
    }

    private void RecordTurn(string sessionId, SessionSpec spec, Guid activationId,
        Result<SessionTurnResult> result, long started, Guid recordId, Guid? parentId)
    {
        var level = result.Ok ? SignalLevel.Info : SignalLevel.Warn;
        _signalLog.Write(new()
        {
            Id = recordId,
            ParentId = parentId,
            Group = "session",
            Name = sessionId,
            StepId = activationId,
            From = "session",
            To = $"model:{spec.ModelProfileId}",
            Mechanism = "session.turn",
            Outcome = result.Ok ? "ok" : result.Error?.Code == ErrorCode.Conflict ? "denied" : "failed",
            DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Level = level,
            Detail = result.Ok
                ? new Dictionary<string, string> { ["step"] = result.Value!.StepNumber.ToString() }
                : new Dictionary<string, string> { ["message"] = result.Error?.Message ?? "" }
        });
    }

    /// <summary>
    /// How many consecutive tool-less steps a turn tolerates before giving up. Not a cap on work: a turn may run
    /// as many tool rounds as it needs. This only catches the other failure mode — a model that keeps talking to
    /// nobody — where looping forever would spend money every round with nothing to show for it.
    /// </summary>
    public const int TextOnlyNudgeLimit = 3;

    /// <summary>What the model is told after a step that reached no one. Request-only: never written to history.</summary>
    public const string ToolkitNudge =
        "<framework>这一步没有调用任何工具，所以对方什么也没收到。要说的话用 speak 发出去；" +
        "做完了、或者要停下来等下一次，就调用 wait。</framework>";

    /// <summary>
    /// The one tool every Session has: going quiet. Stopping is an action the model takes, not something the
    /// framework infers from a step that happened to end without a tool call — otherwise a model that forgot to
    /// act looks exactly like a model that is done.
    /// </summary>
    private sealed class WaitTool : ISessionTool
    {
        public const string ToolName = "wait";
        public static readonly ISessionTool Instance = new WaitTool();
        private static readonly ModelToolDefinition Shape = new(ToolName,
            "Yield this turn and stop until something wakes the conversation again. Call it when there is nothing "
            + "more to say or do — this is the only way a turn ends.", """{"type":"object","properties":{}}""");
        public static ModelToolDefinition Definition => Shape;
        public string Name => ToolName;
        public bool EndsTurn => true;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SessionToolResult>.Success(new SessionToolResult("""{"waited":true}""")));
    }

    /// <summary>
    /// What the model may call this turn: the built-in stop, plus every declared tool that something in the host
    /// can actually run.
    ///
    /// A declared tool with no implementation fails the turn. Offering a tool nobody can run only buys a wasted
    /// round, and quietly dropping it would run a Session with a different toolset than its spec names — the
    /// assembly mistake would then only ever show up as the model behaving oddly.
    /// </summary>
    private Result<List<ModelToolDefinition>> ResolveTools(SessionSpec spec, ISessionToolbox? toolbox)
    {
        var visible = new List<ModelToolDefinition> { WaitTool.Definition };
        var missing = new List<string>();
        foreach (var declared in spec.Tools ?? [])
        {
            if (string.Equals(declared.Name, WaitTool.ToolName, StringComparison.Ordinal)) continue;
            if (toolbox is not null && toolbox.Names.Contains(declared.Name, StringComparer.Ordinal))
            {
                visible.Add(declared);
                continue;
            }
            missing.Add(declared.Name);
        }
        if (missing.Count == 0) return Result<List<ModelToolDefinition>>.Success(visible);
        return Result<List<ModelToolDefinition>>.Failure(new(ErrorCode.Unavailable,
            $"Session declares tools nothing in this host provides: {string.Join(", ", missing)}. " +
            $"Available: {(toolbox is null || toolbox.Names.Count == 0 ? WaitTool.ToolName : string.Join(", ", toolbox.Names.Append(WaitTool.ToolName)))}."));
    }

    private void WriteSignal(Guid stepId, string outcome, SignalLevel level, params (string Key, string Value)[] detail)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in detail) values[key] = value;
        _signalLog.Write(new()
        {
            Id = Guid.NewGuid(),
            ParentId = SignalScope.CurrentId,
            Group = "session",
            Name = "tool",
            StepId = stepId,
            From = "session",
            To = "tools",
            Mechanism = "session.tools",
            Outcome = outcome,
            Level = level,
            Detail = values
        });
    }

    private async ValueTask<Result<SessionTurnResult>> RunTurnCoreAsync(
        string sessionId, SessionSpec spec, Guid activationId, SessionTurnInput input,
        ISessionToolbox? tools, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Session id must use 1-128 ASCII letters, digits, '-' or '_'."));
        if (spec is null || string.IsNullOrWhiteSpace(spec.Objective) ||
            string.IsNullOrWhiteSpace(spec.FormatVersion))
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Session specification is incomplete."));
        if (spec.Tools is { } declaredTools && (declaredTools.Any(t => string.IsNullOrWhiteSpace(t.Name) ||
            string.IsNullOrWhiteSpace(t.Description) || string.IsNullOrWhiteSpace(t.ParametersJson)) ||
            declaredTools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != declaredTools.Count))
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Session tool definitions are invalid or duplicated."));
        try
        {
            foreach (var tool in spec.Tools ?? [])
            {
                using var schema = JsonDocument.Parse(tool.ParametersJson);
                if (schema.RootElement.ValueKind != JsonValueKind.Object)
                    return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, $"Tool parameter schema must be an object: {tool.Name}"));
            }
        }
        catch (JsonException ex)
        { return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Session tool schema is invalid JSON.", ex.Message)); }
        if (activationId == Guid.Empty || input is null)
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Activation id is required."));
        // Content may legitimately be absent: activation is a signal, and what a turn carries is pulled by
        // contributors at assembly time (a Channel's delta, a timer's nothing at all). Requiring messages
        // here would make "wake up and look around" impossible.
        var channelMessages = input.Messages ?? [];
        if (channelMessages.Any(m => string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Source) || m.Content is null) ||
            channelMessages.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != channelMessages.Count)
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Channel messages must be unique and identifiable."));
        // A declared tool with no implementation is not fatal: it is dropped from what the model is offered and
        // recorded, so a script naming a tool this build lacks shows up as "that tool never existed here"
        // instead of a turn that dies for reasons the model cannot see.

        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = ResolveSessionDirectory(sessionId);
            var channel = input.Channel?.ToString();
            Directory.CreateDirectory(directory);
            // One call: it loads from disk on the first turn of a Session's life (replaying a half-finished commit
            // if there is one) and from memory after that. Everything below works on the record.
            var loaded = await LoadSessionAsync(sessionId, directory, spec, channel, cancellationToken).ConfigureAwait(false);
            if (!loaded.Ok || loaded.Value is null) return Result<SessionTurnResult>.Failure(loaded.Error!);
            var record = loaded.Value;
            if (record.State.Locked)
                return Result<SessionTurnResult>.Failure(new(ErrorCode.Denied, "Session is locked."));

            var history = record.History;
            var activation = activationId.ToString("N");
            var activationEntries = history.Where(e => e.ActivationId == activation).ToArray();
            var priorTurn = activationEntries.LastOrDefault(e => e.Role == "assistant" && e.StopReason != StopReason.ToolUse);
            if (priorTurn is not null)
                return Result<SessionTurnResult>.Success(new(sessionId, priorTurn.StepNumber, priorTurn.Content ?? "",
                    priorTurn.ToolCalls ?? [], priorTurn.StopReason ?? StopReason.EndTurn,
                    priorTurn.Usage ?? new ModelUsage(0, 0), DuplicateActivation: true));
            // A tool round is committed as one piece, so a call without a result can only come from an older build
            // that wrote each step separately. Repairing it here — an "interrupted" result, in one commit — is what
            // makes such a Session usable again instead of refusing every turn until somebody archives it.
            var repaired = await RepairDanglingToolCallsAsync(record, cancellationToken).ConfigureAwait(false);
            if (!repaired.Ok) return Result<SessionTurnResult>.Failure(repaired.Error!);
            var activationUser = activationEntries.FirstOrDefault(e => e.Role == "user");
            if (activationEntries.Length > 0 && activationUser is null)
                return Result<SessionTurnResult>.Failure(new(ErrorCode.Conflict, "Session activation is incomplete and cannot be resumed safely."));
            if (activationUser is not null && !SameChannelMessages(activationUser.ChannelMessages, input.Messages ?? []))
                return Result<SessionTurnResult>.Failure(new(ErrorCode.Conflict, "Activation id was reused with different channel messages."));

            var state = record.State;
            if (activationUser is null)
            {
                activationUser = new SessionHistoryEntry($"{activation}:user", activation, state.StepNumber + 1,
                    "user", null, DateTimeOffset.UtcNow, ChannelMessages: input.Messages);
                var timelineState = state with { Revision = state.Revision + 1 };
                var timelineCommit = await CommitAsync(record,
                    new SessionPendingCommit(timelineState, [activationUser]), cancellationToken).ConfigureAwait(false);
                if (!timelineCommit.Ok) return Result<SessionTurnResult>.Failure(timelineCommit.Error!);
                state = timelineState;
                history = record.History;
            }

            var messages = new List<ModelMessage>();
            // The prompt is read here, every turn, from files a person edits. Only the names are part of the
            // immutable spec, so editing a prompt changes the next request instead of breaking the session.
            var promptSet = await ResolvePromptsAsync(record.Spec, activationId, cancellationToken).ConfigureAwait(false);
            if (!promptSet.Ok || promptSet.Value is null) return Result<SessionTurnResult>.Failure(promptSet.Error!);
            if (!promptSet.Value.IsEmpty) messages.Add(new ModelMessage("system", promptSet.Value.Text));
            messages.Add(new ModelMessage("user", record.Spec.Objective));
            messages.AddRange(history.Where(entry => entry.ChannelMessages is null).Select(ToModelMessage));
            var collected = await CollectRoundContextAsync(
                new RoundContextRequest(sessionId, activationId, state.StepNumber + 1, input.Channel, input.Binding),
                cancellationToken).ConfigureAwait(false);
            if (!collected.Ok || collected.Value is null) return Result<SessionTurnResult>.Failure(collected.Error!);

            var persistedTimeline = await PersistTimelineContributionsAsync(record, activation, state,
                collected.Value.Timeline, cancellationToken).ConfigureAwait(false);
            if (!persistedTimeline.Ok || persistedTimeline.Value is null)
                return Result<SessionTurnResult>.Failure(persistedTimeline.Error!);
            state = persistedTimeline.Value.State;
            history = record.History;

            var channelTimeline = CollectChannelTimeline(history);
            var channelContext = RenderChannelContext(channelTimeline, collected.Value.RoundContext);
            if (channelContext.Length > 0) messages.Add(new ModelMessage("user", channelContext));
            var turnUsage = new ModelUsage(0, 0);
            var executedCalls = new List<ModelToolCall>();
            var finalText = "";
            var finalStopReason = StopReason.EndTurn;
            // What the model is offered this turn: the built-in stop, plus every declared tool something in the
            // host can actually run. Assembly mistakes fail here, before a single token is spent.
            var resolved = ResolveTools(record.Spec, tools);
            if (!resolved.Ok || resolved.Value is null) return Result<SessionTurnResult>.Failure(resolved.Error!);
            var visible = resolved.Value;
            var actingTools = visible.Count(definition => !string.Equals(definition.Name, WaitTool.ToolName, StringComparison.Ordinal));
            var nudges = 0;
            while (true)
            {
                var modelStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var modelRecordId = Guid.NewGuid();
                var modelParentId = SignalScope.CurrentId;
                using var modelScope = SignalScope.Enter(modelRecordId, activationId);
                var modelStarted = await _models.CompleteAsync(record.Spec.ModelProfileId,
                    new ModelRequest(messages, visible.Count > 0 ? visible : null), cancellationToken).ConfigureAwait(false);
                if (!modelStarted.Ok || modelStarted.Value is null)
                    return Result<SessionTurnResult>.Failure(modelStarted.Error ?? new(ErrorCode.Unavailable, "Model profile is unavailable."));

                var text = new StringBuilder();
                var toolCalls = new SortedDictionary<int, ToolCallAccumulator>();
                ModelUsage? usage = null;
                StopReason? stopReason = null;
                Error? modelError = null;
                var terminalCount = 0;
                await foreach (var item in modelStarted.Value.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    switch (item)
                    {
                        case TextDelta delta: text.Append(delta.Text); break;
                        case ThinkingDelta: break; // Reasoning is not exposed as user-visible text.
                        case ToolCallDelta delta:
                            if (!toolCalls.TryGetValue(delta.Index, out var accumulator))
                                toolCalls.Add(delta.Index, accumulator = new ToolCallAccumulator());
                            accumulator.Append(delta);
                            break;
                        case ModelCompleted completed:
                            terminalCount++;
                            usage = completed.Usage;
                            stopReason = completed.Reason;
                            break;
                        case ModelFailed failed:
                            terminalCount++;
                            modelError = failed.Error;
                            break;
                        default:
                            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Model provider emitted an unknown event."));
                    }
                    if (terminalCount > 1)
                        return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Model provider emitted multiple terminal events."));
                }
                if (terminalCount != 1)
                    return Result<SessionTurnResult>.Failure(new(ErrorCode.Unavailable, "Model provider ended without a terminal event."));
                _signalLog.Write(new()
                {
                    Id = modelRecordId,
                    ParentId = modelParentId,
                    Group = "model",
                    Name = record.Spec.ModelProfileId,
                    StepId = activationId,
                    From = "session",
                    To = $"model:{record.Spec.ModelProfileId}",
                    Mechanism = "model.request",
                    Outcome = modelError is null ? "ok" : "failed",
                    DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(modelStartedAt).TotalMilliseconds,
                    Level = modelError is null ? SignalLevel.Info : SignalLevel.Warn,
                    Detail = new Dictionary<string, string>
                    {
                        ["step"] = (state.StepNumber + 1).ToString(),
                        ["stop"] = stopReason?.ToString() ?? "",
                        ["promptTokens"] = (usage?.InputTokens ?? 0).ToString(),
                        ["completionTokens"] = (usage?.OutputTokens ?? 0).ToString()
                    }
                });
                if (modelError is not null) return Result<SessionTurnResult>.Failure(modelError);

                var completedUsage = usage ?? new ModelUsage(0, 0);
                turnUsage = AddUsage(turnUsage, completedUsage);
                var completedCalls = toolCalls.Values.Select(c => c.ToModelToolCall()).ToArray();
                if (completedCalls.Any(call => !visible.Any(definition => string.Equals(definition.Name, call.Name, StringComparison.Ordinal))))
                    return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Model returned a tool call that was not offered to the Session."));

                var stepNumber = state.StepNumber + 1;
                var now = DateTimeOffset.UtcNow;
                var isToolRound = completedCalls.Length > 0;
                var assistantStopReason = isToolRound ? StopReason.ToolUse : stopReason ?? StopReason.EndTurn;
                // A step with no tool call speaks to nobody: its text is reasoning, kept in the timeline as the
                // assistant's own record but never shown to the conversation as something it said. Only a tool
                // can reach the other side, which is the whole point of making speaking one.
                var spokeDirectly = !isToolRound && actingTools == 0;
                var assistantEntry = new SessionHistoryEntry($"{activation}:assistant:{stepNumber}", activation, stepNumber,
                    "assistant", text.ToString(), now, completedCalls, Usage: completedUsage, StopReason: assistantStopReason,
                    ChannelMessages: spokeDirectly
                        ? [new SessionChannelMessage($"{activation}:assistant:{stepNumber}", "assistant", "ALN", text.ToString(), now)]
                        : null);
                messages.Add(new ModelMessage("assistant", text.ToString(), isToolRound ? completedCalls : null));
                executedCalls.AddRange(completedCalls);

                if (!isToolRound)
                {
                    if (!spokeDirectly)
                    {
                        // Nobody has seen this text, and the turn is not over: it is nudged towards acting. The
                        // nudge is request-only — writing it down as if a person had said it would poison the
                        // timeline with instructions nobody gave.
                        var committed = state with
                        {
                            Status = SessionStatus.Dormant,
                            StepNumber = stepNumber,
                            Revision = state.Revision + 1,
                            Usage = state.Usage.Add(completedUsage)
                        };
                        var textOnly = await CommitAsync(record, new SessionPendingCommit(committed, [assistantEntry]), cancellationToken)
                            .ConfigureAwait(false);
                        if (!textOnly.Ok) return Result<SessionTurnResult>.Failure(textOnly.Error!);
                        state = committed;
                        nudges++;
                        if (nudges > TextOnlyNudgeLimit)
                        {
                            WriteSignal(activationId, "stalled", SignalLevel.Warn,
                                ("texts", nudges.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                            finalText = text.ToString();
                            finalStopReason = StopReason.EndTurn;
                            break;
                        }
                        messages.Add(new ModelMessage("user", ToolkitNudge));
                        continue;
                    }
                    var spoken = state with
                    {
                        Status = SessionStatus.Dormant,
                        StepNumber = stepNumber,
                        Revision = state.Revision + 1,
                        Usage = state.Usage.Add(completedUsage)
                    };
                    var direct = await CommitAsync(record, new SessionPendingCommit(spoken, [assistantEntry]), cancellationToken)
                        .ConfigureAwait(false);
                    if (!direct.Ok) return Result<SessionTurnResult>.Failure(direct.Error!);
                    state = spoken;
                    finalText = text.ToString();
                    finalStopReason = stopReason ?? StopReason.EndTurn;
                    break;
                }

                // A tool round is one commit: the step and every result it produced. Half a round — a call with no
                // result — is a state nothing can act on, so it must never exist on disk, not even between two
                // writes.
                var entries = new List<SessionHistoryEntry> { assistantEntry };
                var spokenTexts = new List<string>();
                var endsTurn = false;
                foreach (var call in completedCalls)
                {
                    // "wait" is the loop's own tool, so it resolves even when the host handed over nothing.
                    var found = tools?.Find(call.Name);
                    ISessionTool? tool = string.Equals(call.Name, WaitTool.ToolName, StringComparison.Ordinal)
                        ? WaitTool.Instance
                        : found is { Ok: true, Value: { } implementation } ? implementation : null;
                    Result<SessionToolResult> execution;
                    try
                    {
                        execution = tool is not null
                            ? await tool.ExecuteAsync(call, cancellationToken).ConfigureAwait(false)
                            : Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                                found?.Error?.Message ?? $"Tool is not available: {call.Name}"));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    { execution = Result<SessionToolResult>.Failure(new(ErrorCode.Internal, "Session tool execution failed.", ex.Message)); }

                    var outcome = execution.Ok && execution.Value is not null
                        ? execution.Value
                        : new SessionToolResult(JsonSerializer.Serialize(
                            new { error = execution.Error?.Message ?? "Tool returned no result." }, JsonOptions));
                    if (!string.IsNullOrWhiteSpace(outcome.SpokenText)) spokenTexts.Add(outcome.SpokenText!);
                    if (tool is not null && tool.EndsTurn) endsTurn = true;
                    messages.Add(new ModelMessage("tool", outcome.Content, ToolCallId: call.Id, Name: call.Name));
                    entries.Add(new SessionHistoryEntry($"{activation}:tool:{stepNumber}:{call.Id}", activation, stepNumber,
                        "tool", outcome.Content, DateTimeOffset.UtcNow, ToolCallId: call.Id));
                }
                if (spokenTexts.Count > 0)
                    assistantEntry = assistantEntry with
                    {
                        ChannelMessages = [new SessionChannelMessage($"{activation}:assistant:{stepNumber}", "assistant",
                            "ALN", string.Join("\n\n", spokenTexts), now)]
                    };
                entries[0] = assistantEntry;

                var roundState = state with
                {
                    Status = SessionStatus.Dormant,
                    StepNumber = stepNumber,
                    Revision = state.Revision + 1,
                    Usage = state.Usage.Add(completedUsage)
                };
                var round = await CommitAsync(record, new SessionPendingCommit(roundState, entries), cancellationToken)
                    .ConfigureAwait(false);
                if (!round.Ok) return Result<SessionTurnResult>.Failure(round.Error!);
                state = roundState;
                if (endsTurn)
                {
                    // The turn ends because a tool said so, not because a step happened to be silent.
                    finalText = "";
                    finalStopReason = StopReason.EndTurn;
                    break;
                }
            }

            // "Last spoken to" is written after the turn, never before: a turn that died halfway must not make a
            // forgotten Session look alive. A completed turn also clears the superseded note — a spec that
            // conflicts today may be reverted tomorrow, and the evidence of "it works again" is this turn.
            // Metadata only — the revision stays where the committed turn left it, so a pending commit written
            // earlier still recovers against exactly this file.
            state = state with { Channel = channel ?? state.Channel, LastActiveAt = DateTimeOffset.UtcNow, SupersededAt = null };
            record.State = state;
            await WriteAtomicJsonAsync(Path.Combine(directory, "state.json"), state, cancellationToken).ConfigureAwait(false);

            return Result<SessionTurnResult>.Success(new(sessionId, state.StepNumber, finalText, executedCalls,
                finalStopReason, turnUsage));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return Result<SessionTurnResult>.Failure(new(ErrorCode.Unavailable, "Session turn failed.", ex.Message)); }
        finally { gate.Release(); }
    }

    public Result<SessionState> GetState(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
            return Result<SessionState>.Failure(new(ErrorCode.Invalid, "Session id is invalid."));
        string statePath;
        try { statePath = Path.Combine(ResolveSessionDirectory(sessionId), "state.json"); }
        catch (IOException ex) { return Result<SessionState>.Failure(new(ErrorCode.Conflict, "Session data exists in both old and new locations.", ex.Message)); }
        if (!File.Exists(statePath))
            return Result<SessionState>.Failure(new(ErrorCode.Invalid, $"Session not found: {sessionId}"));
        try
        {
            var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllBytes(statePath), JsonOptions);
            return state is null
                ? Result<SessionState>.Failure(new(ErrorCode.Invalid, "Session state is invalid."))
                : Result<SessionState>.Success(state);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return Result<SessionState>.Failure(new(ErrorCode.Unavailable, "Session state could not be read.", ex.Message)); }
    }

    public async ValueTask<Result<int>> DeleteChannelAsync(string channel, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var removed = 0;
        var failed = new List<string>();
        foreach (var (id, directory) in EnumerateSessions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(TryReadState(directory)?.Channel, channel, StringComparison.Ordinal)) continue;
            // The gate is re-checked inside the deletion: a turn that is running gets to finish (or fail) before
            // its files vanish, and the state read above is repeated after the wait so it cannot be stale.
            if (!string.Equals(TryReadState(directory)?.Channel, channel, StringComparison.Ordinal)) continue;
            var deleted = await DeleteSessionAsync(id, directory, cancellationToken).ConfigureAwait(false);
            if (deleted.Ok) removed++;
            else failed.Add($"{id}: {deleted.Error?.Message}");
        }
        return failed.Count == 0
            ? Result<int>.Success(removed)
            : Result<int>.Failure(new(ErrorCode.Unavailable,
                $"{removed} Session(s) of {channel} were removed, {failed.Count} could not be.", string.Join("; ", failed)));
    }

    public ValueTask<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sessions = new List<SessionSummary>();
            foreach (var (id, directory) in EnumerateSessions())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = TryReadState(directory);
                // A Session from before the field existed gets its newest file as an answer. Null would read as
                // "idle since forever", and that is a reason to give something away on the strength of a guess.
                var lastActive = state?.LastActiveAt ?? LastWritten(directory);
                sessions.Add(new(id, state?.Channel, lastActive, state?.StepNumber ?? 0, DirectoryBytes(directory),
                    state?.SupersededAt));
            }
            return ValueTask.FromResult(Result<IReadOnlyList<SessionSummary>>.Success(sessions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return ValueTask.FromResult(Result<IReadOnlyList<SessionSummary>>.Failure(new(ErrorCode.Unavailable, "Sessions could not be listed.", ex.Message))); }
    }

    public async ValueTask<Result> ArchiveAsync(string sessionId, string destinationDirectory, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return Result.Failure(new(ErrorCode.Invalid, "Session id is invalid."));
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            return Result.Failure(new(ErrorCode.Invalid, "An archive destination is required."));
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Resolve once more inside the gate: a turn may have finished while this call was waiting, and the
            // directory it is about to move has to be the one on disk right now.
            var source = Path.Combine(_root, sessionId);
            if (!Directory.Exists(source))
            {
                if (!Directory.Exists(Path.Combine(_legacyRoot, sessionId)))
                    return Result.Failure(new(ErrorCode.Invalid, $"Session not found: {sessionId}"));
                source = ResolveSessionDirectory(sessionId);
            }
            if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
                return Result.Failure(new(ErrorCode.Conflict, $"Archive destination already exists: {destinationDirectory}"));
            Directory.Move(source, destinationDirectory);
            _loaded.TryRemove(sessionId, out _);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result.Failure(new(ErrorCode.Unavailable, "Session could not be archived.", ex.Message)); }
        finally { gate.Release(); }
    }

    public async ValueTask<Result> RestoreAsync(string sessionId, string sourceDirectory, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return Result.Failure(new(ErrorCode.Invalid, "Session id is invalid."));
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            return Result.Failure(new(ErrorCode.Invalid, "An archived session path is required."));
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(sourceDirectory))
                return Result.Failure(new(ErrorCode.Invalid, $"Archived session data is missing: {sourceDirectory}"));
            var target = Path.Combine(_root, sessionId);
            // Two conversations must never become one: if the id has been rebuilt since, the restore is refused
            // and somebody decides what the older history is worth.
            if (Directory.Exists(target) || Directory.Exists(Path.Combine(_legacyRoot, sessionId)))
                return Result.Failure(new(ErrorCode.Conflict,
                    $"A session with this id exists again; restoring would merge two histories: {sessionId}"));
            Directory.CreateDirectory(_root);
            Directory.Move(sourceDirectory, target);
            // Whatever this process remembered about that id is now wrong: the directory it pointed at is gone and
            // the one that just appeared was written by some other run.
            _loaded.TryRemove(sessionId, out _);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result.Failure(new(ErrorCode.Unavailable, "Session could not be restored.", ex.Message)); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The directories this manager wrote: one per Session, current location first, the legacy one second.
    /// Nothing walks them at startup — only deletion and maintenance do, so an instance with a thousand
    /// forgotten Sessions still starts in the time it takes to read one row.
    /// </summary>
    private IReadOnlyList<(string Id, string Directory)> EnumerateSessions()
    {
        // Materialised, not lazy: callers delete the very directories this walk is sitting on, and a live
        // enumeration handle over a folder being emptied is a platform-specific accident waiting to happen.
        var sessions = new List<(string, string)>();
        foreach (var root in new[] { _root, _legacyRoot })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                // Only names this manager could have written: a stray folder in the data root is not ours.
                var id = Path.GetFileName(directory);
                if (IsValidSessionId(id)) sessions.Add((id, directory));
            }
        }
        return sessions;
    }

    /// <summary>Deletes one Session directory with its gate held, so a turn in flight is not pulled apart.</summary>
    private async ValueTask<Result> DeleteSessionAsync(string id, string directory, CancellationToken cancellationToken)
    {
        var gate = _sessionGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            _loaded.TryRemove(id, out _);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result.Failure(new(ErrorCode.Unavailable, $"Session {id} could not be deleted.", ex.Message)); }
        finally { gate.Release(); }
    }

    private static long DirectoryBytes(string directory)
    {
        try { return Directory.EnumerateFiles(directory).Sum(file => new FileInfo(file).Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Fallback activity for a Session that never recorded one: the newest file write. Conservative on
    /// purpose — "now" when the directory is empty or unreadable, so a guess can never make something look idle.
    /// </summary>
    private static DateTimeOffset LastWritten(string directory)
    {
        try
        {
            var newest = Directory.EnumerateFiles(directory).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.UtcNow).Max();
            return new DateTimeOffset(newest, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return DateTimeOffset.UtcNow; }
    }

    /// <summary>
    /// Reads state.json, or null when it is missing or unreadable. Callers use it to recognise their own
    /// sessions by attribute — an unreadable file proves nothing, so it is treated as "not mine" rather than
    /// as a reason to delete something.
    /// </summary>
    private static SessionState? TryReadState(string directory)
    {
        var path = Path.Combine(directory, "state.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<SessionState>(File.ReadAllBytes(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The Session's context: from memory when this process already has it, from disk otherwise.
    ///
    /// A cached record still goes through the spec check, because "you asked for a different persona" has to be
    /// refused the same way whether the timeline came from memory or from the file.
    /// </summary>
    private async ValueTask<Result<SessionRecord>> LoadSessionAsync(string sessionId, string directory,
        SessionSpec requestedSpec, string? channel, CancellationToken cancellationToken)
    {
        if (_loaded.TryGetValue(sessionId, out var cached))
        {
            if (SameSpec(cached.Spec, requestedSpec)) return Result<SessionRecord>.Success(cached);
            await MarkSupersededAsync(cached, cancellationToken).ConfigureAwait(false);
            return Result<SessionRecord>.Failure(new(ErrorCode.Conflict,
                "Session specification is immutable; create a new session id to change it."));
        }

        var loaded = await LoadOrCreateAsync(directory, requestedSpec, channel, cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok || loaded.Value is null) return Result<SessionRecord>.Failure(loaded.Error!);

        var historyResult = await ReadHistoryAsync(Path.Combine(directory, "history.jsonl"), repairTrailingLine: true,
            cancellationToken).ConfigureAwait(false);
        if (!historyResult.Ok || historyResult.Value is null) return Result<SessionRecord>.Failure(historyResult.Error!);

        var recovered = await RecoverPendingAsync(directory, loaded.Value.State, historyResult.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!recovered.Ok || recovered.Value is null) return Result<SessionRecord>.Failure(recovered.Error!);

        var record = new SessionRecord(loaded.Value.Spec, directory) { State = recovered.Value.State };
        record.Adopt(recovered.Value.History);
        _loaded[sessionId] = record;
        return Result<SessionRecord>.Success(record);
    }

    /// <summary>
    /// Notes that a stored Session can never serve the caller that just asked. Written where maintenance can see
    /// it: the specs are immutable, so without the note a dead Session looks exactly like a live one.
    /// </summary>
    private async ValueTask MarkSupersededAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        if (record.State.SupersededAt is not null) return;
        var stale = record.State with { SupersededAt = DateTimeOffset.UtcNow };
        await WriteAtomicJsonAsync(Path.Combine(record.Directory, "state.json"), stale, cancellationToken).ConfigureAwait(false);
        record.State = stale;
    }

    private async ValueTask<Result<LoadedSession>> LoadOrCreateAsync(string directory, SessionSpec requestedSpec,
        string? channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var specPath = Path.Combine(directory, "spec.json");
        var statePath = Path.Combine(directory, "state.json");
        var historyPath = Path.Combine(directory, "history.jsonl");
        SessionSpec persistedSpec;
        if (File.Exists(specPath))
        {
            SessionSpec? readSpec;
            try
            {
                readSpec = JsonSerializer.Deserialize<SessionSpec>(await File.ReadAllBytesAsync(specPath, cancellationToken).ConfigureAwait(false), JsonOptions);
            }
            catch (JsonException ex)
            {
                // A stored spec from an older build is not "some error": this session can never run again, and
                // saying so (with the way out) is the only useful answer. Silence or a generic failure would leave
                // somebody watching a chat that quietly stops answering.
                return Result<LoadedSession>.Failure(new(ErrorCode.Invalid,
                    $"Stored session spec cannot be read by this build: {ex.Message} " +
                    $"Move the session out of the way (archive.move kind=session) or delete sessions/{Path.GetFileName(directory)}; " +
                    "the next message will start a fresh one.",
                    ex.Message));
            }
            if (readSpec is null) return Result<LoadedSession>.Failure(new(ErrorCode.Invalid, "Session specification is invalid."));
            if (!SameSpec(readSpec, requestedSpec))
            {
                // The refusal is written down where maintenance can see it: the stored spec is immutable, so this
                // directory can never serve the caller that just asked. Without the note a dead Session looks
                // exactly like a live one, and the operator's only clue is a Conflict repeated forever.
                if (TryReadState(directory) is { SupersededAt: null } stale)
                    await WriteAtomicJsonAsync(statePath, stale with { SupersededAt = DateTimeOffset.UtcNow }, cancellationToken)
                        .ConfigureAwait(false);
                return Result<LoadedSession>.Failure(new(ErrorCode.Conflict, "Session specification is immutable; create a new session id to change it."));
            }
            persistedSpec = readSpec;
        }
        else
        {
            persistedSpec = requestedSpec;
            await WriteAtomicJsonAsync(specPath, persistedSpec, cancellationToken).ConfigureAwait(false);
        }

        SessionState state;
        if (File.Exists(statePath))
        {
            var readState = JsonSerializer.Deserialize<SessionState>(await File.ReadAllBytesAsync(statePath, cancellationToken).ConfigureAwait(false), JsonOptions);
            if (readState is null)
                return Result<LoadedSession>.Failure(new(ErrorCode.Invalid, "Session state version is unsupported."));
            if (readState.SchemaVersion == 1)
            {
                state = readState with { Revision = readState.StepNumber, SchemaVersion = StateSchemaVersion };
                await WriteAtomicJsonAsync(statePath, state, cancellationToken).ConfigureAwait(false);
            }
            else if (readState.SchemaVersion == StateSchemaVersion) state = readState;
            else return Result<LoadedSession>.Failure(new(ErrorCode.Invalid, "Session state version is unsupported."));
        }
        else
        {
            state = new(SessionStatus.Dormant, 0, 0, false, new SessionUsage(0, 0), StateSchemaVersion, channel, DateTimeOffset.UtcNow);
            await WriteAtomicJsonAsync(statePath, state, cancellationToken).ConfigureAwait(false);
        }
        if (!File.Exists(historyPath)) await File.WriteAllTextAsync(historyPath, "", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return Result<LoadedSession>.Success(new(persistedSpec, state));
    }

    /// <summary>
    /// Replays a commit that the process died in the middle of, against the timeline as it was loaded.
    ///
    /// This is the only place that still reconciles with the file, and it only runs when the breadcrumb exists:
    /// the breadcrumb is written before anything else in a commit, so its presence is the proof that "the last
    /// commit may be half-done". Everything after the first successful load relies on the in-memory record.
    /// </summary>
    private async ValueTask<Result<LoadedRecovery>> RecoverPendingAsync(string directory, SessionState state,
        IReadOnlyList<SessionHistoryEntry> history, CancellationToken cancellationToken)
    {
        var pendingPath = Path.Combine(directory, "commit.pending.json");
        if (!File.Exists(pendingPath)) return Result<LoadedRecovery>.Success(new(state, history));
        var pending = JsonSerializer.Deserialize<SessionPendingCommit>(
            await File.ReadAllBytesAsync(pendingPath, cancellationToken).ConfigureAwait(false), JsonOptions);
        if (pending is null) return Result<LoadedRecovery>.Failure(new(ErrorCode.Invalid, "Pending session commit is invalid."));
        if (pending.State.Revision < state.Revision)
        {
            File.Delete(pendingPath);
            return Result<LoadedRecovery>.Success(new(state, history));
        }
        if (pending.State.Revision > state.Revision + 1)
            return Result<LoadedRecovery>.Failure(new(ErrorCode.Invalid, "Pending session commit is not the next revision."));
        var known = history.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var missing = pending.Entries.Where(entry => known.Add(entry.Id)).ToArray();
        if (missing.Length > 0)
            await AppendEntriesAsync(Path.Combine(directory, "history.jsonl"), missing, cancellationToken).ConfigureAwait(false);
        var next = state;
        if (state.Revision < pending.State.Revision)
        {
            next = pending.State;
            await WriteAtomicJsonAsync(Path.Combine(directory, "state.json"), next, cancellationToken).ConfigureAwait(false);
        }
        File.Delete(pendingPath);
        return Result<LoadedRecovery>.Success(new(next, missing.Length == 0 ? history : [.. history, .. missing]));
    }

    /// <summary>
    /// Writes one commit: the breadcrumb, the new timeline entries, the state — in that order, so a crash at any
    /// point leaves something <see cref="RecoverPendingAsync"/> can finish.
    ///
    /// The ids to append are decided from memory. That is safe because this manager is the only writer of these
    /// directories and it loads a Session before writing to it: what it remembers *is* the file.
    /// </summary>
    private async ValueTask<Result> CommitAsync(SessionRecord record, SessionPendingCommit pending, CancellationToken cancellationToken)
    {
        var directory = record.Directory;
        var pendingPath = Path.Combine(directory, "commit.pending.json");
        await WriteAtomicJsonAsync(pendingPath, pending, cancellationToken).ConfigureAwait(false);
        var missing = pending.Entries.Where(entry => !record.Knows(entry.Id)).ToArray();
        await AppendEntriesAsync(Path.Combine(directory, "history.jsonl"), missing, cancellationToken).ConfigureAwait(false);
        await WriteAtomicJsonAsync(Path.Combine(directory, "state.json"), pending.State, cancellationToken).ConfigureAwait(false);
        File.Delete(pendingPath);
        // Memory changes last: everything above can be undone by replaying the breadcrumb, and a commit that
        // threw must not leave the record claiming entries the file does not have.
        record.Adopt(missing);
        record.State = pending.State;
        return Result.Success();
    }

    private static async ValueTask<Result<IReadOnlyList<SessionHistoryEntry>>> ReadHistoryAsync(
        string path, bool repairTrailingLine, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return Result<IReadOnlyList<SessionHistoryEntry>>.Success([]);
        var info = new FileInfo(path);
        if (info.Length > MaxStoredHistoryBytes)
            return Result<IReadOnlyList<SessionHistoryEntry>>.Failure(new(ErrorCode.Invalid, "Session history exceeds its storage limit."));
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var entries = new List<SessionHistoryEntry>(lines.Length);
        var truncatedTail = false;
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            try
            {
                var item = JsonSerializer.Deserialize<SessionHistoryEntry>(lines[index], JsonOptions);
                if (item is null) throw new JsonException("History entry is null.");
                entries.Add(item);
            }
            catch (JsonException ex)
            {
                if (index != lines.Length - 1 || !repairTrailingLine)
                    return Result<IReadOnlyList<SessionHistoryEntry>>.Failure(new(ErrorCode.Invalid, "Session history contains an invalid record.", ex.Message));
                truncatedTail = true;
                break;
            }
        }
        if (truncatedTail)
        {
            var bytes = entries.Count == 0 ? [] : Encoding.UTF8.GetBytes(string.Join("\n", entries.Select(e => JsonSerializer.Serialize(e, JsonOptions))) + "\n");
            await WriteAtomicBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        }
        return Result<IReadOnlyList<SessionHistoryEntry>>.Success(entries);
    }

    private static async ValueTask AppendEntriesAsync(string historyPath, IReadOnlyList<SessionHistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return;
        await using var stream = new FileStream(historyPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async ValueTask WriteAtomicJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await WriteAtomicBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteAtomicBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Session file path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Turns the spec's selection into text. A missing prompt fails this turn with the list of what exists:
    /// answering as somebody else because a file was renamed is the one outcome nobody can debug later.
    /// </summary>
    private async ValueTask<Result<PromptSet>> ResolvePromptsAsync(SessionSpec spec, Guid activationId,
        CancellationToken cancellationToken)
    {
        var groups = spec.Prompts is { Count: > 0 } chosen
            ? chosen.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : [];
        var selection = new PromptSelection(groups);
        if (selection.IsEmpty)
        {
            // Legal, and said out loud: "no persona at all" is a fact somebody may need to notice.
            _signalLog.Write(new()
            {
                Group = "prompt", Name = "resolve", StepId = activationId, From = "session", To = "prompt",
                Mechanism = "prompt.resolve", Outcome = "empty", Level = SignalLevel.Info,
                Detail = new Dictionary<string, string> { ["message"] = "no prompt group selected" }
            });
            return Result<PromptSet>.Success(new PromptSet(string.Empty, [], string.Empty));
        }

        if (_prompts is null)
            return Result<PromptSet>.Failure(new(ErrorCode.Unavailable,
                "The session names prompt groups but no prompt library is available."));

        var resolved = await _prompts.ResolveAsync(selection, cancellationToken).ConfigureAwait(false);
        _signalLog.Write(new()
        {
            Group = "prompt", Name = "resolve", StepId = activationId, From = "session", To = "prompt",
            Mechanism = "prompt.resolve", Outcome = resolved.Ok ? "ok" : "failed",
            Level = resolved.Ok ? SignalLevel.Info : SignalLevel.Warn,
            Detail = resolved.Ok && resolved.Value is not null
                ? new Dictionary<string, string>
                {
                    ["fingerprint"] = resolved.Value.Fingerprint,
                    ["parts"] = string.Join(",", resolved.Value.Parts.Select(part => $"{part.Group}/{part.Name}"))
                }
                : new Dictionary<string, string> { ["message"] = resolved.Error?.Message ?? "prompt could not be resolved" }
        });
        return resolved;
    }

    private static bool SamePrompts(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        var leftPairs = left ?? new Dictionary<string, string>();
        var rightPairs = right ?? new Dictionary<string, string>();
        if (leftPairs.Count != rightPairs.Count) return false;
        foreach (var (group, name) in leftPairs)
            if (!rightPairs.TryGetValue(group, out var other) || !string.Equals(name, other, StringComparison.Ordinal))
                return false;
        return true;
    }

    private static bool SameSpec(SessionSpec left, SessionSpec right) =>
        StringComparer.Ordinal.Equals(left.ModelProfileId, right.ModelProfileId) &&
        StringComparer.Ordinal.Equals(left.Objective, right.Objective) &&
        StringComparer.Ordinal.Equals(left.FormatVersion, right.FormatVersion) &&
        SamePrompts(left.Prompts, right.Prompts) &&
        (left.Tools ?? []).SequenceEqual(right.Tools ?? []);

    /// <summary>
    /// Finishes a tool round that an older build left half-written: every call that never got a result gets one
    /// saying it was interrupted, in a single commit, so the timeline stops being something nothing can act on.
    /// </summary>
    private async ValueTask<Result> RepairDanglingToolCallsAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        var dangling = FindUnresolvedToolCalls(record.History);
        if (dangling.Count == 0) return Result.Success();
        var interrupted = JsonSerializer.Serialize(new { error = "interrupted" }, JsonOptions);
        var entries = dangling.Select(item => new SessionHistoryEntry(
            $"{item.ActivationId}:tool:{item.StepNumber}:{item.Call.Id}", item.ActivationId, item.StepNumber, "tool",
            interrupted, DateTimeOffset.UtcNow, ToolCallId: item.Call.Id)).ToArray();
        var next = record.State with { Revision = record.State.Revision + 1 };
        return await CommitAsync(record, new SessionPendingCommit(next, entries), cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<(string ActivationId, int StepNumber, ModelToolCall Call)> FindUnresolvedToolCalls(        IReadOnlyList<SessionHistoryEntry> history)
    {
        var results = history.Where(e => e.Role == "tool" && e.ToolCallId is not null)
            .Select(e => $"{e.ActivationId}:{e.StepNumber}:{e.ToolCallId}").ToHashSet(StringComparer.Ordinal);
        return history.Where(e => e.Role == "assistant")
            .SelectMany(e => (e.ToolCalls ?? []).Where(call => !results.Contains($"{e.ActivationId}:{e.StepNumber}:{call.Id}"))
                .Select(call => (e.ActivationId, e.StepNumber, call)))
            .ToArray();
    }

    private static ModelUsage AddUsage(ModelUsage left, ModelUsage right) => new(
        left.InputTokens + right.InputTokens,
        left.OutputTokens + right.OutputTokens,
        (left.CacheReadTokens ?? 0) + (right.CacheReadTokens ?? 0),
        (left.CacheWriteTokens ?? 0) + (right.CacheWriteTokens ?? 0));

    private async ValueTask<Result<PersistedTimeline>> PersistTimelineContributionsAsync(SessionRecord record, string activation,
        SessionState state, IReadOnlyList<CollectedTimelineMessage> contributions,
        CancellationToken cancellationToken)
    {
        var byKey = CollectChannelTimeline(record.History)
            .ToDictionary(message => TimelineKey(message.Source, message.Id), StringComparer.Ordinal);
        var additions = new List<SessionHistoryEntry>();
        foreach (var item in contributions)
        {
            var message = new SessionChannelMessage(item.Id, item.Source, item.Sender, item.Content, item.At);
            var key = TimelineKey(message.Source, message.Id);
            if (byKey.TryGetValue(key, out var prior))
            {
                if (prior != message)
                    return Result<PersistedTimeline>.Failure(new(ErrorCode.Conflict,
                        $"Timeline message id was reused with different content: {item.Source}/{item.Id}"));
                continue;
            }
            byKey.Add(key, message);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            additions.Add(new SessionHistoryEntry($"{activation}:timeline:{digest}", activation, state.StepNumber + 1,
                "user", null, item.At, ChannelMessages: [message]));
        }
        if (additions.Count == 0)
            return Result<PersistedTimeline>.Success(new(state, record.History));

        var nextState = state with { Revision = state.Revision + 1 };
        var committed = await CommitAsync(record, new SessionPendingCommit(nextState, additions), cancellationToken)
            .ConfigureAwait(false);
        if (!committed.Ok) return Result<PersistedTimeline>.Failure(committed.Error!);
        return Result<PersistedTimeline>.Success(new(nextState, record.History));
    }

    private static string TimelineKey(string source, string id) => $"{source}\n{id}";

    private static IReadOnlyList<SessionChannelMessage> CollectChannelTimeline(IReadOnlyList<SessionHistoryEntry> history)
    {
        var messages = new List<SessionChannelMessage>();
        var byKey = new Dictionary<string, SessionChannelMessage>(StringComparer.Ordinal);
        foreach (var message in history.SelectMany(entry => entry.ChannelMessages ?? []))
        {
            var key = TimelineKey(message.Source, message.Id);
            if (!byKey.TryAdd(key, message))
            {
                if (byKey[key] != message)
                    throw new InvalidOperationException($"Channel timeline message id was reused with different content: {message.Source}/{message.Id}");
                continue;
            }
            messages.Add(message);
        }
        return messages;
    }

    private static ModelMessage ToModelMessage(SessionHistoryEntry entry) =>
        new(entry.Role, entry.Content, entry.ToolCalls, entry.ToolCallId);

    private async ValueTask<Result<CollectedRoundContextSnapshot>> CollectRoundContextAsync(
        RoundContextRequest request, CancellationToken cancellationToken)
    {
        var contributors = _roundContextContributors.Snapshot();
        try
        {
            var results = await Task.WhenAll(contributors.Select(async contributor =>
            {
                if (string.IsNullOrWhiteSpace(contributor.Id))
                    throw new InvalidOperationException("Round context contributor id is required.");
                var timelineWriter = new TimelineWriter(contributor.Id);
                var roundWriter = new ContributionWriter(contributor.Id);
                await contributor.ContributeAsync(request, timelineWriter, roundWriter, cancellationToken).ConfigureAwait(false);
                return (Timeline: timelineWriter.Seal(), Round: roundWriter.Seal());
            })).ConfigureAwait(false);
            var roundContext = results.SelectMany(result => result.Round)
                .OrderBy(item => item.Order)
                .ThenBy(item => item.Source, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            var timeline = results.SelectMany(result => result.Timeline)
                .OrderBy(item => item.At)
                .ThenBy(item => item.Source, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            return Result<CollectedRoundContextSnapshot>.Success(new(roundContext, timeline));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Result<CollectedRoundContextSnapshot>.Failure(
                new(ErrorCode.Unavailable, "Round context collection failed.", ex.Message));
        }
    }

    /// <summary>
    /// Renders the channel side of the conversation as one block.
    ///
    /// Only the newest <see cref="RecentChannelMessages"/> messages are carried; the rest are replaced by one
    /// line saying how many were left out, because a model handed a silent tail cannot tell that anything is
    /// missing and will happily answer a question that was already settled three turns earlier.
    ///
    /// This is a different kind of omission from the one the Channel records when a pull hits its own window:
    /// that one is a fact about the conversation (those messages never entered this session), so it stays in
    /// history as a system message. This one is a property of the request — the messages are still in the
    /// timeline and still counted here — so it is rendered as structure, not as someone speaking.
    /// </summary>
    private static string RenderChannelContext(IReadOnlyList<SessionChannelMessage> messages,
        IReadOnlyList<CollectedRoundContext> floating)
    {
        // Nothing to say: an empty <channel_context> would be an empty user message, which some providers
        // reject outright and others read as "the user said nothing".
        if (messages.Count == 0 && floating.Count == 0) return string.Empty;
        var builder = new StringBuilder("<channel_context>\n");
        if (floating.Count > 0)
        {
            builder.AppendLine("<floating_context>");
            foreach (var item in floating)
                builder.Append("<section name=\"").Append(EscapeXmlAttribute(item.Section))
                    .Append("\" source=\"").Append(EscapeXmlAttribute(item.Source))
                    .Append("\" id=\"").Append(EscapeXmlAttribute(item.Id)).AppendLine("\">")
                    .AppendLine(EscapeXmlText(item.Content)).AppendLine("</section>");
            builder.AppendLine("</floating_context>");
        }
        var elided = Math.Max(0, messages.Count - RecentChannelMessages);
        builder.Append("<channel_messages shown=\"").Append(messages.Count - elided)
            .Append("\" total=\"").Append(messages.Count).AppendLine("\">");
        if (elided > 0)
            builder.Append("<omitted count=\"").Append(elided).Append("\">省略更早的 ").Append(elided)
                .Append(" 条消息（本请求只带最近 ").Append(RecentChannelMessages).AppendLine(" 条）。</omitted>");
        for (var index = elided; index < messages.Count; index++)
        {
            var message = messages[index];
            builder.Append("<message id=\"").Append(EscapeXmlAttribute(message.Id))
                .Append("\" source=\"").Append(EscapeXmlAttribute(message.Source)).Append('"');
            if (!string.IsNullOrEmpty(message.Sender))
                builder.Append(" sender=\"").Append(EscapeXmlAttribute(message.Sender)).Append('"');
            builder.Append(" at=\"").Append(message.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).AppendLine("\">")
                .AppendLine(EscapeXmlText(message.Content)).AppendLine("</message>");
        }
        return builder.AppendLine("</channel_messages>").Append("</channel_context>").ToString();
    }

    private static bool SameChannelMessages(IReadOnlyList<SessionChannelMessage>? left,
        IReadOnlyList<SessionChannelMessage> right) => left is not null && left.SequenceEqual(right);

    private static string EscapeXmlText(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EscapeXmlAttribute(string value) => EscapeXmlText(value)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&apos;", StringComparison.Ordinal);

    private sealed record CollectedRoundContext(string Source, string Id, string Section, string Content, int Order);
    private sealed record CollectedTimelineMessage(string Source, string Id, string? Sender, string Content, DateTimeOffset At);
    private sealed record CollectedRoundContextSnapshot(
        IReadOnlyList<CollectedRoundContext> RoundContext,
        IReadOnlyList<CollectedTimelineMessage> Timeline);
    private sealed record PersistedTimeline(SessionState State, IReadOnlyList<SessionHistoryEntry> History);

    private sealed class TimelineWriter(string source) : ISessionTimelineWriter
    {
        private readonly object _gate = new();
        private readonly List<CollectedTimelineMessage> _items = [];
        private bool _sealed;

        public void Add(SessionTimelineContribution contribution)
        {
            ArgumentNullException.ThrowIfNull(contribution);
            if (!ContributionWriter.IsContextIdValue(contribution.Id) || contribution.Content is null ||
                contribution.Content.Length > 64_000)
                throw new ArgumentException("Session timeline contribution is invalid or oversized.", nameof(contribution));
            lock (_gate)
            {
                if (_sealed) throw new InvalidOperationException("Session timeline collection is already complete.");
                if (_items.Any(item => StringComparer.Ordinal.Equals(item.Id, contribution.Id)))
                    throw new InvalidOperationException($"Duplicate timeline contribution id: {contribution.Id}");
                _items.Add(new(source, contribution.Id, contribution.Sender, contribution.Content, contribution.At));
            }
        }

        public IReadOnlyList<CollectedTimelineMessage> Seal()
        {
            lock (_gate)
            {
                _sealed = true;
                return _items.ToArray();
            }
        }
    }

    private sealed class ContributionWriter(string source) : IRoundContextWriter
    {
        private readonly object _gate = new();
        private readonly List<CollectedRoundContext> _items = [];
        private bool _sealed;

        public void Add(RoundContextContribution contribution)
        {
            ArgumentNullException.ThrowIfNull(contribution);
            if (!IsContextIdValue(contribution.Id) || !IsContextIdValue(contribution.Section) || contribution.Content is null ||
                contribution.Content.Length > 64_000)
                throw new ArgumentException("Round context contribution is invalid or oversized.", nameof(contribution));
            lock (_gate)
            {
                if (_sealed) throw new InvalidOperationException("Round context collection is already complete.");
                if (_items.Any(item => StringComparer.Ordinal.Equals(item.Id, contribution.Id)))
                    throw new InvalidOperationException($"Duplicate round context contribution id: {contribution.Id}");
                _items.Add(new(source, contribution.Id, contribution.Section, contribution.Content, contribution.Order));
            }
        }

        public IReadOnlyList<CollectedRoundContext> Seal()
        {
            lock (_gate)
            {
                _sealed = true;
                return _items.ToArray();
            }
        }

        public static bool IsContextIdValue(string value) => value is { Length: > 0 and <= 128 } &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    private string ResolveSessionDirectory(string sessionId)
    {
        var current = Path.Combine(_root, sessionId);
        var legacy = Path.Combine(_legacyRoot, sessionId);
        var currentExists = Directory.Exists(current);
        var legacyExists = Directory.Exists(legacy);
        if (currentExists && legacyExists) throw new IOException("Session data exists in both old and new locations.");
        if (currentExists || !legacyExists) return current;
        Directory.CreateDirectory(_root);
        Directory.Move(legacy, current);
        if (Directory.Exists(_legacyRoot) && !Directory.EnumerateFileSystemEntries(_legacyRoot).Any())
            Directory.Delete(_legacyRoot);
        return current;
    }

    private static bool IsValidSessionId(string value) => value is { Length: > 0 and <= 128 } && value.All(c =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    private sealed record LoadedSession(SessionSpec Spec, SessionState State);
    private sealed record LoadedRecovery(SessionState State, IReadOnlyList<SessionHistoryEntry> History);
    private sealed record SessionPendingCommit(SessionState State, IReadOnlyList<SessionHistoryEntry> Entries);

    /// <summary>
    /// One Session's context, held in memory for as long as this process serves it: the spec, the state and the
    /// timeline. Reading it back from disk on every write made the cost of a turn grow with the square of the
    /// conversation — the file was re-read in full once per commit, and a turn commits several times.
    ///
    /// This is the same trade the rest of the system already makes: state belongs to the component that owns it.
    /// The files stay exactly as they were (state.json and an append-only history.jsonl, both fsynced), so a crash
    /// and a restart lose nothing; what is cached is the *reading*, not the writing.
    ///
    /// Only touched while the Session's gate is held, so no locking of its own.
    /// </summary>
    private sealed class SessionRecord(SessionSpec spec, string directory)
    {
        private readonly List<SessionHistoryEntry> _history = [];
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

        public SessionSpec Spec { get; } = spec;
        public string Directory { get; } = directory;
        public SessionState State { get; set; } = null!;
        public IReadOnlyList<SessionHistoryEntry> History => _history;

        public bool Knows(string id) => _ids.Contains(id);

        /// <summary>Takes entries the timeline does not have yet; an id already present is not added twice.</summary>
        public IReadOnlyList<SessionHistoryEntry> Adopt(IEnumerable<SessionHistoryEntry> entries)
        {
            var added = new List<SessionHistoryEntry>();
            foreach (var entry in entries)
                if (_ids.Add(entry.Id)) { _history.Add(entry); added.Add(entry); }
            return added;
        }
    }

    private sealed class ToolCallAccumulator
    {
        private readonly StringBuilder _id = new();
        private readonly StringBuilder _name = new();
        private readonly StringBuilder _arguments = new();
        public void Append(ToolCallDelta delta)
        {
            if (delta.CallId is not null) _id.Append(delta.CallId);
            if (delta.Name is not null) _name.Append(delta.Name);
            _arguments.Append(delta.ArgumentsJsonFragment);
        }
        public ModelToolCall ToModelToolCall()
        {
            if (_id.Length == 0 || _name.Length == 0)
                throw new InvalidOperationException("Model emitted an incomplete tool call.");
            return new(_id.ToString(), _name.ToString(), _arguments.ToString());
        }
    }
}
