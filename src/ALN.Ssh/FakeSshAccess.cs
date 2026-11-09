using System.Security.Cryptography;
using System.Text;
using ALN.Contracts;

namespace ALN.Ssh;

/// <summary>
/// A scriptable stand-in for the network: same port, no sockets.
///
/// It exists so the parts that are about <i>decisions</i> — which device is live, what a policy allows, what happens
/// when a host key changes, what the model is told when a key was never installed — can be tested without an sshd, a
/// container, or a clock. The real implementation talks SSH.NET; everything a test wants to be deterministic (the
/// host key it offers, the output of a command, the failure of the next connection) is a field here.
/// </summary>
public sealed class FakeSshAccess : ISshAccess
{
    /// <summary>What this fake server identifies itself as, when a probe or a connection reaches it.</summary>
    public string ServerVersion { get; set; } = "SSH-2.0-OpenSSH_9.6-fake";

    /// <summary>The host key it presents. Changing this between calls is how "the machine is not the one you trusted" is tested.</summary>
    public SshFingerprint HostKey { get; set; } = new("SHA256", "ZmFrZS1ob3N0LWtleS1maW5nZXJwcmludA");

    /// <summary>Set to make the next <see cref="ProbeAsync"/> fail instead of answering.</summary>
    public SshErrorKind? FailProbe { get; set; }

    /// <summary>Set to make the next <see cref="ConnectAsync"/> fail instead of connecting (after the host key is checked).</summary>
    public SshErrorKind? FailConnect { get; set; }

    /// <summary>Answer the next <c>RunAsync</c> calls with this instead of the queued script.</summary>
    public Queue<SshCommandResult> Scripted { get; } = new();

    /// <summary>Every command this fake was asked to run, in order — the assertion surface for "what did the model do".</summary>
    public List<string> Commands { get; } = [];

    /// <summary>What <c>DownloadAsync</c> should answer with. Set per test; the bytes come back unchanged.</summary>
    public byte[]? DownloadPayload { get; set; }

    /// <summary>Connections opened and not disposed; lets a test see that replacing a device really closed the socket.</summary>
    public List<FakeConnection> Open { get; } = [];

    public ValueTask<Result<SshKeyPair>> CreateKeyAsync(string handle, ISecretStore store,
        CancellationToken cancellationToken)
    {
        if (!SecretName.IsValid(handle))
            return ValueTask.FromResult(Result<SshKeyPair>.Failure(new(ErrorCode.Invalid, SecretName.Complain(handle))));

        // Not a real key: a fixed body whose bytes are never parsed. The public line looks like one so that "what the
        // model hands the user" is the same shape a real key would be.
        var body = $"fake-private-{handle}".Select(c => (byte)c).ToArray();
        var stored = store.PutAsync(handle, body, cancellationToken).AsTask().GetAwaiter().GetResult();
        if (!stored.Ok) return ValueTask.FromResult(Result<SshKeyPair>.Failure(stored.Error!));

        var fingerprint = new SshFingerprint("SHA256", Convert.ToBase64String(SHA256.HashData(body))[..43]);
        var publicKey = $"ssh-ed25519 {Convert.ToBase64String(Encoding.UTF8.GetBytes(handle))} aln-{handle}";
        return ValueTask.FromResult(Result<SshKeyPair>.Success(
            new SshKeyPair(publicKey, fingerprint, new SshKeyHandle(handle, fingerprint))));
    }

    public ValueTask<Result<SshProbe>> ProbeAsync(SshTarget target, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (FailProbe is { } kind)
        {
            FailProbe = null;
            return ValueTask.FromResult(Result<SshProbe>.Failure(Complain(kind, target)));
        }
        return ValueTask.FromResult(Result<SshProbe>.Success(new SshProbe(ServerVersion, HostKey)));
    }

    public ValueTask<Result<ISshConnection>> ConnectAsync(SshTarget target, SshKeyHandle key, ISecretStore store,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The order matters and is the point: identity first, credential second.
        if (target.Pinned is { } pinned && pinned != HostKey)
            return ValueTask.FromResult(Result<ISshConnection>.Failure(new(ErrorCode.Denied,
                "Host key does not match the one this device was trusted with.",
                $"expected {pinned}, got {HostKey}")));

        var present = store.ExistsAsync(key.Name, cancellationToken).AsTask().GetAwaiter().GetResult();
        if (!present.Ok || present.Value is not true)
            return ValueTask.FromResult(Result<ISshConnection>.Failure(new(ErrorCode.Denied,
                "No private key under that name.", key.Name)));

        if (FailConnect is { } kind)
        {
            FailConnect = null;
            return ValueTask.FromResult(Result<ISshConnection>.Failure(Complain(kind, target)));
        }

        var connection = new FakeConnection(this, target);
        Open.Add(connection);
        return ValueTask.FromResult(Result<ISshConnection>.Success(connection));
    }

    private static Error Complain(SshErrorKind kind, SshTarget target) => kind switch
    {
        SshErrorKind.Refused => new(ErrorCode.Unavailable, $"Connection refused by {target.Host}:{target.Port}."),
        SshErrorKind.Timeout => new(ErrorCode.Unavailable, $"Timed out connecting to {target.Host}:{target.Port}."),
        SshErrorKind.Auth => new(ErrorCode.Denied,
            "Authentication failed: the public key is not installed on the device (or its permissions are wrong)."),
        SshErrorKind.HostKey => new(ErrorCode.Denied, "Host key mismatch."),
        SshErrorKind.Unreachable => new(ErrorCode.Unavailable, $"{target.Host} is not reachable."),
        _ => new(ErrorCode.Internal, $"Fake failure: {kind}."),
    };

    /// <summary>One open fake connection. Its output comes from <see cref="Scripted"/>, one entry per command.</summary>
    public sealed class FakeConnection(FakeSshAccess owner, SshTarget target) : ISshConnection
    {
        public bool Disposed { get; private set; }

        public List<FakeJob> Jobs { get; } = [];

        public SshFingerprint HostKey => owner.HostKey;

        public string? ServerVersion => owner.ServerVersion;

        public ValueTask<Result<SshCommandResult>> RunAsync(string command, SshLimits limits,
            CancellationToken cancellationToken)
        {
            if (Disposed) return ValueTask.FromResult(Result<SshCommandResult>.Failure(new(ErrorCode.Conflict, "Connection is closed.")));
            owner.Commands.Add(command);
            var result = owner.Scripted.Count > 0
                ? owner.Scripted.Dequeue()
                : new SshCommandResult(0, $"ran: {command}", string.Empty, false, TimeSpan.FromMilliseconds(1));
            return ValueTask.FromResult(Result<SshCommandResult>.Success(result));
        }

        public ValueTask<Result<ISshJob>> StartAsync(string command, SshLimits limits,
            CancellationToken cancellationToken)
        {
            if (Disposed) return ValueTask.FromResult(Result<ISshJob>.Failure(new(ErrorCode.Conflict, "Connection is closed.")));
            owner.Commands.Add(command);
            var job = new FakeJob($"{target.Host}-{Jobs.Count + 1}", command);
            Jobs.Add(job);
            return ValueTask.FromResult(Result<ISshJob>.Success(job));
        }

        public async ValueTask<Result> UploadAsync(Stream content, string remotePath,
            CancellationToken cancellationToken)
        {
            if (Disposed) return Result.Failure(new(ErrorCode.Conflict, "Connection is closed."));
            // Bytes, not text: a fake that decoded UTF-8 would hide exactly the bug this path exists to avoid.
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            Uploads.Add(new(remotePath, buffer.ToArray()));
            return Result.Success();
        }

        /// <summary>What was uploaded, so a test can assert the local side really came from a scoped file.</summary>
        public List<(string Path, byte[] Bytes)> Uploads { get; } = [];

        public ValueTask<Result<Stream>> DownloadAsync(string remotePath, long maxBytes,
            CancellationToken cancellationToken)
        {
            if (Disposed) return ValueTask.FromResult(Result<Stream>.Failure(new(ErrorCode.Conflict, "Connection is closed.")));
            var bytes = owner.DownloadPayload ?? Encoding.UTF8.GetBytes($"fake contents of {remotePath}");
            if (bytes.Length > maxBytes)
                return ValueTask.FromResult(Result<Stream>.Failure(new(ErrorCode.Invalid,
                    $"Remote file is larger than {maxBytes} bytes; refusing to truncate it.")));
            return ValueTask.FromResult(Result<Stream>.Success(new MemoryStream(bytes, writable: false)));
        }

        public ValueTask<Result<ISshTunnel>> ForwardLocalAsync(string remoteHost, int remotePort,
            CancellationToken cancellationToken)
        {
            if (Disposed) return ValueTask.FromResult(Result<ISshTunnel>.Failure(new(ErrorCode.Conflict, "Connection is closed.")));
            var tunnel = new FakeTunnel(remoteHost, remotePort);
            Tunnels.Add(tunnel);
            return ValueTask.FromResult(Result<ISshTunnel>.Success(tunnel));
        }

        public List<FakeTunnel> Tunnels { get; } = [];

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A background command that produces text slowly and can be stopped.</summary>
    public sealed class FakeJob(string id, string command) : ISshJob
    {
        private readonly StringBuilder _output = new();
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;

        public string Command { get; } = command;

        public bool Completed { get; private set; }

        public int? ExitCode { get; private set; }

        /// <summary>Appends what the "remote" process wrote, and optionally finishes it.</summary>
        public void Write(string text, int? exitCode = null)
        {
            _output.Append(text);
            if (exitCode is not null)
            {
                ExitCode = exitCode;
                Completed = true;
                _exit.TrySetResult();
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public SshJobOutput Read(int offset, int maxBytes)
        {
            var all = Encoding.UTF8.GetBytes(_output.ToString());
            var from = Math.Clamp(offset, 0, all.Length);
            var take = Math.Min(Math.Max(maxBytes, 0), all.Length - from);
            return new(Encoding.UTF8.GetString(all, from, take), from, take, Completed, ExitCode);
        }

        public ValueTask<Result> KillAsync(CancellationToken cancellationToken)
        {
            Completed = true;
            ExitCode ??= -1;
            // A killed command has ended too: whoever is waiting for the ending hears about it the same way.
            _exit.TrySetResult();
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A forward that binds nothing: the port it reports is the one it was asked for.</summary>
    public sealed class FakeTunnel(string remoteHost, int remotePort) : ISshTunnel
    {
        public int LocalPort { get; } = 40_000 + Random.Shared.Next(0, 1000);

        public string RemoteHost { get; } = remoteHost;

        public int RemotePort { get; } = remotePort;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
