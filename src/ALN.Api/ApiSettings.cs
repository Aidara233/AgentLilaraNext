using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;

namespace ALN.Api;

/// <summary>
/// API configuration lives in <c>&lt;DataRoot&gt;/api/server.json</c>. Tokens are stored as SHA-256
/// hex, never in clear text: a config file that leaks must not hand out access.
/// </summary>
public sealed record ApiSettings(string[] Urls, Dictionary<string, string[]> Roles, ApiToken[] Tokens,
    string[]? Cors = null)
{
    public const string FileName = "server.json";

    public static ApiSettings? Load(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "api", FileName);
        if (!File.Exists(path)) return null;
        var document = JsonSerializer.Deserialize<ApiSettings>(File.ReadAllBytes(path), JsonOptions);
        return document is null || document.Urls.Length == 0 || document.Tokens.Length == 0 ? null : document;
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() }, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>Resolves a bearer token to an actor, or null. Comparison is over digests, not raw bytes.</summary>
    public Actor? Authenticate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var digest = Hash(token);
        foreach (var candidate in Tokens)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(digest),
                    Encoding.ASCII.GetBytes(candidate.Sha256)))
            {
                var capabilities = new List<string>();
                foreach (var role in candidate.Roles)
                    if (Roles.TryGetValue(role, out var granted))
                        capabilities.AddRange(granted);
                return new Actor(candidate.Name, ActorKind.User, capabilities, AccessLevel.Full);
            }
        }
        return null;
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// The origins a browser front end is allowed to call from. Empty by default: cross-origin access
    /// is a deployment decision, and a wildcard would silently turn every page in the browser into a
    /// caller. Wildcards are dropped rather than honoured, and the dropped entries are reported so a
    /// typo in the config never turns into "it silently doesn't work".
    /// </summary>
    public IReadOnlyList<string> Origins
    {
        get
        {
            if (Cors is null || Cors.Length == 0) return [];
            var kept = new List<string>(Cors.Length);
            foreach (var raw in Cors)
            {
                if (!TryNormalizeOrigin(raw, out var origin)) continue;
                if (!kept.Contains(origin, StringComparer.OrdinalIgnoreCase)) kept.Add(origin);
            }
            return kept;
        }
    }

    /// <summary>Entries that were written but cannot be used as an origin; empty means the config is clean.</summary>
    public IReadOnlyList<string> RejectedOrigins =>
        (Cors ?? []).Where(raw => !TryNormalizeOrigin(raw, out _)).ToArray();

    /// <summary>
    /// An origin is scheme + host + port and nothing else. Paths, queries and the wildcard are not
    /// origins: browsers send none of them in the Origin header, so accepting them would only create
    /// configs that look right and never match.
    /// </summary>
    public static bool TryNormalizeOrigin(string? raw, out string origin)
    {
        origin = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim().TrimEnd('/');
        if (trimmed.Contains('*', StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        if (uri.UserInfo.Length > 0) return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}

public sealed record ApiToken(string Name, string Sha256, string[] Roles);
