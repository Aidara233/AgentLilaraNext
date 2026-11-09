using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using ALN.Ssh;

namespace ALN.Tests;

/// <summary>
/// The tools that actually reach the device: run, job, put/get, tunnel. What is pinned here is the part a user notices
/// when it is wrong — a refused command says what is allowed, a failed connection says which kind of failure it was, a
/// background command can be watched and stopped, and a file can only leave through a registered scope.
/// </summary>
public sealed class SshRunTests : IDisposable
{
    private const string Session = "channel.conversation#chat-1";

    private readonly FakeSshAccess _access = new();
    private readonly MemorySecretStore _keys = new();
    private readonly RecordingAudit _audit = new();
    private readonly InMemorySignals _signals = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-ssh-fs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SshTools Tools(SshOptions? options = null, IBinaryFileAccess? files = null, IJobRegistry? jobs = null) =>
        new(_access, _keys, _audit, _signals, options ?? new SshOptions { AllowTunnel = true },
            null, null, null, files, jobs);

    private async Task<SshTools> Ready(SshOptions? options = null, IBinaryFileAccess? files = null,
        string policy = """ "policy":{"mode":"any"} """, IJobRegistry? jobs = null)
    {
        var tools = Tools(options, files, jobs);
        var made = await tools.InitAsync(Session, $$"""{"device":"prod","host":"10.0.0.7","user":"deploy",{{policy}}}""", default);
        Assert.True(made.Ok, made.Error?.Message);
        return tools;
    }

    [Fact]
    public async Task Run_without_a_device_says_what_to_do_first()
    {
        var tools = Tools();
        var ran = await tools.RunAsync(Session, """{"command":"uptime"}""", default);

        Assert.False(ran.Ok);
        Assert.Equal(ErrorCode.Invalid, ran.Error!.Code);
        Assert.Contains("ssh_init", ran.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_cannot_be_uploaded_outside_the_declared_paths()
    {
        // The device's allowlist is about commands; a file write is a different way to end up with a permanent key on
        // somebody's machine, so it needs its own list — and "not listed" means "not allowed".
        var files = new LocalFileSystemAccess(_root, ["workspace"], ["workspace"]);
        var tools = await Ready(
            policy: """ "policy":{"mode":"allowlist","commands":["df"],"paths":["/srv/app"]} """, files: files);
        Assert.True((await files.WriteAsync("workspace", "note.txt", "hello", default)).Ok);

        var refused = await tools.PutAsync(Session,
            """{"scope":"workspace","path":"note.txt","remotePath":"/home/deploy/.ssh/authorized_keys"}""", default);
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Contains("/srv/app", refused.Error.Message, StringComparison.Ordinal);
        Assert.Empty(_access.Open);

        var allowed = await tools.PutAsync(Session,
            """{"scope":"workspace","path":"note.txt","remotePath":"/srv/app/note.txt"}""", default);
        Assert.True(allowed.Ok, allowed.Error?.Message);
    }

    [Fact]
    public async Task An_allowlist_device_without_declared_paths_refuses_file_transfers()
    {
        var files = new LocalFileSystemAccess(_root, ["workspace"], ["workspace"]);
        var tools = await Ready(policy: """ "policy":{"mode":"allowlist","commands":["df"]} """, files: files);

        var refused = await tools.GetAsync(Session,
            """{"remotePath":"/etc/hostname","scope":"workspace","path":"host.txt"}""", default);

        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Contains("未声明", refused.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/srv/app/x", "/srv/app", true)]
    [InlineData("/srv/app", "/srv/app", true)]
    [InlineData("/srv/application/x", "/srv/app", false)]
    [InlineData("/etc/passwd", "/srv/app", false)]
    [InlineData("/srv/app/../.ssh/authorized_keys", "/srv/app", false)]
    [InlineData("relative/path", "/srv/app", false)]
    public void Declared_path_prefixes_match_whole_segments_only(string remotePath, string prefix, bool admitted)
    {
        var policy = new SshCommandPolicy("allowlist", ["df"], [prefix.EndsWith('/') ? prefix : prefix + "/"]);
        Assert.Equal(admitted, policy.AdmitsPath(remotePath));
    }

    [Fact]
    public async Task A_command_outside_the_policy_is_refused_with_the_policy_in_the_answer()
    {
        var tools = await Ready(policy: """ "policy":{"mode":"allowlist","commands":["df","systemctl status"]} """);

        var refused = await tools.RunAsync(Session, """{"command":"rm -rf /"}""", default);

        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Contains("allowlist", refused.Error.Message, StringComparison.Ordinal);
        Assert.Contains("df", refused.Error.Message, StringComparison.Ordinal);
        // Nothing was sent.
        Assert.Empty(_access.Commands);
    }

    [Fact]
    public async Task A_command_that_runs_returns_its_exit_code_and_output()
    {
        var tools = await Ready();
        _access.Scripted.Enqueue(new SshCommandResult(3, "some output", "some error", false, TimeSpan.FromMilliseconds(5)));

        var ran = await tools.RunAsync(Session, """{"command":"systemctl status nginx"}""", default);

        Assert.True(ran.Ok, ran.Error?.Message);
        var text = ran.Value!.Content;
        Assert.Contains("exit=3", text, StringComparison.Ordinal);
        Assert.Contains("some output", text, StringComparison.Ordinal);
        Assert.Contains("some error", text, StringComparison.Ordinal);
        // A non-zero exit is the command's failure, not a transport failure, and the answer says so.
        Assert.Contains("机器是通的", text, StringComparison.Ordinal);
        Assert.Equal(["systemctl status nginx"], _access.Commands);
    }

    [Fact]
    public async Task A_connect_failure_is_reported_as_the_kind_it_is()
    {
        var tools = await Ready();
        _access.FailConnect = SshErrorKind.Auth;

        var refused = await tools.RunAsync(Session, """{"command":"true"}""", default);

        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Contains("public key is not installed", refused.Error.Message, StringComparison.Ordinal);
        // And the failure is remembered, so ssh_hosts can answer "what went wrong" without connecting again.
        Assert.Contains("public key is not installed", tools.Find(Session)!.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_background_command_is_written_into_the_ledger_so_a_restart_can_report_it()
    {
        // The point of the ledger is not bookkeeping for its own sake: it is that a job still running when the host stops
        // is something the next turn can be told about instead of waiting for (docs/28 §9). So the row has to carry the
        // same name the tool uses — two ids for one thing would make "job 3f2a" mean nothing.
        var ledger = new FileJobRegistry();
        var tools = await Ready(jobs: ledger);

        var started = await tools.RunAsync(Session, """{"command":"build.sh","wait":false}""", default);
        Assert.True(started.Ok, started.Error?.Message);
        var job = Assert.Single(_access.Open[0].Jobs);

        var opened = Assert.Single((await ledger.ListAsync(Session, default)).Value!);
        Assert.Equal(job.Id, opened.Id);
        Assert.Equal("ssh.run", opened.Kind);
        Assert.Equal(JobState.Running, opened.State);

        // Looking is one of the ways the ledger learns it ended: the ending is also written when it happens, but the
        // poll has to keep working, because that is what brings a failed write up to date.
        job.Write("done\n", exitCode: 0);
        Assert.True((await tools.JobAsync(Session, """{"action":"list"}""", default)).Ok);
        var finished = Assert.Single((await ledger.ListAsync(Session, default)).Value!);
        Assert.Equal(JobState.Done, finished.State);
        Assert.Contains("exit=0", finished.Result ?? "", StringComparison.Ordinal);

        // And a kill is a decision somebody made, so it is recorded as one — and not announced, because whoever asked
        // for it already knows.
        Assert.True((await tools.RunAsync(Session, """{"command":"long.sh","wait":false}""", default)).Ok);
        var second = _access.Open[0].Jobs[1];
        Assert.True((await tools.JobAsync(Session, $$"""{"action":"kill","id":"{{second.Id}}"}""", default)).Ok);
        var killed = (await ledger.GetAsync(Session, second.Id, default)).Value!;
        Assert.Equal(JobState.Killed, killed.State);
        // What is left to announce is the one that finished while nobody was listening — and *not* the one somebody
        // stopped on purpose.
        var unannounced = await ledger.TakeUnannouncedAsync(Session, default);
        Assert.Contains(unannounced, row => row.Id == job.Id);
        Assert.DoesNotContain(unannounced, row => row.Id == second.Id);
    }

    [Fact]
    public async Task A_background_command_that_ends_is_written_and_announced_without_anybody_asking()
    {
        // Being pulled is enough for a row and not enough for a session: "your build finished" is only worth hearing
        // while it is still a surprise. So the ending is recorded when it happens, by a task nobody waits on — which is
        // also what lets the ledger wake the session that started it (docs/28 §9.4).
        var ledger = new FileJobRegistry();
        var waker = new RecordingWaker();
        ledger.Waker = waker;
        var tools = await Ready(jobs: ledger);

        Assert.True((await tools.RunAsync(Session, """{"command":"build.sh","wait":false}""", default)).Ok);
        var job = Assert.Single(_access.Open[0].Jobs);

        job.Write("done\n", exitCode: 0);
        // The watcher starts by itself; awaiting it here is only so the test does not have to race a background task.
        await tools.WatchAsync(Session, job, default);

        var row = (await ledger.GetAsync(Session, job.Id, default)).Value!;
        Assert.Equal(JobState.Done, row.State);
        Assert.Contains("exit=0", row.Result ?? "", StringComparison.Ordinal);
        // Once, whichever of the two watchers got there first: a row that is already over is not moved again.
        Assert.Equal(new[] { Session }, waker.Woken);
    }

    private sealed class RecordingWaker : ISessionWaker
    {
        public List<string> Woken { get; } = [];

        public ValueTask<Result> WakeAsync(string sessionId, CancellationToken cancellationToken)
        {
            Woken.Add(sessionId);
            return ValueTask.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task Without_a_ledger_a_background_command_still_runs()
    {
        // The work is SSH's; the row is a record of it. A host with no registry keeps running commands and simply forgets
        // them across a restart, which is exactly what it did before the ledger existed.
        var tools = await Ready();

        Assert.True((await tools.RunAsync(Session, """{"command":"deploy.sh","wait":false}""", default)).Ok);
        var listed = await tools.JobAsync(Session, """{"action":"list"}""", default);

        Assert.Contains("deploy.sh", listed.Value!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_background_command_can_be_listed_read_and_killed()
    {
        var tools = await Ready();

        var started = await tools.RunAsync(Session, """{"command":"deploy.sh","wait":false}""", default);
        Assert.True(started.Ok, started.Error?.Message);
        var id = Assert.Single(_access.Open[0].Jobs).Id;
        Assert.Contains(id, started.Value!.Content, StringComparison.Ordinal);

        var job = _access.Open[0].Jobs[0];
        job.Write("step 1\n");

        var listed = await tools.JobAsync(Session, """{"action":"list"}""", default);
        Assert.Contains("running", listed.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("deploy.sh", listed.Value.Content, StringComparison.Ordinal);

        var output = await tools.JobAsync(Session, $$"""{"action":"output","id":"{{id}}"}""", default);
        Assert.True(output.Ok, output.Error?.Message);
        Assert.Contains("step 1", output.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("offset=", output.Value.Content, StringComparison.Ordinal);

        job.Write("step 2\n", exitCode: 0);
        var tail = await tools.JobAsync(Session, $$"""{"action":"output","id":"{{id}}","offset":7}""", default);
        Assert.Contains("step 2", tail.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("结束", tail.Value.Content, StringComparison.Ordinal);

        var killed = await tools.JobAsync(Session, $$"""{"action":"kill","id":"{{id}}"}""", default);
        Assert.True(killed.Ok, killed.Error?.Message);
        Assert.Empty(tools.Find(Session)!.Jobs ?? []);
    }

    [Fact]
    public async Task Jobs_are_capped_so_a_session_cannot_pile_them_up()
    {
        var tools = await Ready(new SshOptions { MaxJobs = 2 });
        Assert.True((await tools.RunAsync(Session, """{"command":"a","wait":false}""", default)).Ok);
        Assert.True((await tools.RunAsync(Session, """{"command":"b","wait":false}""", default)).Ok);

        var refused = await tools.RunAsync(Session, """{"command":"c","wait":false}""", default);

        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Conflict, refused.Error!.Code);
        Assert.Contains("上限 2", refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replacing_the_device_kills_its_jobs_and_closes_its_connection()
    {
        var tools = await Ready();
        Assert.True((await tools.RunAsync(Session, """{"command":"long","wait":false}""", default)).Ok);
        var connection = _access.Open[0];

        Assert.True((await tools.InitAsync(Session, """{"device":"other","host":"10.0.0.8","user":"x"}""", default)).Ok);

        Assert.True(connection.Jobs[0].Completed);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task A_file_leaves_only_through_a_registered_scope()
    {
        var files = new LocalFileSystemAccess(_root, ["workspace"], ["workspace"]);
        var tools = await Ready(files: files);
        Assert.True((await files.WriteAsync("workspace", "note.txt", "hello", default)).Ok);

        var put = await tools.PutAsync(Session,
            """{"scope":"workspace","path":"note.txt","remotePath":"/tmp/note.txt"}""", default);

        Assert.True(put.Ok, put.Error?.Message);
        var uploaded = Assert.Single(_access.Open[0].Uploads);
        Assert.Equal("/tmp/note.txt", uploaded.Path);
        Assert.Equal("hello"u8.ToArray(), uploaded.Bytes);

        // An absolute path, or a scope that was never registered, is refused by the file system — this tool only
        // passes the two arguments along.
        var outside = await tools.PutAsync(Session,
            """{"scope":"/etc","path":"passwd","remotePath":"/tmp/passwd"}""", default);
        Assert.False(outside.Ok);
        Assert.Equal(ErrorCode.Denied, outside.Error!.Code);
    }

    [Fact]
    public async Task A_downloaded_file_lands_in_the_scope()
    {
        var files = new LocalFileSystemAccess(_root, ["workspace"], ["workspace"]);
        var tools = await Ready(files: files);

        var get = await tools.GetAsync(Session,
            """{"remotePath":"/etc/hostname","scope":"workspace","path":"host.txt"}""", default);

        Assert.True(get.Ok, get.Error?.Message);
        var written = await files.ReadAsync("workspace", "host.txt", default);
        Assert.Equal("fake contents of /etc/hostname", written.Value!.Text);
    }

    [Fact]
    public async Task A_binary_file_survives_both_directions_unchanged()
    {
        // The reason the transfer path is bytes and not text: a tarball, a PNG, a compiled binary. NUL bytes and invalid
        // UTF-8 are the shape of "not text", and they have to come out the other side identical.
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        for (var index = 0; index < payload.Length; index += 7) payload[index] = 0;

        var files = new LocalFileSystemAccess(_root, ["workspace"], ["workspace"]);
        var tools = await Ready(files: files);

        // Down: remote → scope.
        _access.DownloadPayload = payload;
        var get = await tools.GetAsync(Session,
            """{"remotePath":"/srv/app/artifact.bin","scope":"workspace","path":"artifact.bin"}""", default);
        Assert.True(get.Ok, get.Error?.Message);
        var saved = Path.Combine(_root, "workspace", "artifact.bin");
        Assert.Equal(payload, await File.ReadAllBytesAsync(saved));

        // Up: scope → remote, from the file that just landed.
        var put = await tools.PutAsync(Session,
            """{"scope":"workspace","path":"artifact.bin","remotePath":"/srv/app/copy.bin"}""", default);
        Assert.True(put.Ok, put.Error?.Message);
        Assert.Equal(payload, Assert.Single(_access.Open[0].Uploads).Bytes);
    }

    [Fact]
    public async Task A_scope_that_does_not_take_bytes_refuses_transfers()
    {
        // Text rooms stay text rooms: the memory library is not a place to park a tarball.
        var files = new LocalFileSystemAccess(_root, ["workspace", "memory"], ["workspace"]);
        Assert.True((await files.WriteAsync("memory", "block.md", "notes", default)).Ok);
        var tools = await Ready(files: files);

        var refused = await tools.PutAsync(Session,
            """{"scope":"memory","path":"block.md","remotePath":"/srv/app/block.md"}""", default);

        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Contains("does not allow binary", refused.Error.Message, StringComparison.Ordinal);
        Assert.Contains("workspace", refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_larger_than_the_limit_is_refused_rather_than_truncated()
    {
        var files = new LocalFileSystemAccess(_root, ["workspace"], ["workspace"]);
        var tools = await Ready(options: new SshOptions { MaxTransferBytes = 1024, AllowTunnel = true }, files: files);
        _access.DownloadPayload = new byte[4096];

        var refused = await tools.GetAsync(Session,
            """{"remotePath":"/srv/app/big.bin","scope":"workspace","path":"big.bin"}""", default);

        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);
        Assert.False(File.Exists(Path.Combine(_root, "workspace", "big.bin")));
    }

    [Fact]
    public async Task Every_command_leaves_an_audit_record_with_the_command_but_not_its_output()
    {
        var tools = await Ready();
        _access.Scripted.Enqueue(new SshCommandResult(0, "SECRET-IN-OUTPUT", string.Empty, false, TimeSpan.Zero));

        Assert.True((await tools.RunAsync(Session, """{"command":"cat /etc/app.conf"}""", default)).Ok);

        var record = Assert.Single(_audit.Records, entry => entry.Action == "ssh.exec");
        Assert.Equal("session:" + Session, record.Actor);
        Assert.Contains("cat /etc/app.conf", record.Detail!, StringComparison.Ordinal);
        // The output is where secrets live: the audit says how much there was, never what it said.
        Assert.DoesNotContain("SECRET-IN-OUTPUT", record.Detail!, StringComparison.Ordinal);

        var signal = Assert.Single(_signals.Snapshot(), entry => entry.Mechanism == "ssh.exec");
        Assert.Equal("prod", signal.Name);
        Assert.DoesNotContain("SECRET-IN-OUTPUT", signal.Detail?["detail"] ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tunnel_is_only_offered_when_the_module_allows_it()
    {
        var off = Tools(new SshOptions { AllowTunnel = false });
        var offers = await off.ToolsAsync(new RoundContextRequest(Session, Guid.NewGuid(), 1, Level: AccessLevel.Full), default);
        Assert.DoesNotContain(offers.Value!, offer => offer.Tool.Name == SshTools.TunnelToolName);

        var on = Tools(new SshOptions { AllowTunnel = true });
        var offered = await on.ToolsAsync(new RoundContextRequest(Session, Guid.NewGuid(), 1, Level: AccessLevel.Operator), default);
        var tunnel = Assert.Single(offered.Value!, offer => offer.Tool.Name == SshTools.TunnelToolName);
        Assert.Equal(AccessLevel.Operator, tunnel.MinLevel);

        // At agent level it is not even in the list, so the model cannot spend a round discovering that.
        var shallow = await on.ToolsAsync(new RoundContextRequest(Session, Guid.NewGuid(), 1, Level: AccessLevel.Agent), default);
        Assert.DoesNotContain(shallow.Value!, offer => offer.Tool.Name == SshTools.TunnelToolName);
    }

    [Fact]
    public async Task A_tunnel_reports_the_loopback_port_it_was_given()
    {
        var tools = await Ready(new SshOptions { AllowTunnel = true });
        var opened = await tools.TunnelAsync(Session, """{"remoteHost":"127.0.0.1","remotePort":5432}""",
            AccessLevel.Operator, default);

        Assert.True(opened.Ok, opened.Error?.Message);
        Assert.Contains("127.0.0.1:", opened.Value!.Content, StringComparison.Ordinal);
        var tunnel = Assert.Single(tools.Find(Session)!.Tunnels!);
        Assert.Equal(5432, tunnel.RemotePort);
    }

    /// <summary>Audit sink that keeps what it was given, so a test can read the record a command left behind.</summary>
    private sealed class RecordingAudit : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];

        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Signal log that keeps everything at every level, so "was a signal written" is answerable.</summary>
    private sealed class InMemorySignals : ISignalLog
    {
        private readonly List<SignalRecord> _records = [];

        public SignalLevel MinLevel => SignalLevel.Debug;

        public bool IsEnabled(SignalLevel level) => true;

        public void Write(SignalRecord record) => _records.Add(record);

        public IReadOnlyList<SignalRecord> Snapshot() => _records;
    }
}
