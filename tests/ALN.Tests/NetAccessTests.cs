using System.Net;
using System.Net.Sockets;
using System.Text;
using ALN.Contracts;
using ALN.Net;

namespace ALN.Tests;

/// <summary>
/// The one door out to the network. What matters here is not that a GET returns text — it is that everything else is
/// refused: an address that was never named, a method that was never listed, a path that leaves the target's origin,
/// a hostname that resolves to the machine itself, a body larger than the ceiling, a call that outlives its deadline,
/// and a fourth call in a minute when three were allowed.
///
/// Every case runs against a socket this test opens on loopback, so nothing here needs the internet.
/// </summary>
public sealed class NetAccessTests
{
    /// <summary>
    /// A target that may not reach a private address is useless in a test — every address here is 127.0.0.1 — so the
    /// ordinary targets turn that one rule off and <see cref="A_target_that_may_not_reach_private_addresses_refuses_loopback"/>
    /// is the test that turns it back on.
    /// </summary>
    private static NetTarget Target(TinyServer server, NetPolicy? policy = null, string? secret = null) =>
        new("test", new Uri(server.Base), policy ?? new NetPolicy
        {
            AllowPrivate = true,
            Methods = ["GET", "HEAD", "POST"],
            MaxResponseBytes = 1024,
            TimeoutSeconds = 5,
            CallsPerMinute = 100,
        }, secret);

    [Fact]
    public async Task A_name_that_was_never_written_down_gets_nothing_and_learns_nothing()
    {
        await using var server = new TinyServer();
        await using var net = new HttpNetAccess([Target(server)]);

        var result = await net.SendAsync(new NetRequest("secrets.example", "GET", "/ok"), default);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.Denied, result.Error!.Code);
        Assert.Contains(nameof(NetErrorKind.NoSuchTarget), result.Error.Detail, StringComparison.Ordinal);
        // A caller that guessed should not learn whether it guessed close.
        Assert.DoesNotContain("secrets.example", result.Error.Detail ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.example", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_method_the_target_does_not_accept_says_which_ones_it_does()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = true, Methods = ["GET"], TimeoutSeconds = 5 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var result = await net.SendAsync(new NetRequest("test", "DELETE", "/ok"), default);

        Assert.False(result.Ok);
        Assert.Equal(nameof(NetErrorKind.NotAllowed), result.Error!.Detail!.Split(':')[0]);
        Assert.Contains("GET", result.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_outside_the_declared_prefixes_is_refused()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = true, Paths = ["/api"], TimeoutSeconds = 5 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var outside = await net.SendAsync(new NetRequest("test", "GET", "/ok"), default);
        var neighbour = await net.SendAsync(new NetRequest("test", "GET", "/apifoo"), default);

        Assert.False(outside.Ok);
        Assert.Equal(nameof(NetErrorKind.NotAllowed), outside.Error!.Detail!.Split(':')[0]);
        // A prefix matches on a segment boundary: /api does not mean /apifoo.
        Assert.False(neighbour.Ok);
        Assert.Contains("/api", neighbour.Error!.Detail!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("/../etc/passwd")]
    [InlineData("http://evil.example/x")]
    public async Task A_path_cannot_take_the_request_to_another_host(string path)
    {
        await using var server = new TinyServer();
        await using var net = new HttpNetAccess([Target(server)]);

        var result = await net.SendAsync(new NetRequest("test", "GET", path), default);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.Invalid, result.Error!.Code);
        Assert.Equal(nameof(NetErrorKind.Invalid), result.Error.Detail!.Split(':')[0]);
    }

    [Fact]
    public async Task A_target_that_may_not_reach_private_addresses_refuses_loopback()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = false, TimeoutSeconds = 5 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var refused = await net.SendAsync(new NetRequest("test", "GET", "/ok"), default);

        Assert.False(refused.Ok);
        Assert.Equal(nameof(NetErrorKind.PrivateAddress), refused.Error!.Detail!.Split(':')[0]);
        Assert.Equal(ErrorCode.Denied, refused.Error.Code);
    }

    [Fact]
    public async Task The_same_target_says_yes_to_loopback_when_a_person_said_so()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = true, TimeoutSeconds = 5 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var result = await net.SendAsync(new NetRequest("test", "GET", "/ok"), default);

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.Equal(200, result.Value!.Status);
        Assert.Equal("hello", result.Value.Text);
        Assert.False(result.Value.Truncated);
    }

    [Fact]
    public async Task A_body_over_the_ceiling_is_marked_truncated_rather_than_silently_short()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = true, MaxResponseBytes = 64, TimeoutSeconds = 5 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var result = await net.SendAsync(new NetRequest("test", "GET", "/big"), default);

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.True(result.Value!.Truncated);
        Assert.Equal(64, result.Value.Bytes);
        Assert.Equal(64, result.Value.Text.Length);
    }

    [Fact]
    public async Task A_call_that_outlives_its_deadline_is_a_timeout_and_comes_back_on_time()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = true, TimeoutSeconds = 1 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var started = Environment.TickCount64;
        var result = await net.SendAsync(new NetRequest("test", "GET", "/slow"), default);
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
        Assert.Equal(nameof(NetErrorKind.Timeout), result.Error.Detail!.Split(':')[0]);
        // Bounded by the deadline, not by how long the server felt like taking (it sleeps for five seconds).
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"took {elapsed}");
    }

    [Fact]
    public async Task A_redirect_is_answered_as_a_redirect_when_the_target_does_not_follow_them()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = true, TimeoutSeconds = 5 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var result = await net.SendAsync(new NetRequest("test", "GET", "/redirect"), default);

        Assert.True(result.Ok);
        Assert.Equal(302, result.Value!.Status);
        // The body of the redirect, not of where it points: following it is exactly what this refused to do.
        Assert.DoesNotContain("hello", result.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_inside_the_target_is_followed_when_the_target_allowed_it()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = true, FollowRedirects = true, MaxRedirects = 2, TimeoutSeconds = 5 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var result = await net.SendAsync(new NetRequest("test", "GET", "/redirect"), default);

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.Equal(200, result.Value!.Status);
        Assert.Equal("hello", result.Value.Text);
    }

    [Fact]
    public async Task A_redirect_that_leaves_the_target_is_refused_even_when_following_is_allowed()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = true, FollowRedirects = true, MaxRedirects = 2, TimeoutSeconds = 5 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var result = await net.SendAsync(new NetRequest("test", "GET", "/away"), default);

        Assert.False(result.Ok);
        // The one thing a redirect must never do: hand the request to a host nobody named.
        Assert.Equal(nameof(NetErrorKind.NotAllowed), result.Error!.Detail!.Split(':')[0]);
        Assert.Contains("redirect", result.Error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_call_a_minute_means_the_second_one_is_told_to_wait()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = true, CallsPerMinute = 1, TimeoutSeconds = 5 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var first = await net.SendAsync(new NetRequest("test", "GET", "/ok"), default);
        var second = await net.SendAsync(new NetRequest("test", "GET", "/ok"), default);

        Assert.True(first.Ok, first.Error?.Detail);
        Assert.False(second.Ok);
        Assert.Equal(ErrorCode.Conflict, second.Error!.Code);
        Assert.True(second.Error.Retryable);
        Assert.Equal(nameof(NetErrorKind.Throttled), second.Error.Detail!.Split(':')[0]);
    }

    [Fact]
    public async Task The_audit_line_names_the_target_and_keeps_the_query_values_out()
    {
        await using var server = new TinyServer();
        var audit = new RecordingAudit();
        await using var net = new HttpNetAccess([Target(server)], audit);

        var result = await net.SendAsync(new NetRequest("test", "GET", "/echo?token=s3cret", Caller: "session-7"),
            default);

        Assert.True(result.Ok, result.Error?.Detail);
        var record = Assert.Single(audit.Records);
        Assert.Equal("session-7", record.Actor);
        Assert.Equal("net.request", record.Action);
        Assert.Equal("test", record.Target.Instance);
        Assert.Equal("ok", record.Outcome);
        // Keys stay, values go: "which endpoint with which parameters" survives, "what the token was" does not.
        Assert.Contains("token", record.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", record.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", record.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_is_recorded_too()
    {
        await using var server = new TinyServer();
        var audit = new RecordingAudit();
        await using var net = new HttpNetAccess([Target(server)], audit);

        await net.SendAsync(new NetRequest("nowhere", "GET", "/ok"), default);

        var record = Assert.Single(audit.Records);
        Assert.Equal("denied", record.Outcome);
        Assert.Equal("nowhere", record.Target.Instance);
    }

    [Fact]
    public async Task A_header_the_policy_did_not_list_is_refused_and_one_it_did_is_sent()
    {
        await using var server = new TinyServer();
        var policy = new NetPolicy { AllowPrivate = true, Headers = ["X-Trace"], TimeoutSeconds = 5 };
        await using var net = new HttpNetAccess([Target(server, policy)]);

        var refused = await net.SendAsync(new NetRequest("test", "GET", "/ok",
            new Dictionary<string, string> { ["X-Other"] = "1" }), default);
        var host = await net.SendAsync(new NetRequest("test", "GET", "/ok",
            new Dictionary<string, string> { ["Host"] = "evil.example" }), default);
        var allowed = await net.SendAsync(new NetRequest("test", "GET", "/echo",
            new Dictionary<string, string> { ["X-Trace"] = "abc" }), default);

        Assert.False(refused.Ok);
        Assert.Contains("X-Trace", refused.Error!.Detail!, StringComparison.Ordinal);
        // Host is never a caller's to set, even when a policy thinks it listed it.
        Assert.False(host.Ok);
        Assert.Contains("Host", host.Error!.Message, StringComparison.Ordinal);
        Assert.True(allowed.Ok, allowed.Error?.Detail);
    }

    [Fact]
    public void Loopback_private_link_local_and_metadata_addresses_are_all_out_of_bounds()
    {
        foreach (var address in new[]
                 {
                     "127.0.0.1", "127.9.9.9", "10.0.0.5", "172.16.3.4", "192.168.1.1", "169.254.169.254",
                     "100.64.0.1", "0.0.0.0", "::1", "fc00::1", "fe80::1", "::ffff:127.0.0.1",
                 })
            Assert.True(HttpNetAccess.IsForbiddenAddress(IPAddress.Parse(address)), address);

        foreach (var address in new[] { "93.184.216.34", "8.8.8.8", "2606:4700::1111" })
            Assert.False(HttpNetAccess.IsForbiddenAddress(IPAddress.Parse(address)), address);
    }

    [Fact]
    public void The_target_file_keeps_what_it_understands_and_says_why_it_dropped_the_rest()
    {
        var root = Path.Combine(Path.GetTempPath(), "aln-net-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "targets.json");
            File.WriteAllText(path, """
            {
              "targets": [
                { "name": "good", "base": "https://example.com", "methods": ["GET"], "maxBytes": 4096 },
                { "name": "with-user", "base": "https://user:pw@example.com" },
                { "name": "not-a-url", "base": "example.com/x" },
                { "name": "bad-method", "base": "https://example.com", "methods": ["FETCH"] },
                { "name": "too-big", "base": "https://example.com", "maxBytes": 999999999 },
                { "name": "good", "base": "https://example.org" }
              ]
            }
            """);

            var load = NetTargetFile.Read(path);

            Assert.Single(load.Targets);
            Assert.Equal("good", load.Targets[0].Name);
            Assert.Equal(4096, load.Targets[0].Policy.MaxResponseBytes);
            Assert.Equal(5, load.Issues.Count);
            // Each refusal names the thing that was wrong, because a person has to fix the file.
            Assert.Contains(load.Issues, i => i.Problem.Contains("user name or password", StringComparison.Ordinal));
            Assert.Contains(load.Issues, i => i.Problem.Contains("absolute http", StringComparison.Ordinal));
            Assert.Contains(load.Issues, i => i.Problem.Contains("FETCH", StringComparison.Ordinal));
            Assert.Contains(load.Issues, i => i.Problem.Contains("maxBytes", StringComparison.Ordinal));
            Assert.Contains(load.Issues, i => i.Problem.Contains("more than once", StringComparison.Ordinal));

            // Nothing is written back on read; a round trip keeps what survived.
            var again = Path.Combine(root, "written.json");
            Assert.True(NetTargetFile.Write(again, load.Targets).Ok);
            var reread = NetTargetFile.Read(again);
            Assert.Single(reread.Targets);
            Assert.Empty(reread.Issues);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_missing_target_file_is_no_targets_not_an_error()
    {
        var load = NetTargetFile.Read(Path.Combine(Path.GetTempPath(), "aln-absent-" + Guid.NewGuid().ToString("N"),
            "targets.json"));

        Assert.Empty(load.Targets);
        Assert.Empty(load.Issues);
    }

    private sealed class RecordingAudit : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];

        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            lock (Records) Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The smallest server that can answer the paths these tests ask for. A raw socket rather than HttpListener:
    /// HttpListener on Windows wants a URL reservation for anything it serves, and a test that needs an administrator
    /// is a test nobody runs.
    /// </summary>
    private sealed class TinyServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public TinyServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public string Base => $"http://127.0.0.1:{Port}";

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream);
                    if (head is null) return;
                    var (_, rawTarget) = head.Value;
                    var query = rawTarget.Contains('?') ? rawTarget[(rawTarget.IndexOf('?') + 1)..] : "";
                    var path = rawTarget.Split('?')[0];

                    switch (path)
                    {
                        case "/slow":
                            await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                            break;
                        case "/redirect":
                            await WriteAsync(stream, 302, "text/plain", "", location: "/ok");
                            return;
                        case "/away":
                            await WriteAsync(stream, 302, "text/plain", "", location: "http://elsewhere.example/x");
                            return;
                    }

                    var body = path switch
                    {
                        "/ok" => "hello",
                        "/big" => new string('x', 4096),
                        "/echo" => query,
                        _ => "other",
                    };
                    await WriteAsync(stream, 200, "text/plain; charset=utf-8", body);
                }
            }
            catch
            {
                // The client hung up first (a timeout test does exactly that). Nothing to report.
            }
        }

        private static async Task<(string Method, string Target)?> ReadHeadAsync(NetworkStream stream)
        {
            var buffer = new List<byte>(1024);
            var one = new byte[1];
            while (buffer.Count < 16 * 1024)
            {
                var read = await stream.ReadAsync(one);
                if (read == 0) return null;
                buffer.Add(one[0]);
                if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
                    break;
            }
            var head = Encoding.ASCII.GetString(buffer.ToArray());
            var firstLine = head.Split("\r\n")[0];
            var parts = firstLine.Split(' ');
            return parts.Length >= 2 ? (parts[0], parts[1]) : null;
        }

        private static async Task WriteAsync(NetworkStream stream, int status, string contentType, string body,
            string? location = null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = new StringBuilder();
            head.Append($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Found")}\r\n");
            head.Append($"Content-Type: {contentType}\r\n");
            head.Append($"Content-Length: {bytes.Length}\r\n");
            if (location is not null) head.Append($"Location: {location}\r\n");
            head.Append("Connection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
            if (bytes.Length > 0) await stream.WriteAsync(bytes);
            await stream.FlushAsync();
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
