using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ALN.Adapter.Qq;
using ALN.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ALN.Tests;

/// <summary>
/// The adapter against a real socket. A fake OneBot that speaks the same wire format as NapCat is the
/// closest this repository can get to the real thing without a QQ account: it proves the parts that
/// unit tests cannot — handshake, echo correlation, and that a pushed event actually reaches the event
/// sink as an envelope and back out as a send.
/// </summary>
public sealed class OneBotConnectionTests
{
    [Fact]
    public async Task A_pushed_group_message_becomes_an_envelope_and_a_reply_goes_back_out()
    {
        await using var bot = await FakeOneBot.StartAsync();
        var sink = new RecordingSink();
        var log = new ListLogger();
        var adapter = CreateAdapter(bot.WebSocketUri, sink, log);

        try
        {
            var started = await adapter.StartAsync(default);
            Assert.True(started.Ok, started.Error?.Detail ?? started.Error?.Message);
            await WaitUntilAsync(() => adapter.Connected, TimeSpan.FromSeconds(5), () =>
                $"the adapter never connected ({bot.WebSocketUri}): {log.Text} | server: {bot.ServerError ?? "no error"}");

            // The fake bot pushes one group message; the adapter must turn it into exactly one envelope.
            bot.PushTextMessage(groupId: 123456, userId: 20000, messageId: 4242, text: "你好");
            AdapterEnvelope envelope;
            try
            {
                envelope = await sink.First.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Assert.Fail($"no envelope reached the sink ({log.Text} | server: {bot.ServerError ?? "no error"} | " +
                            $"frames: {string.Join(" || ", bot.Received)} | published: {sink.Events.Count})");
                return;
            }
            Assert.Equal("adapter.qq#qq-1", envelope.Adapter.ToString());
            Assert.Equal("qq", envelope.PlatformId);
            Assert.Equal("group-123456", envelope.PlatformConversationId);
            Assert.Equal("4242", envelope.PlatformMessageId);
            Assert.Equal("20000", envelope.Sender);
            Assert.Equal("你好", envelope.Text);

            var sent = await adapter.SendAsync(new OutboundMessage("group-123456", "收到"), default);
            Assert.True(sent.Ok,
                $"send failed: {sent.Error?.Code} {sent.Error?.Message} {sent.Error?.Detail} | " +
                $"server: {bot.ServerError ?? "no error"} | {log.Text}");
            Assert.Equal("9999", sent.Value!.AttemptId);
            var action = await bot.NextSendAsync();
            Assert.Equal("send_group_msg", action.GetProperty("action").GetString());
            Assert.Equal(123456, action.GetProperty("params").GetProperty("group_id").GetInt64());
            var segments = action.GetProperty("params").GetProperty("message").EnumerateArray().ToArray();
            Assert.Equal("收到", segments[0].GetProperty("data").GetProperty("text").GetString());

            // The Adapter sends what it is given, as one message. "How many messages this becomes" is a decision
            // made above it (the reply mode), because the Adapter is also the layer a caller reaches for when the
            // whole text really does belong in one piece.
            var verbatim = await adapter.SendAsync(new OutboundMessage("group-123456", "第一段\n\n第二段", ReplyTo: "4242"), default);
            Assert.True(verbatim.Ok, verbatim.Error?.Message);
            var second = await bot.NextSendAsync();
            var secondSegments = second.GetProperty("params").GetProperty("message").EnumerateArray().ToArray();
            Assert.Equal(2, secondSegments.Length);
            Assert.Equal("第一段\n\n第二段", secondSegments[1].GetProperty("data").GetProperty("text").GetString());
            Assert.Equal("9999", verbatim.Value!.PlatformMessageId);
        }
        finally
        {
            await adapter.StopAsync(default);
        }
    }

    [Fact]
    public async Task Sending_to_a_conversation_we_cannot_parse_fails_before_touching_the_socket()
    {
        var sink = new RecordingSink();
        var adapter = CreateAdapter(new Uri("ws://127.0.0.1:9"), sink); // never connected, and never needed

        var started = await adapter.StartAsync(default);
        Assert.True(started.Ok);
        try
        {
            // A bare id has no platform: guessing group vs private is how a message ends up in the wrong place.
            var sent = await adapter.SendAsync(new OutboundMessage("1664093638", "不知道发到哪"), default);
            Assert.False(sent.Ok);
            Assert.Equal(ErrorCode.Invalid, sent.Error!.Code);
            Assert.Contains("p-", sent.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await adapter.StopAsync(default);
        }
    }

    [Fact]
    public void A_spec_without_an_endpoint_fails_to_create_instead_of_connecting_somewhere_random()
    {
        var spec = new AdapterSpec(new Address("adapter", "qq", "qq-1"),
            new AdapterDescriptor("qq", "10000", AdapterDirection.Both), "adapter.qq", new Dictionary<string, string>());

        var error = Assert.Throws<ArgumentException>(() => new QqAdapter(spec, new RecordingSink(), new NullLogger()));
        Assert.Contains("endpoint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spec_with_a_non_websocket_endpoint_is_refused()
    {
        var spec = new AdapterSpec(new Address("adapter", "qq", "qq-1"),
            new AdapterDescriptor("qq", "10000", AdapterDirection.Both), "adapter.qq",
            new Dictionary<string, string> { ["endpoint"] = "http://127.0.0.1:3001" });

        Assert.Throws<ArgumentException>(() => new QqAdapter(spec, new RecordingSink(), new NullLogger()));
    }

    [Fact]
    public async Task The_adapter_can_say_how_the_platform_names_somebody()
    {
        // The name is per place and the remark is per account, so the answer is assembled from two different questions:
        // the group card (or the nickname outside a group) plus the remark the friend list holds.
        await using var bot = await FakeOneBot.StartAsync();
        var sink = new RecordingSink();
        var log = new ListLogger();
        var adapter = CreateAdapter(bot.WebSocketUri, sink, log);

        try
        {
            var started = await adapter.StartAsync(default);
            Assert.True(started.Ok, started.Error?.Detail ?? started.Error?.Message);
            await WaitUntilAsync(() => adapter.Connected, TimeSpan.FromSeconds(5), () =>
                $"the adapter never connected ({bot.WebSocketUri}): {log.Text} | server: {bot.ServerError ?? "no error"}");

            var inGroup = await adapter.ResolveAsync("20000", "group-123456", default);
            Assert.NotNull(inGroup);
            Assert.Equal("小明明", inGroup!.Primary);   // the group card, not the nickname
            Assert.Equal("老王", inGroup.Remark);
            Assert.Equal("小明明(老王)", inGroup.ToString());

            var elsewhere = await adapter.ResolveAsync("20000", "user-20000", default);
            Assert.NotNull(elsewhere);
            Assert.Equal("小明", elsewhere!.Primary);   // outside a group there is no card to ask for

            // Somebody this account knows nothing about: null, because the caller's own fallback (the id) is a better
            // answer than the id handed back as if it were a name.
            Assert.Null(await adapter.ResolveAsync("99999", "group-123456", default));
        }
        finally
        {
            await adapter.StopAsync(default);
        }
    }

    [Fact]
    public async Task An_envelope_carries_the_name_from_the_event_and_the_remark_from_the_friend_list()
    {
        await using var bot = await FakeOneBot.StartAsync();
        var sink = new RecordingSink();
        var log = new ListLogger();
        var adapter = CreateAdapter(bot.WebSocketUri, sink, log);

        try
        {
            var started = await adapter.StartAsync(default);
            Assert.True(started.Ok, started.Error?.Detail ?? started.Error?.Message);
            await WaitUntilAsync(() => adapter.Connected, TimeSpan.FromSeconds(5), () =>
                $"the adapter never connected ({bot.WebSocketUri}): {log.Text} | server: {bot.ServerError ?? "no error"}");

            // Fetch the remarks before the message, so the assertion is about the message path and not about a race
            // with the background fetch that the first message would have started.
            Assert.NotNull(await adapter.ResolveAsync("20000", "group-123456", default));

            bot.PushTextMessage(groupId: 123456, userId: 20000, messageId: 5150, text: "备注呢");
            var envelope = await sink.First.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("20000", envelope.Sender);        // the id, unchanged: a name is not an identity
            Assert.Equal("测试", envelope.SenderName);     // what the event said
            Assert.Equal("老王", envelope.SenderRemark);   // what the account calls them
            Assert.Equal("测试(老王)", ChannelMessage.Compose(envelope.SenderName, envelope.Sender, envelope.SenderRemark));
        }
        finally
        {
            await adapter.StopAsync(default);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> describe)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail(describe());
    }

    private static QqAdapter CreateAdapter(Uri endpoint, RecordingSink sink, IModuleLogger? logger = null) =>
        new(new AdapterSpec(new Address("adapter", "qq", "qq-1"),
                new AdapterDescriptor("qq", "10000", AdapterDirection.Both), "adapter.qq",
                new Dictionary<string, string> { ["endpoint"] = endpoint.ToString() }),
            sink, logger ?? new NullLogger());

    private sealed class RecordingSink : IEventSink
    {
        private readonly TaskCompletionSource<AdapterEnvelope> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AdapterEnvelope> First => _first.Task;
        public ConcurrentQueue<RoutedEvent> Events { get; } = new();

        public ValueTask<Result> PublishAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
        {
            Events.Enqueue(routedEvent);
            if (routedEvent.Name == EventNames.AdapterEnvelope)
            {
                var envelope = JsonSerializer.Deserialize<AdapterEnvelope>(routedEvent.PayloadJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (envelope is not null) _first.TrySetResult(envelope);
            }
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed class NullLogger : IModuleLogger
    {
        public void Info(string message, IReadOnlyDictionary<string, string>? detail = null) { }
        public void Warn(string message, IReadOnlyDictionary<string, string>? detail = null) { }
        public void Error(string message, IReadOnlyDictionary<string, string>? detail = null) { }
    }

    /// <summary>Keeps the adapter's narration so a failing socket test can say what the adapter tried.</summary>
    private sealed class ListLogger : IModuleLogger
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public string Text => string.Join(" | ", _lines);

        public void Info(string message, IReadOnlyDictionary<string, string>? detail = null) => _lines.Enqueue($"info: {message}");
        public void Warn(string message, IReadOnlyDictionary<string, string>? detail = null) =>
            _lines.Enqueue($"warn: {message} {string.Join(",", detail?.Select(pair => $"{pair.Key}={pair.Value}") ?? [])}");
        public void Error(string message, IReadOnlyDictionary<string, string>? detail = null) => _lines.Enqueue($"error: {message}");
    }

    /// <summary>
    /// A WebSocket endpoint that behaves like a minimal OneBot v11 server: it answers actions by echo
    /// and can push events on demand.
    /// </summary>
    private sealed class FakeOneBot : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<JsonElement> _sends = new();
        private readonly TaskCompletionSource<WebSocket> _socket = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeOneBot(WebApplication app) => _app = app;

        public Uri WebSocketUri { get; private set; } = default!;

        public static async Task<FakeOneBot> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();
            // One instance, created before the endpoint is mapped: a second instance would own the
            // completion sources and the endpoint would be updating the first one's.
            var bot = new FakeOneBot(app);
            app.Map("/", context => bot.HandleAsync(context));
            await app.StartAsync();

            // Kestrel resolves port 0 to a real one; app.Urls still carries the configured "0", so the
            // resolved address has to come from the server feature.
            var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
            var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
            var address = new Uri(addresses.Addresses.First());
            Assert.NotEqual(0, address.Port);
            bot.WebSocketUri = new Uri($"ws://127.0.0.1:{address.Port}/");
            return bot;
        }


        public void PushTextMessage(long groupId, long userId, long messageId, string text) =>
            _ = WriteAsync(JsonSerializer.Serialize(new
            {
                post_type = "message",
                message_type = "group",
                group_id = groupId,
                user_id = userId,
                message_id = messageId,
                self_id = 10000,
                time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                sender = new { user_id = userId, nickname = "测试" },
                message = new object[] { new { type = "text", data = new { text } } }
            }));

        public Task<JsonElement> NextSendAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (_sends.TryDequeue(out var action)) return Task.FromResult(action);
                Thread.Sleep(20);
            }
            throw new TimeoutException("The adapter never sent send_group_msg.");
        }

        private async Task HandleAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            _socket.TrySetResult(socket);
            var buffer = new byte[8192];
            using var frame = new MemoryStream();
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    frame.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        frame.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var text = Encoding.UTF8.GetString(frame.ToArray());
                    Received.Enqueue(text);
                    using var document = JsonDocument.Parse(text);
                    var action = document.RootElement.Clone();
                    var name = action.GetProperty("action").GetString();
                    var echo = action.GetProperty("echo").GetString();

                    if (name == "send_group_msg")
                    {
                        _sends.Enqueue(action);
                        await WriteAsync(JsonSerializer.Serialize(new
                        {
                            status = "ok", retcode = 0, data = new { message_id = 9999 }, echo
                        }));
                    }
                    else if (name is "get_friend_list" or "get_group_member_info" or "get_stranger_info")
                    {
                        await WriteAsync(NameAnswer(name, action.GetProperty("params"), echo));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ServerError = $"{ex.GetType().Name}: {ex.Message}";
                throw;
            }
        }

        /// <summary>Raw frames the adapter sent, kept for failure messages.</summary>
        public ConcurrentQueue<string> Received { get; } = new();

        /// <summary>
        /// The three name actions, answered the way a bridge does: a remark only exists for a friend, a card only exists
        /// inside a group, and somebody this account does not know gets an empty object rather than a made-up name.
        /// </summary>
        private static string NameAnswer(string action, JsonElement parameters, string? echo)
        {
            long? user = parameters.TryGetProperty("user_id", out var id) && id.TryGetInt64(out var parsed) ? parsed : null;
            object data = action switch
            {
                "get_friend_list" => new object[]
                {
                    new { user_id = 20000, nickname = "小明", remark = "老王" },
                    new { user_id = 20001, nickname = "小红", remark = "" }
                },
                "get_group_member_info" when user == 20000 => new { user_id = 20000, nickname = "小明", card = "小明明" },
                "get_stranger_info" when user == 20000 => new { user_id = 20000, nickname = "小明" },
                _ => new { }
            };
            return JsonSerializer.Serialize(new { status = "ok", retcode = 0, data, echo });
        }

        public string? ServerError { get; set; }

        private async Task WriteAsync(string json)
        {
            if (!_socket.Task.IsCompleted) await Task.WhenAny(_socket.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            var socket = await _socket.Task;
            var bytes = Encoding.UTF8.GetBytes(json);
            // A real OneBot serialises its writes; two concurrent SendAsync calls on one WebSocket throw.
            await _serverWriteGate.WaitAsync();
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _serverWriteGate.Release(); }
        }

        private readonly SemaphoreSlim _serverWriteGate = new(1, 1);

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(TimeSpan.FromSeconds(2));
            await _app.DisposeAsync();
        }
    }
}
