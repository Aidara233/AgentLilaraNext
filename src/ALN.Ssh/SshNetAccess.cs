using System.Net.Sockets;
using System.Text;
using ALN.Contracts;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ALN.Ssh;

/// <summary>
/// The real transport: the one place in this process that opens a socket to somebody else's machine.
///
/// Everything above it talks to <see cref="ISshAccess"/>, so this file is also the only place that knows what SSH.NET
/// is. Three rules shape it:
///
/// <list type="number">
///   <item>The host key is checked <b>before</b> any key material is offered. That is what makes "pinned" mean something:
///   a device that is not the one you trusted never completes the exchange, so no authentication is attempted.</item>
///   <item>A failure is classified, not just reported. <c>Refused</c>, <c>Timeout</c>, <c>Auth</c>, <c>HostKey</c> and
///   <c>Command</c> lead to five different actions, and a caller cannot tell them apart from "failed".</item>
///   <item>No exception text escapes unexamined. Library messages can contain paths and, for password-shaped failures,
///   what was tried; the <see cref="Error.Detail"/> here carries only the exception type and the target, never the
///   message.</item>
/// </list>
/// </summary>
public sealed class SshNetAccess : ISshAccess
{
    public ValueTask<Result<SshKeyPair>> CreateKeyAsync(string handle, ISecretStore store,
        CancellationToken cancellationToken)
    {
        if (!SecretName.IsValid(handle))
            return ValueTask.FromResult(Result<SshKeyPair>.Failure(new(ErrorCode.Invalid, SecretName.Complain(handle))));

        var generated = SshKeyFactory.Create(handle);
        return PutAsync(handle, generated, store, cancellationToken);
    }

    private static async ValueTask<Result<SshKeyPair>> PutAsync(string handle, SshKeyFactory.GeneratedKey generated,
        ISecretStore store, CancellationToken cancellationToken)
    {
        var stored = await store.PutAsync(handle, generated.PrivateKeyPem, cancellationToken).ConfigureAwait(false);
        if (!stored.Ok) return Result<SshKeyPair>.Failure(stored.Error!);
        return Result<SshKeyPair>.Success(new SshKeyPair(generated.PublicKey, generated.Fingerprint,
            new SshKeyHandle(handle, generated.Fingerprint)));
    }

    /// <summary>
    /// Asks the device who it is, without saying who we are: a connection with no authentication method does the key
    /// exchange, gets the host key, and is then refused by the server — which is exactly the information needed, and
    /// nothing more. A refused authentication here is a <b>successful</b> probe.
    /// </summary>
    public async ValueTask<Result<SshProbe>> ProbeAsync(SshTarget target, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        SshFingerprint? seen = null;
        var connection = new ConnectionInfo(target.Host, target.Port, target.User,
            new NoneAuthenticationMethod(target.User))
        {
            Timeout = timeout,
        };

        using var client = new SshClient(connection);
        client.HostKeyReceived += (_, args) =>
        {
            seen = SshKeyFactory.Fingerprint(args.HostKey);
            args.CanTrust = true; // A probe reports; it does not decide. Deciding happens where a pin exists.
        };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (SshAuthenticationException)
        {
            // Expected: we offered no credential. The exchange already happened, so the probe has what it came for.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<SshProbe>.Failure(SshFailures.Map(ex, target, seen));
        }

        if (seen is null)
            return Result<SshProbe>.Failure(new(ErrorCode.Unavailable,
                $"{target.Host} answered but offered no host key."));

        return Result<SshProbe>.Success(new SshProbe(SafeServerVersion(client), seen));
    }

    public async ValueTask<Result<ISshConnection>> ConnectAsync(SshTarget target, SshKeyHandle key,
        ISecretStore store, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var opened = await store.OpenAsync(key.Name, cancellationToken).ConfigureAwait(false);
        if (!opened.Ok) return Result<ISshConnection>.Failure(opened.Error!);
        if (opened.Value!.Content is null)
            return Result<ISshConnection>.Failure(new(ErrorCode.Denied,
                "The private key for this device is gone (it may have expired). Initialise the device again.",
                key.Name));

        PrivateKeyFile privateKey;
        try
        {
            await using var content = opened.Value.Content;
            privateKey = new PrivateKeyFile(content);
        }
        catch (Exception ex)
        {
            // Deliberately no message from the library: a parser complaint can quote the key's text.
            return Result<ISshConnection>.Failure(new(ErrorCode.Internal,
                "The stored private key could not be read.", ex.GetType().Name));
        }

        SshFingerprint? seen = null;
        var mismatch = false;
        var connection = new ConnectionInfo(target.Host, target.Port, target.User,
            new PrivateKeyAuthenticationMethod(target.User, privateKey))
        {
            Timeout = timeout,
        };

        var client = new SshClient(connection);
        client.HostKeyReceived += (_, args) =>
        {
            seen = SshKeyFactory.Fingerprint(args.HostKey);
            // The pin is checked here, before authentication: a machine that is not the one this device was trusted with
            // never sees a credential. That is the entire value of pinning.
            mismatch = target.Pinned is not null && seen != target.Pinned;
            args.CanTrust = !mismatch;
        };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (mismatch)
                return Result<ISshConnection>.Failure(new(ErrorCode.Denied,
                    "This is not the machine you were talking to before. Nothing was sent to it.",
                    $"expected {target.Pinned}, got {seen}"));
            return Result<ISshConnection>.Failure(SshFailures.Map(ex, target, seen));
        }

        return Result<ISshConnection>.Success(new SshNetConnection(client, connection, seen!, SafeServerVersion(client)));
    }

    /// <summary>
    /// The version banner, which sshd hands out before authentication. Kept because "what is on the other side" is the
    /// first question every diagnosis needs, and it is not a secret.
    /// </summary>
    private static string? SafeServerVersion(SshClient client)
    {
        try { return client.ConnectionInfo.ServerVersion; }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return null; }
    }
}

/// <summary>
/// Turns a library exception into the five kinds a caller can act on, and keeps the message out of it.
///
/// The classification is the whole point: "connection refused" is a machine with no sshd, a timeout is a firewall, an
/// authentication failure is a key that was never installed, and a host-key mismatch is somebody in the middle. A caller
/// that reads one "failed" cannot tell a user anything useful about any of them.
/// </summary>
public static class SshFailures
{
    public static Error Map(Exception ex, SshTarget target, SshFingerprint? seen)
    {
        var where = $"{target.Host}:{target.Port}";
        return ex switch
        {
            SshAuthenticationException => new(ErrorCode.Denied,
                "Authentication failed: the public key is not installed for this user, or its permissions are wrong.",
                $"{where} ({ex.GetType().Name})"),

            SshOperationTimeoutException => new(ErrorCode.Unavailable,
                $"Timed out waiting for {where}.",
                ex.GetType().Name),

            // A cancelled wait that nobody asked to cancel is a timeout: the deadline this call set fired, and saying
            // "internal error" would send whoever reads it looking in the wrong place.
            OperationCanceledException or TaskCanceledException => new(ErrorCode.Unavailable,
                $"Timed out waiting for {where}.", ex.GetType().Name),

            SshConnectionException when seen is not null && target.Pinned is not null && seen != target.Pinned =>
                new(ErrorCode.Denied, "This is not the machine you were talking to before.",
                    $"expected {target.Pinned}, got {seen}"),

            SocketException socket => socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => new(ErrorCode.Unavailable,
                    $"Nothing is listening for SSH on {where}.", socket.SocketErrorCode.ToString()),
                SocketError.HostNotFound or SocketError.NoData => new(ErrorCode.Invalid,
                    $"The host name did not resolve: {target.Host}.", socket.SocketErrorCode.ToString()),
                SocketError.TimedOut => new(ErrorCode.Unavailable, $"Timed out connecting to {where}.",
                    socket.SocketErrorCode.ToString()),
                _ => new(ErrorCode.Unavailable, $"Could not reach {where}.", socket.SocketErrorCode.ToString()),
            },

            SshConnectionException => new(ErrorCode.Unavailable, $"The SSH connection to {where} failed.",
                ex.GetType().Name),

            // Anything else is reported as an internal failure with the type only: library messages can quote paths,
            // configuration and key material, and none of that belongs in a tool result.
            _ => new(ErrorCode.Internal, $"SSH failed against {where}.", ex.GetType().Name),
        };
    }
}
