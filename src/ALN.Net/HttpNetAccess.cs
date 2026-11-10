using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ALN.Contracts;

namespace ALN.Net;

/// <summary>
/// The only thing in this process that opens an HTTP connection to somewhere else.
///
/// A request cannot name a host: it names a target, and a target is a line in <c>net/targets.json</c> that a person
/// wrote. Everything else here is that idea being defended — the method has to be on the target's list, the path has
/// to stay under the target's base (no <c>//elsewhere/x</c> tricks), the address the name resolves to has to be
/// somewhere a target was allowed to point at, the body stops at a ceiling, and the whole call stops at a deadline.
///
/// Every one of those refusals is written down before it is returned. A refusal that leaves no trace is
/// indistinguishable from a bug, and "the model says it can't reach the docs site" is a question someone will ask.
/// </summary>
public sealed class HttpNetAccess : INetAccess, INetTargetEditing, IAdHocNetAccess, IAsyncDisposable
{
    /// <summary>
    /// Above this, a response is not a document: it is a file somebody pointed a fetch tool at. Refused instead of
    /// truncated, because a caller that asked for a web page and got eight megabytes has a configuration problem worth
    /// seeing rather than a very large string worth keeping.
    /// </summary>
    public const int HardCeilingBytes = 8 * 1024 * 1024;

    /// <summary>Where a target's decision about private addresses travels to the connect callback, which knows the
    /// socket but not the target.</summary>
    private static readonly HttpRequestOptionsKey<bool> AllowPrivateKey = new("aln.net.allow-private");

    /// <summary>
    /// The gate every ad hoc call shares. A target named in the file gets its own; an address handed over must not,
    /// because a name invented per call would leave one dictionary entry behind per host ever fetched.
    /// </summary>
    private const string AdHocGateKey = "\u0000ad-hoc";

    /// <summary>
    /// Headers a caller may never set, even if a policy lists them. Each one is a way to talk to a different server
    /// than the one the target names: <c>Host</c> says so outright, <c>Authorization</c> and <c>Cookie</c> carry
    /// somebody's credentials, and the two framing headers let a caller reshape the request a proxy will see.
    /// </summary>
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Authorization", "Cookie", "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Authorization" };

    private readonly ConcurrentDictionary<string, NetTarget> _targets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private readonly HttpClient _client;
    private readonly IAuditSink? _audit;
    private readonly ISignalLog? _signals;
    private readonly ISecretStore? _secrets;

    public HttpNetAccess(IEnumerable<NetTarget> targets, IAuditSink? audit = null, ISignalLog? signals = null,
        ISecretStore? secrets = null)
    {
        foreach (var target in targets) _targets[target.Name] = target;
        _audit = audit;
        _signals = signals;
        _secrets = secrets;
        _client = new HttpClient(new SocketsHttpHandler
        {
            // A redirect is the classic way around an allowlist: the approved host answers 302 to somewhere nobody
            // approved, and a client that follows it has just visited that address. When a target does allow them,
            // the handler still refuses — the redirect is followed here, one hop at a time, so every hop is checked
            // against the same policy and lands in the same audit line.
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 4,
            ConnectCallback = ConnectAsync
        }, disposeHandler: true)
        {
            // Deadlines are the caller's, expressed once for the whole call. HttpClient's own timeout covers
            // "send and receive headers", which is neither what was asked for nor enough to bound a slow body.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public IReadOnlyList<string> Targets => _targets.Keys.Order(StringComparer.Ordinal).ToArray();

    public NetTarget? Describe(string target) => _targets.TryGetValue(target, out var found) ? found : null;

    /// <summary>
    /// Replaces one target's policy in place. Anything already in flight finishes under the policy it started with —
    /// changing a ceiling while a request is being read is how a clean truncation turns into an exception.
    /// </summary>
    public bool UpdatePolicy(string target, NetPolicy policy)
    {
        if (!_targets.TryGetValue(target, out var found)) return false;
        _targets[target] = found with { Policy = policy };
        _gates[target] = new Gate(policy.MaxConcurrent);
        return true;
    }

    /// <summary>
    /// Adds a target or replaces one outright, address included. Creating is asked for rather than assumed: a caller
    /// that meant to adjust a target it had already written should be told when the name does not exist, not given a
    /// new one pointing somewhere it just said.
    /// </summary>
    public Result Replace(NetTarget target, bool allowCreate = false)
    {
        if (string.IsNullOrWhiteSpace(target.Name) || target.Name.Any(char.IsWhiteSpace))
            return Result.Failure(new(ErrorCode.Invalid, "A target needs a name with no spaces."));
        if (target.Base.Scheme != Uri.UriSchemeHttps && target.Base.Scheme != Uri.UriSchemeHttp)
            return Result.Failure(new(ErrorCode.Invalid, "A target must be http or https."));
        if (target.Base.UserInfo.Length > 0)
            return Result.Failure(new(ErrorCode.Invalid,
                "A target's address must not carry a user name or password — use its secret."));
        if (!allowCreate && !_targets.ContainsKey(target.Name))
            return Result.Failure(new(ErrorCode.Invalid, $"There is no target named '{target.Name}'.",
                "pass create: true to add one"));

        _targets[target.Name] = target;
        // A fresh gate, so the new ceiling is the one that counts from here. Requests already inside the old gate finish
        // under the limits they started with; that is the only way a policy change cannot turn a read into a surprise.
        _gates[target.Name] = new Gate(target.Policy.MaxConcurrent);
        return Result.Success();
    }

    public Result Remove(string name)
    {
        if (!_targets.TryRemove(name, out _))
            return Result.Failure(new(ErrorCode.Invalid, $"There is no target named '{name}'."));
        _gates.TryRemove(name, out _);
        return Result.Success();
    }

    public async ValueTask<Result<NetResponse>> SendAsync(NetRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!_targets.TryGetValue(request.Target, out var target))
            // The name is not echoed: a caller that guessed at a target should learn nothing from having guessed.
            return await Refuse(request, started, new NetFailure(NetErrorKind.NoSuchTarget, "No such target.",
                "the name is not in net/targets.json"), cancellationToken);

        return await SendToAsync(target, request, target.Name, started, cancellationToken);
    }

    /// <summary>
    /// An address somebody handed over instead of a name from the file.
    ///
    /// The origin of that address becomes a target for exactly this call — same policy shape, same gates, same audit
    /// line (its name is <c>local:&lt;authority&gt;</c>, so "where did this connection go" stays answerable). The gate is
    /// shared and fixed rather than keyed by that name: a name invented per call must not leave an entry behind that
    /// outlives it.
    /// </summary>
    public async ValueTask<Result<NetResponse>> SendAsync(AdHocRequest request, NetPolicy policy,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var address = request.Address;
        // Only the shape is checked here. Whether the address resolves somewhere reachable is the resolve gate's job
        // below, and a URL carrying a user name is refused because that is a credential on its way into an audit line.
        var wellFormed = address.IsAbsoluteUri
                         && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
                         && address.UserInfo.Length == 0;
        var authority = wellFormed ? address.Authority : "?";
        var origin = wellFormed ? address.GetLeftPart(UriPartial.Authority) + "/" : "http://invalid.invalid/";
        var target = new NetTarget($"local:{authority}", new Uri(origin), policy);
        var path = wellFormed && address.PathAndQuery.Length > 0 ? address.PathAndQuery : "/";
        var named = new NetRequest(target.Name, request.Method, path, request.Headers, request.Body, request.MaxBytes,
            request.TimeoutSeconds, request.Caller);
        if (!wellFormed)
            return await Refuse(named, started, new NetFailure(NetErrorKind.Invalid,
                "That is not an address this can fetch.",
                "an absolute http or https URL with no user name in it"), cancellationToken);

        return await SendToAsync(target, named, AdHocGateKey, started, cancellationToken);
    }

    /// <summary>
    /// The path a request takes once its destination is decided — named, or handed over.
    ///
    /// Everything from here down is identical for both, which is what keeps "temporary" from quietly meaning "less
    /// checked": a target a person wrote and an address somebody just gave are the same six gates.
    /// </summary>
    private async ValueTask<Result<NetResponse>> SendToAsync(NetTarget target, NetRequest request, string gateKey,
        long started, CancellationToken cancellationToken)
    {
        var policy = target.Policy;
        var method = (request.Method ?? "").Trim().ToUpperInvariant();
        if (method.Length == 0 || !policy.Methods.Any(m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase)))
            return await Refuse(request, started, new NetFailure(NetErrorKind.NotAllowed,
                $"{request.Target} does not accept {method}.", "allowed: " + string.Join(", ", policy.Methods)),
                cancellationToken);

        if (!TryBuildUri(target, request.Path ?? "", out var uri, out var pathProblem))
            return await Refuse(request, started, new NetFailure(NetErrorKind.Invalid, "Path refused.", pathProblem),
                cancellationToken);

        if (!PathAllowed(policy, request.Path ?? ""))
            return await Refuse(request, started, new NetFailure(NetErrorKind.NotAllowed,
                $"{request.Target} does not serve that path.",
                "allowed: " + (policy.Paths.Count == 0 ? "any path" : string.Join(", ", policy.Paths))), cancellationToken);

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, value) in request.Headers ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>())
        {
            if (ForbiddenHeaders.Contains(name))
                return await Refuse(request, started, new NetFailure(NetErrorKind.NotAllowed,
                    $"Header '{name}' is set by the target, not by the caller."), cancellationToken);
            if (policy.Headers.Count > 0 && !policy.Headers.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase)))
                return await Refuse(request, started, new NetFailure(NetErrorKind.NotAllowed,
                    $"Header '{name}' is not allowed on {request.Target}.",
                    "allowed: " + string.Join(", ", policy.Headers)), cancellationToken);
            headers.Add(new(name, value ?? ""));
        }

        var gate = _gates.GetOrAdd(gateKey, _ => new Gate(policy.MaxConcurrent));
        if (!gate.TryReserveCall(policy.CallsPerMinute, out var retryAfter))
            return await Refuse(request, started, new NetFailure(NetErrorKind.Throttled,
                $"{request.Target} is over its rate ({policy.CallsPerMinute} per minute).",
                $"retry in {Math.Ceiling(retryAfter.TotalSeconds)}s", Retryable: true), cancellationToken);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds ?? policy.TimeoutSeconds, 1, 600));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await gate.EnterSlotAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Note(request, "cancelled", "the caller gave up", started, cancellationToken: CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException)
        {
            return await Refuse(request, started, new NetFailure(NetErrorKind.Throttled,
                $"{request.Target} already has {gate.Concurrency} call(s) in flight.",
                $"waited {timeout.TotalSeconds}s", Retryable: true), cancellationToken);
        }

        try
        {
            // Redirects are followed here, one hop at a time, rather than by the handler — the handler would follow
            // them to anywhere, and the whole point of this surface is that "anywhere" is not reachable. Each hop is
            // checked against the same target, so a 302 cannot move a request off the origin a person approved.
            var current = uri;
            for (var hop = 0; ; hop++)
            {
                using var message = new HttpRequestMessage(new HttpMethod(method), current);
                message.Options.Set(AllowPrivateKey, policy.AllowPrivate);
                if (request.Body is { Length: > 0 } body) message.Content = new ByteArrayContent(body.ToArray());
                foreach (var (name, value) in headers)
                {
                    // Content-Type belongs to the content, not to the request: on the message's own header collection it
                    // is silently dropped, and a POST without one is a POST the far side has to guess about.
                    if (message.Content is not null &&
                        string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        message.Content.Headers.TryAddWithoutValidation(name, value);
                    else
                        message.Headers.TryAddWithoutValidation(name, value);
                }

                if (!await TryAuthorize(message, target, deadline.Token))
                    return await Refuse(request, started, new NetFailure(NetErrorKind.NotAllowed,
                        $"The credential for {request.Target} is not available.", target.SecretRef), cancellationToken);

                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!policy.FollowRedirects || !IsRedirect(response.StatusCode) || hop >= policy.MaxRedirects ||
                    response.Headers.Location is not { } location)
                    return await ReadResponse(request, target, policy, response, started, deadline.Token);

                if (!TryFollow(target, current, location, out var next))
                    return await Refuse(request, started, new NetFailure(NetErrorKind.NotAllowed,
                        $"{request.Target} redirected somewhere it may not go.",
                        "the redirect leaves the target's origin"), cancellationToken);
                current = next;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Note(request, "cancelled", "the caller gave up", started, cancellationToken: CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException)
        {
            return await Refuse(request, started, new NetFailure(NetErrorKind.Timeout,
                $"{request.Target} did not answer within {timeout.TotalSeconds}s."), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException)
        {
            // The connect callback's refusals arrive wrapped: a private address is a decision, not a network fault, and
            // telling them apart is the difference between "your network is down" and "somebody pointed this at the
            // metadata service".
            var kind = FindCause(ex) ?? NetErrorKind.Unreachable;
            return await Refuse(request, started, new NetFailure(kind,
                kind == NetErrorKind.PrivateAddress
                    ? $"{request.Target} resolves to an address it may not reach."
                    : $"Could not reach {request.Target}.",
                ex.InnerException?.Message ?? ex.Message, Retryable: kind == NetErrorKind.Unreachable), cancellationToken);
        }
        finally
        {
            gate.Exit();
        }
    }

    private async ValueTask<Result<NetResponse>> ReadResponse(NetRequest request, NetTarget target, NetPolicy policy,
        HttpResponseMessage response, long started, CancellationToken cancellationToken)
    {
        var limit = Math.Min(request.MaxBytes ?? policy.MaxResponseBytes, HardCeilingBytes);
        var declared = response.Content.Headers.ContentLength;
        if (declared is > HardCeilingBytes)
            return await Refuse(request, started, new NetFailure(NetErrorKind.TooLarge,
                $"{request.Target} answered {declared} bytes.", $"the ceiling here is {HardCeilingBytes}"), cancellationToken);

        var collected = new MemoryStream();
        var truncated = false;
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                var room = limit - collected.Length;
                if (read > room)
                {
                    if (room > 0) collected.Write(buffer, 0, (int)room);
                    truncated = true;
                    break;
                }
                collected.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var text = Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers)
            headers[name] = string.Join(", ", values);
        foreach (var (name, values) in response.Content.Headers)
            headers[name] = string.Join(", ", values);
        var answer = new NetResponse((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), text,
            collected.Length, truncated, response.RequestMessage?.RequestUri ?? target.Base,
            Stopwatch.GetElapsedTime(started), headers);
        await Note(request, truncated ? "truncated" : "ok",
            $"{answer.Status} {collected.Length} byte(s)", started, cancellationToken: CancellationToken.None);
        return Result<NetResponse>.Success(answer);
    }

    /// <summary>
    /// Injects the target's credential, if it has one. The secret is read into a string for exactly as long as it takes
    /// to set the header — the same unavoidable exposure SSH has when it hands a key to its client, and the reason
    /// nothing else in this file ever sees it: not the audit, not the signal, not the response.
    /// </summary>
    private async ValueTask<bool> TryAuthorize(HttpRequestMessage message, NetTarget target, CancellationToken cancellationToken)
    {
        if (target.SecretRef is not { Length: > 0 } name) return true;
        if (_secrets is null) return false;
        var opened = await _secrets.OpenAsync(name, cancellationToken);
        if (!opened.Ok || opened.Value?.Content is not { } stream) return false;
        using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var secret = (await reader.ReadToEndAsync(cancellationToken)).Trim();
            if (secret.Length == 0) return false;
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        }
        return true;
    }

    /// <summary>
    /// Builds the absolute URI from a relative path, and refuses anything that would leave the target's own origin.
    ///
    /// The dangerous one is <c>//evil.example/x</c>: as a relative reference it is not a path at all, it is a new
    /// authority, and <see cref="Uri"/> resolves it to a different host. So the answer is not "did the string look
    /// relative" but "does the result still point where the target points", checked on the resolved URI.
    /// </summary>
    private static bool TryBuildUri(NetTarget target, string path, out Uri uri, out string? problem)
    {
        uri = target.Base;
        problem = null;
        if (path.Length == 0)
        {
            problem = "a path is required; use / for the target's root";
            return false;
        }
        if (!path.StartsWith('/'))
        {
            problem = "the path must start with / — it is relative to the target, not a URL";
            return false;
        }
        foreach (var segment in path.Split('/', '?', '#', '\\'))
        {
            if (segment == "..")
            {
                problem = "the path must not climb with ..";
                return false;
            }
        }
        if (!Uri.TryCreate(target.Base, path, out var resolved))
        {
            problem = "the path is not a valid relative reference";
            return false;
        }
        if (resolved.Scheme != target.Base.Scheme || !string.Equals(resolved.Host, target.Base.Host, StringComparison.OrdinalIgnoreCase)
            || resolved.Port != target.Base.Port)
        {
            problem = "the path leaves the target's origin";
            return false;
        }
        uri = resolved;
        return true;
    }

    /// <summary>Prefix match on a segment boundary: <c>/api</c> allows <c>/api/v1</c> and not <c>/apifoo</c>.</summary>
    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// Where a redirect would take the request, and whether that is still the target. A redirect is how a permitted
    /// host hands a client to a host nobody approved of, so a hop is allowed only if it keeps the same scheme, host and
    /// port — the same test the request path goes through.
    /// </summary>
    private static bool TryFollow(NetTarget target, Uri current, Uri location, out Uri next)
    {
        next = current;
        if (location.IsAbsoluteUri)
        {
            next = location;
        }
        else
        {
            if (!Uri.TryCreate(current, location, out var resolved) || resolved is null) return false;
            next = resolved;
        }
        return next.Scheme == target.Base.Scheme
               && string.Equals(next.Host, target.Base.Host, StringComparison.OrdinalIgnoreCase)
               && next.Port == target.Base.Port;
    }

    private static bool PathAllowed(NetPolicy policy, string path)
    {
        if (policy.Paths.Count == 0) return true;
        var clean = path.Split('?', '#')[0];
        foreach (var prefix in policy.Paths)
        {
            var wanted = prefix.TrimEnd('/');
            if (wanted.Length == 0) return true;
            if (clean.Equals(wanted, StringComparison.Ordinal)) return true;
            if (clean.StartsWith(wanted + "/", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves the target's name here rather than letting the handler do it, for one reason: this is the only place
    /// that can look at <b>what the name resolved to</b> before a socket is opened. An allowlist names hosts; a
    /// hostname that answers <c>127.0.0.1</c> or <c>169.254.169.254</c> would otherwise be a doorway.
    ///
    /// Every address is checked, not just the first: a name that resolves to one public and one private address must
    /// not be reachable just because the public one came first.
    /// </summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var allowPrivate = context.InitialRequestMessage.Options.TryGetValue(AllowPrivateKey, out var allowed) && allowed;
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new NetConnectException(NetErrorKind.Unreachable, ex.Message, ex);
        }

        if (!allowPrivate)
        {
            foreach (var address in addresses)
            {
                if (!IsForbiddenAddress(address)) continue;
                throw new NetConnectException(NetErrorKind.PrivateAddress,
                    $"{host} resolves to {address}, which this target may not reach");
            }
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (ex is OperationCanceledException) throw;
                last = ex;
            }
        }

        throw new NetConnectException(NetErrorKind.Unreachable, last?.Message ?? $"{host} has no usable address", last);
    }

    /// <summary>
    /// Addresses a target may not reach unless a person said so: loopback, private, link-local (which is where cloud
    /// metadata services live), carrier-grade NAT, the unspecified address, multicast and broadcast. IPv6 is checked
    /// through its IPv4-mapped form, because <c>::ffff:127.0.0.1</c> is a loopback address wearing a different hat.
    /// </summary>
    public static bool IsForbiddenAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)
            || address.Equals(IPAddress.None)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 => true,                                   // 0.0.0.0/8
                10 => true,                                  // private
                127 => true,                                 // loopback
                169 when bytes[1] == 254 => true,            // link-local, incl. 169.254.169.254
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
                192 when bytes[1] == 168 => true,
                100 when bytes[1] >= 64 && bytes[1] <= 127 => true,   // CGNAT
                224 => true,                                 // multicast
                _ => bytes[0] >= 240,                        // reserved + broadcast
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            return (bytes[0] & 0xFE) == 0xFC;                // fc00::/7, unique local
        }

        return true;
    }

    private static NetErrorKind? FindCause(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is NetConnectException refused) return refused.Kind;
        return null;
    }

    private async ValueTask<Result<NetResponse>> Refuse(NetRequest request, long started, NetFailure failure,
        CancellationToken cancellationToken)
    {
        await Note(request, OutcomeOf(failure.Kind), failure.Message + (failure.Detail is null ? "" : " " + failure.Detail),
            started, cancellationToken: CancellationToken.None);
        return Result<NetResponse>.Failure(failure.ToError());
    }

    private static string OutcomeOf(NetErrorKind kind) => kind switch
    {
        NetErrorKind.NoSuchTarget or NetErrorKind.NotAllowed or NetErrorKind.PrivateAddress => "denied",
        NetErrorKind.Invalid => "invalid",
        NetErrorKind.Timeout => "timeout",
        NetErrorKind.Throttled => "throttled",
        _ => "failed",
    };

    /// <summary>
    /// One audit line and one signal per call, whatever happened. The path is recorded without its query values: a
    /// query string is where tokens, search terms and account ids end up, and an audit that keeps them is a log that
    /// has to be protected like a credential store. The keys stay — "which endpoint, with which parameters" is the
    /// part that answers a question later.
    /// </summary>
    private async ValueTask Note(NetRequest request, string outcome, string? detail, long started,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        var safePath = WithoutQueryValues(request.Path ?? "");
        var line = $"{request.Method} {safePath}" + (detail is null ? "" : " — " + detail);
        if (_audit is not null)
        {
            try
            {
                await _audit.AppendAsync(new AuditRecord(DateTimeOffset.UtcNow, Guid.NewGuid(),
                    request.Caller ?? "unknown", new Address("net", "target", request.Target), "net.request", outcome,
                    line), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                // The audit is best-effort here in the narrow sense that a failing sink must not turn a successful
                // fetch into an exception the caller has to interpret. The signal below still records it.
            }
        }
        _signals?.Write(new SignalRecord
        {
            Group = "net",
            Name = request.Target,
            Mechanism = "http",
            From = request.Caller ?? "",
            To = request.Target,
            Outcome = outcome,
            DurationMs = elapsed.TotalMilliseconds,
            Detail = new Dictionary<string, string>
            {
                ["method"] = request.Method ?? "",
                ["path"] = safePath,
                ["detail"] = detail ?? "",
            },
        });
    }

    /// <summary>Keeps the shape of a query and drops what was in it: <c>?q=a&amp;page=2</c> becomes <c>?q&amp;page</c>.</summary>
    internal static string WithoutQueryValues(string path)
    {
        var question = path.IndexOf('?');
        if (question < 0) return path;
        var pathPart = path[..question];
        var query = path[(question + 1)..];
        var keys = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=')[0])
            .Where(key => key.Length > 0);
        var rebuilt = string.Join("&", keys);
        return rebuilt.Length == 0 ? pathPart : pathPart + "?" + rebuilt;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Per-target limits: how often, how many at once. Kept next to the name so a policy change cannot leave
    /// the counters of the old policy behind.</summary>
    private sealed class Gate(int concurrency)
    {
        private const long WindowMs = 60_000;
        private readonly SemaphoreSlim _slots = new(Math.Max(1, concurrency), Math.Max(1, concurrency));
        private readonly Queue<long> _calls = new();
        private readonly object _lock = new();

        public int Concurrency { get; } = Math.Max(1, concurrency);

        /// <summary>
        /// One call is reserved before the socket is opened, so a burst cannot all pass the check and then queue.
        /// <paramref name="perMinute"/> of zero or less means "may not be called" rather than "unlimited" — a zero in a
        /// configuration file is almost never somebody asking for unlimited traffic.
        /// </summary>
        public bool TryReserveCall(int perMinute, out TimeSpan retryAfter)
        {
            retryAfter = TimeSpan.Zero;
            var now = Environment.TickCount64;
            lock (_lock)
            {
                while (_calls.Count > 0 && now - _calls.Peek() >= WindowMs) _calls.Dequeue();
                if (perMinute <= 0) { retryAfter = TimeSpan.FromMinutes(1); return false; }
                if (_calls.Count >= perMinute)
                {
                    retryAfter = TimeSpan.FromMilliseconds(WindowMs - (now - _calls.Peek()));
                    return false;
                }
                _calls.Enqueue(now);
                return true;
            }
        }

        public Task EnterSlotAsync(CancellationToken cancellationToken) => _slots.WaitAsync(cancellationToken);

        public void Exit() => _slots.Release();
    }
}

/// <summary>
/// A refusal raised inside the connect callback, where the only thing available is an exception. It carries the kind so
/// the caller can still be told "this name resolves somewhere you may not go" instead of "network error".
/// </summary>
public sealed class NetConnectException(NetErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public NetErrorKind Kind { get; } = kind;
}
