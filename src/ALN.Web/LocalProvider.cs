using System.Diagnostics;
using System.Text.RegularExpressions;
using ALN.Contracts;
using ReverseMarkdown;

namespace ALN.Web;

/// <summary>
/// The provider that goes and gets the page itself.
///
/// It is the only one of the three that can reach an address on the local network, behind a login, or on a host
/// nobody indexed — because it is the only one whose request leaves <b>this</b> process. That is the whole reason it
/// exists, and it is also why it is the only one that pays a price: the HTML-to-text step, which the other two have
/// spent years improving and we have not.
///
/// Private addresses are allowed by default. The address usually arrives because a person pasted it, and "paste a URL
/// and I will read it" is the feature; an allowlist in front of that would be the other door again. What keeps it
/// honest is the audit line: the port records <c>local:&lt;authority&gt;</c> and the IP it actually reached.
/// </summary>
public sealed class LocalProvider : IFetchProvider
{
    public const string ProviderName = "local";

    /// <summary>Script and style blocks are not prose: their contents would otherwise arrive as a wall of code.</summary>
    private static readonly Regex NonProse = new(
        @"<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>|<!--.*?-->",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex TitleTag = new(@"<title\b[^>]*>(.*?)</title>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex Whitespace = new(@"[ \t]{2,}", RegexOptions.Compiled);

    private readonly IAdHocNetAccess _net;
    private readonly NetPolicy _policy;
    // Only the one option that changes the output's shape. Comments, scripts and styles are stripped before the
    // converter ever sees the page, and unknown tags are kept with their contents by default — dropping a <figure>
    // would take its text with it, and the text is what this provider is for.
    private readonly Converter _converter = new(new Config { GithubFlavored = true });

    public LocalProvider(IAdHocNetAccess net, bool allowPrivate = true, int maxBytes = 2 * 1024 * 1024,
        int timeoutSeconds = 20)
    {
        _net = net;
        _policy = new NetPolicy
        {
            Methods = ["GET"],
            AllowPrivate = allowPrivate,
            MaxResponseBytes = maxBytes,
            TimeoutSeconds = timeoutSeconds,
            CallsPerMinute = 60,
            MaxConcurrent = 4,
            // Redirects are followed by the port, one hop at a time, each hop held to this origin: a permitted page
            // cannot hand us to somewhere else.
            FollowRedirects = true,
            MaxRedirects = 3,
        };
    }

    public string Name => ProviderName;

    public async ValueTask<Result<WebPage>> FetchAsync(WebFetchRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            return Result<WebPage>.Failure(new WebFailure(WebErrorKind.InvalidUrl,
                "That is not a page address.", "an absolute http or https URL").ToError());

        var sent = await _net.SendAsync(new AdHocRequest(address, "GET", Caller: "web:local"), _policy, cancellationToken)
            .ConfigureAwait(false);
        if (!sent.Ok || sent.Value is null)
            return Result<WebPage>.Failure(sent.Error!.FromNet(ProviderName));

        var response = sent.Value;
        if (response.Status is 404 or 410)
            return Result<WebPage>.Failure(new WebFailure(WebErrorKind.NoContent,
                "That page does not exist.", request.Url).ToError());
        if (response.Status is 401 or 403)
            return Result<WebPage>.Failure(new WebFailure(WebErrorKind.Blocked,
                "That page refused the request.", $"HTTP {response.Status}").ToError());
        if (response.Status is < 200 or >= 300)
            return Result<WebPage>.Failure(new WebFailure(WebErrorKind.Upstream,
                $"That page answered {response.Status}.", request.Url,
                Retryable: response.Status >= 500).ToError());

        var body = response.Text;
        var markdown = LooksLikeHtml(response.ContentType, body) ? ToMarkdown(body) : body.Trim();
        if (markdown.Length == 0)
            return Result<WebPage>.Failure(new WebFailure(WebErrorKind.NoContent,
                "That page came back with no readable text.", request.Url).ToError());

        var truncated = response.Truncated;
        if (request.MaxChars is { } max && markdown.Length > max)
        {
            markdown = markdown[..max];
            truncated = true;
        }

        return Result<WebPage>.Success(new WebPage(request.Url, FindTitle(body), markdown, response.Bytes, truncated,
            Stopwatch.GetElapsedTime(started)));
    }

    /// <summary>
    /// Whether to run the converter at all. A <c>text/plain</c> or a <c>.md</c> is already what a model wants, and
    /// putting it through an HTML parser would turn it into a single paragraph.
    /// </summary>
    private static bool LooksLikeHtml(string? contentType, string body)
    {
        if (contentType is { Length: > 0 })
            return contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);
        var head = body.AsSpan(0, Math.Min(body.Length, 512)).TrimStart();
        return head.StartsWith("<") && !head.StartsWith("<html:", StringComparison.OrdinalIgnoreCase);
    }

    private string ToMarkdown(string html)
    {
        var prose = NonProse.Replace(html, " ");
        try
        {
            var markdown = _converter.Convert(prose);
            // The converter keeps a lot of vertical space where a block was removed; two blank lines in a row is the
            // most a paragraph break needs.
            markdown = Regex.Replace(markdown, @"\n{3,}", "\n\n");
            return Whitespace.Replace(markdown, " ").Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A page we cannot convert is still a page: hand back the text with the tags stripped rather than nothing.
            var stripped = Regex.Replace(prose, "<[^>]+>", " ");
            return Whitespace.Replace(stripped, " ").Trim();
        }
    }

    private static string? FindTitle(string html)
    {
        var match = TitleTag.Match(html);
        if (!match.Success) return null;
        var title = Whitespace.Replace(match.Groups[1].Value, " ").Trim();
        return title.Length == 0 ? null : title;
    }
}
