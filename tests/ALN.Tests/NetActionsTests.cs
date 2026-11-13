using System.Text.Json;
using ALN.Contracts;
using ALN.Net;
using ALN.Runtime;
using ALN.Ssh;

namespace ALN.Tests;

/// <summary>
/// The allowlist as verbs. What is being tested is not that a name can be written into a file — it is that the three
/// faces of this decision agree: what the running process will reach, what the file says after a restart, and what a
/// caller is told. A change that only lands in one of them is the failure this surface exists to prevent.
/// </summary>
public sealed class NetActionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-net-actions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (ControlPlane Plane, HttpNetAccess Net, FileSecretStore Secrets, string File) Build()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "net", "targets.json");
        var net = new HttpNetAccess([]);
        var secrets = new FileSecretStore(Path.Combine(_root, "net", "secrets"));
        var plane = new ControlPlane(new SilentAudit(), NullSignalLog.Instance);
        var registered = NetActions.Register(plane, net, net, secrets, file);
        Assert.True(registered.Ok, registered.Error?.Message);
        return (plane, net, secrets, file);
    }

    private static Actor Operator => new("tester", ActorKind.User, [Actor.Any], AccessLevel.Operator);

    private static Actor Model => new("model", ActorKind.Model, [Actor.Any], AccessLevel.Agent);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task An_operator_can_add_a_target_and_the_file_ends_up_saying_the_same_thing()
    {
        var (plane, net, _, file) = Build();

        var result = await plane.InvokeAsync(Operator, NetActions.Set, Json("""
        {"name":"docs","create":true,"base":"https://example.com","paths":["/docs"],"methods":["GET"],"maxBytes":2048}
        """), default);

        Assert.True(result.Ok, result.Error?.Detail);
        // In force now…
        Assert.Equal(["docs"], net.Targets);
        var live = net.Describe("docs")!;
        Assert.Equal(2048, live.Policy.MaxResponseBytes);
        Assert.Equal(["GET"], live.Policy.Methods);
        // …and after a restart.
        var onDisk = NetTargetFile.Read(file);
        Assert.Single(onDisk.Targets);
        Assert.Empty(onDisk.Issues);
        Assert.Equal("https://example.com/", onDisk.Targets[0].Base.ToString());
        Assert.Equal(["/docs"], onDisk.Targets[0].Policy.Paths);
    }

    [Fact]
    public async Task Changing_a_target_keeps_the_fields_that_were_not_mentioned()
    {
        var (plane, net, _, _) = Build();
        await plane.InvokeAsync(Operator, NetActions.Set, Json("""
        {"name":"docs","create":true,"base":"https://example.com","paths":["/docs"],"callsPerMinute":7}
        """), default);

        var changed = await plane.InvokeAsync(Operator, NetActions.Set, Json("""
        {"name":"docs","timeoutSeconds":3}
        """), default);

        Assert.True(changed.Ok, changed.Error?.Detail);
        var target = net.Describe("docs")!;
        Assert.Equal(3, target.Policy.TimeoutSeconds);
        // The address and the paths were not in the second call, so they are what they were.
        Assert.Equal(["/docs"], target.Policy.Paths);
        Assert.Equal(7, target.Policy.CallsPerMinute);
        Assert.Equal("https://example.com/", target.Base.ToString());
    }

    [Fact]
    public async Task A_name_that_does_not_exist_yet_has_to_be_asked_for()
    {
        var (plane, net, _, _) = Build();

        var refused = await plane.InvokeAsync(Operator, NetActions.Set, Json("""
        {"name":"typo","base":"https://example.com"}
        """), default);

        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);
        Assert.Contains("create", refused.Error.Detail!, StringComparison.Ordinal);
        Assert.Empty(net.Targets);
    }

    [Fact]
    public async Task A_credential_is_written_to_the_store_and_comes_back_masked()
    {
        var (plane, net, secrets, _) = Build();

        var written = await plane.InvokeAsync(Operator, NetActions.Set, Json("""
        {"name":"api","create":true,"base":"https://api.example.com","secret":"tok-abcdef"}
        """), default);
        Assert.True(written.Ok, written.Error?.Detail);

        var got = await plane.InvokeAsync(Operator, NetActions.Get, Json("""{"name":"api"}"""), default);
        Assert.True(got.Ok, got.Error?.Detail);
        Assert.Equal("***", got.Value.GetProperty("secret").GetString());
        // The value itself is on disk (0600), not in any answer.
        Assert.DoesNotContain("tok-abcdef", got.Value.GetRawText(), StringComparison.Ordinal);
        Assert.True((await secrets.ExistsAsync("api", default)).Value);

        // Sending "***" back means "keep it", not "set it to three stars".
        var again = await plane.InvokeAsync(Operator, NetActions.Set, Json("""
        {"name":"api","secret":"***","timeoutSeconds":9}
        """), default);
        Assert.True(again.Ok, again.Error?.Detail);
        Assert.Equal("api", net.Describe("api")!.SecretRef);
        Assert.Equal(9, net.Describe("api")!.Policy.TimeoutSeconds);
    }

    [Fact]
    public async Task Removing_a_target_leaves_the_credential_where_it_was()
    {
        var (plane, net, secrets, file) = Build();
        await plane.InvokeAsync(Operator, NetActions.Set, Json("""
        {"name":"api","create":true,"base":"https://api.example.com","secret":"tok-abcdef"}
        """), default);

        var removed = await plane.InvokeAsync(Operator, NetActions.Remove, Json("""{"name":"api"}"""), default);

        Assert.True(removed.Ok, removed.Error?.Detail);
        Assert.Empty(net.Targets);
        Assert.Empty(NetTargetFile.Read(file).Targets);
        // Forgetting a name should not quietly destroy a key somebody else may still be using.
        Assert.True((await secrets.ExistsAsync("api", default)).Value);
    }

    [Fact]
    public async Task A_model_may_read_the_list_and_add_a_target()
    {
        // 2026-11-12 改，原名 A_model_may_read_the_list_but_not_extend_it。net.control 降到 agent，理由是它与
        // net.request 同深：一张能被一次对话扩写的白名单本来就不是白名单——因为门口本来就没挂白名单，每次请求
        // 都是"地址由调用方说"。所以这里现在钉的是"它记得住"，不是"它被挡住"。
        var (plane, net, _, _) = Build();
        await plane.InvokeAsync(Operator, NetActions.Set, Json("""
        {"name":"docs","create":true,"base":"https://example.com"}
        """), default);

        var seen = await plane.InvokeAsync(Model, NetActions.List, Json("{}"), default);
        var extended = await plane.InvokeAsync(Model, NetActions.Set, Json("""
        {"name":"anywhere","create":true,"base":"https://anywhere.example"}
        """), default);

        Assert.True(seen.Ok, seen.Error?.Detail);
        Assert.Equal("docs", seen.Value.GetProperty("targets")[0].GetProperty("name").GetString());
        Assert.True(extended.Ok, extended.Error?.Detail);
        Assert.Equal(["anywhere", "docs"], net.Targets.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_target_whose_file_cannot_be_written_still_works_now_and_says_so()
    {
        var (plane, net, _, _) = Build();
        // A directory where the file should be: the write fails, the change is still in force.
        Directory.CreateDirectory(Path.Combine(_root, "net", "targets.json"));

        var result = await plane.InvokeAsync(Operator, NetActions.Set, Json("""
        {"name":"docs","create":true,"base":"https://example.com"}
        """), default);

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.False(result.Value.GetProperty("persisted").GetBoolean());
        Assert.NotEmpty(result.Value.GetProperty("note").GetString() ?? "");
        Assert.Equal(["docs"], net.Targets);
    }

    [Fact]
    public async Task The_fetch_action_reaches_a_registered_target_and_hands_back_the_body()
    {
        var (plane, net, _, _) = Build();
        var (port, served) = await ServeOnceAsync();
        var created = await plane.InvokeAsync(Operator, NetActions.Set, Json($$"""
        {"name":"local","create":true,"base":"http://127.0.0.1:{{port}}","allowPrivate":true}
        """), default);
        Assert.True(created.Ok, created.Error?.Detail);

        var result = await plane.InvokeAsync(Operator, NetActions.Fetch,
            Json("""{"target":"local","path":"/hi"}"""), default);
        await served;

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.Equal(200, result.Value.GetProperty("status").GetInt32());
        Assert.Equal("hello", result.Value.GetProperty("text").GetString());
        Assert.False(result.Value.GetProperty("truncated").GetBoolean());
        Assert.Equal(["local"], net.Targets);
    }

    [Fact]
    public async Task Fetching_from_a_name_that_was_never_written_down_is_refused()
    {
        var (plane, _, _, _) = Build();

        var result = await plane.InvokeAsync(Operator, NetActions.Fetch,
            Json("""{"target":"nowhere","path":"/hi"}"""), default);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.Denied, result.Error!.Code);
        Assert.Equal(nameof(NetErrorKind.NoSuchTarget), result.Error.Detail!.Split(':')[0]);
    }

    /// <summary>Answers exactly one request and stops: enough for one action, and no port left listening afterwards.</summary>
    private static Task<(int Port, Task Served)> ServeOnceAsync(int status = 200, string body = "hello",
        string? extraHeader = null)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            // One read is enough: the request head arrives in one segment for a GET, and the answer does not depend on
            // what was in it.
            _ = await stream.ReadAsync(new byte[2048]);
            var payload = System.Text.Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} Done\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\n"
                       + (extraHeader is null ? "" : extraHeader + "\r\n")
                       + "Connection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
            listener.Stop();
        });
        return Task.FromResult((port, served));
    }

    [Fact]
    public void The_five_net_verbs_sit_on_the_table_at_the_levels_they_claim()
    {
        var (plane, _, _, _) = Build();

        var ids = plane.Catalog
            .Where(action => action.Id.StartsWith("net.", StringComparison.Ordinal))
            .Select(action => action.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["net.fetch", "net.targets.get", "net.targets.list", "net.targets.remove", "net.targets.set"], ids);
        // 2026-11-12 改：改地址与凭据的那一半从 operator 降到 agent。"现在就 POST 到任意地址"（net.request）一直是
        // agent 级的裸门，那么"把这次用的地址记下来"不比它更深；留一道 operator 只会让模型每次重问一遍。
        Assert.Equal(AccessLevel.Agent, AccessLevels.Of(Capabilities.NetRead));
        Assert.Equal(AccessLevel.Agent, AccessLevels.Of(Capabilities.NetControl));
        // A verb nobody described cannot be called from a schema-driven client.
        Assert.All(plane.Catalog.Where(action => action.Id.StartsWith("net.", StringComparison.Ordinal)),
            action => Assert.False(string.IsNullOrWhiteSpace(action.ParamsJson)));
    }

    private sealed class SilentAudit : IAuditSink
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
