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
    private const int StateSchemaVersion = 1;
    private const long MaxStoredHistoryBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly IModelManager _models;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.Ordinal);

    public SessionManager(string dataRoot, IModelManager models)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(models);
        _root = Path.GetFullPath(Path.Combine(dataRoot, "chat"));
        _models = models;
    }

    public async ValueTask<Result<SessionTurnResult>> RunTurnAsync(
        string sessionId, SessionSpec spec, Guid activationId, string userInput, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Session id must use 1-128 ASCII letters, digits, '-' or '_'."));
        if (spec is null || string.IsNullOrWhiteSpace(spec.ModelProfileId) || string.IsNullOrWhiteSpace(spec.Objective) ||
            string.IsNullOrWhiteSpace(spec.SystemPrompt) || string.IsNullOrWhiteSpace(spec.FormatVersion))
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Session specification is incomplete."));
        if (activationId == Guid.Empty || string.IsNullOrWhiteSpace(userInput))
            return Result<SessionTurnResult>.Failure(new(ErrorCode.Invalid, "Activation id and user input are required."));

        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(_root, sessionId);
            Directory.CreateDirectory(directory);
            var session = await LoadOrCreateAsync(directory, spec, cancellationToken).ConfigureAwait(false);
            if (!session.Ok || session.Value is null) return Result<SessionTurnResult>.Failure(session.Error!);
            if (session.Value.State.Locked)
                return Result<SessionTurnResult>.Failure(new(ErrorCode.Denied, "Session is locked."));

            var recovered = await RecoverPendingAsync(directory, cancellationToken).ConfigureAwait(false);
            if (!recovered.Ok) return Result<SessionTurnResult>.Failure(recovered.Error!);
            session = await LoadOrCreateAsync(directory, spec, cancellationToken).ConfigureAwait(false);
            if (!session.Ok || session.Value is null) return Result<SessionTurnResult>.Failure(session.Error!);

            var historyResult = await ReadHistoryAsync(Path.Combine(directory, "history.jsonl"), repairTrailingLine: true, cancellationToken)
                .ConfigureAwait(false);
            if (!historyResult.Ok || historyResult.Value is null)
                return Result<SessionTurnResult>.Failure(historyResult.Error!);
            var history = historyResult.Value;
            var priorTurn = history.LastOrDefault(e => e.ActivationId == activationId.ToString("N") && e.Role == "assistant");
            if (priorTurn is not null)
            {
                return Result<SessionTurnResult>.Success(new(sessionId, priorTurn.StepNumber, priorTurn.Content ?? "",
                    priorTurn.ToolCalls ?? [], priorTurn.StopReason ?? StopReason.EndTurn,
                    priorTurn.Usage ?? new ModelUsage(0, 0), DuplicateActivation: true));
            }

            var messages = new List<ModelMessage>
            {
                new("system", session.Value.Spec.SystemPrompt),
                new("user", session.Value.Spec.Objective)
            };
            messages.AddRange(history.Select(ToModelMessage));
            messages.Add(new ModelMessage("user", userInput));

            var modelStarted = await _models.CompleteAsync(session.Value.Spec.ModelProfileId,
                new ModelRequest(messages), cancellationToken).ConfigureAwait(false);
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
            var completedCalls = toolCalls.Values.Select(c => c.ToModelToolCall()).ToArray();
            if (completedCalls.Length > 0)
                return Result<SessionTurnResult>.Failure(new(ErrorCode.Unavailable, "This Session does not have a tool executor configured."));
            var nextStep = session.Value.State.StepNumber + 1;
            var activation = activationId.ToString("N");
            var now = DateTimeOffset.UtcNow;
            var entries = new SessionHistoryEntry[]
            {
                new($"{activation}:user", activation, nextStep, "user", userInput, now),
                new($"{activation}:assistant", activation, nextStep, "assistant", text.ToString(), now,
                    completedCalls, Usage: completedUsage, StopReason: stopReason)
            };
            var newState = session.Value.State with
            {
                Status = SessionStatus.Dormant,
                StepNumber = nextStep,
                Usage = session.Value.State.Usage.Add(completedUsage)
            };
            var committed = await CommitTurnAsync(directory, new SessionPendingCommit(newState, entries), cancellationToken)
                .ConfigureAwait(false);
            if (!committed.Ok) return Result<SessionTurnResult>.Failure(committed.Error!);

            return Result<SessionTurnResult>.Success(new(sessionId, nextStep, text.ToString(), completedCalls,
                stopReason ?? StopReason.EndTurn, completedUsage));
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
        var statePath = Path.Combine(_root, sessionId, "state.json");
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
            if (readSpec != requestedSpec)
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
            if (readState is null || readState.SchemaVersion != StateSchemaVersion)
                return Result<LoadedSession>.Failure(new(ErrorCode.Invalid, "Session state version is unsupported."));
            state = readState;
        }
        else
        {
            state = new(SessionStatus.Dormant, 0, false, new SessionUsage(0, 0), StateSchemaVersion);
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
        if (pending.State.StepNumber < current.StepNumber)
        {
            File.Delete(pendingPath);
            return Result.Success();
        }
        if (pending.State.StepNumber > current.StepNumber + 1)
            return Result.Failure(new(ErrorCode.Invalid, "Pending session commit is not the next revision."));
        var historyPath = Path.Combine(directory, "history.jsonl");
        var historyResult = await ReadHistoryAsync(historyPath, repairTrailingLine: true, cancellationToken).ConfigureAwait(false);
        if (!historyResult.Ok || historyResult.Value is null) return Result.Failure(historyResult.Error!);
        await AppendMissingEntriesAsync(historyPath, historyResult.Value, pending.Entries, cancellationToken).ConfigureAwait(false);
        if (current.StepNumber < pending.State.StepNumber)
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

    private static ModelMessage ToModelMessage(SessionHistoryEntry entry) =>
        new(entry.Role, entry.Content, entry.ToolCalls, entry.ToolCallId);

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
