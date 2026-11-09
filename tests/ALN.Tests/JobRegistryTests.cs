using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// The table of background work. Two things are being proved here, and they are different questions:
/// <b>a job that was running when the process died is reported as interrupted</b> — not failed, not running, because
/// nobody decided it and nothing is doing it — and <b>saying so happens once</b>, so a long conversation is not
/// reintroduced to the same dead job every turn.
/// </summary>
public sealed class JobRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-jobs-" + Guid.NewGuid().ToString("N"));

    public JobRegistryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private SqliteStore Store() => new(Path.Combine(_root, "aln.db"));

    private static JobSeed Seed(string session = "session-1", string label = "群文件 周报.zip") =>
        new("file.fetch", label, session);

    [Fact]
    public async Task A_job_is_open_until_something_says_how_it_ended()
    {
        var registry = new FileJobRegistry();

        var opened = await registry.OpenAsync(Seed(), default);

        Assert.True(opened.Ok, opened.Error?.Message);
        var listed = await registry.ListAsync("session-1", default);
        var job = Assert.Single(listed.Value!);
        Assert.Equal(JobState.Running, job.State);
        Assert.Equal("file.fetch", job.Kind);
        Assert.False(job.Announced);
        // Nothing was announced: a job that is still running has no news in it.
        Assert.Empty(await registry.TakeUnannouncedAsync("session-1", default));
    }

    [Fact]
    public async Task Progress_that_does_not_know_the_total_keeps_the_one_it_had()
    {
        var registry = new FileJobRegistry();
        var opened = await registry.OpenAsync(Seed(), default);
        var id = opened.Value!.Id;

        Assert.True((await registry.ProgressAsync("session-1", id, 100, 1000, "bytes", default)).Ok);
        // A later tick that cannot say how big the thing is must not erase the denominator: "100 of unknown" is not
        // better information than "100 of 1000".
        Assert.True((await registry.ProgressAsync("session-1", id, 400, null, null, default)).Ok);

        var job = (await registry.GetAsync("session-1", id, default)).Value!;
        Assert.Equal(400, job.Done);
        Assert.Equal(1000, job.Total);
        Assert.Equal("bytes", job.Unit);
        Assert.Equal("400 B / 1000 B", job.Progress);
    }

    [Fact]
    public async Task A_job_that_was_running_when_the_process_died_is_interrupted_and_never_failed()
    {
        var store = Store();
        var before = new FileJobRegistry(store);
        var opened = await before.OpenAsync(Seed(), default);
        await before.ProgressAsync("session-1", opened.Value!.Id, 4096, 8192, "bytes", default);
        store.Dispose();

        // The second registry is a restart: nothing it can see started this job, so whatever it was doing died with the
        // previous process. "Failed" would be a lie — nobody decided anything — and "running" would be a worse one.
        using var after = Store();
        var restarted = new FileJobRegistry(after);
        var listed = await restarted.ListAsync("session-1", default);

        var job = Assert.Single(listed.Value!);
        Assert.Equal(JobState.Interrupted, job.State);
        Assert.Contains("重启", job.Error ?? "", StringComparison.Ordinal);
        // What it had got to is kept: it is the only thing that tells the model whether redoing it is cheap.
        Assert.Equal(4096, job.Done);
        Assert.Equal(8192, job.Total);
    }

    [Fact]
    public async Task An_interrupted_job_is_announced_once_and_then_forgotten()
    {
        var store = Store();
        var before = new FileJobRegistry(store);
        await before.OpenAsync(Seed(label: "ssh 上的 build"), default);
        store.Dispose();

        using var after = Store();
        var restarted = new FileJobRegistry(after);
        var taken = await restarted.TakeUnannouncedAsync("session-1", default);

        var job = Assert.Single(taken);
        Assert.Equal(JobState.Interrupted, job.State);
        Assert.Equal("ssh 上的 build", job.Label);
        // Once: the second turn of the same conversation is not told again, and the row is gone — an old finished job is
        // not news, and keeping it would make the document grow with the history rather than with the waiting.
        Assert.Empty(await restarted.TakeUnannouncedAsync("session-1", default));
        Assert.Empty((await restarted.ListAsync("session-1", default)).Value!);
    }

    [Fact]
    public async Task A_job_that_finished_while_nobody_was_awake_is_announced_too()
    {
        var registry = new FileJobRegistry();
        var opened = await registry.OpenAsync(Seed(label: "下载 周报.zip"), default);
        await registry.CompleteAsync("session-1", opened.Value!.Id, "存到了 media/file/ab/x.zip", default);

        var taken = await registry.TakeUnannouncedAsync("session-1", default);

        var job = Assert.Single(taken);
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal("存到了 media/file/ab/x.zip", job.Result);
    }

    [Fact]
    public async Task A_killed_job_is_not_news_because_somebody_asked_for_it()
    {
        var registry = new FileJobRegistry();
        var opened = await registry.OpenAsync(Seed(), default);

        Assert.True((await registry.KillAsync("session-1", opened.Value!.Id, default)).Ok);

        // Whoever stopped it already knows. Announcing it again would spend the model's attention on something that
        // needs no decision.
        Assert.Empty(await registry.TakeUnannouncedAsync("session-1", default));
        Assert.Equal(JobState.Killed, (await registry.GetAsync("session-1", opened.Value.Id, default)).Value!.State);
    }

    [Fact]
    public async Task A_late_tick_cannot_bring_an_interrupted_job_back_to_life()
    {
        var store = Store();
        var before = new FileJobRegistry(store);
        var opened = await before.OpenAsync(Seed(), default);
        store.Dispose();

        using var after = Store();
        var restarted = new FileJobRegistry(after);
        // A thread from the old process that had not noticed it was dead: its progress arrives after the reconcile.
        Assert.True((await restarted.ProgressAsync("session-1", opened.Value!.Id, 999, null, null, default)).Ok);

        var job = (await restarted.GetAsync("session-1", opened.Value.Id, default)).Value!;
        Assert.Equal(JobState.Interrupted, job.State);
        Assert.Null(job.Done);
    }

    [Fact]
    public async Task Jobs_belong_to_one_conversation_and_the_document_does_not_grow_without_a_bound()
    {
        var registry = new FileJobRegistry();
        for (var index = 0; index < FileJobRegistry.MaxRowsPerSession + 5; index++)
        {
            var opened = await registry.OpenAsync(Seed(label: $"下载 {index}"), default);
            await registry.CompleteAsync("session-1", opened.Value!.Id, null, default);
        }
        var other = await registry.OpenAsync(Seed(session: "session-2", label: "别的会话"), default);

        var mine = (await registry.ListAsync("session-1", default)).Value!;
        Assert.True(mine.Count <= FileJobRegistry.MaxRowsPerSession, $"kept {mine.Count} rows");
        // Another conversation's jobs are not this conversation's business: one table, one owner per row.
        Assert.DoesNotContain(mine, job => job.Id == other.Value!.Id);
        Assert.Single((await registry.ListAsync("session-2", default)).Value!);
    }

    [Fact]
    public async Task A_registry_with_no_store_still_works_and_simply_forgets_on_restart()
    {
        var registry = new FileJobRegistry();
        var opened = await registry.OpenAsync(Seed(), default);

        Assert.True(opened.Ok);
        Assert.Single((await registry.ListAsync("session-1", default)).Value!);
        // Nothing to reconcile: with no store there is no previous run to have left anything behind.
        var forgotten = await new FileJobRegistry().ListAsync("session-1", default);
        Assert.Empty(forgotten.Value!);
    }

    // ---- being told, without asking ---------------------------------------------------------------

    /// <summary>Records who was woken, and can refuse to let it happen.</summary>
    private sealed class RecordingWaker(bool fail = false) : ISessionWaker
    {
        public List<string> Woken { get; } = [];

        public ValueTask<Result> WakeAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (fail) return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "Nobody is listening.")));
            Woken.Add(sessionId);
            return ValueTask.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task A_job_that_ends_wakes_its_session_exactly_once()
    {
        // The row is written by whoever noticed the ending; being told is what turns that into a turn. Two endings of
        // one job is one ending: the second must not interrupt the session to say what it already heard.
        var registry = new FileJobRegistry();
        var waker = new RecordingWaker();
        registry.Waker = waker;
        var opened = await registry.OpenAsync(Seed(), default);

        Assert.True((await registry.CompleteAsync("session-1", opened.Value!.Id, "done", default)).Ok);
        Assert.Equal(new[] { "session-1" }, waker.Woken);

        Assert.True((await registry.CompleteAsync("session-1", opened.Value.Id, "done again", default)).Ok);
        Assert.Equal(new[] { "session-1" }, waker.Woken);
    }

    [Fact]
    public async Task A_failure_wakes_the_session_and_a_kill_does_not()
    {
        var registry = new FileJobRegistry();
        var waker = new RecordingWaker();
        registry.Waker = waker;

        var failed = await registry.OpenAsync(Seed(label: "下载 周报.zip"), default);
        Assert.True((await registry.FailAsync("session-1", failed.Value!.Id, "连接断了", default)).Ok);
        Assert.Equal(new[] { "session-1" }, waker.Woken);

        // A kill is a decision somebody made in a turn that is still running. Waking them for it would interrupt that
        // very turn to report their own request back to them.
        var killed = await registry.OpenAsync(Seed(session: "session-2"), default);
        Assert.True((await registry.KillAsync("session-2", killed.Value!.Id, default)).Ok);
        Assert.Equal(new[] { "session-1" }, waker.Woken);
    }

    [Fact]
    public async Task A_wake_that_fails_does_not_undo_the_row()
    {
        // The record is the fact; being told is an attempt. A registry that refused to record an ending because the
        // session could not be reached would be losing the only copy of what happened.
        var registry = new FileJobRegistry();
        registry.Waker = new RecordingWaker(fail: true);
        var opened = await registry.OpenAsync(Seed(), default);

        Assert.True((await registry.CompleteAsync("session-1", opened.Value!.Id, "done", default)).Ok);

        var row = (await registry.GetAsync("session-1", opened.Value.Id, default)).Value!;
        Assert.Equal(JobState.Done, row.State);
        // And it is still unannounced, which is what makes a missed wake-up late rather than lost.
        Assert.False(row.Announced);
        Assert.Single(await registry.TakeUnannouncedAsync("session-1", default));
    }

    [Fact]
    public async Task A_restart_tells_nobody_until_the_session_speaks()
    {
        // Coming back up is not news: nothing finished, and everything that was running is uncertain. Waking every
        // session that had a row would be a host that starts by interrupting people about work it cannot even describe.
        var store = Store();
        var before = new FileJobRegistry(store);
        await before.OpenAsync(Seed(), default);
        store.Dispose();

        using var after = Store();
        var restarted = new FileJobRegistry(after);
        var waker = new RecordingWaker();
        restarted.Waker = waker;

        Assert.Single((await restarted.ListAsync("session-1", default)).Value!);
        Assert.Empty(waker.Woken);
        // It is still there to be read, though: the interruption is the first thing this conversation has to hear.
        Assert.Equal(JobState.Interrupted, Assert.Single(await restarted.TakeUnannouncedAsync("session-1", default)).State);
    }
}
