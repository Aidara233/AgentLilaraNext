using System.Text.Json;
using ALN.Contracts;

namespace ALN.Net;

/// <summary>One line of <c>net/targets.json</c> that could not be used, and why.</summary>
public sealed record NetTargetIssue(string Entry, string Problem);

/// <summary>What a read produced: the targets that survived, and the entries that did not.</summary>
public sealed record NetTargetLoad(IReadOnlyList<NetTarget> Targets, IReadOnlyList<NetTargetIssue> Issues);

/// <summary>
/// The file that says where this process may reach: <c>&lt;dataRoot&gt;/net/targets.json</c>.
///
/// A file rather than a database row, next to <c>api/server.json</c>, for the same reason: it is a deployment decision
/// that people review, diff and keep in version control. Missing means no targets — there is no shipped default,
/// because a default would be an address nobody chose.
///
/// A bad entry is <b>dropped and reported</b>, never guessed at. Accepting <c>"base": "example.com/x"</c> by
/// inventing a scheme, or <c>"maxBytes": 0</c> by calling it unlimited, would produce a running system that does not
/// match the file — and the file is the only thing anyone reading it can reason about.
/// </summary>
public static class NetTargetFile
{
    public const string FileName = "targets.json";

    public static string PathIn(string dataRoot) => Path.Combine(dataRoot, "net", FileName);

    /// <summary>Reads the file. A missing file is an empty list, not an error: "nothing may be reached" is a valid
    /// deployment, and it is the one a fresh data root gets.</summary>
    public static NetTargetLoad Read(string path)
    {
        var targets = new List<NetTarget>();
        var issues = new List<NetTargetIssue>();
        if (!File.Exists(path)) return new NetTargetLoad(targets, issues);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            issues.Add(new NetTargetIssue(FileName, ex.Message));
            return new NetTargetLoad(targets, issues);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("targets", out var list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new NetTargetIssue(FileName, "expected an object with a \"targets\" array"));
                return new NetTargetLoad(targets, issues);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var element in list.EnumerateArray())
            {
                var label = $"targets[{index++}]";
                var parsed = Parse(element, out var problem);
                if (parsed is null)
                {
                    issues.Add(new NetTargetIssue(label, problem ?? "unreadable"));
                    continue;
                }
                if (!seen.Add(parsed.Name))
                {
                    issues.Add(new NetTargetIssue(label, $"name '{parsed.Name}' appears more than once"));
                    continue;
                }
                targets.Add(parsed);
            }
        }

        return new NetTargetLoad(targets, issues);
    }

    /// <summary>
    /// Writes the whole file. Temp-then-rename, so a reader never sees half a configuration and a crash leaves the
    /// previous one in force.
    /// </summary>
    public static Result Write(string path, IEnumerable<NetTarget> targets)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.CreateDirectory(directory);
            var body = new
            {
                targets = targets.Select(t => new
                {
                    name = t.Name,
                    @base = t.Base.ToString(),
                    methods = t.Policy.Methods,
                    paths = t.Policy.Paths,
                    headers = t.Policy.Headers,
                    maxBytes = t.Policy.MaxResponseBytes,
                    timeoutSeconds = t.Policy.TimeoutSeconds,
                    followRedirects = t.Policy.FollowRedirects,
                    maxRedirects = t.Policy.MaxRedirects,
                    callsPerMinute = t.Policy.CallsPerMinute,
                    maxConcurrent = t.Policy.MaxConcurrent,
                    allowPrivate = t.Policy.AllowPrivate,
                    secret = t.SecretRef,
                }).ToArray(),
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            });
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, json);
            File.Move(temp, path, overwrite: true);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Result.Failure(new Error(ErrorCode.Unavailable, "Could not write the target file.", ex.Message));
        }
    }

    /// <summary>One entry. Null with a reason through <paramref name="problem"/> when it cannot be used; every field a
    /// person wrote is checked, and none is defaulted silently.</summary>
    private static NetTarget? Parse(JsonElement element, out string? problem)
    {
        problem = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = "expected an object";
            return null;
        }

        if (!element.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            problem = "\"name\" is required";
            return null;
        }
        var name = nameElement.GetString()!.Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
        {
            problem = "\"name\" must be a single word with no spaces";
            return null;
        }

        if (!element.TryGetProperty("base", out var baseElement) || baseElement.ValueKind != JsonValueKind.String)
        {
            problem = "\"base\" is required (e.g. https://example.com)";
            return null;
        }
        if (!Uri.TryCreate(baseElement.GetString()!.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            problem = "\"base\" must be an absolute http or https URL";
            return null;
        }
        if (baseUri.UserInfo.Length > 0)
        {
            problem = "\"base\" must not carry a user name or password — put the credential in \"secret\"";
            return null;
        }
        if (baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0)
        {
            problem = "\"base\" must not carry a query or fragment";
            return null;
        }

        var policy = new NetPolicy();
        if (element.TryGetProperty("methods", out var methodsElement))
        {
            if (!TryStrings(methodsElement, out var methods, out problem)) return null;
            if (methods.Count == 0)
            {
                problem = "\"methods\" is empty — leave it out to accept GET and HEAD, or list what is allowed";
                return null;
            }
            var bad = methods.FirstOrDefault(m => m is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS"));
            if (bad is not null)
            {
                problem = $"method '{bad}' is not one this surface speaks (GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS)";
                return null;
            }
            policy = policy with { Methods = methods };
        }
        if (element.TryGetProperty("paths", out var pathsElement))
        {
            if (!TryStrings(pathsElement, out var paths, out problem)) return null;
            var bad = paths.FirstOrDefault(p => !p.StartsWith('/'));
            if (bad is not null)
            {
                problem = $"path prefix '{bad}' must start with /";
                return null;
            }
            policy = policy with { Paths = paths };
        }
        if (element.TryGetProperty("headers", out var headersElement))
        {
            if (!TryStrings(headersElement, out var headers, out problem)) return null;
            policy = policy with { Headers = headers };
        }

        if (!TryInt(element, "maxBytes", 1, HttpNetAccess.HardCeilingBytes, policy.MaxResponseBytes, out var maxBytes, out problem))
            return null;
        if (!TryInt(element, "timeoutSeconds", 1, 600, policy.TimeoutSeconds, out var timeout, out problem)) return null;
        if (!TryInt(element, "callsPerMinute", 1, 100_000, policy.CallsPerMinute, out var rate, out problem)) return null;
        if (!TryInt(element, "maxConcurrent", 1, 64, policy.MaxConcurrent, out var concurrent, out problem)) return null;
        if (!TryInt(element, "maxRedirects", 0, 20, policy.MaxRedirects, out var redirects, out problem)) return null;

        var allowPrivate = false;
        if (element.TryGetProperty("allowPrivate", out var privateElement))
        {
            if (privateElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                problem = "\"allowPrivate\" must be true or false";
                return null;
            }
            allowPrivate = privateElement.GetBoolean();
        }
        var followRedirects = false;
        if (element.TryGetProperty("followRedirects", out var redirectElement))
        {
            if (redirectElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                problem = "\"followRedirects\" must be true or false";
                return null;
            }
            followRedirects = redirectElement.GetBoolean();
        }
        if (followRedirects && redirects == 0) redirects = 3;

        string? secret = null;
        if (element.TryGetProperty("secret", out var secretElement) && secretElement.ValueKind != JsonValueKind.Null)
        {
            if (secretElement.ValueKind != JsonValueKind.String || secretElement.GetString()!.Trim().Length == 0)
            {
                problem = "\"secret\" must be the name of a stored secret";
                return null;
            }
            secret = secretElement.GetString()!.Trim();
        }

        policy = policy with
        {
            MaxResponseBytes = maxBytes,
            TimeoutSeconds = timeout,
            CallsPerMinute = rate,
            MaxConcurrent = concurrent,
            FollowRedirects = followRedirects,
            MaxRedirects = redirects,
            AllowPrivate = allowPrivate,
        };
        return new NetTarget(name, baseUri, policy, secret);
    }

    private static bool TryStrings(JsonElement element, out IReadOnlyList<string> values, out string? problem)
    {
        problem = null;
        values = [];
        if (element.ValueKind != JsonValueKind.Array)
        {
            problem = "expected an array of strings";
            return false;
        }
        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || text.Trim().Length == 0)
            {
                problem = "expected an array of non-empty strings";
                return false;
            }
            list.Add(text.Trim());
        }
        values = list;
        return true;
    }

    private static bool TryInt(JsonElement element, string name, int min, int max, int fallback, out int value,
        out string? problem)
    {
        value = fallback;
        problem = null;
        if (!element.TryGetProperty(name, out var found) || found.ValueKind == JsonValueKind.Null) return true;
        if (found.ValueKind != JsonValueKind.Number || !found.TryGetInt32(out var parsed))
        {
            problem = $"\"{name}\" must be a whole number";
            return false;
        }
        if (parsed < min || parsed > max)
        {
            problem = $"\"{name}\" must be between {min} and {max}";
            return false;
        }
        value = parsed;
        return true;
    }
}
