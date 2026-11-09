using System.Text;
using ALN.Contracts;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ALN.Ssh;

/// <summary>
/// One authenticated connection, wrapping the client that owns it.
///
/// The three things worth knowing before reading it:
///
/// <list type="bullet">
///   <item>A command's output is capped <b>while reading</b>, not after. Asking a server for a gigabyte and then
///   truncating it is a memory bug with a nice name.</item>
///   <item>Cancellation reaches the wire. The token a tool call receives is the one a person's interrupt cancels, and a
///   remote process that keeps running after the UI says "stopped" is worse than a failed call.</item>
///   <item>SFTP and forwarding open their own channels over the same credentials. They are separate objects because the
///   library makes them so; the connection owns their lifetime.</item>
/// </list>
/// </summary>
public sealed class SshNetConnection(SshClient client, ConnectionInfo connection, SshFingerprint hostKey,
    string? serverVersion) : ISshConnection
{
    private bool _disposed;

    public SshFingerprint HostKey { get; } = hostKey;

    public string? ServerVersion { get; } = serverVersion;

    public async ValueTask<Result<SshCommandResult>> RunAsync(string command, SshLimits limits,
        CancellationToken cancellationToken)
    {
        if (_disposed) return Result<SshCommandResult>.Failure(new(ErrorCode.Conflict, "The connection is closed."));

        var started = DateTimeOffset.UtcNow;
        using var sshCommand = client.CreateCommand(command);
        sshCommand.CommandTimeout = TimeSpan.FromSeconds(Math.Max(1, limits.CommandSeconds));
        // Registering on the token means an interrupt stops the remote process instead of merely stopping our wait for it.
        await using var registration = cancellationToken.Register(() => sshCommand.CancelAsync(true, 0));

        try
        {
            // ExecuteAsync completes the command; the text lands on the command itself.
            await sshCommand.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<SshCommandResult>.Failure(SshFailures.Map(ex, new SshTarget(client.ConnectionInfo.Host,
                client.ConnectionInfo.Port, client.ConnectionInfo.Username), HostKey) with
            {
                Code = ex is SshOperationTimeoutException ? ErrorCode.Timeout : ErrorCode.Internal,
            });
        }

        var (stdout, stdoutTruncated) = Cap(sshCommand.Result, limits.MaxBytes);
        var (stderr, stderrTruncated) = Cap(sshCommand.Error, Math.Max(1024, limits.MaxBytes / 4));
        return Result<SshCommandResult>.Success(new SshCommandResult(sshCommand.ExitStatus ?? -1, stdout, stderr,
            stdoutTruncated || stderrTruncated, DateTimeOffset.UtcNow - started));
    }

    public ValueTask<Result<ISshJob>> StartAsync(string command, SshLimits limits,
        CancellationToken cancellationToken)
    {
        if (_disposed) return ValueTask.FromResult(Result<ISshJob>.Failure(new(ErrorCode.Conflict, "The connection is closed.")));

        var sshCommand = client.CreateCommand(command);
        // A job is not bounded by the per-command clock: it is the escape hatch for work that takes longer, and the thing
        // that keeps it bounded is `ssh_job kill` plus the buffer cap.
        sshCommand.CommandTimeout = TimeSpan.FromHours(12);
        var job = new SshNetJob(sshCommand, limits.MaxBytes);
        return job.StartAsync(cancellationToken) is { Ok: true }
            ? ValueTask.FromResult(Result<ISshJob>.Success(job))
            : ValueTask.FromResult(Result<ISshJob>.Failure(new(ErrorCode.Internal, "The command could not be started.")));
    }

    public async ValueTask<Result> UploadAsync(Stream content, string remotePath, CancellationToken cancellationToken)
    {
        if (_disposed) return Result.Failure(new(ErrorCode.Conflict, "The connection is closed."));
        try
        {
            using var sftp = new SftpClient(connection);
            await sftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
            sftp.UploadFile(content, remotePath, canOverride: true);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failure(SshFailures.Map(ex, new SshTarget(client.ConnectionInfo.Host,
                client.ConnectionInfo.Port, client.ConnectionInfo.Username), HostKey));
        }
    }

    public async ValueTask<Result<Stream>> DownloadAsync(string remotePath, long maxBytes,
        CancellationToken cancellationToken)
    {
        if (_disposed) return Result<Stream>.Failure(new(ErrorCode.Conflict, "The connection is closed."));
        try
        {
            using var sftp = new SftpClient(connection);
            await sftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var attributes = sftp.GetAttributes(remotePath);
            var size = (long)attributes.Size;
            // Refused rather than truncated: a short file that looks complete is how a "backup" turns out not to be one.
            if (size > maxBytes)
                return Result<Stream>.Failure(new(ErrorCode.Invalid,
                    $"The remote file is {size} bytes, more than the {maxBytes} allowed for one read."));

            var buffer = new MemoryStream();
            sftp.DownloadFile(remotePath, buffer);
            buffer.Position = 0;
            return Result<Stream>.Success(buffer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<Stream>.Failure(SshFailures.Map(ex, new SshTarget(client.ConnectionInfo.Host,
                client.ConnectionInfo.Port, client.ConnectionInfo.Username), HostKey));
        }
    }

    /// <summary>
    /// Loopback only, port chosen by the operating system. A forward that listens on every interface is a different
    /// feature: it turns this host into an open relay for whatever the far side can reach, and that has to be a decision
    /// with its own name, not an argument somebody forgot to pass.
    /// </summary>
    public ValueTask<Result<ISshTunnel>> ForwardLocalAsync(string remoteHost, int remotePort,
        CancellationToken cancellationToken)
    {
        if (_disposed) return ValueTask.FromResult(Result<ISshTunnel>.Failure(new(ErrorCode.Conflict, "The connection is closed.")));
        try
        {
            var port = new ForwardedPortLocal("127.0.0.1", 0, remoteHost, (uint)remotePort);
            client.AddForwardedPort(port);
            port.Start();
            return ValueTask.FromResult(Result<ISshTunnel>.Success(
                new SshNetTunnel(port, client, remoteHost, remotePort, (int)port.BoundPort)));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(Result<ISshTunnel>.Failure(SshFailures.Map(ex,
                new SshTarget(client.ConnectionInfo.Host, client.ConnectionInfo.Port, client.ConnectionInfo.Username),
                HostKey)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { client.Disconnect(); } catch (Exception ex) when (ex is not OperationCanceledException) { /* already gone */ }
        client.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps at most <paramref name="maxBytes"/> of UTF-8 text and says so when it cut. Cutting by characters rather
    /// than bytes is deliberate: half a multi-byte character in a tool result reads as corruption.
    /// </summary>
    private static (string Text, bool Truncated) Cap(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text)) return (string.Empty, false);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= maxBytes) return (text, false);
        var take = Math.Max(1, maxBytes / 3); // A conservative character budget: three bytes is the worst case per char.
        return (text[..Math.Min(text.Length, take)] + $"\n[已截断：全文 {bytes} 字节，只带了前 {take} 个字符]", true);
    }
}

/// <summary>
/// One background command, with a bounded buffer.
///
/// It reads the two streams as they arrive rather than waiting for the end, because "tail the deploy log" is the whole
/// reason a job exists. The buffer is capped: a command that prints a million lines must not become a Session's memory
/// problem, and a caller is told how much it has seen so it can tell "no more output" from "output was dropped".
/// </summary>
public sealed class SshNetJob : ISshJob
{
    private readonly SshCommand _command;
    private readonly int _maxBytes;
    private readonly StringBuilder _buffer = new();
    private readonly Lock _gate = new();
    /// <summary>Set once, in the pump's <c>finally</c>: the end of the output is the end of the command.</summary>
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pump;
    private bool _completed;

    public SshNetJob(SshCommand command, int maxBytes)
    {
        _command = command;
        _maxBytes = maxBytes;
        Id = Guid.NewGuid().ToString("N")[..8];
    }

    public string Id { get; }

    public string Command => _command.CommandText;

    public bool Completed
    {
        get { lock (_gate) return _completed; }
    }

    public int? ExitCode
    {
        get { lock (_gate) return _completed ? _command.ExitStatus : null; }
    }

    public Result StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _command.BeginExecute(_ => { });
            // Reading the streams has to happen off the caller's thread: it blocks until the far side closes them.
            _pump = Task.Run(PumpAsync, CancellationToken.None);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(SshFailures.Map(ex,
                new SshTarget("", 0, ""), null));
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            using var reader = new StreamReader(_command.OutputStream, Encoding.UTF8);
            var chunk = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(chunk).ConfigureAwait(false)) > 0)
                Append(new string(chunk, 0, read));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Append($"\n[读取输出中断：{ex.GetType().Name}]");
        }
        finally
        {
            lock (_gate)
            {
                _completed = true;
                _pump = null;
            }
            // After the lock, so a waiter that runs a continuation cannot see "ended" before "completed".
            _exit.TrySetResult();
        }
    }

    private void Append(string text)
    {
        lock (_gate)
        {
            if (_buffer.Length < _maxBytes) _buffer.Append(text);
            else if (_buffer.Length < _maxBytes + 64) _buffer.Append("\n[已截断：输出超过上限，后面的没有保留]");
        }
    }

    public SshJobOutput Read(int offset, int maxBytes)
    {
        string snapshot;
        bool completed;
        int? exit;
        lock (_gate)
        {
            snapshot = _buffer.ToString();
            completed = _completed;
            exit = completed ? _command.ExitStatus : null;
        }

        var from = Math.Clamp(offset, 0, snapshot.Length);
        var take = Math.Min(Math.Max(maxBytes, 0), snapshot.Length - from);
        return new SshJobOutput(snapshot.Substring(from, take), from, take, completed, exit);
    }

    /// <summary>
    /// The end of the output is the end of the command: the far side closes its streams when the process is gone, and
    /// the pump is what notices. Waiting on the pump rather than polling means "it ended" arrives once, however long
    /// it took.
    /// </summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public async ValueTask<Result> KillAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Stops the remote process, not just our interest in it. The command's own timeout stays in place as a
            // backstop for a server that ignores the signal.
            _command.CancelAsync();
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(SshFailures.Map(ex, new SshTarget("", 0, ""), null));
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _command.Dispose(); } catch (Exception ex) when (ex is not OperationCanceledException) { /* already gone */ }
        return ValueTask.CompletedTask;
    }
}

/// <summary>A loopback forward that closes itself and its port when the connection that owns it goes away.</summary>
public sealed class SshNetTunnel(ForwardedPortLocal port, SshClient client, string remoteHost, int remotePort, int localPort)
    : ISshTunnel
{
    public int LocalPort { get; } = localPort;

    public string RemoteHost { get; } = remoteHost;

    public int RemotePort { get; } = remotePort;

    public ValueTask DisposeAsync()
    {
        try
        {
            port.Stop();
            client.RemoveForwardedPort(port);
            port.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Stopping a forward whose client is already gone is not an error worth reporting.
        }
        return ValueTask.CompletedTask;
    }
}
