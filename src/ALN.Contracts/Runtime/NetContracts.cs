namespace ALN.Contracts;

/// <summary>
/// One place this process is allowed to reach, <b>named</b>.
///
/// The name is the whole interface. A caller never holds a URL: it says "use <c>docs</c> and fetch <c>/release-notes</c>",
/// and which host that is stays a fact about configuration. That is the same trade <see cref="IFileSystemAccess"/> makes
/// with scopes and <see cref="ISshAccess"/> makes with <see cref="SshTarget"/> — and it is the reason there is no
/// parameter anywhere in this file that could carry <c>https://example.com</c>.
///
/// A caller that could name a host could reach a host; a caller that can only name a target reaches exactly what a
/// person wrote down. Whatever is reached, the audit line says the target's name, so "who fetched what" stays readable
/// without keeping a copy of the URL (which is where credentials like to hide — see <see cref="SecretRef"/>).
/// </summary>
/// <param name="Base">Scheme, host and port. <b>Never carries user information</b> (<c>https://user:pw@host/</c> is
/// refused when the configuration is read): the credential belongs in <paramref name="SecretRef"/>, where it can be
/// kept out of logs, out of audits and out of anything that displays a target.</param>
/// <param name="SecretRef">
/// The name of a secret in <see cref="ISecretStore"/>, injected by the implementation at the moment of the request.
/// Nothing in this contract returns it, and no response echoes it: a caller that has the target's name still cannot
/// read the key that target uses.
/// </param>
public sealed record NetTarget(string Name, Uri Base, NetPolicy Policy, string? SecretRef = null)
{
    public override string ToString() => Name;
}

/// <summary>
/// What one target may be used for. Every field defaults to the narrow answer, because the only thing a target is
/// asked to prove is that a person meant it.
///
/// These are per-target rather than global on purpose: a documentation mirror and a status endpoint need different
/// ceilings, and a shared number would end up sized for the loosest caller.
/// </summary>
public sealed record NetPolicy
{
    /// <summary>Methods allowed by name. Anything not listed is refused with the list in the answer.</summary>
    public IReadOnlyList<string> Methods { get; init; } = ["GET", "HEAD"];

    /// <summary>
    /// Path prefixes, matched on a segment boundary (<c>/api</c> does not allow <c>/apifoo</c>). Empty means "any path
    /// on this target" — the target itself is already the narrowing, and a path list is for when it is not enough.
    /// </summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>
    /// Request headers a caller may set. Empty (the default) means none. <c>Host</c>, <c>Authorization</c>,
    /// <c>Cookie</c>, <c>Content-Length</c> and <c>Transfer-Encoding</c> are refused even when listed: those are how a
    /// caller would talk to a different server than the one this target names.
    /// </summary>
    public IReadOnlyList<string> Headers { get; init; } = [];

    /// <summary>
    /// Largest response this target will hand back whole. Beyond it the answer is marked truncated (or refused, when
    /// the caller asked for the complete body) — never silently short, because a short document and a complete one
    /// look the same once they are text.
    /// </summary>
    public int MaxResponseBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Total time for the call, DNS and connect and body included. Not a socket read timeout: "the request took a
    /// minute in twelve pieces" is still a request that took a minute.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// Redirects are not followed by default. A redirect is the classic way around an allowlist: the permitted host
    /// answers <c>302 https://elsewhere</c>, and a client that follows it has just visited an address nobody approved.
    /// </summary>
    public bool FollowRedirects { get; init; }

    public int MaxRedirects { get; init; }

    /// <summary>Calls per minute, counted over a sliding window across every caller of this target.</summary>
    public int CallsPerMinute { get; init; } = 30;

    /// <summary>Requests allowed in flight at once for this target.</summary>
    public int MaxConcurrent { get; init; } = 2;

    /// <summary>
    /// Whether this target may resolve to a loopback, private or link-local address.
    ///
    /// Off by default, and deliberately so: an allowlist says <i>which name</i>, not <i>which address that name
    /// resolves to</i>. A permitted host that starts answering <c>127.0.0.1</c> (or a hostname an attacker controls
    /// that answers <c>169.254.169.254</c>) would otherwise turn this port into a scanner for everything the machine
    /// can reach. A person who really does mean "the model server on this box" says so, per target.
    /// </summary>
    public bool AllowPrivate { get; init; }
}

/// <summary>
/// One request, described relative to a target. There is no URL field and no absolute path field — by construction,
/// not by validation.
/// </summary>
/// <param name="Target">The target's name, as it appears in configuration. Unknown names are refused, and the refusal
/// does not echo what was asked for.</param>
/// <param name="Path">Path and query, relative to the target's base (<c>/v1/items?page=2</c>). Must start with
/// <c>/</c>; must not be absolute, must not contain a scheme, and must not try to climb out with <c>..</c>.</param>
/// <param name="Body">Bytes for the methods that take one. Empty for <c>GET</c>/<c>HEAD</c>.</param>
/// <param name="MaxBytes">A ceiling below the target's own, when one call wants less. Never above it.</param>
/// <param name="Caller">
/// Who to write into the audit line — a Session id, a module id, a script instance. Null means "somebody who did not
/// say", which is recorded as such: the audit answers "who fetched this", and an answer of "unknown" is a fact worth
/// having rather than a hole filled with a guess.
/// </param>
public sealed record NetRequest(
    string Target,
    string Method,
    string Path,
    IReadOnlyDictionary<string, string>? Headers = null,
    ReadOnlyMemory<byte>? Body = null,
    int? MaxBytes = null,
    int? TimeoutSeconds = null,
    string? Caller = null);

/// <summary>
/// What came back. A 404 or a 500 is a success here: the far side answered, which is the only question this surface
/// settles. Whether that answer is an error belongs to whoever asked.
/// </summary>
/// <param name="Bytes">Bytes actually handed back, after any truncation.</param>
/// <param name="Truncated">Set by whoever applied the limit, never inferred from a short body.</param>
/// <param name="FinalUri">Where the answer actually came from — differs from the request only when redirects were
/// allowed, and is reported so a caller can see that it was moved.</param>
public sealed record NetResponse(
    int Status,
    string? ContentType,
    string Text,
    long Bytes,
    bool Truncated,
    Uri FinalUri,
    TimeSpan Elapsed);

/// <summary>
/// Why a call did not reach an answer, in the kinds a caller can act on differently.
///
/// Folded into one "failed", every one of these is the same sentence — "the network didn't work" — and a person
/// cannot fix anything from it. <see cref="NoSuchTarget"/> and <see cref="NotAllowed"/> are configuration,
/// <see cref="PrivateAddress"/> is a decision somebody has to make, <see cref="Unreachable"/> and
/// <see cref="Timeout"/> are the far side, <see cref="TooLarge"/> is the answer being the wrong shape for this
/// surface, and <see cref="Throttled"/> is this process protecting itself.
/// </summary>
public enum NetErrorKind
{
    None = 0,
    NoSuchTarget,
    NotAllowed,
    Invalid,
    PrivateAddress,
    Unreachable,
    Timeout,
    TooLarge,
    Throttled,
}

/// <summary>
/// A refusal that still says which kind it is. <see cref="Error"/> has one free-text field, so the kind is carried
/// here and spelled into <c>Detail</c> on the way out — a caller that only has the <see cref="Result"/> can still tell
/// "this target does not exist" from "this target timed out" by reading one word.
/// </summary>
public sealed record NetFailure(NetErrorKind Kind, string Message, string? Detail = null, bool Retryable = false)
{
    public Error ToError() => new(CodeOf(Kind), Message, Detail is null ? Kind.ToString() : $"{Kind}: {Detail}", Retryable);

    /// <summary>How each kind maps onto the error codes the rest of the system already speaks.</summary>
    public static ErrorCode CodeOf(NetErrorKind kind) => kind switch
    {
        NetErrorKind.NoSuchTarget or NetErrorKind.NotAllowed or NetErrorKind.PrivateAddress => ErrorCode.Denied,
        NetErrorKind.Invalid => ErrorCode.Invalid,
        NetErrorKind.Timeout => ErrorCode.Timeout,
        NetErrorKind.Unreachable => ErrorCode.Unavailable,
        NetErrorKind.TooLarge => ErrorCode.Invalid,
        NetErrorKind.Throttled => ErrorCode.Conflict,
        _ => ErrorCode.Internal,
    };
}

/// <summary>
/// The only thing in this process that speaks HTTP to the outside world, and therefore the only thing that needs a
/// client. (SSH keeps its own port: a different protocol with a different threat model, and merging them would give
/// both the union of their permissions.)
///
/// It takes a name and a relative path, applies the target's policy, records one audit line and returns text. Nothing
/// here accepts an address, and nothing here is generic: this is a fetch surface, not an HTTP client with a policy
/// bolted on. Code that wants the latter should be code that was reviewed for it.
/// </summary>
public interface INetAccess
{
    /// <summary>The targets this process knows, by name only. A name never reveals where it points.</summary>
    IReadOnlyList<string> Targets { get; }

    /// <summary>
    /// One target's configuration, for whoever displays or edits it. Null when there is no such target — indistinguishable
    /// on purpose from "not allowed to ask", which is decided above this port.
    /// </summary>
    NetTarget? Describe(string target);

    /// <summary>
    /// Performs one call. Bounded by the target's policy, recorded whether it succeeds or not.
    /// </summary>
    ValueTask<Result<NetResponse>> SendAsync(NetRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Changing the allowlist while the process runs.
///
/// Separate from <see cref="INetAccess"/> on purpose: everything that fetches holds the read-only port, and only the
/// control plane and the parameter surface get this one. A single interface with both halves would mean every caller
/// that can read a page can also repoint it — and repointing a target is exactly the move that turns "the model may
/// read the docs site" into "the model may read anything".
/// </summary>
public interface INetTargetEditing
{
    /// <summary>
    /// Adds a target or replaces the one with that name, including where it points. False when there was no such
    /// target and <paramref name="allowCreate"/> was not given — creating is a decision, so it is asked for.
    /// </summary>
    Result Replace(NetTarget target, bool allowCreate = false);

    /// <summary>Forgets a target. Calls already in flight finish; the name stops resolving for new ones.</summary>
    Result Remove(string name);
}
