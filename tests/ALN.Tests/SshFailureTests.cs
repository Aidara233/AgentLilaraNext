using System.Net.Sockets;
using ALN.Contracts;
using ALN.Ssh;
using Renci.SshNet.Common;

namespace ALN.Tests;

/// <summary>
/// The five kinds a failure can be, and the promise that a library message never becomes a tool result.
///
/// This is the only health check the SSH surface has: a session finds out that a key is not installed by running
/// something. So the classification is the feature — and the redaction is the other half of it, because library
/// messages quote paths and, for password-shaped failures, what was tried.
/// </summary>
public sealed class SshFailureTests
{
    private static readonly SshTarget Target = new("10.0.0.7", 22, "deploy");

    [Fact]
    public void An_authentication_failure_points_at_the_key_not_the_network()
    {
        var error = SshFailures.Map(new SshAuthenticationException("Permission denied (publickey)."), Target, null);

        Assert.Equal(ErrorCode.Denied, error.Code);
        Assert.Contains("public key is not installed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_port_is_told_apart_from_a_firewall()
    {
        var refused = SshFailures.Map(new SocketException((int)SocketError.ConnectionRefused), Target, null);
        Assert.Equal(ErrorCode.Unavailable, refused.Code);
        Assert.Contains("Nothing is listening", refused.Message, StringComparison.Ordinal);

        var timeout = SshFailures.Map(new SshOperationTimeoutException("timed out"), Target, null);
        Assert.Equal(ErrorCode.Unavailable, timeout.Code);
        Assert.Contains("Timed out", timeout.Message, StringComparison.Ordinal);
        Assert.NotEqual(refused.Message, timeout.Message);
    }

    [Fact]
    public void A_host_name_that_does_not_resolve_is_the_callers_mistake()
    {
        var error = SshFailures.Map(new SocketException((int)SocketError.HostNotFound), Target, null);
        Assert.Equal(ErrorCode.Invalid, error.Code);
        Assert.Contains("did not resolve", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_host_key_reports_both_fingerprints_and_says_nothing_was_sent()
    {
        var trusted = new SshFingerprint("SHA256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var seen = new SshFingerprint("SHA256", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        var target = Target with { Pinned = trusted };

        var error = SshFailures.Map(new SshConnectionException("host key mismatch"), target, seen);

        Assert.Equal(ErrorCode.Denied, error.Code);
        Assert.Contains(trusted.ToString(), error.Detail!, StringComparison.Ordinal);
        Assert.Contains(seen.ToString(), error.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_failure_keeps_the_library_message_out_of_the_result()
    {
        // The message here stands in for anything a library might quote: a path, a key name, a passphrase prompt.
        var error = SshFailures.Map(new InvalidOperationException("failed reading /home/deploy/.ssh/id_ed25519"),
            Target, null);

        Assert.Equal(ErrorCode.Internal, error.Code);
        Assert.DoesNotContain("/home/deploy", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/deploy", error.Detail ?? string.Empty, StringComparison.Ordinal);
        // The type name is safe and is what makes a report searchable.
        Assert.Contains("InvalidOperationException", error.Detail, StringComparison.Ordinal);
        // And it names the machine, so the reader knows which device to look at.
        Assert.Contains("10.0.0.7:22", error.Message, StringComparison.Ordinal);
    }
}
