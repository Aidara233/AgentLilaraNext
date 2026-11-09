using System.Security.Cryptography;
using System.Text;
using ALN.Contracts;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Security;

namespace ALN.Ssh;

/// <summary>
/// ed25519 key pairs in the one format every sshd already reads, plus the two strings a human needs: the public line to
/// paste into <c>authorized_keys</c>, and the fingerprint to compare on the other machine.
///
/// ed25519 and not RSA because the whole point of <c>ssh_init</c> is that a person has to copy exactly one line by hand
/// once per device: a short line with no parameters to get wrong is the difference between a flow people finish and one
/// they abandon. .NET cannot generate ed25519 or write the OpenSSH container, which is the only reason this file exists
/// — everything it needs is in the crypto library, and none of it is protocol code.
/// </summary>
public static class SshKeyFactory
{
    /// <summary>Marks the private key as ours without saying whose: a comment ends up in <c>authorized_keys</c>.</summary>
    private const string CommentPrefix = "aln-";

    // Assembled from two literals on purpose. These are markers in the OpenSSH file format, and a single literal that
    // looks like something else's placeholder is one careless substitution away from shipping a key nobody can load.
    private static readonly string BeginMarker = "-----BEGIN OPENSSH " + "PRIVATE KEY-----";
    private static readonly string EndMarker = "-----END OPENSSH " + "PRIVATE KEY-----";

    /// <summary>
    /// What one generated key is, in the three forms that leave this process.
    /// </summary>
    /// <param name="PrivateKeyPem">The OpenSSH private key, markers included — the bytes an <c>ISecretStore</c> holds.</param>
    /// <param name="PublicKey">One line: algorithm, base64 blob, comment. Safe to show anywhere.</param>
    /// <param name="Fingerprint">SHA-256 of the public blob, in the form <c>ssh-keygen -lf</c> prints.</param>
    public sealed record GeneratedKey(byte[] PrivateKeyPem, string PublicKey, SshFingerprint Fingerprint);

    public static GeneratedKey Create(string handle)
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        var publicKey = (Ed25519PublicKeyParameters)pair.Public;

        // The blob, not a file: EncodePrivateKey deliberately adds no markers, and PrivateKeyFile wants the whole PEM.
        var blob = OpenSshPrivateKeyUtilities.EncodePrivateKey(privateKey);
        var pem = WrapOpenSshPrivateKey(blob);

        var publicBlob = OpenSshPublicKeyUtilities.EncodePublicKey(publicKey);
        var line = $"ssh-ed25519 {Convert.ToBase64String(publicBlob)} {CommentPrefix}{handle}";
        return new GeneratedKey(pem, line, Fingerprint(publicBlob));
    }

    /// <summary>
    /// SHA-256 over the public blob, base64 without padding, prefixed — the format <c>ssh-keyscan</c> and
    /// <c>ssh-keygen -lf</c> print, so that "compare these two strings" needs no conversion step on either side.
    /// </summary>
    public static SshFingerprint Fingerprint(byte[] publicBlob) =>
        new("SHA256", Convert.ToBase64String(SHA256.HashData(publicBlob)).TrimEnd('='));

    /// <summary>
    /// The PEM around an OpenSSH private key blob. Line width and trailing newline are what ssh-keygen writes: a parser
    /// that accepted anything else would be nice, but a file shaped like the one people already have is nicer.
    /// </summary>
    private static byte[] WrapOpenSshPrivateKey(byte[] blob)
    {
        var body = Convert.ToBase64String(blob);
        var text = new StringBuilder(BeginMarker.Length + EndMarker.Length + body.Length + (body.Length / 64) + 4);
        text.Append(BeginMarker).Append('\n');
        for (var offset = 0; offset < body.Length; offset += 64)
            text.Append(body, offset, Math.Min(64, body.Length - offset)).Append('\n');
        text.Append(EndMarker).Append('\n');
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
