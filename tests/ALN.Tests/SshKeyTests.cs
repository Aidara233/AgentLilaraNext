using ALN.Ssh;
using Renci.SshNet;

namespace ALN.Tests;

/// <summary>
/// The generated key has to be loadable by the SSH client and by a real sshd — that is the whole contract. This test
/// cannot prove the second half without a server, so it proves the part that can be proven offline: SSH.NET parses what
/// this factory writes, the public line is one paste-able line, and the fingerprint has the shape people compare.
/// </summary>
public sealed class SshKeyTests
{
    [Fact]
    public void A_generated_key_is_parsed_by_the_ssh_client()
    {
        var made = SshKeyFactory.Create("prod-web");

        using var stream = new MemoryStream(made.PrivateKeyPem);
        var key = new PrivateKeyFile(stream);

        // Parsing is the assertion: an unparsable key would throw here, and that is exactly the failure a user would
        // otherwise discover after pasting the public half onto the device.
        Assert.NotNull(key);
        Assert.NotNull(key.HostKeyAlgorithms);
    }

    [Fact]
    public void The_public_line_is_one_line_with_the_algorithm_and_the_handle_as_a_comment()
    {
        var made = SshKeyFactory.Create("prod-web");
        var line = made.PublicKey;

        Assert.DoesNotContain('\n', line);
        Assert.StartsWith("ssh-ed25519 ", line, StringComparison.Ordinal);
        Assert.EndsWith(" aln-prod-web", line, StringComparison.Ordinal);
        // Three fields: algorithm, base64 blob, comment. Anything else is not a line authorized_keys accepts.
        Assert.Equal(3, line.Split(' ').Length);
    }

    [Fact]
    public void The_fingerprint_looks_like_the_one_ssh_keygen_prints()
    {
        var made = SshKeyFactory.Create("x");

        Assert.Equal("SHA256", made.Fingerprint.Algorithm);
        // 32 bytes of SHA-256 in base64 without padding is 43 characters, which is what "SHA256:…" always is.
        Assert.Equal(43, made.Fingerprint.Value.Length);
        Assert.StartsWith("SHA256:", made.Fingerprint.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('=', made.Fingerprint.Value);
    }

    [Fact]
    public void Two_keys_are_never_the_same()
    {
        var one = SshKeyFactory.Create("a");
        var two = SshKeyFactory.Create("a");

        Assert.NotEqual(one.PublicKey, two.PublicKey);
        Assert.NotEqual(one.Fingerprint, two.Fingerprint);
    }

    [Fact]
    public void The_private_key_carries_the_markers_a_pem_reader_expects()
    {
        var made = SshKeyFactory.Create("a");
        var text = System.Text.Encoding.ASCII.GetString(made.PrivateKeyPem);

        Assert.StartsWith("-----BEGIN OPENSSH ", text, StringComparison.Ordinal);
        Assert.EndsWith("-----\n", text, StringComparison.Ordinal);
        // Every base64 line is 64 characters except the last: what ssh-keygen writes, and what tools that re-wrap
        // (or refuse to re-wrap) a key expect.
        var body = text.Split('\n').Skip(1).Where(line => line.Length > 0 && !line.StartsWith("-----END", StringComparison.Ordinal)).ToArray();
        Assert.All(body.SkipLast(1), line => Assert.Equal(64, line.Length));
    }
}
