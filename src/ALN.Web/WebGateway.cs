using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ALN.Contracts;

namespace ALN.Web;

/// <summary>
/// Which providers exist, in two tables. Search and fetch are separate maps on purpose: the arrangement that makes the
/// most sense in practice is one provider searching and another retrieving, and a single map would make that an error.
/// </summary>
public sealed class WebProviderRegistry : IWebProviderRegistry
{
    private readonly Dictionary<string, ISearchProvider> _search = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IFetchProvider> _fetch = new(StringComparer.OrdinalIgnoreCase);

    public void Add(ISearchProvider provider) => _search[provider.Name] = provider;

    public void Add(IFetchProvider provider) => _fetch[provider.Name] = provider;

    public ISearchProvider? Search(string? name) =>
        name is not null && _search.TryGetValue(name, out var found) ? found : null;

    public IFetchProvider? Fetch(string? name) =>
        name is not null && _fetch.TryGetValue(name, out var found) ? found : null;

    public IReadOnlyList<string> SearchNames => _search.Keys.Order(StringComparer.Ordinal).ToArray();

    public IReadOnlyList<string> FetchNames => _fetch.Keys.Order(StringComparer.Ordinal).ToArray();
}

/// <summary>
/// The one place the two capabilities meet: it picks the provider, remembers what it just fetched, and writes down
/// every call.
///
/// Everything here is per-deployment rather than per-provider, which is the point: a third provider added later gets
/// the quota, the cache and the audit line for free, because they live here and not in the providers. Three copies of
/// this logic would drift, and the newest copy would be the one that forgot.
/// </summary>
public sealed class WebGateway(
    WebProviderRegistry registry,
    WebOptions options,
    IAuditSink? audit = null,
    ISignalLog? signals = null) : IWebGateway
{
    /// <summary>Most pages kept for reuse. Bounded because the cache is a convenience, not a store.</summary>
    private const int MaxCachedPages = 200;

    private readonly ConcurrentDictionary<string, (WebPage Page, DateTimeOffset At)> _cache =
        new(StringComparer.Ordinal);

    public string? SearchProvider => options.Search;

    public string? FetchProvider => options.Fetch;

    public IReadOnlyList<string> SearchProviders => registry.SearchNames;

    public IReadOnlyList<string> FetchProviders => registry.FetchNames;

    public async ValueTask<Result<WebSearchResults>> SearchAsync(WebSearchQuery query, WebCall call,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var provider = registry.Search(options.Search);
        if (provider is null)
            return await Refuse<WebSearchResults>(call, "web.search", options.Search ?? "(none)", query.Query,
                new WebFailure(WebErrorKind.NotConfigured, "This deployment has no search provider.",
                    "available: " + (registry.SearchNames.Count == 0 ? "none" : string.Join(", ", registry.SearchNames))),
                cancellationToken);

        var result = await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        await RecordAsync(call, "web.search", provider.Name, query.Query,
            result.Ok ? "ok" : KindOf(result.Error), Stopwatch.GetElapsedTime(started),
            result.Ok ? $"{result.Value?.Hits.Count ?? 0} result(s)" : result.Error?.Message,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<Result<WebPage>> FetchAsync(WebFetchRequest request, WebCall call,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var provider = registry.Fetch(options.Fetch);
        if (provider is null)
            return await Refuse<WebPage>(call, "web.fetch", options.Fetch ?? "(none)", SafeUrl(request.Url),
                new WebFailure(WebErrorKind.NotConfigured, "This deployment has no fetch provider.",
                    "available: " + (registry.FetchNames.Count == 0 ? "none" : string.Join(", ", registry.FetchNames))),
                cancellationToken);

        var wanted = request with { MaxChars = request.MaxChars ?? options.MaxChars };
        var key = CacheKey(wanted.Url);

        // A page read twice in the same ten minutes is one page. The cache is checked before the provider is asked, so
        // a repeat costs nothing — not a call, not a credit, not a row in somebody's log.
        if (_cache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(Math.Max(1, options.CacheMinutes)))
        {
            var page = cached.Page;
            if (wanted.MaxChars is { } max && page.Markdown.Length > max)
                page = page with { Markdown = page.Markdown[..max], Truncated = true };
            await RecordAsync(call, "web.fetch", provider.Name, SafeUrl(wanted.Url), "cache",
                Stopwatch.GetElapsedTime(started), $"{page.Markdown.Length} char(s)", cancellationToken)
                .ConfigureAwait(false);
            return Result<WebPage>.Success(page);
        }

        var result = await provider.FetchAsync(wanted, cancellationToken).ConfigureAwait(false);
        if (result.Ok && result.Value is not null) Remember(key, result.Value);
        await RecordAsync(call, "web.fetch", provider.Name, SafeUrl(wanted.Url),
            result.Ok ? "ok" : KindOf(result.Error), Stopwatch.GetElapsedTime(started),
            result.Ok ? $"{result.Value?.Markdown.Length ?? 0} char(s)" : result.Error?.Message, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private void Remember(string key, WebPage page)
    {
        _cache[key] = (page, DateTimeOffset.UtcNow);
        if (_cache.Count <= MaxCachedPages) return;
        // Oldest first, and only as much as needed: the cache is a convenience and a full sweep would be a stall.
        foreach (var stale in _cache.OrderBy(entry => entry.Value.At).Take(_cache.Count - MaxCachedPages).ToArray())
            _cache.TryRemove(stale.Key, out _);
    }

    /// <summary>
    /// The address without its query values. A URL is an object in the audit; the token in its query string is a
    /// credential, and the audit is a file people copy around.
    /// </summary>
    private static string SafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.GetLeftPart(UriPartial.Path)
            : url.Length <= 200 ? url : url[..200];

    /// <summary>Same address, one spelling: scheme and host lower case, no fragment.</summary>
    private static string CacheKey(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.GetLeftPart(UriPartial.Query).ToLowerInvariant() + parsed.Fragment
            : url;

    private static string KindOf(Error? error) =>
        error?.Detail?.Split(':')[0] is { Length: > 0 } kind ? kind.ToLowerInvariant() : "failed";

    private async ValueTask<Result<T>> Refuse<T>(WebCall call, string action, string provider, string subject,
        WebFailure failure, CancellationToken cancellationToken)
    {
        await RecordAsync(call, action, provider, subject, failure.Kind.ToString().ToLowerInvariant(), TimeSpan.Zero,
            failure.Message, cancellationToken).ConfigureAwait(false);
        return Result<T>.Failure(failure.ToError());
    }

    /// <summary>
    /// One line per call, whoever it was. A refusal is recorded as carefully as a success — a refusal with no trace is
    /// indistinguishable from a bug, and "the model says searching does not work" is a question somebody will ask.
    /// </summary>
    private async ValueTask RecordAsync(WebCall call, string action, string provider, string subject, string outcome,
        TimeSpan elapsed, string? detail, CancellationToken cancellationToken)
    {
        if (audit is not null)
        {
            try
            {
                await audit.AppendAsync(new AuditRecord(DateTimeOffset.UtcNow, Guid.NewGuid(), call.Caller,
                    new Address("web", action, provider), action, outcome,
                    $"{subject} — {detail}"), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                // A failing audit sink must not turn an answer into an exception the caller has to interpret. The
                // signal below still carries the call.
            }
        }

        signals?.Write(new SignalRecord
        {
            Group = "web",
            Name = provider,
            Mechanism = action.StartsWith("web.", StringComparison.Ordinal) ? action[4..] : action,
            From = call.Caller,
            To = provider,
            Outcome = outcome,
            DurationMs = elapsed.TotalMilliseconds,
            Detail = new Dictionary<string, string>
            {
                ["subject"] = subject,
                ["detail"] = detail ?? "",
                ["turn"] = call.TurnId ?? "",
            },
        });
    }
}

/// <summary>
/// How this deployment is wired: which provider does which job, and what the ceilings are.
///
/// It is a plain file (<c>web/search.json</c>) next to <c>api/server.json</c> and <c>net/targets.json</c>, and missing
/// means "no web capability at all" — the same shape as the other two, and the same reason: a default would be a
/// decision nobody made.
/// </summary>
public sealed record WebOptions
{
    /// <summary>Provider name for searching, or null for none. Null is a valid deployment.</summary>
    public string? Search { get; init; }

    public string? Fetch { get; init; }

    /// <summary>Results a search returns unless the caller asks for fewer.</summary>
    public int MaxResults { get; init; } = 8;

    /// <summary>Characters a fetched page keeps. Beyond this a page stops being context and becomes a budget.</summary>
    public int MaxChars { get; init; } = 32_000;

    public int SearchesPerTurn { get; init; } = 3;

    public int FetchesPerTurn { get; init; } = 8;

    public int CacheMinutes { get; init; } = 10;

    // Per-provider knobs, flat rather than nested: three fields are easier to read in a file than a tree.
    public string DeepSeekTarget { get; init; } = "deepseek";
    public int DeepSeekCount { get; init; } = 8;
    /// <summary>An IP address the search provider uses to localise results. Empty: it tells a third party where we are.</summary>
    public string? DeepSeekLocalization { get; init; }

    public string TavilyTarget { get; init; } = "tavily";
    /// <summary><c>advanced</c> costs twice the credits and is not the default for that reason.</summary>
    public string TavilyDepth { get; init; } = "basic";
    public string TavilyTopic { get; init; } = "general";
    public string? TavilyCountry { get; init; }
    public string? TavilyLanguage { get; init; }

    /// <summary>
    /// Whether the local provider may reach a loopback or private address. True by default: the address usually
    /// arrives because a person pasted it, and refusing it would make this provider pointless.
    /// </summary>
    public bool LocalAllowPrivate { get; init; } = true;

    public int LocalMaxBytes { get; init; } = 2 * 1024 * 1024;

    public int LocalTimeoutSeconds { get; init; } = 20;
}

/// <summary>Reads <c>&lt;dataRoot&gt;/web/search.json</c>. A missing file is "no web capability", not an error.</summary>
public static class WebOptionsFile
{
    public const string FileName = "search.json";

    public static string PathIn(string dataRoot) => Path.Combine(dataRoot, "web", FileName);

    public static (WebOptions Options, IReadOnlyList<string> Issues) Read(string path)
    {
        var defaults = new WebOptions();
        if (!File.Exists(path)) return (defaults, []);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (defaults, ["expected a JSON object"]);

            var issues = new List<string>();
            var options = new WebOptions
            {
                Search = Field.Text(root, "search"),
                Fetch = Field.Text(root, "fetch"),
                MaxResults = Field.Int(root, "maxResults", 1, 20, defaults.MaxResults, issues),
                MaxChars = Field.Int(root, "maxChars", 500, 200_000, defaults.MaxChars, issues),
                SearchesPerTurn = Field.Int(root, "searchesPerTurn", 0, 100, defaults.SearchesPerTurn, issues),
                FetchesPerTurn = Field.Int(root, "fetchesPerTurn", 0, 100, defaults.FetchesPerTurn, issues),
                CacheMinutes = Field.Int(root, "cacheMinutes", 0, 1440, defaults.CacheMinutes, issues),
                DeepSeekTarget = Field.Text(root, "deepseekTarget") ?? defaults.DeepSeekTarget,
                DeepSeekCount = Field.Int(root, "deepseekCount", 1, 50, defaults.DeepSeekCount, issues),
                DeepSeekLocalization = Field.Text(root, "deepseekLocalization"),
                TavilyTarget = Field.Text(root, "tavilyTarget") ?? defaults.TavilyTarget,
                TavilyDepth = Field.Text(root, "tavilyDepth") ?? defaults.TavilyDepth,
                TavilyTopic = Field.Text(root, "tavilyTopic") ?? defaults.TavilyTopic,
                TavilyCountry = Field.Text(root, "tavilyCountry"),
                TavilyLanguage = Field.Text(root, "tavilyLanguage"),
                LocalAllowPrivate = Field.Bool(root, "localAllowPrivate", true),
                LocalMaxBytes = Field.Int(root, "localMaxBytes", 1024, 8 * 1024 * 1024, defaults.LocalMaxBytes, issues),
                LocalTimeoutSeconds = Field.Int(root, "localTimeoutSeconds", 1, 600, defaults.LocalTimeoutSeconds, issues),
            };
            return (options, issues);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (defaults, [ex.Message]);
        }
    }

    /// <summary>Small readers that say what was wrong instead of quietly substituting a default.</summary>
    private static class Field
    {
        public static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text
                ? text.Trim()
                : null;

        public static bool Bool(JsonElement root, string name, bool fallback) =>
            root.TryGetProperty(name, out var value)
                ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback }
                : fallback;

        public static int Int(JsonElement root, string name, int min, int max, int fallback, List<string> issues)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
            {
                issues.Add($"\"{name}\" must be a whole number; using {fallback}");
                return fallback;
            }
            if (parsed < min || parsed > max)
            {
                issues.Add($"\"{name}\" must be between {min} and {max}; using {fallback}");
                return fallback;
            }
            return parsed;
        }
    }
}
