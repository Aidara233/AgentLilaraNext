using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Net;
using ALN.Web;

namespace ALN.Tests;

/// <summary>
/// The two remote providers and the one local one, against a socket this test opens: what goes out, what comes back,
/// and what each provider's own vocabulary turns into.
///
/// Two things here are worth more than the rest. The bodies the providers send: <c>need_content</c>,
/// <c>include_answer</c> and <c>include_raw_content</c> must all be false, because turning any of them on is how a
/// search stops being three thousand tokens and becomes thirty. And the loopback provider: the only one whose request
/// leaves this process, and therefore the only one whose output is our own HTML-to-text worth looking at.
/// </summary>
public sealed class WebProviderTests
{
    private static NetTarget Target(Server server, string name = "provider") => new(name,
        new Uri(server.Base + "/"), new NetPolicy
        {
            AllowPrivate = true,
            Methods = ["GET", "POST"],
            TimeoutSeconds = 5,
            CallsPerMinute = 100,
            MaxResponseBytes = 512 * 1024,
        });

    [Fact]
    public async Task A_deepseek_search_is_parsed_and_does_not_ask_for_page_bodies()
    {
        await using var server = new Server("""
        {"results":[{"rerank_score":2.4,"title":"DeepSeek V4 released","url":"https://example.com/v4",
          "snippet":"short","summary":"long","site_name":"Example","authority":3,
          "date":"2026-06-01T00:00:00+00:00"}]}
        """);
        await using var net = new HttpNetAccess([Target(server)]);
        var provider = new DeepSeekProvider(net, "provider");

        var result = await provider.SearchAsync(new WebSearchQuery("deepseek v4", MaxResults: 5), default);
        await server.WaitAsync();

        Assert.True(result.Ok, result.Error?.Detail);
        var hit = Assert.Single(result.Value!.Hits);
        Assert.Equal("DeepSeek V4 released", hit.Title);
        Assert.Equal(2.4, hit.Score);
        Assert.Equal(3, hit.ScoreCeiling);
        Assert.Equal(3, hit.Authority);
        Assert.NotNull(hit.PublishedAt);

        // The request: the body of a page is what web_fetch is for.
        using var sent = JsonDocument.Parse(server.LastBody);
        Assert.False(sent.RootElement.GetProperty("need_content").GetBoolean());
        Assert.Equal(5, sent.RootElement.GetProperty("count").GetInt32());
        Assert.Contains("/v1/tools/search", server.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tavily_search_pins_the_two_fields_that_would_bloat_it()
    {
        await using var server = new Server("""
        {"results":[{"title":"T","url":"https://example.com/a","content":"<chunk 1> alpha […] <chunk 2> beta","score":0.81}]}
        """);
        await using var net = new HttpNetAccess([Target(server)]);
        var provider = new TavilyProvider(net, "provider");

        var result = await provider.SearchAsync(new WebSearchQuery("anything"), default);
        await server.WaitAsync();

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.Equal(0.81, Assert.Single(result.Value!.Hits).Score);

        using var sent = JsonDocument.Parse(server.LastBody);
        // Our own model is the one that answers; buying somebody else's answer first is two translations.
        Assert.False(sent.RootElement.GetProperty("include_answer").GetBoolean());
        Assert.False(sent.RootElement.GetProperty("include_raw_content").GetBoolean());
    }

    [Fact]
    public async Task A_search_that_finds_nothing_is_an_answer_and_not_a_failure()
    {
        await using var server = new Server("""{"results":[]}""");
        await using var net = new HttpNetAccess([Target(server)]);
        var provider = new DeepSeekProvider(net, "provider");

        var result = await provider.SearchAsync(new WebSearchQuery("nothing at all"), default);
        await server.WaitAsync();

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.Empty(result.Value!.Hits);
    }

    [Theory]
    [InlineData(403, WebErrorKind.Blocked, false)]
    [InlineData(404, WebErrorKind.NoContent, false)]
    [InlineData(402, WebErrorKind.Quota, false)]
    [InlineData(429, WebErrorKind.RateLimited, true)]
    [InlineData(503, WebErrorKind.Upstream, true)]
    public async Task Each_providers_status_codes_are_told_apart(int status, WebErrorKind kind, bool retryable)
    {
        await using var server = new Server("{\"error\":{\"message\":\"no\"}}", status);
        await using var net = new HttpNetAccess([Target(server)]);
        var provider = new DeepSeekProvider(net, "provider");

        var result = await provider.SearchAsync(new WebSearchQuery("x"), default);
        await server.WaitAsync();

        Assert.False(result.Ok);
        Assert.StartsWith(kind.ToString(), result.Error!.Detail, StringComparison.Ordinal);
        Assert.Equal(retryable, result.Error.Retryable);
    }

    [Fact]
    public async Task Tavilys_plan_limits_are_quota_and_its_capacity_shortage_is_worth_a_retry()
    {
        await using var server = new Server("{\"detail\":{\"error\":\"over plan\"}}", 432);
        await using var net = new HttpNetAccess([Target(server)]);
        var provider = new TavilyProvider(net, "provider");

        var result = await provider.SearchAsync(new WebSearchQuery("x"), default);
        await server.WaitAsync();

        Assert.False(result.Ok);
        Assert.StartsWith(nameof(WebErrorKind.Quota), result.Error!.Detail, StringComparison.Ordinal);
        // Waiting does not fix a plan limit, so it is not offered as one.
        Assert.False(result.Error.Retryable);
        // The message the provider gave is preserved: "rejected" alone is not something a person can act on.
        Assert.Contains("over plan", result.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_local_provider_reads_a_page_itself_and_hands_back_markdown()
    {
        await using var server = new Server("""
        <html><head><title>Release notes</title><style>body{}</style></head>
        <body><script>var x=1;</script><h1>What changed</h1><p>Fixed a thing.</p><ul><li>one</li></ul></body></html>
        """, contentType: "text/html; charset=utf-8");
        await using var net = new HttpNetAccess([]);
        var provider = new LocalProvider(net);

        var result = await provider.FetchAsync(new WebFetchRequest(server.Base + "/notes"), default);
        await server.WaitAsync();

        Assert.True(result.Ok, result.Error?.Detail);
        var page = result.Value!;
        Assert.Equal("Release notes", page.Title);
        Assert.Contains("# What changed", page.Markdown, StringComparison.Ordinal);
        Assert.Contains("Fixed a thing.", page.Markdown, StringComparison.Ordinal);
        // Script and style bodies are not prose; letting them through is how a page becomes a wall of code.
        Assert.DoesNotContain("var x=1", page.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("body{}", page.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_local_provider_says_so_when_a_page_has_nothing_to_read()
    {
        await using var server = new Server("", 404);
        await using var net = new HttpNetAccess([]);
        var provider = new LocalProvider(net);

        var result = await provider.FetchAsync(new WebFetchRequest(server.Base + "/gone"), default);
        await server.WaitAsync();

        Assert.False(result.Ok);
        Assert.StartsWith(nameof(WebErrorKind.NoContent), result.Error!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_gateway_keeps_a_page_it_just_fetched_and_does_not_ask_again()
    {
        await using var server = new Server("<html><body><p>cached</p></body></html>", 200, "text/html",
            keepServing: true);
        await using var net = new HttpNetAccess([]);
        var registry = new WebProviderRegistry();
        registry.Add(new LocalProvider(net));
        var options = new WebOptions { Search = null, Fetch = LocalProvider.ProviderName };
        var gateway = new WebGateway(registry, options);

        var first = await gateway.FetchAsync(new WebFetchRequest(server.Base + "/p"), new WebCall("test"), default);
        var second = await gateway.FetchAsync(new WebFetchRequest(server.Base + "/p"), new WebCall("test"), default);

        Assert.True(first.Ok, first.Error?.Detail);
        Assert.True(second.Ok, second.Error?.Detail);
        // Two answers, one request: the second came from the cache, which is why the counter is still one.
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public void A_deployment_with_no_provider_chosen_offers_nothing()
    {
        var registry = new WebProviderRegistry();
        registry.Add(new LocalProvider(new HttpNetAccess([])));
        var options = new WebOptions { Search = null, Fetch = null };
        var gateway = new WebGateway(registry, options);

        Assert.Null(gateway.SearchProvider);
        Assert.Null(gateway.FetchProvider);
        // The providers exist — the deployment chose not to use them, which is a different thing from them being absent.
        Assert.Contains(LocalProvider.ProviderName, gateway.FetchProviders);
    }

    [Fact]
    public async Task A_mixed_deployment_searches_with_one_provider_and_fetches_with_another()
    {
        await using var searchServer = new Server("""{"results":[{"title":"hit","url":"https://example.com/a"}]}""");
        await using var fetchServer = new Server("<html><body><p>local page</p></body></html>", 200, "text/html");
        await using var net = new HttpNetAccess([Target(searchServer, "tavily")]);
        var registry = new WebProviderRegistry();
        var tavilySearch = new TavilyProvider(net, "tavily");
        registry.Add((ISearchProvider)tavilySearch);
        registry.Add(new LocalProvider(net));
        var gateway = new WebGateway(registry, new WebOptions { Search = "tavily", Fetch = LocalProvider.ProviderName });

        var found = await gateway.SearchAsync(new WebSearchQuery("q"), new WebCall("test"), default);
        var page = await gateway.FetchAsync(new WebFetchRequest(fetchServer.Base + "/p"), new WebCall("test"), default);
        await searchServer.WaitAsync();
        await fetchServer.WaitAsync();

        Assert.True(found.Ok, found.Error?.Detail);
        Assert.Equal("tavily", found.Value!.Provider);
        Assert.True(page.Ok, page.Error?.Detail);
        Assert.Contains("local page", page.Value!.Markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// One provider endpoint. Records the request line and the body, answers with whatever the test said, and counts
    /// how many times it was asked — the last of which is how a cache is proven rather than assumed.
    /// </summary>
    private sealed class Server(string body, int status = 200, string contentType = "application/json",
        bool keepServing = false) : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener =
            new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        private int _requests;

        public string Base
        {
            get
            {
                _listener.Start();
                _loop = Task.Run(AcceptAsync);
                return $"http://127.0.0.1:{((System.Net.IPEndPoint)_listener.LocalEndpoint).Port}";
            }
        }

        public string LastBody { get; private set; } = "";
        public string LastPath { get; private set; } = "";
        public int Requests => Volatile.Read(ref _requests);

        public Task WaitAsync() => keepServing ? Task.CompletedTask : _loop;

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch
                {
                    return;
                }

                try
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var (head, payload) = await ReadAsync(stream);
                        Interlocked.Increment(ref _requests);
                        LastPath = head.Split(' ').Skip(1).FirstOrDefault() ?? "";
                        LastBody = payload;
                        var bytes = Encoding.UTF8.GetBytes(body);
                        var response = $"HTTP/1.1 {status} Whatever\r\nContent-Type: {contentType}\r\n"
                                       + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                        await stream.WriteAsync(bytes);
                        await stream.FlushAsync();
                    }
                }
                catch
                {
                    // The client gave up first; nothing here reports that.
                }

                if (!keepServing) return;
            }
        }

        private static async Task<(string Head, string Body)> ReadAsync(System.Net.Sockets.NetworkStream stream)
        {
            var buffer = new List<byte>(2048);
            var one = new byte[1];
            while (buffer.Count < 64 * 1024)
            {
                if (await stream.ReadAsync(one) == 0) break;
                buffer.Add(one[0]);
                if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' &&
                    buffer[^1] == '\n') break;
            }

            var head = Encoding.ASCII.GetString(buffer.ToArray());
            var length = 0;
            foreach (var line in head.Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line[15..].Trim(), out var parsed))
                    length = parsed;
            if (length == 0) return (head, "");

            var payload = new byte[length];
            var read = 0;
            while (read < length)
            {
                var got = await stream.ReadAsync(payload.AsMemory(read, length - read));
                if (got == 0) break;
                read += got;
            }
            return (head, Encoding.UTF8.GetString(payload, 0, read));
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
            _stop.Dispose();
        }
    }
}
