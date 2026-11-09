using System.Text;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// One tool, and it is the only way a model reaches the network: <c>net_fetch</c>.
///
/// The argument is a <b>name</b>, not a URL. That is the whole design of this tool and why it is worth a tool at all:
/// the model can read a page somebody approved and cannot reach anywhere else, and the sentence "fetch
/// https://example.com" is not one this shape can express. Its mistakes are also cheap and legible — "no such target"
/// with the list of names available is a fixable answer, while a hostname it half-remembered is not.
///
/// It is offered only when there is at least one target. A tool that can never succeed costs a round and teaches the
/// model to distrust the whole list (docs/27 §6, the same reason <c>ssh_save</c> is not shown at agent level).
/// </summary>
public sealed class NetTools(INetAccess net) : ISessionToolSource
{
    public const string SourceId = "system.net-tools";
    public const string FetchToolName = "net_fetch";

    /// <summary>
    /// What one fetch hands back at most. The port's own ceiling is about protecting this process; this one is about
    /// protecting the next request — a page that is 1 MiB of markup costs more than the round is worth, and the tool
    /// says it stopped rather than pretending the world is short.
    /// </summary>
    private const int MaxToolChars = 32_000;

    private readonly INetAccess _net = net;

    public string Id => SourceId;

    public static readonly ModelToolDefinition FetchDefinition = new(FetchToolName,
        "Read something over HTTP from one of the places this deployment allows. You pass the name of a target, not an "
        + "address: the names are fixed by whoever runs this system, and a name you were not given does not exist. "
        + "The answer is the response body as text plus its status — a 404 is a successful call that found nothing. "
        + "Long bodies come back truncated, and the result says so.",
        """
        {"type":"object","properties":{
          "target":{"type":"string","description":"The name of an allowed target. If you do not know one, ask; do not guess a hostname."},
          "path":{"type":"string","description":"Path and query, starting with / — e.g. /releases?page=2. Relative to the target, never a full URL."},
          "method":{"type":"string","enum":["GET","HEAD","POST"],"description":"HTTP method. Default GET."},
          "body":{"type":"string","description":"Request body as text, for POST. Omit otherwise."}},
         "required":["target","path"]}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> offers = _net.Targets.Count == 0
            ? []
            : [new(new FetchTool(this), FetchDefinition, ActsExternally: true)];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private sealed class FetchTool(NetTools owner) : ISessionTool
    {
        public string Name => FetchToolName;

        /// <summary>Fetching something is a step, not a reply: the turn goes on and the model decides what to say.</summary>
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call,
            CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);

            var target = ToolArguments.Text(arguments, "target")?.Trim();
            if (string.IsNullOrEmpty(target))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "net_fetch needs a target name."));

            var path = ToolArguments.Text(arguments, "path")?.Trim() ?? "";
            if (path.Length == 0) path = "/";

            var method = ToolArguments.Text(arguments, "method")?.Trim();
            method = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
            var body = ToolArguments.Text(arguments, "body");

            var sent = await owner._net.SendAsync(new NetRequest(target, method, path,
                    Body: body is null ? null : Encoding.UTF8.GetBytes(body),
                    Caller: "model"), cancellationToken).ConfigureAwait(false);
            if (!sent.Ok || sent.Value is null) return Describe(sent.Error!, owner._net.Targets);

            var response = sent.Value;
            var builder = new StringBuilder();
            builder.Append($"{method} {target}{path} → {response.Status} {response.ContentType ?? "(no type)"}, ")
                .Append($"{response.Bytes} byte(s)");

            var text = response.Text;
            if (text.Length > MaxToolChars) text = text[..MaxToolChars];
            if (response.Truncated || text.Length < response.Text.Length)
                builder.Append(", cut short — ask for a narrower path if you need the rest");

            builder.AppendLine().AppendLine();
            builder.Append(text);
            return ToolArguments.Ok(builder.ToString());
        }

        /// <summary>
        /// Turns a refusal into something the model can act on. The list of names is attached to "no such target"
        /// because that is the one mistake a model actually makes here, and a name it can copy is a mistake it does
        /// not make twice.
        /// </summary>
        private static Result<SessionToolResult> Describe(Error error, IReadOnlyList<string> targets)
        {
            if (error.Detail is null ||
                !error.Detail.StartsWith(nameof(NetErrorKind.NoSuchTarget), StringComparison.Ordinal))
                return Result<SessionToolResult>.Failure(error);

            var known = targets.Count == 0 ? "none are configured" : "available: " + string.Join(", ", targets);
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "There is no target by that name.", known));
        }
    }
}
