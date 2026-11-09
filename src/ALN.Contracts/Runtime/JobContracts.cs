namespace ALN.Contracts;

/// <summary>
/// What happened to a job. <see cref="Interrupted"/> is the one that only exists because a process can die: a job that
/// was running when the host stopped is not failed and not killed — nobody decided it — and saying "failed" would be a
/// lie the model then tries to fix.
/// </summary>
public enum JobState
{
    Running,
    Done,
    Failed,
    Killed,

    /// <summary>Still marked running when the process that owned it went away. The work did not finish, and nobody
    /// stopped it on purpose: the only honest thing to say is that it is gone (docs/28 §9).</summary>
    Interrupted
}

/// <summary>
/// A job as it begins: what it is, whose turn it belongs to, and what to call it. Everything else the registry fills in.
/// </summary>
/// <param name="Kind">Where it came from: <c>ssh.run</c>, <c>file.fetch</c>. Not a tool name — the tool is one of the
/// ways to start the same kind.</param>
/// <param name="Label">One line a person reads: the command, or "群文件 周报.zip". It is what a restart has to be able
/// to name, which is why it is stored rather than derived.</param>
/// <param name="Id">
/// The name the caller already uses for this job, when it has one. It exists so that two views of the same work agree:
/// SSH numbers its own background jobs, and a ledger with a second id would make the model's "job 3f2a" mean nothing
/// when it asks the table about it. Null means the registry picks one.
/// </param>
public sealed record JobSeed(string Kind, string Label, string SessionId, string? Id = null);

/// <summary>
/// One job as anything outside the registry sees it.
///
/// It carries <b>progress</b> and not the output: bytes counted, a total when the size is known, and a unit that says
/// whether that is bytes or lines. Streaming output belongs to the thing that produces it — a shell writes chunks, an
/// HTTP fetch does not — and putting a buffer in the shared contract would decide that question for everybody
/// (<c>docs/28 §9</c>).
/// </summary>
/// <param name="Announced">Whether the session has already been told this job is over. A finished job that nobody was
/// awake to hear about is not the same as one that was reported; the difference is what stops a restart from telling the
/// same story twice.</param>
public sealed record JobSnapshot(
    string Id,
    string Kind,
    string Label,
    string SessionId,
    DateTimeOffset StartedAt,
    JobState State,
    long? Done = null,
    long? Total = null,
    string? Unit = null,
    string? Result = null,
    string? Error = null,
    DateTimeOffset? EndedAt = null,
    bool Announced = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOver => State is not JobState.Running;

    /// <summary>"3.2 MB / 12 MB" when a total is known, else "3.2 MB". Null when there is no progress to speak of.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Progress => Done is not { } done
        ? null
        : Total is { } total
            ? $"{Measure(done, Unit)} / {Measure(total, Unit)}"
            : Measure(done, Unit);

    private static string Measure(long value, string? unit) => unit switch
    {
        "bytes" => value switch
        {
            >= 1024L * 1024 * 1024 => $"{value / (1024.0 * 1024 * 1024):0.#} GB",
            >= 1024 * 1024 => $"{value / (1024.0 * 1024):0.#} MB",
            >= 1024 => $"{value / 1024.0:0.#} KB",
            _ => $"{value} B"
        },
        null or "" => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => $"{value} {unit}"
    };
}

/// <summary>
/// Where background work is written down: one table, for every kind of work, so that "is something still running for
/// this conversation" is one question with one answer.
///
/// <b>Why this is not the capability table we deleted (I-33).</b> That one was a claim about somebody else — a sender
/// asking an Adapter what it could do, which meant a copy of a fact that lived elsewhere and could drift. This is the
/// fact itself: nothing else knows whether a download is still running. The test is whether an abstraction has a second
/// place that has to agree with it; a registry that <i>is</i> the state has none.
///
/// <b>What survives a restart:</b> the row, not the work. A running job cannot be picked up again — its socket, its file
/// handle, the thread that was reading are all gone. So the row is turned into <see cref="JobState.Interrupted"/> and
/// kept until the session has been told, because a model that thinks its download is still coming will wait for
/// something that does not exist (<c>docs/28 §9</c>).
/// </summary>
public interface IJobRegistry
{
    /// <summary>Records a job that is starting now and hands back the row (with its id).</summary>
    ValueTask<Result<JobSnapshot>> OpenAsync(JobSeed seed, CancellationToken cancellationToken);

    /// <summary>
    /// How far along it is. <paramref name="total"/> null means "not known", which is not zero: a download whose length
    /// the platform never sent is not a download of nothing.
    /// </summary>
    ValueTask<Result> ProgressAsync(string sessionId, string jobId, long done, long? total, string? unit,
        CancellationToken cancellationToken);

    /// <summary>It finished. <paramref name="result"/> is the one line the model reads when it wakes up.</summary>
    ValueTask<Result> CompleteAsync(string sessionId, string jobId, string? result, CancellationToken cancellationToken);

    ValueTask<Result> FailAsync(string sessionId, string jobId, string error, CancellationToken cancellationToken);

    /// <summary>It was stopped on purpose, by whoever asked for it.</summary>
    ValueTask<Result> KillAsync(string sessionId, string jobId, CancellationToken cancellationToken);

    /// <summary>Everything this session started, newest last. Reads through to the durable copy, so a session that
    /// outlived a restart still gets its rows.</summary>
    ValueTask<Result<IReadOnlyList<JobSnapshot>>> ListAsync(string sessionId, CancellationToken cancellationToken);

    ValueTask<Result<JobSnapshot>> GetAsync(string sessionId, string jobId, CancellationToken cancellationToken);

    /// <summary>
    /// The jobs that ended without the session ever being told — an interrupted one from a previous run, or one that
    /// finished while nobody was awake. Reading them marks them told, because "tell it once" is the whole point: a wake
    /// note that repeats is a model re-doing work it already knows about.
    /// </summary>
    ValueTask<IReadOnlyList<JobSnapshot>> TakeUnannouncedAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Whether anything is still running for this session — asked by callers that must not block a turn on it.</summary>
    ValueTask<bool> AnyRunningAsync(string sessionId, CancellationToken cancellationToken);
}
