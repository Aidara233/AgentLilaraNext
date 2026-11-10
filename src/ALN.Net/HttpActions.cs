using System.Text;
using System.Text.Json;
using ALN.Contracts;

namespace ALN.Net;

/// <summary>
/// The raw door: one HTTP request to an address the caller names, and the answer as it came.
///
/// This is not the page reader (<c>web_fetch</c>) and not the allowlisted port (<c>net.targets.json</c>). It is what
/// sits under both when neither is the right shape: "call this endpoint with this body and show me the status, the
/// headers and the bytes". Nothing is pre-approved here — the record is the control — which is why the four rules it
/// does keep are the ones that would otherwise make it a different feature: no <c>Host</c> (a request that says one
/// thing and goes to another), a size ceiling, a deadline, and an audit line whose header values are never written.
/// </summary>
public static class HttpActions
{
    public const string Request = "http.request";

    /// <summary>
    /// One policy for every raw call, because there is no target to carry one. Private addresses are allowed by
    /// default: the reason this door exists is that the address somebody just handed over may be anywhere, and an
    /// allowlist in front of it would be the other door again.
    /// </summary>
    public static readonly NetPolicy RawPolicy = new()
    {
        Methods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
        AllowPrivate = true,
        MaxResponseBytes = 1024 * 1024,
        TimeoutSeconds = 30,
        CallsPerMinute = 60,
        MaxConcurrent = 4,
        // Following a redirect is allowed here and checked hop by hop inside the port: it stays on the origin of the
        // address that was given, and the answer reports where it ended up.
        FollowRedirects = true,
        MaxRedirects = 3,
    };

    public static Result Register(IControlPlane plane, IAdHocNetAccess net)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        if (net is null) return Result.Failure(new(ErrorCode.Invalid, "A network port is required."));

        return plane.Register(new(Request,
            "Send one HTTP request to an address and get the answer back as it came: status, headers, body, and where "
            + "the request ended up if it was redirected. Use it to call an API or to check what a server actually "
            + "says; use web_fetch to read a page as text. The address is not checked against any list — every call "
            + "is recorded instead.",
            Capabilities.NetRequest, true,
            ParamsJson: """
            {"type":"object","properties":{
              "url":{"type":"string","description":"Absolute http or https URL, no user name in it."},
              "method":{"type":"string","enum":["GET","HEAD","POST","PUT","PATCH","DELETE","OPTIONS"],"description":"Default GET."},
              "headers":{"type":"object","additionalProperties":{"type":"string"},"description":"Request headers. Host, Content-Length, Transfer-Encoding, Connection, Authorization and Cookie are refused."},
              "body":{"type":"string","description":"Request body as text. Set contentType when it is JSON."},
              "contentType":{"type":"string","description":"Shorthand for a Content-Type header, e.g. application/json."},
              "timeoutSeconds":{"type":"integer","minimum":1,"maximum":600},
              "maxBytes":{"type":"integer","minimum":1,"maximum":8388608}},
             "required":["url"],"additionalProperties":false}
            """), (arguments, cancellationToken) =>
            SendAsync(net, arguments, "action:http.request", cancellationToken));
    }

    /// <summary>
    /// One call, from arguments to the answer. Shared with the model's tool so the two cannot drift: a person in the
    /// web UI and a model sending the same request get the same rules and the same shape of answer.
    /// </summary>
    public static async ValueTask<Result<JsonElement>> SendAsync(IAdHocNetAccess net, JsonElement arguments,
        string caller, CancellationToken cancellationToken)
    {
        var parsed = Parse(arguments, caller);
        if (!parsed.Ok || parsed.Value is null)
            return Result<JsonElement>.Failure(parsed.Error ??
                new Error(ErrorCode.Invalid, "The request could not be read."));
        var request = parsed.Value;

        var sent = await net.SendAsync(request, RawPolicy, cancellationToken).ConfigureAwait(false);
        if (!sent.Ok || sent.Value is null) return Result<JsonElement>.Failure(sent.Error!);
        return Result<JsonElement>.Success(ToJson(sent.Value));
    }

    /// <summary>What one call needs, from a JSON object. Exposed so the tool validates its arguments the same way.</summary>
    public static Result<AdHocRequest> Parse(JsonElement arguments, string caller)
    {
        if (!Text(arguments, "url", out var url))
            return Result<AdHocRequest>.Failure(new(ErrorCode.Invalid, "http.request needs a url."));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address))
            return Result<AdHocRequest>.Failure(new(ErrorCode.Invalid,
                "That is not an absolute URL.", "pass something like https://api.example.com/v1/status"));

        var method = Text(arguments, "method", out var declaredMethod) ? declaredMethod.ToUpperInvariant() : "GET";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (arguments.TryGetProperty("headers", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in headerElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    return Result<AdHocRequest>.Failure(new(ErrorCode.Invalid,
                        $"Header '{property.Name}' must be a string."));
                headers[property.Name] = property.Value.GetString() ?? "";
            }
        }

        var body = Text(arguments, "body", out var declaredBody) ? declaredBody : null;
        if (Text(arguments, "contentType", out var contentType) &&
            !headers.ContainsKey("Content-Type") && body is not null)
            headers["Content-Type"] = contentType;

        if (!Number(arguments, "timeoutSeconds", 1, 600, out var timeout, out var numberProblem))
            return Result<AdHocRequest>.Failure(numberProblem!);
        if (!Number(arguments, "maxBytes", 1, HttpNetAccess.HardCeilingBytes, out var maxBytes, out numberProblem))
            return Result<AdHocRequest>.Failure(numberProblem!);

        return Result<AdHocRequest>.Success(new AdHocRequest(address, method, headers.Count == 0 ? null : headers,
            body is null ? null : Encoding.UTF8.GetBytes(body), maxBytes, timeout, caller));
    }

    /// <summary>The answer as JSON: status, headers, body, and where it ended up.</summary>
    public static JsonElement ToJson(NetResponse response) => JsonSerializer.SerializeToElement(new
    {
        status = response.Status,
        contentType = response.ContentType,
        headers = response.Headers ?? new Dictionary<string, string>(),
        body = response.Text,
        bytes = response.Bytes,
        truncated = response.Truncated,
        finalUrl = response.FinalUri.ToString(),
        elapsedMs = Math.Round(response.Elapsed.TotalMilliseconds, 1),
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static bool Text(JsonElement arguments, string name, out string value)
    {
        value = "";
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()?.Trim() ?? "";
        return value.Length > 0;
    }

    private static bool Number(JsonElement arguments, string name, int min, int max, out int? value, out Error? problem)
    {
        value = null;
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
