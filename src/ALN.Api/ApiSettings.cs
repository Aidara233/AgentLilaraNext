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
public sealed record ApiSettings(string[] Urls, Dictionary<string, string[]> Roles, ApiToken[] Tokens)
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
                return new Actor(candidate.Name, ActorKind.User, capabilities);
            }
        }
        return null;
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public sealed record ApiToken(string Name, string Sha256, string[] Roles);
