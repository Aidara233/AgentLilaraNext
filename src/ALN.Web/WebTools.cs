using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Web;

/// <summary>
/// The two tools a model gets: <c>web_search</c> and <c>web_fetch</c>.
///
/// They are separate verbs because they cost different things and answer different questions. Search is a query that
/// returns pointers; fetch turns one of those pointers into text. Leaving search's body content out (which both
/// providers allow, and which would otherwise fill a round with ten whole pages) is what makes the two-step worth it:
/// three searches and two fetches is a few thousand tokens, while one search with bodies is a dozen.
///
/// A tool only appears when the deployment has a provider for it — a verb that can never succeed costs a round and
/// teaches the model to distrust the list.
/// </summary>
public sealed class WebTools(IWebGateway gateway, WebOptions options) : ISessionToolSource
{
    public const string SourceId = "system.web-tools";
    public const string SearchToolName = "web_search";
    public const string FetchToolName = "web_fetch";

    /// <summary>Most of a page that reaches the model, whatever the provider or the gateway said.</summary>
    private const int MaxPageChars = 48_000;

    private readonly IWebGateway _gateway = gateway;
    private readonly WebOptions _options = options;

    public string Id => SourceId;

    public static readonly ModelToolDefinition SearchDefinition = new(SearchToolName,
        "Search the web. Returns a numbered list of results: title, URL, and a short extract from each. It does not "
        + "return whole pages — pick the ones worth reading and pass their URL to web_fetch. Use it when the question "
        + "is about something current, or something you do not know, rather than something that would be a guess.",
        """
        {"type":"object","properties":{
          "query":{"type":"string","description":"What to look for, in words. Any operator the provider supports can go in it."},
          "maxResults":{"type":"integer","minimum":1,"maximum":20,"description":"How many results at most."},
          "recency":{"type":"string","enum":["any","day","week","month","year"],"description":"How far back to look. Omit for any time."},
          "domains":{"type":"array","items":{"type":"string"},"description":"Only these sites, e.g. [\"docs.example.com\"]. Optional."}},
         "required":["query"],"additionalProperties":false}
        """);

    public static readonly ModelToolDefinition FetchDefinition = new(FetchToolName,
        "Read one web page and get it back as Markdown: headings, lists, links and all. Give the full URL — including "
        + "one a person pasted into the conversation, or one web_search just returned. Use this when you need what a "
        + "specific page actually says; use web_search when you do not know which page yet.",
        """
        {"type":"object","properties":{
          "url":{"type":"string","description":"The full address, starting with http:// or https://."},
          "maxChars":{"type":"integer","minimum":500,"maximum":200000,"description":"Keep at most this many characters. Omit for the deployment default."}},
         "required":["url"],"additionalProperties":false}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = new TurnBudget(_options);
        var offers = new List<SessionToolOffer>(2);
        if (_gateway.SearchProvider is not null)
            offers.Add(new(new SearchTool(this, budget), SearchDefinition, ActsExternally: true));
        if (_gateway.FetchProvider is not null)
            offers.Add(new(new FetchTool(this, budget), FetchDefinition, ActsExternally: true));
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    /// <summary>
    /// How many of each this round may make. Per turn and not global: a model that searched six times has not learned
    /// anything the seventh search will teach it, and the ceiling is what turns that into an answer instead of a loop.
    /// </summary>
    private sealed class TurnBudget(WebOptions options)
    {
        private int _searches;
        private int _fetches;

        public bool TakeSearch(out string? refusal)
        {
            refusal = null;
            if (_searches < options.SearchesPerTurn)
            {
                _searches++;
                return true;
            }
            refusal = $"This turn has already searched {options.SearchesPerTurn} time(s). Work with what came back, "
                      + "or ask for one narrow enough to be worth the next turn.";
            return false;
        }

        public bool TakeFetch(out string? refusal)
        {
            refusal = null;
            if (_fetches < options.FetchesPerTurn)
            {
                _fetches++;
                return true;
            }
            refusal = $"This turn has already fetched {options.FetchesPerTurn} page(s). Work with what came back.";
            return false;
        }
    }

    private sealed class SearchTool(WebTools owner, TurnBudget budget) : ISessionTool
    {
        public string Name => SearchToolName;

        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call,
            CancellationToken cancellationToken)
        {
            if (Parse(call.ArgumentsJson) is not { } arguments) return BadJson();
            var query = Text(arguments, "query");
            if (string.IsNullOrWhiteSpace(query))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "web_search needs a query."));
            if (!budget.TakeSearch(out var refusal))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Conflict, refusal!));

            var recency = Text(arguments, "recency")?.ToLowerInvariant() switch
            {
                "day" => WebRecency.Day,
                "week" => WebRecency.Week,
                "month" => WebRecency.Month,
                "year" => WebRecency.Year,
                _ => (WebRecency?)null,
            };
            var domains = arguments.TryGetProperty("domains", out var domainElement) &&
                          domainElement.ValueKind == JsonValueKind.Array
                ? domainElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).ToArray()
                : null;
            var max = arguments.TryGetProperty("maxResults", out var maxElement) && maxElement.TryGetInt32(out var parsed)
                ? parsed
                : (int?)null;

            var found = await owner._gateway.SearchAsync(
                // Original is left null: the words a person actually used are in the round's request, not in what the
                // model wrote here, and passing the compressed query as its own origin would be a field filled with
                // itself. Wiring it to the round's user input is a one-line change once that is plumbed through.
                new WebSearchQuery(query!, max, recency, domains),
                new WebCall("model"), cancellationToken).ConfigureAwait(false);
            if (!found.Ok || found.Value is null) return Result<SessionToolResult>.Failure(found.Error!);

            var results = found.Value;
            if (results.Hits.Count == 0)
                // An empty answer is an answer: saying "nothing was found" is different from reporting a failure, and
                // only one of them tells the model to try different words instead of giving up.
                return Ok($"{results.Provider} found nothing for “{results.Query}”. Try different words, or narrower ones.");

            var builder = new StringBuilder();
            builder.Append(results.Provider).Append(" — ").Append(results.Hits.Count).Append(" result(s) for “")
                .Append(results.Query).AppendLine("”");
            for (var index = 0; index < results.Hits.Count; index++)
            {
                var hit = results.Hits[index];
                builder.AppendLine().Append(index + 1).Append(". ").AppendLine(hit.Title);
                builder.Append("   ").AppendLine(hit.Url);
                var extract = hit.Summary ?? hit.Snippet;
                if (!string.IsNullOrWhiteSpace(extract))
                    builder.Append("   ").AppendLine(Shorten(extract!, 400));
                if (hit.PublishedAt is { } published)
                    builder.Append("   published ").AppendLine(published.ToString("yyyy-MM-dd"));
            }
            builder.AppendLine().Append("Pass a URL above to web_fetch to read the whole page.");
            return Ok(builder.ToString());
        }
    }

    private sealed class FetchTool(WebTools owner, TurnBudget budget) : ISessionTool
    {
        public string Name => FetchToolName;

        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call,
            CancellationToken cancellationToken)
        {
            if (Parse(call.ArgumentsJson) is not { } arguments) return BadJson();
            var url = Text(arguments, "url");
            if (string.IsNullOrWhiteSpace(url))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "web_fetch needs a url."));
            if (!budget.TakeFetch(out var refusal))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Conflict, refusal!));

            var max = arguments.TryGetProperty("maxChars", out var maxElement) && maxElement.TryGetInt32(out var parsed)
                ? parsed
                : (int?)null;

            var fetched = await owner._gateway.FetchAsync(new WebFetchRequest(url!, max), new WebCall("model"),
                cancellationToken).ConfigureAwait(false);
            if (!fetched.Ok || fetched.Value is null) return Result<SessionToolResult>.Failure(fetched.Error!);

            var page = fetched.Value;
            var text = page.Markdown.Length > MaxPageChars ? page.Markdown[..MaxPageChars] : page.Markdown;
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(page.Title)) builder.AppendLine(page.Title!.Trim());
            builder.Append("source: ").AppendLine(page.Url);
            if (page.Truncated || text.Length < page.Markdown.Length)
                builder.AppendLine("(the page was cut short — fetch it with a larger maxChars if the rest matters)");
            builder.AppendLine().Append(text);
            return Ok(builder.ToString());
        }
    }

    private static JsonElement? Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Result<SessionToolResult> BadJson() =>
        Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "The arguments were not valid JSON."));

    private static Result<SessionToolResult> Ok(string content) =>
        Result<SessionToolResult>.Success(new SessionToolResult(content));

    private static string? Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim()
            : null;

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text.ReplaceLineEndings(" ") : text[..max].ReplaceLineEndings(" ") + "…";
}
