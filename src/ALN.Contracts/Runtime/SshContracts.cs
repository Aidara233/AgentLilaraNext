namespace ALN.Contracts;

/// <summary>
/// A host key, as the two halves that matter: the algorithm it was made with and the base64 the operator compares.
///
/// It is the one thing in this surface that is both a secret and not a secret: printable, comparable on two machines,
/// and worthless to an attacker — which is exactly why the whole trust story here is built out of it.
/// </summary>
public sealed record SshFingerprint(string Algorithm, string Value)
{
    public override string ToString() => $"{Algorithm}:{Value}";
}

/// <summary>Where a command would go. No credential is in here: a target is configuration, a key is a reference.</summary>
/// <param name="Pinned">
/// The host key this device is expected to present, when somebody said so up front. Null means "take what it offers
/// the first time and refuse anything else afterwards" — see <see cref="ISshAccess.ConnectAsync"/>.
/// </param>
public sealed record SshTarget(string Host, int Port, string User, SshFingerprint? Pinned = null);

/// <summary>
/// What one connection attempt saw. Both fields are available before authentication: an SSH server identifies itself
/// and offers its host key first, and only then asks who you are — so "wrong key" and "wrong password" are told apart
/// from "nothing is listening" without sending a credential anywhere.
/// </summary>
/// <param name="HostKey">Null only when the peer never got that far.</param>
public sealed record SshProbe(string? ServerVersion, SshFingerprint? HostKey);

/// <summary>How a command's outcome is reported, in the five kinds a caller can act on differently.</summary>
/// <remarks>
/// These are separate on purpose and not folded into one "failed": the only health check this surface has is running
/// something, so the answer to a failed run is the whole diagnosis. <see cref="Refused"/> and <see cref="Timeout"/> are
/// the network, <see cref="Auth"/> is "the key is not installed yet", <see cref="HostKey"/> is "this is not the machine
/// you were talking to last time, stop", and <see cref="Command"/> is "it worked, the command failed".
/// </remarks>
public enum SshErrorKind
{
    None = 0,
    Refused,
    Timeout,
    Unreachable,
    Auth,
    HostKey,
    Command,
    /// <summary>Nothing to run on: no configuration, or one that cannot be used for this call.</summary>
    Unusable,
}

/// <summary>What one command produced. <see cref="Truncated"/> is never inferred from the length: it is set by whoever
/// applied the limit, so a caller cannot mistake a short answer for a complete one.</summary>
public sealed record SshCommandResult(int ExitCode, string StandardOutput, string StandardError, bool Truncated,
    TimeSpan Duration);

/// <summary>Per-call limits. Passed in rather than configured in the port: they are the module's policy, not transport.</summary>
public sealed record SshLimits(int MaxBytes = 64 * 1024, int CommandSeconds = 30);

/// <summary>
/// A generated key pair, as the caller is allowed to see it: the public line (safe to paste anywhere) and a handle that
/// names where the private half lives. There is no property carrying the private key, and no method returning it as
/// text — a shape that cannot express "hand me the secret" is one less way to leak it.
/// </summary>
public sealed record SshKeyPair(string PublicKey, SshFingerprint Fingerprint, SshKeyHandle Handle);

/// <summary>
/// The name of a private key inside a <see cref="ISecretStore"/>, plus what it fingerprints to. A reference, never
/// material: it travels through configuration files and state stores, and none of those should ever hold a key.
/// </summary>
public sealed record SshKeyHandle(string Name, SshFingerprint Fingerprint);

/// <summary>
/// A key's bytes as a stream, or nothing at all.
///
/// The wrapper is not ceremony: <c>Result&lt;T&gt;.Ok</c> means "there is a value", and "no key is stored under that
/// name" is a successful answer, not a failure. Wrapping it is the only way to say that without inventing an error
/// code for a normal state — and an error code for "absent" would end up being caught and ignored by exactly the
/// callers that should not be ignoring it.
/// </summary>
public sealed record SecretContent(Stream? Content);

/// <summary>
/// Where private keys live, as an interface with no way to read one back as text.
///
/// The omissions are the design. <see cref="OpenAsync"/> hands out a stream so the bytes go straight into the SSH
/// client and can be dropped; there is no <c>GetString</c>, no <c>ReadAllBytes</c> returning <c>string</c>, nothing
/// that turns a key into something a log line could accidentally contain. Callers that "need to check" a private key
/// instead get <see cref="SshKeyPair.Fingerprint"/>, which is the only question anyone actually has.
///
/// Two implementations exist for the two channels, and they are not interchangeable: keys belonging to a Session's
/// live configuration stay in memory (<see cref="MemorySecretStore"/>-shaped), keys that were saved to disk live on
/// disk. Moving one to the other is <c>ssh_save</c>, an operator's decision.
/// </summary>
public interface ISecretStore
{
    /// <summary>Writes (or replaces) one secret. Nothing about the value is returned, logged or remembered.</summary>
    ValueTask<Result> PutAsync(string name, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken);

    /// <summary>Content, or <c>null</c> content when nothing is stored under that name — distinct from an empty stream.</summary>
    ValueTask<Result<SecretContent>> OpenAsync(string name, CancellationToken cancellationToken);

    ValueTask<Result<bool>> ExistsAsync(string name, CancellationToken cancellationToken);

    ValueTask<Result> DeleteAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// The only thing in this process that speaks SSH, and therefore the only thing that needs a network stack.
///
/// It is a port for the same reason <see cref="IFileSystemAccess"/> is one: "which machines this can be pointed at" is
/// a decision that belongs to configuration and to the call site, not to a library quietly holding a socket. Nothing
/// here takes an absolute path, and nothing returns key material.
///
/// The first connection to a device is the one moment a human is unavoidably involved: this surface generates the key
/// and reports the host key it saw, but only a person on the other machine can install the public half. Everything
/// else — probe, authenticate, run, copy, forward — is reachable without asking again.
/// </summary>
public interface ISshAccess
{
    /// <summary>
    /// Generates an ed25519 pair and puts the private half in <paramref name="store"/> under <paramref name="handle"/>,
    /// returning the public line to hand to whoever owns the device.
    /// </summary>
    ValueTask<Result<SshKeyPair>> CreateKeyAsync(string handle, ISecretStore store, CancellationToken cancellationToken);

    /// <summary>
    /// Connects far enough to read the server's identity and host key, then hangs up without authenticating. Bounded by
    /// <paramref name="timeout"/>: this is the smallest egress this surface has, and it must not be able to hang.
    /// </summary>
    ValueTask<Result<SshProbe>> ProbeAsync(SshTarget target, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Authenticates and returns a usable connection. The host key is checked against <see cref="SshTarget.Pinned"/>
    /// before any credential leaves the process, and a mismatch is <see cref="SshErrorKind.HostKey"/> with both
    /// fingerprints in the detail — a caller must be able to tell "reinstalled" from "somebody in the middle".
    /// </summary>
    ValueTask<Result<ISshConnection>> ConnectAsync(SshTarget target, SshKeyHandle key, ISecretStore store,
        TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// One authenticated connection. Owned by whoever opened it — a Session's live configuration — and disposed when that
/// configuration goes away, so "the key is gone" and "the socket is gone" happen in that order.
/// </summary>
public interface ISshConnection : IAsyncDisposable
{
    SshFingerprint HostKey { get; }

    string? ServerVersion { get; }

    /// <summary>Runs one command to completion, bounded by the limits the caller passed in.</summary>
    ValueTask<Result<SshCommandResult>> RunAsync(string command, SshLimits limits, CancellationToken cancellationToken);

    /// <summary>
    /// Starts one command and returns immediately. The job keeps running when the turn that started it ends, which is
    /// the point: a deploy takes longer than one round, and a Session must be able to come back and look.
    /// </summary>
    ValueTask<Result<ISshJob>> StartAsync(string command, SshLimits limits, CancellationToken cancellationToken);

    /// <summary>Copies a local stream to a remote path. The caller decides what "local" means (a scoped file).</summary>
    ValueTask<Result> UploadAsync(Stream content, string remotePath, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a remote path for reading. The stream <b>is</b> the transfer: reading it is what moves bytes, and the
    /// caller disposes it whether or not it read to the end.
    ///
    /// <paramref name="maxBytes"/> is a ceiling the caller asks for, not one this interface needs: a positive value
    /// refuses a larger file rather than truncating it (a silently short file is indistinguishable from a complete one
    /// once it is on disk), and zero or less means no ceiling at all. The number is the caller's because the right one
    /// belongs to what the bytes are for — an artifact moving to a workspace has none worth naming, while a platform
    /// handing over an attachment has whatever its own protocol allows.
    /// </summary>
    ValueTask<Result<Stream>> DownloadAsync(string remotePath, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Forwards a loopback port to somewhere the far side can reach. Only <c>127.0.0.1</c> is ever bound: a forward
    /// that listens on all interfaces turns this host into an open relay, which is a different feature with a
    /// different threat model.
    /// </summary>
    ValueTask<Result<ISshTunnel>> ForwardLocalAsync(string remoteHost, int remotePort, CancellationToken cancellationToken);
}

/// <summary>
/// One background command. Output is a bounded buffer, not a promise of completeness: the caller is told how much it
/// has received and where the truncation began.
/// </summary>
public interface ISshJob : IAsyncDisposable
{
    string Id { get; }

    string Command { get; }

    bool Completed { get; }

    int? ExitCode { get; }

    /// <summary>Output so far, at most <paramref name="maxBytes"/>, read from <paramref name="offset"/>.</summary>
    SshJobOutput Read(int offset, int maxBytes);

    /// <summary>
    /// Completes when the command has ended — finished, failed or killed — and never blocks it.
    ///
    /// The alternative is asking <see cref="Completed"/> on a timer, which turns one fact into a question somebody has
    /// to keep asking. A caller that wants to record the ending (the ledger) or be woken by it needs the fact once, so
    /// this is the shape it takes. Cancelling the wait does not touch the command: it stops waiting, not watching.
    /// </summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Stops the remote command. Not optional: a job nobody can stop is a job that outlives the Session.</summary>
    ValueTask<Result> KillAsync(CancellationToken cancellationToken);
}

/// <summary>What a job has produced so far.</summary>
/// <param name="Offset">Where this chunk starts; pass <c>Offset + Length</c> back in to continue.</param>
public sealed record SshJobOutput(string Text, int Offset, int Length, bool Completed, int? ExitCode);

/// <summary>One loopback forward, alive until disposed.</summary>
public interface ISshTunnel : IAsyncDisposable
{
    /// <summary>The port actually bound. Chosen by the operating system, reported back — never guessed by the caller.</summary>
    int LocalPort { get; }

    string RemoteHost { get; }

    int RemotePort { get; }
}
