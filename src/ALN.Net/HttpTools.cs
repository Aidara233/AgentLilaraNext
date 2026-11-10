using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Net;

/// <summary>
/// The raw HTTP door, as one tool: <c>http_request</c>.
///
/// It exists next to <c>web_fetch</c> because they answer different questions. <c>web_fetch</c> asks "what does this
/// page say" and gets text that somebody else already extracted; this one asks "what does this endpoint reply" and
/// gets the status, the headers and the bytes exactly as they arrived. A model that wants to call an API, check a
/// redirect, read a <c>Location</c> or see why something is 422 needs this one, and a model that wants to read an
/// article needs the other.
///
/// The four things it refuses are in <see cref="HttpActions"/>; everything else is the caller's business, and the
/// audit line is what stands in place of an allowlist.
/// </summary>
public sealed class HttpTools(IAdHocNetAccess net) : ISessionToolSource
{
    public const string SourceId = "system.http-tools";
    public const string RequestToolName = "http_request";

    /// <summary>
    /// How much of a response body reaches the model. A tool result is a step's worth of context: beyond this the
    /// answer stops being an answer and becomes the next request's problem.
    /// </summary>
    private const int MaxBodyChars = 32_000;

    /// <summary>How many response headers are shown. The interesting ones are at the top; the rest are noise.</summary>
    private const int MaxHeadersShown = 12;

    private readonly IAdHocNetAccess _net = net;

    public string Id => SourceId;

    public static readonly ModelToolDefinition RequestDefinition = new(RequestToolName,
        "Send one HTTP request to any address and get back exactly what the server answered: status code, headers, "
        + "body, and the URL it ended up at if it was redirected. Use this to call an API, to check what a redirect "
        + "points at, or to see a response you cannot get any other way. It is not the page reader — if you want to "
        + "read a web page as text, fetch it through web_fetch instead, which returns clean text rather than markup.",
        """
        {"type":"object","properties":{
          "url":{"type":"string","description":"Absolute http or https URL. A user may paste one straight from their message."},
          "method":{"type":"string","enum":["GET","HEAD","POST","PUT","PATCH","DELETE","OPTIONS"],"description":"Default GET."},
          "headers":{"type":"object","additionalProperties":{"type":"string"},"description":"Request headers. Host, Content-Length, Transfer-Encoding, Connection, Authorization and Cookie are refused."},
          "body":{"type":"string","description":"Request body as text."},
          "contentType":{"type":"string","description":"Shorthand for a Content-Type header, e.g. application/json."},
          "timeoutSeconds":{"type":"integer","minimum":1,"maximum":600,"description":"Default 30."},
          "maxBytes":{"type":"integer","minimum":1,"maximum":8388608,"description":"Ceiling on the body. Default 1 MiB."}},
         "required":["url"],"additionalProperties":false}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> offers = [new(new RequestTool(this), RequestDefinition, ActsExternally: true)];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private sealed class RequestTool(HttpTools owner) : ISessionTool
    {
        public string Name => RequestToolName;

        /// <summary>A request is a step, not a reply: the turn goes on and the model decides what to say about it.</summary>
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call,
            CancellationToken cancellationToken)
        {
            JsonElement arguments;
            try
            {
                arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson)
                    .RootElement.Clone();
            }
            catch (JsonException)
            {
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "The arguments were not valid JSON."));
            }

            var sent = await HttpActions.SendAsync(owner._net, arguments, "tool:http_request", cancellationToken)
                .ConfigureAwait(false);
            if (!sent.Ok || sent.Value.ValueKind != JsonValueKind.Object)
                return Result<SessionToolResult>.Failure(sent.Error!);

            return Result<SessionToolResult>.Success(new SessionToolResult(Render(sent.Value)));
        }

        /// <summary>
        /// The answer as text rather than JSON, because a model reads a status line and a body faster than it reads
        /// six levels of nesting — and because the body is usually the point.
        /// </summary>
        private static string Render(JsonElement answer)
        {
            var builder = new StringBuilder();
            builder.Append("HTTP ")
                .Append(answer.TryGetProperty("status", out var status) ? status.GetInt32() : 0)
                .Append(" · ")
                .Append(answer.TryGetProperty("contentType", out var type) && type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : "(no content type)")
                .Append(" · ")
                .Append(answer.TryGetProperty("bytes", out var bytes) ? bytes.GetInt64() : 0)
                .Append(" byte(s) · ")
                .Append(answer.TryGetProperty("elapsedMs", out var elapsed) ? elapsed.GetDouble() : 0)
                .AppendLine(" ms");

            if (answer.TryGetProperty("finalUrl", out var finalUrl) && finalUrl.ValueKind == JsonValueKind.String &&
                finalUrl.GetString() is { } landed)
                builder.Append("landed at: ").AppendLine(landed);

            if (answer.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                var shown = headers.EnumerateObject().Take(MaxHeadersShown)
                    .Select(header => $"{header.Name}: {Truncate(header.Value.GetString() ?? "", 120)}");
                builder.Append("headers: ").AppendLine(string.Join("; ", shown));
            }

            var body = answer.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString() ?? ""
                : "";
            if (body.Length > MaxBodyChars) body = body[..MaxBodyChars];
            var truncated = answer.TryGetProperty("truncated", out var truncatedElement) &&
                            truncatedElement.ValueKind == JsonValueKind.True;
            if (truncated || body.Length < (answer.TryGetProperty("body", out var original) &&
                                            original.ValueKind == JsonValueKind.String
                    ? (original.GetString()?.Length ?? 0)
                    : 0))
                builder.AppendLine("(the body was cut short — ask for less with maxBytes, or for a different path)");

            builder.AppendLine().Append(body);
            return builder.ToString();
        }

        private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
    }
}
