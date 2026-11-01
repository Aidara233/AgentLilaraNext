using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>Runs one serialized model step per activation and persists completed turns.</summary>
public sealed class SessionManager : ISessionManager
{
    private const int StateSchemaVersion = 2;
    private const long MaxStoredHistoryBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly string _legacyRoot;
    private readonly IModelManager _models;
    private readonly ISignalLog _signalLog;
    private readonly IRoundContextContributorRegistry _roundContextContributors;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.Ordinal);

    public SessionManager(string dataRoot, IModelManager models, IRoundContextContributorRegistry? roundContextContributors = null,
        ISignalLog? signalLog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(models);
        _root = Path.GetFullPath(dataRoot);
        _legacyRoot = Path.Combine(_root, "chat");
        _models = models;
        _roundContextContributors = roundContextContributors ?? new RoundContextContributorRegistry();
        _signalLog = signalLog ?? NullSignalLog.Instance;
    }

    public async ValueTask<Result<SessionTurnResult>> RunTurnAsync(
        string sessionId, SessionSpec spec, Guid activationId, SessionTurnInput input,
        ISessionToolExecutor? toolExecutor, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = await RunTurnCoreAsync(sessionId, spec, activationId, input, toolExecutor, cancellationToken).ConfigureAwait(false);
            RecordTurn(sessionId, spec, activationId, result, started);
            return result;
        }
        catch (OperationCanceledException)
        {
            _signalLog.Write(new()
            {
                Group = "session", Name = sessionId, StepId = activationId,
                From = "session", To = $"model:{spec.ModelProfileId}", Mechanism = "session.turn",
                Outcome = "timeout", Level = SignalLevel.Warn,
                DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
            throw;
        }
    }

    private void RecordTurn(string sessionId, SessionSpec spec, Guid activationId,
        Result<SessionTurnResult> result, long started)
    {
        var level = result.Ok ? SignalLevel.Info : SignalLevel.Warn;
        _signalLog.Write(new()
        {
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

    private async ValueTask<Result<SessionTurnResult>> RunTurnCoreAsync(
        string sessionId, SessionSpec spec, Guid activationId, SessionTurnInput input,
        ISessionToolExecutor? toolExecutor, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Session id must use 1-128 ASCII letters, digits, '-' or '_'."));
        if (spec is null || string.IsNullOrWhiteSpace(spec.ModelProfileId) || string.IsNullOrWhiteSpace(spec.Objective) ||
            string.IsNullOrWhiteSpace(spec.SystemPrompt) || string.IsNullOrWhiteSpace(spec.FormatVersion))
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
        if (activationId == Guid.Empty || input?.Messages is not { Count: > 0 } channelMessages ||
            channelMessages.Any(m => string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Source) || m.Content is null) ||
            channelMessages.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != channelMessages.Count)
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Activation id and unique channel messages are required."));
        if (spec.Tools is { Count: > 0 } && toolExecutor is null)
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Unavailable, "Session tools are declared but no tool executor is available."));

        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = ResolveSessionDirectory(sessionId);
            Directory.CreateDirectory(directory);
            var session = await LoadOrCreateAsync(directory, spec, cancellationToken).ConfigureAwait(false);
            if (!session.Ok || session.Value is null) return Result<SessionTurnResult>.Failure(session.Error!);
            if (session.Value.State.Locked)
                return Result<SessionTurnResult>.Failure(new(ErrorCode.Denied, "Session is locked."));

            var recovered = await RecoverPendingAsync(directory, cancellationToken).ConfigureAwait(false);
            if (!recovered.Ok) return Result<SessionTurnResult>.Failure(recovered.Error!);
            session = await LoadOrCreateAsync(directory, spec, cancellationToken).ConfigureAwait(false);
            if (!session.Ok || session.Value is null) return Result<SessionTurnResult>.Failure(session.Error!);

            var historyPath = Path.Combine(directory, "history.jsonl");
            var historyResult = await ReadHistoryAsync(historyPath, repairTrailingLine: true, cancellationToken).ConfigureAwait(false);
            if (!historyResult.Ok || historyResult.Value is null) return Result<SessionTurnResult>.Failure(historyResult.Error!);
            var history = historyResult.Value;
            var activation = activationId.ToString("N");
            var activationEntries = history.Where(e => e.ActivationId == activation).ToArray();
            var priorTurn = activationEntries.LastOrDefault(e => e.Role == "assistant" && e.StopReason != StopReason.ToolUse);
            if (priorTurn is not null)
                return Result<SessionTurnResult>.Success(new(sessionId, priorTurn.StepNumber, priorTurn.Content ?? "",
                    priorTurn.ToolCalls ?? [], priorTurn.StopReason ?? StopReason.EndTurn,
                    priorTurn.Usage ?? new ModelUsage(0, 0), DuplicateActivation: true));
            if (HasUnresolvedToolCalls(history))
                return Result<SessionTurnResult>.Failure(new(ErrorCode.Conflict, "Session contains an unresolved tool call that requires recovery."));
            var activationUser = activationEntries.FirstOrDefault(e => e.Role == "user");
            if (activationEntries.Length > 0 && activationUser is null)
                return Result<SessionTurnResult>.Failure(new(ErrorCode.Conflict, "Session activation is incomplete and cannot be resumed safely."));
            if (activationUser is not null && !SameChannelMessages(activationUser.ChannelMessages, input.Messages))
                return Result<SessionTurnResult>.Failure(new(ErrorCode.Conflict, "Activation id was reused with different channel messages."));

            var state = session.Value.State;
            if (activationUser is null)
            {
                activationUser = new SessionHistoryEntry($"{activation}:user", activation, state.StepNumber + 1,
                    "user", null, DateTimeOffset.UtcNow, ChannelMessages: input.Messages);
                var timelineState = state with { Revision = state.Revision + 1 };
                var timelineCommit = await CommitTurnAsync(directory,
                    new SessionPendingCommit(timelineState, [activationUser]), cancellationToken).ConfigureAwait(false);
                if (!timelineCommit.Ok) return Result<SessionTurnResult>.Failure(timelineCommit.Error!);
                state = timelineState;
                history = history.Append(activationUser).ToArray();
            }

            var messages = new List<ModelMessage>
            {
                new("system", session.Value.Spec.SystemPrompt),
                new("user", session.Value.Spec.Objective)
            };
            messages.AddRange(history.Where(entry => entry.ChannelMessages is null).Select(ToModelMessage));
            var collected = await CollectRoundContextAsync(
                new RoundContextRequest(sessionId, activationId, state.StepNumber + 1), cancellationToken).ConfigureAwait(false);
            if (!collected.Ok || collected.Value is null) return Result<SessionTurnResult>.Failure(collected.Error!);

            var persistedTimeline = await PersistTimelineContributionsAsync(directory, activation, state, history,
                collected.Value.Timeline, cancellationToken).ConfigureAwait(false);
            if (!persistedTimeline.Ok || persistedTimeline.Value is null)
                return Result<SessionTurnResult>.Failure(persistedTimeline.Error!);
            state = persistedTimeline.Value.State;
            history = persistedTimeline.Value.History;

            var channelTimeline = CollectChannelTimeline(history);
            messages.Add(new ModelMessage("user", RenderChannelContext(channelTimeline, collected.Value.RoundContext)));
            var turnUsage = new ModelUsage(0, 0);
            var executedCalls = new List<ModelToolCall>();
            var finalText = "";
            var finalStopReason = StopReason.EndTurn;
            while (true)
            {
                var modelStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var modelStarted = await _models.CompleteAsync(session.Value.Spec.ModelProfileId,
                    new ModelRequest(messages, session.Value.Spec.Tools), cancellationToken).ConfigureAwait(false);
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
                    Group = "model",
                    Name = session.Value.Spec.ModelProfileId,
                    StepId = activationId,
                    From = "session",
                    To = $"model:{session.Value.Spec.ModelProfileId}",
                    Mechanism = "model.request",
                    Outcome = modelError is null ? "ok" : "failed",
                    DurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(modelStartedAt).TotalMilliseconds,
                    Level = modelError is null ? SignalLevel.Debug : SignalLevel.Warn,
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
                if (completedCalls.Length > 0 && (toolExecutor is null || session.Value.Spec.Tools is not { Count: > 0 } declaredSchemas ||
                    completedCalls.Any(c => !declaredSchemas.Any(t => t.Name == c.Name))))
                    return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Model returned a tool call that was not declared by the Session."));

                var stepNumber = state.StepNumber + 1;
                var revision = state.Revision + 1;
                var now = DateTimeOffset.UtcNow;
                var assistantStopReason = completedCalls.Length > 0 ? StopReason.ToolUse : stopReason ?? StopReason.EndTurn;
                IReadOnlyList<SessionChannelMessage>? assistantChannelMessage = completedCalls.Length == 0
                    ? [new SessionChannelMessage($"{activation}:assistant:{stepNumber}", "assistant", "ALN", text.ToString(), now)]
                    : null;
                var assistantEntry = new SessionHistoryEntry($"{activation}:assistant:{stepNumber}", activation, stepNumber,
                    "assistant", text.ToString(), now, completedCalls, Usage: completedUsage, StopReason: assistantStopReason,
                    ChannelMessages: assistantChannelMessage);
                var modelEntries = new List<SessionHistoryEntry> { assistantEntry };
                var modelState = state with
                {
                    Status = SessionStatus.Dormant,
                    StepNumber = stepNumber,
                    Revision = revision,
                    Usage = state.Usage.Add(completedUsage)
                };
                var modelCommit = await CommitTurnAsync(directory, new SessionPendingCommit(modelState, modelEntries), cancellationToken)
                    .ConfigureAwait(false);
                if (!modelCommit.Ok) return Result<SessionTurnResult>.Failure(modelCommit.Error!);
                state = modelState;
                messages.Add(new ModelMessage("assistant", text.ToString(), completedCalls.Length > 0 ? completedCalls : null));
                executedCalls.AddRange(completedCalls);

                if (completedCalls.Length == 0)
                {
                    finalText = text.ToString();
                    finalStopReason = stopReason ?? StopReason.EndTurn;
                    break;
                }

                foreach (var call in completedCalls)
                {
                    Result<string> execution;
                    try { execution = await toolExecutor!.ExecuteAsync(sessionId, activationId, call, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { execution = Result<string>.Failure(new(ErrorCode.Internal, "Session tool execution failed.", ex.Message)); }
                    var toolContent = execution.Ok && execution.Value is not null
                        ? execution.Value
                        : JsonSerializer.Serialize(new { error = execution.Error?.Message ?? "Tool returned no result." }, JsonOptions);
                    var toolMessage = new ModelMessage("tool", toolContent, ToolCallId: call.Id, Name: call.Name);
                    messages.Add(toolMessage);
                    var toolRevisionState = state with { Revision = state.Revision + 1 };
                    var toolEntry = new SessionHistoryEntry($"{activation}:tool:{stepNumber}:{call.Id}", activation, stepNumber,
                        "tool", toolContent, DateTimeOffset.UtcNow, ToolCallId: call.Id);
                    var toolCommit = await CommitTurnAsync(directory,
                        new SessionPendingCommit(toolRevisionState, [toolEntry]), cancellationToken).ConfigureAwait(false);
                    if (!toolCommit.Ok) return Result<SessionTurnResult>.Failure(toolCommit.Error!);
                    state = toolRevisionState;
                }
            }

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

    private async ValueTask<Result<LoadedSession>> LoadOrCreateAsync(string directory, SessionSpec requestedSpec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var specPath = Path.Combine(directory, "spec.json");
        var statePath = Path.Combine(directory, "state.json");
        var historyPath = Path.Combine(directory, "history.jsonl");
        SessionSpec persistedSpec;
        if (File.Exists(specPath))
        {
            var readSpec = JsonSerializer.Deserialize<SessionSpec>(await File.ReadAllBytesAsync(specPath, cancellationToken).ConfigureAwait(false), JsonOptions);
            if (readSpec is null) return Result<LoadedSession>.Failure(new(ErrorCode.Invalid, "Session specification is invalid."));
            if (!SameSpec(readSpec, requestedSpec))
                return Result<LoadedSession>.Failure(new(ErrorCode.Conflict, "Session specification is immutable; create a new session id to change it."));
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
            state = new(SessionStatus.Dormant, 0, 0, false, new SessionUsage(0, 0), StateSchemaVersion);
            await WriteAtomicJsonAsync(statePath, state, cancellationToken).ConfigureAwait(false);
        }
        if (!File.Exists(historyPath)) await File.WriteAllTextAsync(historyPath, "", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return Result<LoadedSession>.Success(new(persistedSpec, state));
    }

    private async ValueTask<Result> RecoverPendingAsync(string directory, CancellationToken cancellationToken)
    {
        var pendingPath = Path.Combine(directory, "commit.pending.json");
        if (!File.Exists(pendingPath)) return Result.Success();
        var pending = JsonSerializer.Deserialize<SessionPendingCommit>(
            await File.ReadAllBytesAsync(pendingPath, cancellationToken).ConfigureAwait(false), JsonOptions);
        if (pending is null) return Result.Failure(new(ErrorCode.Invalid, "Pending session commit is invalid."));
        var statePath = Path.Combine(directory, "state.json");
        var current = JsonSerializer.Deserialize<SessionState>(await File.ReadAllBytesAsync(statePath, cancellationToken).ConfigureAwait(false), JsonOptions);
        if (current is null) return Result.Failure(new(ErrorCode.Invalid, "Session state is invalid during recovery."));
        if (pending.State.Revision < current.Revision)
        {
            File.Delete(pendingPath);
            return Result.Success();
        }
        if (pending.State.Revision > current.Revision + 1)
            return Result.Failure(new(ErrorCode.Invalid, "Pending session commit is not the next revision."));
        var historyPath = Path.Combine(directory, "history.jsonl");
        var historyResult = await ReadHistoryAsync(historyPath, repairTrailingLine: true, cancellationToken).ConfigureAwait(false);
        if (!historyResult.Ok || historyResult.Value is null) return Result.Failure(historyResult.Error!);
        await AppendMissingEntriesAsync(historyPath, historyResult.Value, pending.Entries, cancellationToken).ConfigureAwait(false);
        if (current.Revision < pending.State.Revision)
            await WriteAtomicJsonAsync(statePath, pending.State, cancellationToken).ConfigureAwait(false);
        File.Delete(pendingPath);
        return Result.Success();
    }

    private async ValueTask<Result> CommitTurnAsync(string directory, SessionPendingCommit pending, CancellationToken cancellationToken)
    {
        var pendingPath = Path.Combine(directory, "commit.pending.json");
        await WriteAtomicJsonAsync(pendingPath, pending, cancellationToken).ConfigureAwait(false);
        var historyPath = Path.Combine(directory, "history.jsonl");
        var historyResult = await ReadHistoryAsync(historyPath, repairTrailingLine: true, cancellationToken).ConfigureAwait(false);
        if (!historyResult.Ok || historyResult.Value is null) return Result.Failure(historyResult.Error!);
        await AppendMissingEntriesAsync(historyPath, historyResult.Value, pending.Entries, cancellationToken).ConfigureAwait(false);
        await WriteAtomicJsonAsync(Path.Combine(directory, "state.json"), pending.State, cancellationToken).ConfigureAwait(false);
        File.Delete(pendingPath);
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

    private static async ValueTask AppendMissingEntriesAsync(string historyPath, IReadOnlyList<SessionHistoryEntry> existing,
        IReadOnlyList<SessionHistoryEntry> additions, CancellationToken cancellationToken)
    {
        var ids = existing.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var missing = additions.Where(e => ids.Add(e.Id)).ToArray();
        if (missing.Length == 0) return;
        await using var stream = new FileStream(historyPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        foreach (var entry in missing)
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

    private static bool SameSpec(SessionSpec left, SessionSpec right) =>
        StringComparer.Ordinal.Equals(left.ModelProfileId, right.ModelProfileId) &&
        StringComparer.Ordinal.Equals(left.Objective, right.Objective) &&
        StringComparer.Ordinal.Equals(left.SystemPrompt, right.SystemPrompt) &&
        StringComparer.Ordinal.Equals(left.FormatVersion, right.FormatVersion) &&
        (left.Tools ?? []).SequenceEqual(right.Tools ?? []);

    private static bool HasUnresolvedToolCalls(IReadOnlyList<SessionHistoryEntry> history)
    {
        var calls = history.Where(e => e.Role == "assistant").SelectMany(e => (e.ToolCalls ?? [])
            .Select(call => $"{e.ActivationId}:{e.StepNumber}:{call.Id}")).ToHashSet(StringComparer.Ordinal);
        var results = history.Where(e => e.Role == "tool" && e.ToolCallId is not null)
            .Select(e => $"{e.ActivationId}:{e.StepNumber}:{e.ToolCallId}").ToHashSet(StringComparer.Ordinal);
        return calls.Except(results, StringComparer.Ordinal).Any();
    }

    private static ModelUsage AddUsage(ModelUsage left, ModelUsage right) => new(
        left.InputTokens + right.InputTokens,
        left.OutputTokens + right.OutputTokens,
        (left.CacheReadTokens ?? 0) + (right.CacheReadTokens ?? 0),
        (left.CacheWriteTokens ?? 0) + (right.CacheWriteTokens ?? 0));

    private async ValueTask<Result<PersistedTimeline>> PersistTimelineContributionsAsync(string directory, string activation,
        SessionState state, IReadOnlyList<SessionHistoryEntry> history, IReadOnlyList<CollectedTimelineMessage> contributions,
        CancellationToken cancellationToken)
    {
        var existing = CollectChannelTimeline(history);
        var byKey = existing.ToDictionary(message => TimelineKey(message.Source, message.Id), StringComparer.Ordinal);
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
            return Result<PersistedTimeline>.Success(new(state, history));

        var nextState = state with { Revision = state.Revision + 1 };
        var committed = await CommitTurnAsync(directory, new SessionPendingCommit(nextState, additions), cancellationToken)
            .ConfigureAwait(false);
        if (!committed.Ok) return Result<PersistedTimeline>.Failure(committed.Error!);
        return Result<PersistedTimeline>.Success(new(nextState, history.Concat(additions).ToArray()));
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

    private static string RenderChannelContext(IReadOnlyList<SessionChannelMessage> messages,
        IReadOnlyList<CollectedRoundContext> floating)
    {
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
        builder.AppendLine("<channel_messages>");
        foreach (var message in messages)
        {
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
    private sealed record SessionPendingCommit(SessionState State, IReadOnlyList<SessionHistoryEntry> Entries);

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
