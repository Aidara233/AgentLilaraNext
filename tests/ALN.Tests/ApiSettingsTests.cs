using System.Text.Json;
using ALN.Api;

namespace ALN.Tests;

/// <summary>
/// The server.json contract. Two things here are easy to get wrong and expensive to notice: a config
/// written before <c>cors</c> existed must still load, and a hand-typed origin that browsers will
/// never send must be reported rather than silently accepted.
/// </summary>
public sealed class ApiSettingsTests
{
    private static ApiSettings Parse(string json) =>
        JsonSerializer.Deserialize<ApiSettings>(json, ApiSettings.JsonOptions)!;

    [Fact]
    public void ConfigWithoutCorsStillLoadsAndAllowsNoCrossOriginCallers()
    {
        var settings = Parse("""
            {
              "urls": ["http://127.0.0.1:5199"],
              "roles": { "admin": ["*"] },
              "tokens": [{ "name": "a", "sha256": "00", "roles": ["admin"] }]
            }
            """);

        Assert.Null(settings.Cors);
        Assert.Empty(settings.Origins);
        Assert.Empty(settings.RejectedOrigins);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5200", "http://127.0.0.1:5200")]
    [InlineData("http://127.0.0.1:5200/", "http://127.0.0.1:5200")]
    [InlineData("  https://aln.example  ", "https://aln.example")]
    public void OriginsAreNormalisedToSchemeHostPort(string raw, string expected)
    {
        Assert.True(ApiSettings.TryNormalizeOrigin(raw, out var origin));
        Assert.Equal(expected, origin);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("http://127.0.0.1:5200/ui")]
    [InlineData("http://127.0.0.1:5200?x=1")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("http://user:secret@127.0.0.1:5200")]
    [InlineData("127.0.0.1:5200")]
    [InlineData("")]
    public void ValuesThatAreNotOriginsAreRejected(string raw)
    {
        Assert.False(ApiSettings.TryNormalizeOrigin(raw, out _));
    }

    [Fact]
    public void UnusableEntriesAreReportedAndDroppedInsteadOfQuietlyIgnored()
    {
        var settings = Parse("""
            {
              "urls": ["http://127.0.0.1:5199"],
              "roles": { "admin": ["*"] },
              "tokens": [{ "name": "a", "sha256": "00", "roles": ["admin"] }],
              "cors": ["http://127.0.0.1:5200", "http://127.0.0.1:5200/", "*", "http://127.0.0.1:5200/ui"]
            }
            """);

        Assert.Equal(["http://127.0.0.1:5200"], settings.Origins);
        Assert.Equal(["*", "http://127.0.0.1:5200/ui"], settings.RejectedOrigins);
    }

    [Fact]
    public void TokensResolveByDigestAndUnknownTokensAreRefused()
    {
        var hash = ApiSettings.Hash("secret-token");
        var settings = Parse($$"""
            {
              "urls": ["http://127.0.0.1:5199"],
              "roles": { "viewer": ["*.read"] },
              "tokens": [{ "name": "v", "sha256": "{{hash}}", "roles": ["viewer"] }]
            }
            """);

        var actor = settings.Authenticate("secret-token");
        Assert.NotNull(actor);
        Assert.Equal("v", actor!.Id);
        Assert.True(actor.Allows("signals.read"));
        Assert.False(actor.Allows("channels.control"));

        Assert.Null(settings.Authenticate("secret-token "));
        Assert.Null(settings.Authenticate(null));
        Assert.Null(settings.Authenticate(""));
    }
}
