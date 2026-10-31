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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.Ordinal);

    public SessionManager(string dataRoot, IModelManager models)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(models);
        _root = Path.GetFullPath(dataRoot);
        _legacyRoot = Path.Combine(_root, "chat");
        _models = models;
    }

    public async ValueTask<Result<SessionTurnResult>> RunTurnAsync(
        string sessionId, SessionSpec spec, Guid activationId, string userInput,
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
        if (activationId == Guid.Empty || string.IsNullOrWhiteSpace(userInput))
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Activation id and user input are required."));
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
            if (activationUser is not null && !StringComparer.Ordinal.Equals(activationUser.Content, userInput))
                return Result<SessionTurnResult>.Failure(new(ErrorCode.Conflict, "Activation id was reused with different input."));

            var messages = new List<ModelMessage>
            {
                new("system", session.Value.Spec.SystemPrompt),
                new("user", session.Value.Spec.Objective)
            };
            messages.AddRange(history.Select(ToModelMessage));
            var state = session.Value.State;
            var userEntryPending = activationUser is null;
            if (userEntryPending) messages.Add(new ModelMessage("user", userInput));
            var turnUsage = new ModelUsage(0, 0);
            var executedCalls = new List<ModelToolCall>();
            var finalText = "";
            var finalStopReason = StopReason.EndTurn;
            while (true)
            {
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
                var assistantEntry = new SessionHistoryEntry($"{activation}:assistant:{stepNumber}", activation, stepNumber,
                    "assistant", text.ToString(), now, completedCalls, Usage: completedUsage, StopReason: assistantStopReason);
                var modelEntries = new List<SessionHistoryEntry>();
                if (userEntryPending)
                    modelEntries.Add(new($"{activation}:user", activation, stepNumber, "user", userInput, now));
                modelEntries.Add(assistantEntry);
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
                userEntryPending = false;
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

    private static ModelMessage ToModelMessage(SessionHistoryEntry entry) =>
        new(entry.Role, entry.Content, entry.ToolCalls, entry.ToolCallId);

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
