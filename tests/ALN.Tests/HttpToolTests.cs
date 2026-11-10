using System.Text.Json;
using ALN.Contracts;
using ALN.Net;

namespace ALN.Tests;

/// <summary>
/// The raw door: an address the caller names, and the answer exactly as it came.
///
/// What matters here is that it is a different thing from the page reader — status, headers and bytes, not extracted
/// text — and that the four rules it does keep are actually kept. The rest of the gates (private addresses, ceilings,
/// deadlines) live in the port and are tested on the port.
/// </summary>
public sealed class HttpToolTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task The_raw_door_returns_the_status_the_headers_and_the_body()
    {
        var (port, served) = ServeOnce(201, """{"ok":true}""", "x-request-id: abc");
        await using var net = new HttpNetAccess([]);

        var result = await HttpActions.SendAsync(net, Json($$"""
        {"url":"http://127.0.0.1:{{port}}/v1/thing","method":"POST","body":"{\"a\":1}","contentType":"application/json"}
        """), "test", default);
        await served;

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.Equal(201, result.Value.GetProperty("status").GetInt32());
        Assert.Equal("""{"ok":true}""", result.Value.GetProperty("body").GetString());
        // The response headers come back too — that is the whole difference from the page reader. Key case is the
        // server's, not ours, so the check is on the rendered object rather than on an exact property name.
        var headers = result.Value.GetProperty("headers").GetRawText();
        Assert.Contains("x-request-id", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("abc", headers, StringComparison.Ordinal);
        // The address that was given is also where it ended up: no redirect here, and the answer says so.
        Assert.Contains($":{port}/v1/thing", result.Value.GetProperty("finalUrl").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_header_is_refused_because_it_would_talk_to_somewhere_else()
    {
        await using var net = new HttpNetAccess([]);

        var result = await HttpActions.SendAsync(net, Json("""
        {"url":"http://127.0.0.1:1/x","headers":{"Host":"evil.example"}}
        """), "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.Denied, result.Error!.Code);
        Assert.Contains("Host", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pw@example.com/x")]
    public async Task An_address_that_is_not_a_fetchable_one_is_refused(string url)
    {
        await using var net = new HttpNetAccess([]);

        var result = await HttpActions.SendAsync(net, Json($$"""{"url":"{{url}}"}"""), "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.Invalid, result.Error!.Code);
    }

    [Fact]
    public void The_tool_carries_its_own_schema_and_offers_one_verb()
    {
        var tools = new HttpTools(new HttpNetAccess([]));

        Assert.Equal(HttpTools.SourceId, tools.Id);
        Assert.Equal("http_request", HttpTools.RequestDefinition.Name);
        // A model that has to guess a field name wastes a round; the schema is the answer to that.
        Assert.Contains("\"url\"", HttpTools.RequestDefinition.ParametersJson, StringComparison.Ordinal);
        Assert.Contains("web_fetch", HttpTools.RequestDefinition.Description, StringComparison.Ordinal);
    }

    /// <summary>One request, one answer, then stop. Returns the port and a task that finishes when it has replied.</summary>
    private static (int Port, Task Served) ServeOnce(int status, string body, string? extraHeader)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[4096]);
            var payload = System.Text.Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} Done\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\n"
                       + (extraHeader is null ? "" : extraHeader + "\r\n")
                       + "Connection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
            listener.Stop();
        });
        return (port, served);
    }
}
