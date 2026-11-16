using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ALN.Adapter.Qq;
using ALN.Contracts;
using ALN.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ALN.Tests;

/// <summary>
/// The wrapped QQ verbs, as the model would reach them: <c>qq.group.list</c> instead of <c>get_group_list</c> with a
/// hand-built payload, and a level per verb instead of one door for the whole platform.
///
/// Three things are pinned here, and each one is a decision rather than an implementation detail:
/// <list type="number">
///   <item>the levels — reading is the agent's work, muting/kicking/approving is a person's, and a refusal says which;</item>
///   <item>the payload — <c>group</c> takes <c>123456</c>, <c>"123456"</c> or <c>group-123456</c> and the bridge is sent the
///   number, because that is the half of "wrapped" that a model benefits from most (docs/16 §10.1);</item>
///   <item>the refusal — a platform retcode becomes a failure carrying the platform's own sentence, unlike the raw door,
///   where the platform's verdict is a successful call with a refusal inside it.</item>
/// </list>
/// </summary>
public sealed class QqOperationTests
{
    private static readonly Address AdapterAddress = new("adapter", "qq", "ops");

    // ---- the table -----------------------------------------------------------------------------

    [Fact]
    public void Every_verb_is_in_the_table_with_a_schema_and_a_level()
    {
        var (plane, _) = SetUp();
        var registered = plane.Catalog.Where(action => action.Id.StartsWith("qq.", StringComparison.Ordinal)).ToArray();

        // Nothing silently missing: the count comes from the table, and the table is what this asserts against.
        Assert.Equal(QqOperations.Ids.Count, registered.Length);
        foreach (var action in registered)
        {
            Assert.NotNull(action.ParamsJson);
            using var schema = JsonDocument.Parse(action.ParamsJson!);
            // The instance is an argument, not a binding: one host can carry two QQ accounts.
            Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("address", out _), action.Id);
            Assert.Contains("address", schema.RootElement.GetProperty("required")
                .EnumerateArray().Select(item => item.GetString()));
            // The family says which kind of work; the verb may sit deeper (docs/07 §9.1). These six are the ones where
            // acting on somebody else, or on the group itself, is a person's decision rather than the model's.
            var personOnly = action.Id is QqOperations.GroupMute or QqOperations.GroupMuteAll or QqOperations.GroupKick
                or QqOperations.GroupAdmin or QqOperations.GroupNameSet or QqOperations.GroupLeave
                or QqOperations.GroupRequestDecide or QqOperations.FriendRequestDecide;
            Assert.Equal(personOnly ? AccessLevel.Operator : AccessLevel.Agent, action.RequiredLevel);
            // A read that says it changes state would teach a model to be afraid of it, and a write that does not is worse.
            Assert.Equal(!action.Id.Contains(".list", StringComparison.Ordinal)
                       && !action.Id.EndsWith(".get", StringComparison.Ordinal)
                       && !action.Id.EndsWith(".members", StringComparison.Ordinal)
                       && !action.Id.EndsWith(".notices", StringComparison.Ordinal)
                       && !action.Id.EndsWith(".pending", StringComparison.Ordinal)
                       && !action.Id.EndsWith("qq.status", StringComparison.Ordinal), action.Mutating);
        }
    }

    [Fact]
    public async Task Reading_is_the_agents_own_work_and_the_answer_has_stable_field_names()
    {
        await using var bridge = await FakeBridge.StartAsync();
        var (plane, adapter) = await SetUpWithAdapterAsync(bridge);
        try
        {
            var listed = await plane.InvokeAsync(Actor(AccessLevel.Agent), QqOperations.GroupList,
                Args(new { address = AdapterAddress.ToString() }), default);

            Assert.True(listed.Ok, listed.Error?.Message);
            // The platform's shape ("group_id", "max_member_count") is the platform's; this answer is ours, and it is the
            // same on every bridge — which is what lets a model write one call instead of one call per dialect.
            Assert.Equal(1, listed.Value.GetProperty("count").GetInt32());
            var group = listed.Value.GetProperty("groups")[0];
            Assert.Equal(123456L, group.GetProperty("id").GetInt64());
            Assert.Equal("测试群", group.GetProperty("name").GetString());
            Assert.Equal(9L, group.GetProperty("members").GetInt64());

            // And the call the bridge saw is OneBot's own verb, with the argument OneBot spells.
            var call = Assert.Single(bridge.CallsOf("get_group_list"));
            Assert.False(call.GetProperty("params").GetProperty("no_cache").GetBoolean());
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task Muting_somebody_is_a_persons_decision_and_the_refusal_says_so()
    {
        await using var bridge = await FakeBridge.StartAsync();
        var (plane, adapter) = await SetUpWithAdapterAsync(bridge);
        try
        {
            var refused = await plane.InvokeAsync(Actor(AccessLevel.Agent), QqOperations.GroupMute,
                Args(new { address = AdapterAddress.ToString(), group = 123456, user = 20000, seconds = 600 }), default);

            Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
            // The refusal names the level, because a caller who cannot act on "denied" retries or works around it.
            Assert.Contains("operator", refused.Error.Message, StringComparison.Ordinal);
            // And nothing reached the bridge: a refusal that still sends the call is a refusal the platform pays for.
            Assert.Empty(bridge.CallsOf("set_group_ban"));

            var allowed = await plane.InvokeAsync(Actor(AccessLevel.Operator), QqOperations.GroupMute,
                Args(new { address = AdapterAddress.ToString(), group = 123456, user = 20000, seconds = 600 }), default);

            Assert.True(allowed.Ok, allowed.Error?.Message);
            var payload = Assert.Single(bridge.CallsOf("set_group_ban")).GetProperty("params");
            // Numbers, because the platform takes numbers — the caller's spelling is not the wire's spelling.
            Assert.Equal(JsonValueKind.Number, payload.GetProperty("group_id").ValueKind);
            Assert.Equal(123456L, payload.GetProperty("group_id").GetInt64());
            Assert.Equal(20000L, payload.GetProperty("user_id").GetInt64());
            Assert.Equal(600L, payload.GetProperty("duration").GetInt64());
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_conversation_id_is_accepted_where_the_platform_wants_a_number()
    {
        await using var bridge = await FakeBridge.StartAsync();
        var (plane, adapter) = await SetUpWithAdapterAsync(bridge);
        try
        {
            // "group-123456" is how this system names the conversation everywhere else; requiring the bare number here
            // would be the one place a caller has to translate, and translations are where ids get crossed.
            var asked = await plane.InvokeAsync(Actor(AccessLevel.Agent), QqOperations.GroupMemberGet,
                Args(new { address = AdapterAddress.ToString(), group = "group-123456", user = "user-20000" }), default);

            Assert.True(asked.Ok, asked.Error?.Message);
            var payload = Assert.Single(bridge.CallsOf("get_group_member_info")).GetProperty("params");
            Assert.Equal(123456L, payload.GetProperty("group_id").GetInt64());
            Assert.Equal(20000L, payload.GetProperty("user_id").GetInt64());
            Assert.Equal("组长", asked.Value.GetProperty("card").GetString());
            Assert.Equal("admin", asked.Value.GetProperty("role").GetString());
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_platform_refusal_is_a_failure_carrying_the_platforms_own_sentence()
    {
        await using var bridge = await FakeBridge.StartAsync();
        var (plane, adapter) = await SetUpWithAdapterAsync(bridge);
        try
        {
            // This bridge says "no" for one group, the way a real one says "muted", "not a member" or "no permission".
            var refused = await plane.InvokeAsync(Actor(AccessLevel.Operator), QqOperations.GroupMute,
                Args(new { address = AdapterAddress.ToString(), group = 999, user = 20000, seconds = 60 }), default);

            Assert.False(refused.Ok);
            Assert.Equal(ErrorCode.Unavailable, refused.Error!.Code);
            Assert.Contains("100", refused.Error.Message, StringComparison.Ordinal);
            Assert.Contains("没有权限", refused.Error.Message, StringComparison.Ordinal);
            // Not retryable: a call that failed may still have happened, and "retry" is how a mute lands twice.
            Assert.False(refused.Error.Retryable);
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task An_unknown_verb_argument_is_refused_before_the_platform_is_asked()
    {
        await using var bridge = await FakeBridge.StartAsync();
        var (plane, adapter) = await SetUpWithAdapterAsync(bridge);
        try
        {
            var missing = await plane.InvokeAsync(Actor(AccessLevel.Operator), QqOperations.GroupMute,
                Args(new { address = AdapterAddress.ToString(), group = 123456, user = 20000 }), default);

            Assert.Equal(ErrorCode.Invalid, missing.Error!.Code);
            // The reason names the argument and its unit: "invalid" alone teaches a model to guess.
            Assert.Contains("seconds", missing.Error.Message, StringComparison.Ordinal);
            Assert.Empty(bridge.CallsOf("set_group_ban"));
        }
        finally { await adapter.StopAsync(default); }
    }

    // ---- joining a group: the chain the operator asked for -------------------------------------

    [Fact]
    public async Task Who_is_waiting_to_join_is_readable_and_the_decision_is_a_persons()
    {
        await using var bridge = await FakeBridge.StartAsync();
        var (plane, adapter) = await SetUpWithAdapterAsync(bridge);
        try
        {
            // Step one: what is waiting. The flag is handed over as text, ready to be passed back — a model that has to
            // reconstruct it from a number is a model that will get it wrong once per request.
            var pending = await plane.InvokeAsync(Actor(AccessLevel.Agent), QqOperations.GroupRequestPending,
                Args(new { address = AdapterAddress.ToString() }), default);

            Assert.True(pending.Ok, pending.Error?.Message);
            var join = pending.Value.GetProperty("joins")[0];
            Assert.Equal("1712345678", join.GetProperty("flag").GetString());
            Assert.Equal("想进群", join.GetProperty("message").GetString());
            var invite = pending.Value.GetProperty("invites")[0];
            Assert.Equal("1700000000", invite.GetProperty("flag").GetString());

            // Step two: the decision. Letting a stranger into the account's reach is a person's, not the model's.
            var agent = await plane.InvokeAsync(Actor(AccessLevel.Agent), QqOperations.GroupRequestDecide,
                Args(new { address = AdapterAddress.ToString(), flag = "1712345678", kind = "add", approve = true }), default);
            Assert.Equal(ErrorCode.Denied, agent.Error!.Code);
            Assert.Empty(bridge.CallsOf("set_group_add_request"));

            var person = await plane.InvokeAsync(Actor(AccessLevel.Operator), QqOperations.GroupRequestDecide,
                Args(new { address = AdapterAddress.ToString(), flag = "1712345678", kind = "add", approve = true }), default);
            Assert.True(person.Ok, person.Error?.Message);
            var payload = Assert.Single(bridge.CallsOf("set_group_add_request")).GetProperty("params");
            // `sub_type` and not `type`: OneBot accepts both spellings, and this is the one the report carries.
            Assert.Equal("add", payload.GetProperty("sub_type").GetString());
            Assert.True(payload.GetProperty("approve").GetBoolean());
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_kind_that_cannot_be_one_of_the_two_is_refused_here_not_by_the_platform()
    {
        await using var bridge = await FakeBridge.StartAsync();
        var (plane, adapter) = await SetUpWithAdapterAsync(bridge);
        try
        {
            var wrong = await plane.InvokeAsync(Actor(AccessLevel.Operator), QqOperations.GroupRequestDecide,
                Args(new { address = AdapterAddress.ToString(), flag = "1", kind = "joined", approve = true }), default);

            Assert.Equal(ErrorCode.Invalid, wrong.Error!.Code);
            Assert.Contains("add", wrong.Error.Message, StringComparison.Ordinal);
            // A mismatch would come back from the platform as "no such request", which reads like the flag was wrong.
            Assert.Empty(bridge.CallsOf("set_group_add_request"));
        }
        finally { await adapter.StopAsync(default); }
    }

    // ---- the edges -----------------------------------------------------------------------------

    [Fact]
    public async Task An_address_that_is_not_a_qq_instance_says_which_kind_of_no_it_is()
    {
        var (plane, manager) = SetUp();
        // A real instance of another platform: the verb exists, the address exists, and the answer is still no.
        var spec = new AdapterSpec(new Address("adapter", "file", "plain"),
            new AdapterDescriptor("file", "acc", AdapterDirection.Both), "adapter.file");
        Assert.True((await manager.CreateAsync(spec, _ => new PlainAdapter(spec), default)).Ok);

        var wrongPlatform = await plane.InvokeAsync(Actor(AccessLevel.Agent), QqOperations.GroupList,
            Args(new { address = "adapter.file#plain" }), default);
        Assert.Equal(ErrorCode.Invalid, wrongPlatform.Error!.Code);
        Assert.Contains("不是 QQ 适配器", wrongPlatform.Error.Message, StringComparison.Ordinal);

        var unknown = await plane.InvokeAsync(Actor(AccessLevel.Agent), QqOperations.GroupList,
            Args(new { address = "adapter.qq#nobody" }), default);
        Assert.Equal(ErrorCode.Invalid, unknown.Error!.Code);
        Assert.Contains("没有适配器", unknown.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_raw_door_is_still_there_for_everything_this_table_does_not_name()
    {
        // The two are meant to coexist: wrapping the verbs a conversation needs does not mean the bridge's own list is
        // closed, and a wrapped verb does not narrow the door (docs/16 §10, §10.1).
        var (plane, _) = SetUp();
        var raw = plane.Catalog.Single(action => action.Id == AdapterActions.Raw);
        Assert.Equal(AccessLevel.Agent, raw.RequiredLevel);
        Assert.Contains(plane.Catalog, action => action.Id == QqOperations.GroupMute);
    }

    [Fact]
    public async Task The_package_puts_its_own_verbs_in_the_table_when_it_starts()
    {
        // Where the verbs come from matters as much as that they exist: they are registered by the QQ package at start,
        // not by the host, so "the bridge gained a verb" costs a package version and never a host release (docs/16 §10.1).
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapters", [new("adapter.qq", "adapter")]).Ok);
        var manager = new AdapterManager(types, new NoBindings());
        var plane = new ControlPlane(new SilentAudit(), SignalLog.Open(null));
        Assert.True(manager.RegisterActions(plane).Ok);
        Assert.DoesNotContain(plane.Catalog, action => action.Id.StartsWith("qq.", StringComparison.Ordinal));

        var module = new QqAdapterModule();
        var context = new PackageContext(new Dictionary<Type, object>
        {
            [typeof(IEventSink)] = new SilentSink(),
            [typeof(IModuleLoggerFactory)] = new SilentLoggerFactory(),
            [typeof(IAdapterManager)] = manager,
            [typeof(IAdapterFactoryRegistry)] = new AdapterFactoryRegistry(),
            [typeof(IControlPlane)] = plane
        });
        var started = await module.StartAsync(context, default);

        Assert.True(started.Ok, started.Error?.Message);
        // Every verb this package owns, and its factory: the two halves of "the package brought its platform".
        Assert.Equal(QqOperations.Ids.Count,
            plane.Catalog.Count(action => action.Id.StartsWith("qq.", StringComparison.Ordinal)));
        // And it still needs nothing from the host but the runtime that every package needs.
        Assert.Equal("aln.bundled.runtime", Assert.Single(module.Manifest.Dependencies).Id);
    }

    // ---- plumbing ------------------------------------------------------------------------------

    private static Actor Actor(AccessLevel level) => new("model", ActorKind.Model, [ALN.Contracts.Actor.Any], level);

    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

    private static (ControlPlane Plane, AdapterManager Manager) SetUp()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.adapters",
            [new("adapter.file", "adapter"), new("adapter.qq", "adapter")]).Ok);
        var manager = new AdapterManager(types, new NoBindings());
        var plane = new ControlPlane(new SilentAudit(), SignalLog.Open(null));
        Assert.True(manager.RegisterActions(plane).Ok);
        Assert.True(QqOperations.Register(plane, manager).Ok);
        return (plane, manager);
    }

    private static async Task<(ControlPlane Plane, QqAdapter Adapter)> SetUpWithAdapterAsync(FakeBridge bridge)
    {
        var (plane, manager) = SetUp();
        var adapter = NewAdapter(bridge.WebSocketUri!);
        var spec = new AdapterSpec(AdapterAddress, new AdapterDescriptor("qq", "ops", AdapterDirection.Both),
            "adapter.qq");
        var created = await manager.CreateAsync(spec, _ => adapter, default);
        Assert.True(created.Ok, created.Error?.Message);
        Assert.True((await adapter.StartAsync(default)).Ok);
        Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
        return (plane, adapter);
    }

    private static QqAdapter NewAdapter(Uri endpoint) => new(
        new AdapterSpec(AdapterAddress, new AdapterDescriptor("qq", "ops", AdapterDirection.Both), "adapter.qq",
            new Dictionary<string, string> { ["endpoint"] = endpoint.ToString() }),
        new SilentSink(), new SilentLogger());

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private sealed class SilentSink : IEventSink
    {
        public ValueTask<Result> PublishAsync(RoutedEvent routedEvent, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
    }

    private sealed class SilentLogger : IModuleLogger
    {
        public void Info(string message, IReadOnlyDictionary<string, string>? detail = null) { }
        public void Warn(string message, IReadOnlyDictionary<string, string>? detail = null) { }
        public void Error(string message, IReadOnlyDictionary<string, string>? detail = null) { }
    }

    private sealed class SilentLoggerFactory : IModuleLoggerFactory
    {
        private readonly SilentLogger _logger = new();
        public IModuleLogger For(string moduleId) => _logger;
    }

    /// <summary>A package context with no host behind it: what a plugin sees is only what was put in the bag.</summary>
    private sealed class PackageContext(IReadOnlyDictionary<Type, object> services) : IModulePluginContext
    {
        private readonly Dictionary<string, object> _own = new(StringComparer.Ordinal);

        public string PackageId => "aln.adapter.qq";

        public IServiceProvider HostServices { get; } = new Bag(services);

        public Result RegisterService(string serviceId, object service)
        {
            _own[serviceId] = service;
            return Result.Success();
        }

        public Result<T> GetService<T>(string serviceId) where T : class =>
            _own.TryGetValue(serviceId, out var found) && found is T typed
                ? Result<T>.Success(typed)
                : Result<T>.Failure(new(ErrorCode.Invalid, $"No service: {serviceId}"));

        private sealed class Bag(IReadOnlyDictionary<Type, object> services) : IServiceProvider
        {
            public object? GetService(Type serviceType) => services.TryGetValue(serviceType, out var found) ? found : null;
        }
    }

    private sealed class NoBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) => ValueTask.FromResult(Result.Success());
    }

    private sealed class SilentAudit : IAuditSink
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>An adapter of another platform: it exists, it answers, and it is not a QQ account.</summary>
    private sealed class PlainAdapter(AdapterSpec spec) : IChatAdapter
    {
        public AdapterDescriptor Descriptor { get; } = spec.Descriptor;
        public AdapterState State => AdapterState.Running;
        public Address Address { get; } = spec.Address;
        public ModuleId Id => Address.ToModuleId();
        public ValueTask<Result> StartAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> InitializeAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> ReadyAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> AbortAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken ct) =>
            ValueTask.FromResult(Result<SendResult>.Success(new SendResult(true, "m-1")));
    }

    /// <summary>
    /// A OneBot v11 bridge on a real socket: one forward WebSocket, answers by echo, and keeps every call so the payload
    /// can be read rather than inferred. Canned data, because what is being tested is our side of the wire.
    /// </summary>
    private sealed class FakeBridge : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<JsonElement> _calls = new();
        private readonly SemaphoreSlim _write = new(1, 1);
        private WebSocket? _socket;

        private FakeBridge(WebApplication app) => _app = app;

        public Uri? WebSocketUri { get; private set; }
        public bool IsConnected => _socket is { State: WebSocketState.Open };
        public IReadOnlyList<JsonElement> CallsOf(string action) => _calls
            .Where(call => string.Equals(call.GetProperty("action").GetString(), action, StringComparison.Ordinal))
            .ToArray();

        public static async Task<FakeBridge> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();
            var bridge = new FakeBridge(app);

            app.Map("/", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                bridge._socket = socket;
                await bridge.ReceiveAsync(socket, context.RequestAborted);
            });

            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.First();
            bridge.WebSocketUri = new UriBuilder(new Uri(address)) { Scheme = "ws" }.Uri;
            return bridge;
        }

        private async Task ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var frame = new MemoryStream();
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                frame.SetLength(0);
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    frame.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);

                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(frame.ToArray()));
                var root = document.RootElement.Clone();
                _calls.Enqueue(root);
                var echo = root.TryGetProperty("echo", out var echoElement) ? echoElement.GetString() : null;
                var action = root.GetProperty("action").GetString();
                var parameters = root.GetProperty("params");
                switch (action)
                {
                    case "get_login_info":
                        await WriteAsync(socket, new { status = "ok", retcode = 0, echo,
                            data = new { user_id = 10000, nickname = "ops" } });
                        break;
                    case "get_group_list":
                        await WriteAsync(socket, new { status = "ok", retcode = 0, echo, data = new object[]
                        {
                            new { group_id = 123456, group_name = "测试群", member_count = 9, max_member_count = 200 }
                        } });
                        break;
                    case "get_group_member_info":
                        await WriteAsync(socket, new { status = "ok", retcode = 0, echo, data = new
                        {
                            group_id = 123456, user_id = 20000, nickname = "小林", card = "组长", role = "admin"
                        } });
                        break;
                    case "get_group_system_msg":
                        // Both lists, and a null second call would be the other legal shape ("none waiting").
                        await WriteAsync(socket, new { status = "ok", retcode = 0, echo, data = new
                        {
                            invited_requests = new object[]
                            {
                                new { request_id = 1_700_000_000L, invitor_uin = 20000, invitor_nick = "小林",
                                    group_id = 123456, group_name = "测试群", @checked = false }
                            },
                            join_requests = new object[]
                            {
                                new { request_id = 1_712_345_678L, requester_uin = 30000, requester_nick = "路人",
                                    message = "想进群", group_id = 123456, group_name = "测试群", @checked = false }
                            }
                        } });
                        break;
                    case "set_group_ban":
                        if (parameters.GetProperty("group_id").GetInt64() == 999)
                            await WriteAsync(socket, new { status = "failed", retcode = 100,
                                message = "没有权限", wording = "权限不足", echo });
                        else
                            await WriteAsync(socket, new { status = "ok", retcode = 0, echo, data = (object?)null });
                        break;
                    case "set_group_add_request":
                        await WriteAsync(socket, new { status = "ok", retcode = 0, echo, data = (object?)null });
                        break;
                    default:
                        await WriteAsync(socket, new { status = "ok", retcode = 0, echo, data = (object?)null });
                        break;
                }
            }
        }

        private async Task WriteAsync(WebSocket socket, object payload)
        {
            await _write.WaitAsync();
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { _write.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(stop.Token);
            }
            catch { /* the socket may already be gone */ }
            await _app.DisposeAsync();
            _write.Dispose();
        }
    }
}
