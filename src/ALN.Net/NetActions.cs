using System.Text.Json;
using ALN.Contracts;

namespace ALN.Net;

/// <summary>
/// The operator's view of the allowlist: what may be reached, and changing it.
///
/// Four verbs, and the split between them is the point. <c>list</c> and <c>get</c> are agent-level — a model asked
/// "can you read that page" should be able to answer with the names it is allowed to use instead of guessing one.
/// <c>set</c> and <c>remove</c> are operator-level: what goes into the file decides where traffic leaves this machine,
/// and an allowlist that can be extended from inside a conversation is not one.
///
/// Nothing here returns a credential. A target's secret is <c>"***"</c> on the way out and <c>"***"</c> means "keep
/// what is there" on the way in — the same rule <c>models.provider.get/save</c> follows, and for the same reason: with
/// one half of it missing, opening the settings page once would blank every key in the file.
/// </summary>
public static class NetActions
{
    public const string List = "net.targets.list";
    public const string Get = "net.targets.get";
    public const string Set = "net.targets.set";
    public const string Remove = "net.targets.remove";
    public const string Fetch = "net.fetch";

    /// <summary>What a secret reads as. Never the secret itself.</summary>
    public const string Masked = "***";

    /// <summary>
    /// Most text one fetch hands back. The port's ceiling protects this process; this one protects whoever reads the
    /// answer — a hundred kilobytes of markup in an action result is a round spent on something nobody will read, and
    /// the answer says it was cut rather than pretending the page was short.
    /// </summary>
    public const int MaxFetchChars = 32_000;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static Result Register(IControlPlane plane, INetAccess net, INetTargetEditing editing,
        ISecretStore? secrets, string targetFilePath)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        if (net is null) return Result.Failure(new(ErrorCode.Invalid, "A network port is required."));

        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(List, "Where this process may reach, by name: the address each one points at, what it accepts, its "
                    + "ceilings, and whether it carries a credential. Names are the only thing a fetch takes.",
                Capabilities.NetRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(Describe(net)))),

            (new(Get, "One target in full: address, methods, path prefixes, headers, limits, and whether it has a "
                    + "secret (never the secret itself).",
                Capabilities.NetRead, false,
                ParamsJson: """
                {"type":"object","properties":{
                  "name":{"type":"string","description":"The target's name."}},
                 "required":["name"],"additionalProperties":false}
                """, Subject: "name"), (arguments, _) =>
            {
                if (!Text(arguments, "name", out var name))
                    return ValueTask.FromResult(Result<JsonElement>.Failure(
                        new(ErrorCode.Invalid, "net.targets.get requires 'name'.")));
                var target = net.Describe(name);
                if (target is null)
                    return ValueTask.FromResult(Result<JsonElement>.Failure(
                        new(ErrorCode.Invalid, $"There is no target named '{name}'.")));
                return ValueTask.FromResult(Result<JsonElement>.Success(One(target)));
            }),

            (new(Set, "Add a target, or change one that already exists. Fields left out keep their current value; "
                    + "'secret' set to *** keeps the stored credential. Applied to the running process first, then "
                    + "written to net/targets.json — so a file that cannot be written leaves a change that works now "
                    + "and not after a restart, which the answer says out loud.",
                Capabilities.NetControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "name":{"type":"string","description":"The target's name: one word, used by every fetch."},
                  "create":{"type":"boolean","description":"Required to add a name that does not exist yet."},
                  "base":{"type":"string","description":"Absolute http(s) URL, scheme and host only — e.g. https://example.com. Required when creating."},
                  "methods":{"type":"array","items":{"type":"string"},"description":"Allowed methods. Default GET, HEAD."},
                  "paths":{"type":"array","items":{"type":"string"},"description":"Path prefixes, each starting with /. Omit to allow any path on this target."},
                  "headers":{"type":"array","items":{"type":"string"},"description":"Request headers a caller may set. Default none."},
                  "maxBytes":{"type":"integer","minimum":1,"maximum":8388608},
                  "timeoutSeconds":{"type":"integer","minimum":1,"maximum":600},
                  "callsPerMinute":{"type":"integer","minimum":1,"maximum":100000},
                  "maxConcurrent":{"type":"integer","minimum":1,"maximum":64},
                  "allowPrivate":{"type":"boolean","description":"Whether this target may resolve to loopback or a private address. Default false."},
                  "followRedirects":{"type":"boolean","description":"Default false. A redirect leaves the address this target names."},
                  "secret":{"type":"string","description":"A credential to send as Authorization: Bearer. *** keeps the stored one; an empty string removes it."}},
                 "required":["name"],"additionalProperties":false}
                """, Subject: "name"), (arguments, ct) => SetAsync(arguments, net, editing, secrets, targetFilePath, ct)),

            (new(Remove, "Forget a target. Calls already in flight finish; the name stops resolving for new ones. Its "
                    + "stored credential is left alone — removing a name should not silently destroy a key somebody may "
                    + "still be using.",
                Capabilities.NetControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "name":{"type":"string"}},
                 "required":["name"],"additionalProperties":false}
                """, Subject: "name"), (arguments, _) =>
            {
                if (!Text(arguments, "name", out var name))
                    return ValueTask.FromResult(Result<JsonElement>.Failure(
                        new(ErrorCode.Invalid, "net.targets.remove requires 'name'.")));
                var removed = editing.Remove(name);
                if (!removed.Ok) return ValueTask.FromResult(Result<JsonElement>.Failure(removed.Error!));
                var persisted = Persist(net, targetFilePath);
                return ValueTask.FromResult(Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new
                {
                    name, removed = true, persisted.Ok, note = persisted.Ok ? null : persisted.Error?.Detail
                }, Web)));
            }),

            (new(Fetch, "Fetch something from one of the places this process may reach. The target is a name, not an "
                    + "address: a name that was never written down does not exist. This is the same door the model's "
                    + "net_fetch tool uses, offered here so a script or a person can use it without a tool call — "
                    + "same allowlist, same ceilings, same audit line.",
                Capabilities.NetRead, false,
                ParamsJson: """
                {"type":"object","properties":{
                  "target":{"type":"string","description":"The name of an allowed target."},
                  "path":{"type":"string","description":"Path and query, starting with / — relative to the target."},
                  "method":{"type":"string","enum":["GET","HEAD","POST","PUT","PATCH","DELETE"],"description":"Default GET."},
                  "body":{"type":"string","description":"Request body as text, for methods that take one."}},
                 "required":["target","path"],"additionalProperties":false}
                """, Subject: "target"), (arguments, ct) => FetchAsync(arguments, net, ct))
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    /// <summary>
    /// One fetch, as an action: the same port, the same policy, the same audit line as the model's tool. It exists
    /// because two callers have no other way in — a script (which declares <c>net.read</c> and calls
    /// <c>ctx.actions.call</c>) and a person in the web UI who wants to see whether a target works before handing it
    /// to a model.
    /// </summary>
    private static async ValueTask<Result<JsonElement>> FetchAsync(JsonElement arguments, INetAccess net,
        CancellationToken cancellationToken)
    {
        if (!Text(arguments, "target", out var target))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "net.fetch requires 'target'."));
        var path = Text(arguments, "path", out var declaredPath) ? declaredPath : "/";
        var method = Text(arguments, "method", out var declaredMethod) ? declaredMethod.ToUpperInvariant() : "GET";
        var body = Text(arguments, "body", out var declaredBody) ? declaredBody : null;

        var sent = await net.SendAsync(new NetRequest(target, method, path,
            Body: body is null ? null : System.Text.Encoding.UTF8.GetBytes(body),
            Caller: "action:" + Fetch), cancellationToken).ConfigureAwait(false);
        if (!sent.Ok || sent.Value is null) return Result<JsonElement>.Failure(sent.Error!);

        var response = sent.Value;
        var text = response.Text;
        var tooLong = text.Length > MaxFetchChars;
        if (tooLong) text = text[..MaxFetchChars];
        return Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new
        {
            status = response.Status,
            contentType = response.ContentType,
            text,
            bytes = response.Bytes,
            truncated = response.Truncated || tooLong,
            elapsedMs = Math.Round(response.Elapsed.TotalMilliseconds, 1),
        }, Web));
    }

    private static async ValueTask<Result<JsonElement>> SetAsync(JsonElement arguments, INetAccess net,
        INetTargetEditing editing, ISecretStore? secrets, string targetFilePath, CancellationToken cancellationToken)
    {
        if (!Text(arguments, "name", out var name))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "net.targets.set requires 'name'."));

        var create = arguments.TryGetProperty("create", out var created) && created.ValueKind == JsonValueKind.True;
        var existing = net.Describe(name);
        if (existing is null && !create)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"There is no target named '{name}'.",
                "pass create: true, with a base, to add one"));

        var baseText = Text(arguments, "base", out var declared) ? declared : existing?.Base.ToString();
        if (string.IsNullOrWhiteSpace(baseText) || !Uri.TryCreate(baseText, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                "A target needs an absolute http(s) address in 'base'."));

        var policy = existing?.Policy ?? new NetPolicy();
        if (arguments.TryGetProperty("methods", out var methods))
        {
            if (!Strings(methods, out var list, out var problem))
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "methods: " + problem));
            if (list.Count == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "methods is empty — omit it for GET and HEAD."));
            var bad = list.FirstOrDefault(m => m is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS"));
            if (bad is not null)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"'{bad}' is not a method this surface speaks."));
            policy = policy with { Methods = list };
        }
        if (arguments.TryGetProperty("paths", out var paths))
        {
            if (!Strings(paths, out var list, out var problem))
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "paths: " + problem));
            var bad = list.FirstOrDefault(p => !p.StartsWith('/'));
            if (bad is not null)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"path prefix '{bad}' must start with /."));
            policy = policy with { Paths = list };
        }
        if (arguments.TryGetProperty("headers", out var headers))
        {
            if (!Strings(headers, out var list, out var problem))
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "headers: " + problem));
            policy = policy with { Headers = list };
        }

        if (!Int(arguments, "maxBytes", 1, HttpNetAccess.HardCeilingBytes, policy.MaxResponseBytes, out var maxBytes, out var badNumber))
            return Result<JsonElement>.Failure(badNumber!);
        if (!Int(arguments, "timeoutSeconds", 1, 600, policy.TimeoutSeconds, out var timeout, out badNumber))
            return Result<JsonElement>.Failure(badNumber!);
        if (!Int(arguments, "callsPerMinute", 1, 100_000, policy.CallsPerMinute, out var rate, out badNumber))
            return Result<JsonElement>.Failure(badNumber!);
        if (!Int(arguments, "maxConcurrent", 1, 64, policy.MaxConcurrent, out var concurrent, out badNumber))
            return Result<JsonElement>.Failure(badNumber!);

        var allowPrivate = Bool(arguments, "allowPrivate", policy.AllowPrivate);
        var followRedirects = Bool(arguments, "followRedirects", policy.FollowRedirects);
        policy = policy with
        {
            MaxResponseBytes = maxBytes,
            TimeoutSeconds = timeout,
            CallsPerMinute = rate,
            MaxConcurrent = concurrent,
            AllowPrivate = allowPrivate,
            FollowRedirects = followRedirects,
        };

        // The secret is handled before the target is stored, because writing it is the step that can fail for a reason
        // worth reporting ("that name is not valid"), and a target that points at a credential that was never written
        // would fail much later with a message about authentication.
        var secret = existing?.SecretRef;
        var secretNote = (string?)null;
        if (arguments.TryGetProperty("secret", out var secretElement) && secretElement.ValueKind != JsonValueKind.Null)
        {
            if (secretElement.ValueKind != JsonValueKind.String)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "secret must be a string."));
            var value = secretElement.GetString()!;
            if (value == Masked)
            {
                // "***" means "unchanged" — the value the caller saw, not a credential that happens to be stars.
            }
            else if (value.Length == 0)
            {
                secret = null;
                secretNote = existing?.SecretRef is { } old ? $"credential '{old}' is no longer used by this target" : null;
            }
            else
            {
                if (secrets is null)
                    return Result<JsonElement>.Failure(new(ErrorCode.Unavailable,
                        "This process has no secret store, so a credential cannot be kept.", "targets.json can name one"));
                var stored = await secrets.PutAsync(name, System.Text.Encoding.UTF8.GetBytes(value), cancellationToken);
                if (!stored.Ok) return Result<JsonElement>.Failure(stored.Error!);
                secret = name;
                secretNote = "credential written";
            }
        }

        var replaced = editing.Replace(new NetTarget(name, baseUri, policy, secret), allowCreate: create);
        if (!replaced.Ok) return Result<JsonElement>.Failure(replaced.Error!);

        var persisted = Persist(net, targetFilePath);
        var applied = net.Describe(name);
        return Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new
        {
            target = applied is null ? null : Shape(applied),
            created = existing is null,
            persisted = persisted.Ok,
            note = persisted.Ok ? secretNote : persisted.Error?.Detail,
        }, Web));
    }

    /// <summary>Writes the whole list back. Called after the change is already in force: a file that cannot be written
    /// is reported as "works now, not after a restart", which is the truth and is better than refusing a change that
    /// the running process can already honour.</summary>
    private static Result Persist(INetAccess net, string path)
    {
        var all = new List<NetTarget>();
        foreach (var name in net.Targets)
            if (net.Describe(name) is { } target) all.Add(target);
        return NetTargetFile.Write(path, all);
    }

    private static JsonElement Describe(INetAccess net)
    {
        var targets = new List<object>();
        foreach (var name in net.Targets)
            if (net.Describe(name) is { } target) targets.Add(Shape(target));
        return JsonSerializer.SerializeToElement(new { targets }, Web);
    }

    private static JsonElement One(NetTarget target) => JsonSerializer.SerializeToElement(Shape(target), Web);

    private static object Shape(NetTarget target) => new
    {
        name = target.Name,
        @base = target.Base.ToString(),
        methods = target.Policy.Methods,
        paths = target.Policy.Paths,
        headers = target.Policy.Headers,
        maxBytes = target.Policy.MaxResponseBytes,
        timeoutSeconds = target.Policy.TimeoutSeconds,
        followRedirects = target.Policy.FollowRedirects,
        maxRedirects = target.Policy.MaxRedirects,
        callsPerMinute = target.Policy.CallsPerMinute,
        maxConcurrent = target.Policy.MaxConcurrent,
        allowPrivate = target.Policy.AllowPrivate,
        // The name, never the value: a credential that can be read through the surface it authenticates is one that
        // ends up in a screenshot of the settings page.
        secret = target.SecretRef is null ? null : Masked,
    };

    private static bool Text(JsonElement arguments, string name, out string value)
    {
        value = "";
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()?.Trim() ?? "";
        return value.Length > 0;
    }

    private static bool Strings(JsonElement element, out IReadOnlyList<string> values, out string problem)
    {
        problem = "";
        var list = new List<string>();
        values = list;
        if (element.ValueKind != JsonValueKind.Array)
        {
            problem = "expected an array of strings";
            return false;
        }
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || text.Trim().Length == 0)
            {
                problem = "expected an array of non-empty strings";
                return false;
            }
            list.Add(text.Trim());
        }
        return true;
    }

    private static bool Bool(JsonElement arguments, string name, bool fallback)
    {
        if (!arguments.TryGetProperty(name, out var element)) return fallback;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static bool Int(JsonElement arguments, string name, int min, int max, int fallback, out int value,
        out Error? problem)
    {
        value = fallback;
        problem = null;
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var parsed))
        {
            problem = new(ErrorCode.Invalid, $"{name} must be a whole number.");
            return false;
        }
        if (parsed < min || parsed > max)
        {
            problem = new(ErrorCode.Invalid, $"{name} must be between {min} and {max}.");
            return false;
        }
        value = parsed;
        return true;
    }
}
