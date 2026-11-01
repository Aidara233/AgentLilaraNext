namespace ALN.Model.OpenAIChat;

/// <summary>
/// Turns whatever endpoint a human typed into the single URL this protocol actually posts to.
/// People paste the base host, the docs' example URL, or the full completions path; all three mean
/// the same thing, so the adapter normalizes instead of demanding one exact spelling.
/// </summary>
public static class ChatEndpoint
{
    public const string DefaultHost = "https://api.openai.com";
    private const string Suffix = "chat/completions";

    /// <summary>Terminal segments that are part of a *method* URL, not of the base, and are dropped.</summary>
    private static readonly string[] MethodSegments = ["chat/completions", "completions", "responses", "models"];

    public static Contracts.Result<Uri> Normalize(string? endpoint, bool allowLoopbackHttp = true)
    {
        var candidate = string.IsNullOrWhiteSpace(endpoint) ? DefaultHost : endpoint.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return Contracts.Result<Uri>.Failure(new(Contracts.ErrorCode.Invalid, "Model endpoint must be an absolute URL."));
        if (uri.Scheme is not ("http" or "https"))
            return Contracts.Result<Uri>.Failure(new(Contracts.ErrorCode.Invalid, "Model endpoint must use http or https."));
        if (uri.Scheme == "http" && !(allowLoopbackHttp && uri.IsLoopback))
            return Contracts.Result<Uri>.Failure(new(Contracts.ErrorCode.Invalid,
                "Model endpoint must use HTTPS; plain HTTP is allowed only for loopback endpoints."));
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return Contracts.Result<Uri>.Failure(new(Contracts.ErrorCode.Invalid, "Model endpoint must not embed credentials."));

        // Query and fragment belong to a specific request, never to a base.
        var path = uri.AbsolutePath.TrimEnd('/');
        path = StripMethodSegments(path);
        if (!EndsWithVersion(path)) path = path + "/v1";
        var builder = new UriBuilder(uri) { Path = path + "/" + Suffix, Query = string.Empty, Fragment = string.Empty };
        return Contracts.Result<Uri>.Success(builder.Uri);
    }

    private static string StripMethodSegments(string path)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var segment in MethodSegments)
            {
                if (!path.EndsWith("/" + segment, StringComparison.OrdinalIgnoreCase)) continue;
                path = path[..^(segment.Length + 1)];
                changed = true;
                break;
            }
        }
        return path;
    }

    /// <summary>"v1", "v1beta", "v4" — keep what the operator already chose instead of a second /v1.</summary>
    private static bool EndsWithVersion(string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash < 0) return false;
        var last = path[(slash + 1)..];
        if (last.Length < 2 || (last[0] is not ('v' or 'V')) || !char.IsAsciiDigit(last[1])) return false;
        return last.All(c => char.IsAsciiLetterOrDigit(c));
    }
}
