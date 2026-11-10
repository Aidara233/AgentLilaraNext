namespace ALN.Contracts;

/// <summary>
/// Something that can be asked, named so configuration can point at it.
///
/// Search and fetch are two interfaces rather than one with two flags, and the reason is the same one this codebase
/// applies everywhere else: a capability belongs on the type, not in a field that can disagree with the implementation.
/// A provider that cannot search simply has no <see cref="ISearchProvider"/> — there is nothing to check and nothing to
/// get wrong later, and "local cannot search" stops being a rule and becomes a compile error waiting to happen.
/// </summary>
public interface IWebProvider
{
    /// <summary>The name configuration uses. One word, stable, not a description.</summary>
    string Name { get; }
}

/// <summary>Finds pages for a query. One implementation per provider; nothing here knows about HTTP.</summary>
public interface ISearchProvider : IWebProvider
{
    ValueTask<Result<WebSearchResults>> SearchAsync(WebSearchQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Retrieves one page. The address is a full URL, because the thing that most often arrives is a link somebody pasted
/// — a provider that could only be asked for names would be useless for that.
/// </summary>
public interface IFetchProvider : IWebProvider
{
    ValueTask<Result<WebPage>> FetchAsync(WebFetchRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The providers this process knows how to use, in two tables.
///
/// Two tables and not one: which provider searches and which fetches are separate deployment decisions, and mixing
/// them (search with one, fetch with another) is the most useful arrangement rather than a configuration mistake.
/// </summary>
public interface IWebProviderRegistry
{
    void Add(ISearchProvider provider);
    void Add(IFetchProvider provider);

    ISearchProvider? Search(string? name);
    IFetchProvider? Fetch(string? name);

    /// <summary>Names that can search, in the order they were registered — what a refusal lists.</summary>
    IReadOnlyList<string> SearchNames { get; }

    IReadOnlyList<string> FetchNames { get; }
}

/// <summary>How far back a search should look. A word, not a number of days: the number is the provider's business.</summary>
public enum WebRecency { Any = 0, Day, Week, Month, Year }

/// <summary>
/// One search. <see cref="MaxResults"/> is a ceiling the caller may lower, never raise past the deployment's own.
/// </summary>
/// <param name="Query">What to look for, in words. Providers may support their own operators; nothing here interprets
/// them, because a query is the caller's language and not a field of ours.</param>
/// <param name="Domains">Restrict to these sites. How a provider expresses it varies; a provider that cannot honour it
/// must say so rather than ignore it.</param>
/// <param name="Original">
/// The question that caused this search, when the caller has it. A query written by a model is a compressed thing;
/// handing the provider the words a person actually used measurably improves recall, and the one provider that accepts
/// it asks for exactly this field.
/// </param>
public sealed record WebSearchQuery(
    string Query,
    int? MaxResults = null,
    WebRecency? Recency = null,
    IReadOnlyList<string>? Domains = null,
    string? Original = null);

/// <summary>
/// One result, normalised across providers.
///
/// The fields that only some providers fill are nullable on purpose, and <see cref="ScoreCeiling"/> exists because the
/// scales differ (one provider scores 0–1, another 0–3): the number is kept as the provider reported it, with the
/// ceiling beside it, rather than divided into a comparability that does not exist. A score is for ordering one
/// answer's results, not for comparing two answers.
/// </summary>
/// <param name="Snippet">Short, for display. Absent when the provider has none.</param>
/// <param name="Summary">Longer, for a model to read without opening the page.</param>
/// <param name="Authority">A provider's own tier, when it has one. Null means "no opinion", not "untrustworthy".</param>
public sealed record WebHit(
    string Title,
    string Url,
    string? Snippet = null,
    string? Summary = null,
    double? Score = null,
    double? ScoreCeiling = null,
    int? Authority = null,
    DateTimeOffset? PublishedAt = null,
    string? Site = null);

/// <summary>What one search produced. An empty <see cref="Hits"/> is an answer, not a failure: nothing was found.</summary>
public sealed record WebSearchResults(
    string Provider,
    string Query,
    IReadOnlyList<WebHit> Hits,
    TimeSpan Elapsed);

/// <summary>
/// One page to retrieve. <paramref name="MaxChars"/> is a ceiling on what comes back, not on what is downloaded: a
/// caller asking for less is asking for less context, not for a broken page.
/// </summary>
public sealed record WebFetchRequest(string Url, int? MaxChars = null);

/// <summary>
/// One page as text. Markdown and not plain text, because a page's shape carries meaning that a flat run of words
/// throws away — headings, lists, code blocks, and the links that let a model decide what to read next.
/// </summary>
/// <param name="Bytes">What the provider actually retrieved, which may be more than <paramref name="Markdown"/> shows.</param>
/// <param name="Truncated">Set when the ceiling cut it, never inferred from a short page.</param>
public sealed record WebPage(
    string Url,
    string? Title,
    string Markdown,
    long Bytes,
    bool Truncated,
    TimeSpan Elapsed);

/// <summary>Who is asking. Quotas count per turn; the audit line records the caller.</summary>
public sealed record WebCall(string Caller, string? TurnId = null);

/// <summary>
/// Why a web call did not produce an answer, in the kinds a caller can act on.
///
/// The providers each have their own vocabulary — one speaks OpenAI-compatible errors and HTTP status codes, the other
/// has four different ways of saying "your plan will not allow this" — and none of that belongs above them. What
/// belongs above them is this: a caller needs to know whether to fix the query, wait, pay, or stop.
/// </summary>
public enum WebErrorKind
{
    None = 0,
    /// <summary>No provider is configured for this capability. A deployment state, not a mistake by the caller.</summary>
    NotConfigured,
    /// <summary>The address is not one this can be asked for.</summary>
    InvalidUrl,
    /// <summary>The provider cannot do what was asked (a site filter it cannot express, a format it does not serve).</summary>
    NotSupported,
    /// <summary>The provider refused on policy grounds — a site it will not retrieve, a page it will not serve.</summary>
    Blocked,
    /// <summary>Reached, but there was no usable content: an empty page, an unsupported type, a 404.</summary>
    NoContent,
    /// <summary>Too many calls too quickly. Wait and try again.</summary>
    RateLimited,
    /// <summary>Out of credits, over plan, or unpaid. Waiting does not help.</summary>
    Quota,
    /// <summary>The far side failed. Retrying may work.</summary>
    Upstream,
    Timeout,
}

/// <summary>A refusal that still says which kind it is, and how it lands on the error codes the rest of the system speaks.</summary>
public sealed record WebFailure(WebErrorKind Kind, string Message, string? Detail = null, bool Retryable = false)
{
    public Error ToError() => new(CodeOf(Kind), Message,
        Detail is null ? Kind.ToString() : $"{Kind}: {Detail}", Retryable);

    public static ErrorCode CodeOf(WebErrorKind kind) => kind switch
    {
        WebErrorKind.NotConfigured => ErrorCode.Unavailable,
        WebErrorKind.InvalidUrl or WebErrorKind.NotSupported or WebErrorKind.NoContent => ErrorCode.Invalid,
        WebErrorKind.Blocked => ErrorCode.Denied,
        WebErrorKind.RateLimited or WebErrorKind.Quota => ErrorCode.Conflict,
        WebErrorKind.Upstream => ErrorCode.Unavailable,
        WebErrorKind.Timeout => ErrorCode.Timeout,
        _ => ErrorCode.Internal,
    };
}

/// <summary>
/// The one place the two capabilities meet: what a tool calls, what a script calls, what counts, what is recorded.
///
/// It sits above the providers and below the callers, and it is the only place that knows which provider is in force.
/// A gateway that did less than this (no quota, no cache, no record) would push those three into every provider, and a
/// provider is exactly the wrong place for them: the third one added would be the one that forgot.
/// </summary>
public interface IWebGateway
{
    /// <summary>The provider in force for searching, or null when this deployment has none.</summary>
    string? SearchProvider { get; }

    string? FetchProvider { get; }

    IReadOnlyList<string> SearchProviders { get; }

    IReadOnlyList<string> FetchProviders { get; }

    ValueTask<Result<WebSearchResults>> SearchAsync(WebSearchQuery query, WebCall call, CancellationToken cancellationToken);

    ValueTask<Result<WebPage>> FetchAsync(WebFetchRequest request, WebCall call, CancellationToken cancellationToken);
}
