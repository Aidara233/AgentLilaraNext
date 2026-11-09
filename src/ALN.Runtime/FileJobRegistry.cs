using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The one table of background work, written down so that a restart leaves a trace instead of a silence.
///
/// <b>What is persisted and what is not.</b> The row — what it was, whose it was, how far it got, how it ended — yes.
/// The work itself, no, and nothing can bring it back: the socket closed, the file handle is gone, the thread that was
/// reading belongs to a process that no longer exists. So the row is what survives, and on the next start a row that
/// still says <see cref="JobState.Running"/> is turned into <see cref="JobState.Interrupted"/> and held until the
/// session has been told (docs/28 §9). A model that believes its download is still coming waits for nothing.
///
/// <b>Why the state store and not a table of its own.</b> One JSON document per session, keyed by the session, is the
/// smallest thing that answers "what is this conversation still waiting on" — and it needs no schema migration for a
/// feature whose whole payload is a few short fields.
///
/// <b>Two limits, both about not growing forever.</b> Rows beyond <see cref="MaxRowsPerSession"/> are dropped oldest
/// first, and a finished row is only kept while it is still news; a session that has been told keeps nothing but the
/// running ones. Neither number is a platform fact — they are what keeps a long conversation from writing a document
/// that costs more to read than it is worth.
/// </summary>
public sealed class FileJobRegistry : IJobRegistry
{
    /// <summary>How many rows one session's document may hold. Progress on the same job overwrites; this is about
    /// how many separate jobs a long-lived conversation can accumulate.</summary>
    public const int MaxRowsPerSession = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IStateStore? _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Loaded, reconciled rows per session. Null store means memory only — a test, or a host without one.</summary>
    private readonly Dictionary<string, List<JobSnapshot>> _rows = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public FileJobRegistry(IStateStore? store = null, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Who to tell when a row first says the work is over.
    ///
    /// A settable property rather than a constructor argument, for one concrete reason: a Session is built with the
    /// registry, so the registry cannot be built with the Session. The host attaches this on the line after it builds
    /// the sessions, before anything is served. Null means "record, do not tell" — a host with no sessions, or a test.
    ///
    /// <b>Why the registry tells anybody at all.</b> It is the only thing that knows an ending is <i>new</i>: a
    /// completion that arrives for a row already over changes nothing, and waking a session to say so would interrupt
    /// it with something it has been told. Pushing that decision out to every producer would mean each of them
    /// re-deriving it from a second read, and one of them getting it wrong.
    /// </summary>
    public ISessionWaker? Waker { get; set; }

    private const string Scope = "job";

    public async ValueTask<Result<JobSnapshot>> OpenAsync(JobSeed seed, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seed.SessionId))
            return Result<JobSnapshot>.Failure(new(ErrorCode.Invalid, "A job must belong to a session."));
        if (string.IsNullOrWhiteSpace(seed.Label))
            return Result<JobSnapshot>.Failure(new(ErrorCode.Invalid, "A job needs a label: it is what a restart names."));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await LoadAsync(seed.SessionId, cancellationToken).ConfigureAwait(false);
            var id = string.IsNullOrWhiteSpace(seed.Id) ? Guid.NewGuid().ToString("N")[..12] : seed.Id.Trim();
            if (rows.Exists(row => string.Equals(row.Id, id, StringComparison.Ordinal)))
                return Result<JobSnapshot>.Failure(new(ErrorCode.Conflict, $"这个会话里已经有 job \"{id}\"。"));
            var job = new JobSnapshot(id, seed.Kind, seed.Label, seed.SessionId,
                _clock.GetUtcNow(), JobState.Running);
            rows.Add(job);
            Prune(rows);
            var written = await SaveAsync(seed.SessionId, rows, cancellationToken).ConfigureAwait(false);
            return written.Ok ? Result<JobSnapshot>.Success(job) : Result<JobSnapshot>.Failure(written.Error!);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> ProgressAsync(string sessionId, string jobId, long done, long? total, string? unit,
        CancellationToken cancellationToken)
    {
        var moved = await MutateAsync(sessionId, jobId, job => job with
        {
            Done = done,
            // A total that the caller stopped knowing about is not a reason to erase it: null here means "this call has
            // nothing to say about the total", and overwriting a known size with nothing would lose the denominator.
            Total = total ?? job.Total,
            Unit = unit ?? job.Unit
        }, cancellationToken).ConfigureAwait(false);
        return Settled(moved);
    }

    public async ValueTask<Result> CompleteAsync(string sessionId, string jobId, string? result,
        CancellationToken cancellationToken)
    {
        var moved = await MutateAsync(sessionId, jobId, job => job with
        {
            State = JobState.Done, Result = result, Error = null,
            EndedAt = _clock.GetUtcNow(), Announced = false
        }, cancellationToken).ConfigureAwait(false);
        if (!moved.Ok) return Settled(moved);
        // Only a first ending is news. A second completion — a poll of a job the watcher already recorded — moves
        // nothing, and waking the session for it would interrupt a turn to say what has been said (docs/28 §9.4).
        if (moved.Value) await WakeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async ValueTask<Result> FailAsync(string sessionId, string jobId, string error,
        CancellationToken cancellationToken)
    {
        var moved = await MutateAsync(sessionId, jobId, job => job with
        {
            State = JobState.Failed, Error = error, EndedAt = _clock.GetUtcNow(), Announced = false
        }, cancellationToken).ConfigureAwait(false);
        if (!moved.Ok) return Settled(moved);
        // A failure is the case where being told matters most: without a wake-up the session waits for something that
        // will never arrive. Which row it was is not passed on — the note reads it from the table.
        if (moved.Value) await WakeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async ValueTask<Result> KillAsync(string sessionId, string jobId, CancellationToken cancellationToken)
    {
        var moved = await MutateAsync(sessionId, jobId, job => job with
        {
            // Announced, so it is never news: whoever asked for the kill already knows, and the session is running the
            // turn that asked. That is also why a kill does not wake anybody.
            State = JobState.Killed, EndedAt = _clock.GetUtcNow(), Announced = true
        }, cancellationToken).ConfigureAwait(false);
        return Settled(moved);
    }

    public async ValueTask<Result<IReadOnlyList<JobSnapshot>>> ListAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyList<JobSnapshot>>.Success(rows.ToArray());
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result<JobSnapshot>> GetAsync(string sessionId, string jobId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var found = rows.FirstOrDefault(row => string.Equals(row.Id, jobId, StringComparison.Ordinal));
            return found is null
                ? Result<JobSnapshot>.Failure(new(ErrorCode.Invalid,
                    $"这个会话里没有 job \"{jobId}\"。用 jobs.list 看现有的。"))
                : Result<JobSnapshot>.Success(found);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<JobSnapshot>> TakeUnannouncedAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var pending = rows.Where(row => row.IsOver && !row.Announced).ToArray();
            if (pending.Length == 0) return [];

            for (var index = 0; index < rows.Count; index++)
                if (rows[index].IsOver && !rows[index].Announced) rows[index] = rows[index] with { Announced = true };
            // Said out loud once, then forgotten: an old finished job is not news, and keeping it would make every
            // document grow with the conversation's history rather than with what it is still waiting on.
            rows.RemoveAll(row => row.IsOver && row.Announced);
            await SaveAsync(sessionId, rows, cancellationToken).ConfigureAwait(false);
            return pending;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<bool> AnyRunningAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return rows.Exists(row => row.State == JobState.Running);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<Result<bool>> MutateAsync(string sessionId, string jobId, Func<JobSnapshot, JobSnapshot> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var index = rows.FindIndex(row => string.Equals(row.Id, jobId, StringComparison.Ordinal));
            if (index < 0)
                return Result<bool>.Failure(new(ErrorCode.Invalid, $"这个会话里没有 job \"{jobId}\"。"));
            // A job that is already over is not moved again. A late progress tick from a task whose process is gone — or
            // a completion arriving after the row was turned into "interrupted" — would otherwise resurrect work that
            // nobody is doing, and the model would wait for it a second time.
            if (rows[index].IsOver) return Result<bool>.Success(false);
            rows[index] = change(rows[index]);
            var saved = await SaveAsync(sessionId, rows, cancellationToken).ConfigureAwait(false);
            return saved.Ok ? Result<bool>.Success(true) : Result<bool>.Failure(saved.Error!);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// "Did it move" folded back into the plain answer the port promises. The distinction matters only to the two calls
    /// that can end a job, and only there because an ending that changed nothing is not worth waking anybody for.
    /// </summary>
    private static Result Settled(Result<bool> moved) =>
        moved.Ok ? Result.Success() : Result.Failure(moved.Error!);

    /// <summary>
    /// Attempts the wake-up, and never lets its outcome decide whether the row was written.
    ///
    /// The two facts are different in kind: the row is the record, and a record that failed because a notification did
    /// not land would be a lie about what happened. A session that was not woken is not uninformed either — the row is
    /// still unannounced, so its next turn carries it. That is the whole reason the ledger keeps an
    /// <see cref="JobSnapshot.Announced"/> flag.
    /// </summary>
    private async ValueTask WakeAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (Waker is not { } waker) return;
        try { await waker.WakeAsync(sessionId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* See above: the row stands, the wake-up was an attempt. */ }
    }

    /// <summary>
    /// The rows for one session, reconciled against this process. A row that still says running cannot be: nothing in
    /// this process started it, so whatever it was doing died with the previous one. Turning it into
    /// <see cref="JobState.Interrupted"/> here — on the first touch, not at startup — is what makes a host that was
    /// down for a week report the same thing as one that was restarted a second ago.
    /// </summary>
    private async ValueTask<List<JobSnapshot>> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_rows.TryGetValue(sessionId, out var loaded)) return loaded;

        var rows = new List<JobSnapshot>();
        if (_store is not null)
        {
            var read = await _store.ReadAsync(Scope, StateName.Encode(sessionId), cancellationToken).ConfigureAwait(false);
            if (read.Ok && !string.IsNullOrWhiteSpace(read.Value))
            {
                try { rows = JsonSerializer.Deserialize<List<JobSnapshot>>(read.Value!, Json) ?? []; }
                catch (JsonException) { rows = []; }
            }
        }

        var changed = false;
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].State != JobState.Running) continue;
            rows[index] = rows[index] with
            {
                State = JobState.Interrupted,
                EndedAt = rows[index].EndedAt ?? _clock.GetUtcNow(),
                Announced = false,
                // Not "it failed" and not "it is still going": the process that knew is gone. For a pulled job it may even
                // have finished after the last time anybody looked, and saying either way would be a guess.
                Error = "宿主重启时这个任务还在跑：进程没了，它到底做完没有没人知道。要结果就重做一遍。"
            };
            changed = true;
        }
        _rows[sessionId] = rows;
        if (changed) await SaveAsync(sessionId, rows, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private async ValueTask<Result> SaveAsync(string sessionId, List<JobSnapshot> rows,
        CancellationToken cancellationToken)
    {
        if (_store is null) return Result.Success();
        var written = await _store.WriteAsync(Scope, StateName.Encode(sessionId), JsonSerializer.Serialize(rows, Json),
            cancellationToken).ConfigureAwait(false);
        return written.Ok
            ? Result.Success()
            : Result.Failure(new(ErrorCode.Unavailable, "后台任务写不进状态库：重启后这些任务会消失。",
                written.Error?.Message));
    }

    /// <summary>Drops the oldest finished rows first: a running one is what the session is still waiting on, so it is
    /// the last thing to go.</summary>
    private static void Prune(List<JobSnapshot> rows)
    {
        while (rows.Count > MaxRowsPerSession)
        {
            var oldestFinished = -1;
            for (var index = 0; index < rows.Count; index++)
                if (rows[index].IsOver && (oldestFinished < 0 || rows[index].StartedAt < rows[oldestFinished].StartedAt))
                    oldestFinished = index;
            if (oldestFinished < 0) return; // everything is running: the caller's own limit is what stops this.
            rows.RemoveAt(oldestFinished);
        }
    }
}
