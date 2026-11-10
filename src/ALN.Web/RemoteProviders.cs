using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALN.Contracts;

namespace ALN.Web;

/// <summary>
/// The two providers that hand the work to somebody else's index: DeepSeek's Search &amp; Fetch, and Tavily's search
/// and extract.
///
/// Both go through <see cref="INetAccess"/> with a target name, so their traffic lands in the same audit line, the same
/// ceilings and the same private-address rule as anything else — a provider is not a hole in the port, it is a caller
/// of it. Neither knows that the other exists.
/// </summary>
public sealed class DeepSeekProvider : ISearchProvider, IFetchProvider
{
    public const string ProviderName = "deepseek";

    private const string SearchPath = "/v1/tools/search";
    private const string FetchPath = "/v1/tools/fetch";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly INetAccess _net;
    private readonly string _target;
    private readonly int _count;
    private readonly string? _localization;
    private readonly string _caller;

    /// <summary>
    /// <paramref name="localization"/> is an <b>IP address</b> the provider uses to localise results. Empty by default:
    /// filling it tells a third party roughly where this deployment is, which is a decision and not a default.
    /// </summary>
    public DeepSeekProvider(INetAccess net, string target, int count = 8, string? localization = null,
        string caller = "web:deepseek")
    {
        _net = net;
        _target = target;
        _count = count;
        _localization = localization;
        _caller = caller;
    }

    public string Name => ProviderName;

    public async ValueTask<Result<WebSearchResults>> SearchAsync(WebSearchQuery query, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var payload = new Dictionary<string, object?>
        {
            ["query"] = Compose(query),
            ["count"] = Math.Clamp(query.MaxResults ?? _count, 1, 50),
            // Off, always: the body of a page is what web_fetch is for, and ten of them at once is a round spent.
            ["need_content"] = false,
        };
        if (!string.IsNullOrWhiteSpace(query.Original) && query.Original!.Length <= 128)
            payload["ori_question"] = query.Original;
        if (!string.IsNullOrWhiteSpace(_localization)) payload["localization"] = _localization;

        var sent = await SendAsync(SearchPath, payload, cancellationToken).ConfigureAwait(false);
        if (!sent.Ok || sent.Value is null) return Result<WebSearchResults>.Failure(sent.Error!.FromNet(ProviderName));
        var response = sent.Value;
        if (response.Status != 200) return Result<WebSearchResults>.Failure(FromStatus(response, ProviderName));

        var hits = new List<WebHit>();
        try
        {
            using var document = JsonDocument.Parse(response.Text);
            if (document.RootElement.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array)
                foreach (var item in results.EnumerateArray())
                {
                    var url = Text(item, "url");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    hits.Add(new WebHit(
                        Title: Text(item, "title") ?? url,
                        Url: url,
                        Snippet: Text(item, "snippet"),
                        Summary: Text(item, "summary"),
                        Score: Number(item, "rerank_score"),
                        // This provider scores 0–3; the ceiling travels with the number so nobody divides it into a
                        // comparability that does not exist.
                        ScoreCeiling: 3,
                        Authority: Whole(item, "authority"),
                        PublishedAt: Date(item, "date"),
                        Site: Text(item, "site_name")));
                }
        }
        catch (JsonException ex)
        {
            return Result<WebSearchResults>.Failure(new WebFailure(WebErrorKind.Upstream,
                "deepseek answered with something that is not JSON.", ex.Message).ToError());
        }

        // An empty list is an answer: nothing was found. It is not an error, and a caller that treats it as one ends up
        // telling people the search is broken.
        return Result<WebSearchResults>.Success(new WebSearchResults(ProviderName, query.Query, hits,
            Stopwatch.GetElapsedTime(started)));
    }

    public async ValueTask<Result<WebPage>> FetchAsync(WebFetchRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var sent = await SendAsync(FetchPath, new Dictionary<string, object?> { ["url"] = request.Url },
            cancellationToken).ConfigureAwait(false);
        if (!sent.Ok || sent.Value is null) return Result<WebPage>.Failure(sent.Error!.FromNet(ProviderName));
        var response = sent.Value;
        if (response.Status != 200) return Result<WebPage>.Failure(FromStatus(response, ProviderName));

        string title;
        string markdown;
        try
        {
            using var document = JsonDocument.Parse(response.Text);
            title = Text(document.RootElement, "title") ?? request.Url;
            markdown = Text(document.RootElement, "content") ?? "";
        }
        catch (JsonException ex)
        {
            return Result<WebPage>.Failure(new WebFailure(WebErrorKind.Upstream,
                "deepseek answered with something that is not JSON.", ex.Message).ToError());
        }

        if (markdown.Trim().Length == 0)
            return Result<WebPage>.Failure(new WebFailure(WebErrorKind.NoContent,
                "That page came back with no readable content.", request.Url).ToError());

        var truncated = request.MaxChars is { } max && markdown.Length > max;
        if (truncated) markdown = markdown[..request.MaxChars!.Value];
        return Result<WebPage>.Success(new WebPage(request.Url, title, markdown, markdown.Length, truncated,
            Stopwatch.GetElapsedTime(started)));
    }

    /// <summary>
    /// The query as this provider reads it: operators appended to the text, because that is the only way this one
    /// expresses "only these sites" and "not older than a week". The caller's own words are left alone.
    /// </summary>
    private static string Compose(WebSearchQuery query)
    {
        var text = new StringBuilder(query.Query);
        if (query.Domains is { Count: > 0 })
            text.Append(' ').Append(string.Join(" OR ", query.Domains.Select(domain => $"site:{domain}")));
        if (query.Recency is { } recency && recency != WebRecency.Any)
        {
            var days = recency switch
            {
                WebRecency.Day => 1,
                WebRecency.Week => 7,
                WebRecency.Month => 31,
                _ => 365,
            };
            text.Append(" after:")
                .Append(DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    private async ValueTask<Result<NetResponse>> SendAsync(string path, object payload,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        return await _net.SendAsync(new NetRequest(_target, "POST", path,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body: body, Caller: _caller), cancellationToken).ConfigureAwait(false);
    }

    internal static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var parsed)
            ? parsed
            : null;

    internal static int? Whole(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    internal static DateTimeOffset? Date(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// How this provider's status codes read in our vocabulary. 402 is the one worth naming: "the account has no
    /// balance" looks like any other failure and is the one no amount of retrying fixes.
    /// </summary>
    internal static Error FromStatus(NetResponse response, string provider)
    {
        var kind = response.Status switch
        {
            400 or 413 => WebErrorKind.InvalidUrl,
            401 or 402 or 403 => response.Status == 402 ? WebErrorKind.Quota : WebErrorKind.NotConfigured,
            404 => WebErrorKind.NoContent,
            408 or 504 => WebErrorKind.Timeout,
            429 => WebErrorKind.RateLimited,
            >= 500 => WebErrorKind.Upstream,
            _ => WebErrorKind.Upstream,
        };
        if (response.Status == 403) kind = WebErrorKind.Blocked;
        return new WebFailure(kind, $"{provider} answered {response.Status}.",
            Excerpt(response.Text), Retryable: kind is WebErrorKind.RateLimited or WebErrorKind.Upstream or WebErrorKind.Timeout)
            .ToError();
    }

    /// <summary>What the provider said about the refusal, short enough to keep. The useful part is usually in there.</summary>
    internal static string Excerpt(string body) =>
        body.Length <= 300 ? body : body[..300] + "…";
}

/// <summary>
/// Tavily: one search endpoint with a wide parameter surface, and an extract endpoint for the page this search pointed
/// at.
///
/// Two things are pinned in code rather than exposed: <c>include_answer</c> and <c>include_raw_content</c>. The first
/// asks Tavily's own model to answer the question before ours reads it — two translations and two places to be wrong;
/// the second fills the result list with whole pages, which is exactly what the second step is for.
/// </summary>
public sealed class TavilyProvider : ISearchProvider, IFetchProvider
{
    public const string ProviderName = "tavily";

    private const string SearchPath = "/search";
    private const string ExtractPath = "/extract";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Tavily marks chunk boundaries inline; they are noise once the text is in one field.</summary>
    private static readonly Regex ChunkMarkers = new(@"<chunk \d+>|\[…\]|\[\.\.\.\]", RegexOptions.Compiled);

    private readonly INetAccess _net;
    private readonly string _target;
    private readonly int _maxResults;
    private readonly string _depth;
    private readonly string _topic;
    private readonly string? _country;
    private readonly string? _language;
    private readonly string _caller;

    public TavilyProvider(INetAccess net, string target, int maxResults = 8, string depth = "basic",
        string topic = "general", string? country = null, string? language = null, string caller = "web:tavily")
    {
        _net = net;
        _target = target;
        _maxResults = maxResults;
        _depth = depth;
        _topic = topic;
        _country = country;
        _language = language;
        _caller = caller;
    }

    public string Name => ProviderName;

    public async ValueTask<Result<WebSearchResults>> SearchAsync(WebSearchQuery query, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query.Query,
            ["search_depth"] = _depth,
            ["topic"] = _topic,
            ["max_results"] = Math.Clamp(query.MaxResults ?? _maxResults, 1, 20),
            // Both pinned false: see the class note.
            ["include_answer"] = false,
            ["include_raw_content"] = false,
            // A date is cheap and often decisive; filtering by it is not, so the window only orders.
            ["include_published_date"] = true,
        };
        if (query.Recency is { } recency && recency != WebRecency.Any)
            payload["time_range"] = recency switch
            {
                WebRecency.Day => "day",
                WebRecency.Week => "week",
                WebRecency.Month => "month",
                _ => "year",
            };
        if (query.Domains is { Count: > 0 }) payload["include_domains"] = query.Domains;
        if (!string.IsNullOrWhiteSpace(_country)) payload["country"] = _country;
        if (!string.IsNullOrWhiteSpace(_language)) payload["language"] = _language;

        var sent = await SendAsync(SearchPath, payload, cancellationToken).ConfigureAwait(false);
        if (!sent.Ok || sent.Value is null) return Result<WebSearchResults>.Failure(sent.Error!.FromNet(ProviderName));
        var response = sent.Value;
        if (response.Status != 200) return Result<WebSearchResults>.Failure(FromStatus(response));

        var hits = new List<WebHit>();
        try
        {
            using var document = JsonDocument.Parse(response.Text);
            if (document.RootElement.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array)
                foreach (var item in results.EnumerateArray())
                {
                    var url = DeepSeekProvider.Text(item, "url");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    var content = DeepSeekProvider.Text(item, "content");
                    hits.Add(new WebHit(
                        Title: DeepSeekProvider.Text(item, "title") ?? url,
                        Url: url,
                        // This provider has one snippet field; it is the whole of what a caller gets without fetching.
                        Snippet: content,
                        Summary: null,
                        Score: DeepSeekProvider.Number(item, "score"),
                        ScoreCeiling: 1,
                        Authority: null,
                        PublishedAt: DeepSeekProvider.Date(item, "published_date"),
                        Site: null));
                }
        }
        catch (JsonException ex)
        {
            return Result<WebSearchResults>.Failure(new WebFailure(WebErrorKind.Upstream,
                "tavily answered with something that is not JSON.", ex.Message).ToError());
        }

        return Result<WebSearchResults>.Success(new WebSearchResults(ProviderName, query.Query, hits,
            Stopwatch.GetElapsedTime(started)));
    }

    public async ValueTask<Result<WebPage>> FetchAsync(WebFetchRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var sent = await SendAsync(ExtractPath, new Dictionary<string, object?>
        {
            ["urls"] = new[] { request.Url },
            ["extract_depth"] = "basic",
        }, cancellationToken).ConfigureAwait(false);
        if (!sent.Ok || sent.Value is null) return Result<WebPage>.Failure(sent.Error!.FromNet(ProviderName));
        var response = sent.Value;
        if (response.Status != 200) return Result<WebPage>.Failure(FromStatus(response));

        string? markdown = null;
        string? title = null;
        try
        {
            using var document = JsonDocument.Parse(response.Text);
            if (document.RootElement.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array)
                foreach (var item in results.EnumerateArray())
                {
                    markdown = DeepSeekProvider.Text(item, "raw_content") ?? DeepSeekProvider.Text(item, "content");
                    title = DeepSeekProvider.Text(item, "title");
                    break;
                }
        }
        catch (JsonException ex)
        {
            return Result<WebPage>.Failure(new WebFailure(WebErrorKind.Upstream,
                "tavily answered with something that is not JSON.", ex.Message).ToError());
        }

        if (string.IsNullOrWhiteSpace(markdown))
            return Result<WebPage>.Failure(new WebFailure(WebErrorKind.NoContent,
                "That page came back with no readable content.", request.Url).ToError());

        markdown = ChunkMarkers.Replace(markdown!, " ");
        var truncated = request.MaxChars is { } max && markdown.Length > max;
        if (truncated) markdown = markdown[..request.MaxChars!.Value];
        return Result<WebPage>.Success(new WebPage(request.Url, title, markdown, markdown.Length, truncated,
            Stopwatch.GetElapsedTime(started)));
    }

    private async ValueTask<Result<NetResponse>> SendAsync(string path, object payload,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        return await _net.SendAsync(new NetRequest(_target, "POST", path,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body: body, Caller: _caller), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Its four ways of saying no. 432 and 433 are the plan and the pay-as-you-go limit — the ones where waiting is
    /// pointless and the account owner has to do something. 434 is capacity, which is the one that is worth a retry.
    /// </summary>
    internal static Error FromStatus(NetResponse response)
    {
        var retryable = response.Status is 429 or 434 or >= 500;
        var kind = response.Status switch
        {
            400 or 422 => WebErrorKind.InvalidUrl,
            401 => WebErrorKind.NotConfigured,
            429 => WebErrorKind.RateLimited,
            432 or 433 => WebErrorKind.Quota,
            434 => WebErrorKind.RateLimited,
            >= 500 => WebErrorKind.Upstream,
            _ => WebErrorKind.Upstream,
        };
        return new WebFailure(kind, $"tavily answered {response.Status}.", Reason(response.Text), Retryable: retryable)
            .ToError();
    }

    /// <summary>Tavily puts its message in <c>detail.error</c>, and in <c>detail</c> as an array of validation issues.</summary>
    private static string Reason(string body)
    {
        if (body.Length == 0) return "";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.Object &&
                    detail.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? "";
                return DeepSeekProvider.Excerpt(detail.ToString());
            }
        }
        catch (JsonException)
        {
            // Not JSON, or not the shape expected: return what came, which is the only honest thing left.
        }
        return DeepSeekProvider.Excerpt(body);
    }
}

/// <summary>Turning the port's failure into the web layer's vocabulary. The port already names its kind in Detail.</summary>
internal static class NetToWeb
{
    public static Error FromNet(this Error error, string provider)
    {
        var kind = error.Detail?.StartsWith(nameof(NetErrorKind.Timeout), StringComparison.Ordinal) == true
            ? WebErrorKind.Timeout
            : error.Detail?.StartsWith(nameof(NetErrorKind.Throttled), StringComparison.Ordinal) == true
                ? WebErrorKind.RateLimited
                : WebErrorKind.Upstream;
        return new WebFailure(kind, $"{provider} could not be reached.", error.Detail, Retryable: error.Retryable)
            .ToError();
    }
}
