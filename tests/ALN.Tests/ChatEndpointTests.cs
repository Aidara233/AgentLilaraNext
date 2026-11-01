using ALN.Contracts;
using ALN.Model.OpenAIChat;

namespace ALN.Tests;

public sealed class ChatEndpointTests
{
    [Theory]
    // Whatever a person types means the same base; only the protocol knows the method path.
    [InlineData("https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/")]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("https://api.deepseek.com/v1/")]
    [InlineData("https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/v1/chat/completions?foo=bar#frag")]
    [InlineData("  https://api.deepseek.com/v1  ")]
    public void Base_host_and_full_method_url_normalize_to_the_same_request_url(string input)
    {
        var result = ChatEndpoint.Normalize(input);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", result.Value!.ToString());
    }

    [Fact]
    public void Missing_endpoint_falls_back_to_the_provider_default()
    {
        var result = ChatEndpoint.Normalize(null);

        Assert.True(result.Ok);
        Assert.Equal("https://api.openai.com/v1/chat/completions", result.Value!.ToString());
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434", "http://127.0.0.1:11434/v1/chat/completions")]
    [InlineData("http://127.0.0.1:11434/v1", "http://127.0.0.1:11434/v1/chat/completions")]
    [InlineData("http://127.0.0.1:11434/v1/chat/completions", "http://127.0.0.1:11434/v1/chat/completions")]
    // A proxy path in front of the API is preserved; only the version/method tail is normalized.
    [InlineData("https://gateway.example.com/openai/v1", "https://gateway.example.com/openai/v1/chat/completions")]
    [InlineData("https://gateway.example.com/proxy", "https://gateway.example.com/proxy/v1/chat/completions")]
    [InlineData("https://api.example.com/v1beta", "https://api.example.com/v1beta/chat/completions")]
    public void Real_path_prefixes_and_versions_are_kept(string input, string expected)
    {
        var result = ChatEndpoint.Normalize(input);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(expected, result.Value!.ToString());
    }

    [Fact]
    public void Credentials_embedded_in_the_url_are_rejected()
    {
        // Built rather than written out, so no credential-shaped literal lives in the test source.
        var endpoint = new UriBuilder("https://api.deepseek.com/v1") { UserName = "user", Password = "secret" }.Uri.ToString();

        var result = ChatEndpoint.Normalize(endpoint);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.Invalid, result.Error?.Code);
    }

    [Theory]
    [InlineData("http://api.deepseek.com", ErrorCode.Invalid)]        // plain HTTP off loopback
    [InlineData("ftp://api.deepseek.com", ErrorCode.Invalid)]
    [InlineData("not a url", ErrorCode.Invalid)]
    public void Unusable_endpoints_are_rejected_with_a_reason(string input, ErrorCode expected)
    {
        var result = ChatEndpoint.Normalize(input);

        Assert.False(result.Ok);
        Assert.Equal(expected, result.Error?.Code);
    }
}
