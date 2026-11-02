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

            // A blank line is the model's way of saying "next message": one reply, several platform messages,
            // and the quoted reply stays on the first one only.
            var split = await adapter.SendAsync(new OutboundMessage("group-123456", "第一段\n\n第二段\n\n第三段", ReplyTo: "4242"), default);
            Assert.True(split.Ok, split.Error?.Message);
            var first = await bot.NextSendAsync();
            var second = await bot.NextSendAsync();
            var third = await bot.NextSendAsync();
            // The quote is its own segment, so the text is what the text segments say.
            static string TextOf(JsonElement action) => string.Concat(action.GetProperty("params").GetProperty("message")
                .EnumerateArray()
                .Where(segment => segment.GetProperty("type").GetString() == "text")
                .Select(segment => segment.GetProperty("data").GetProperty("text").GetString()));
            Assert.Equal(["第一段", "第二段", "第三段"], new[] { first, second, third }.Select(TextOf));
            // The quote rides on the first message; the rest of the same answer would only add arrows.
            Assert.Equal(2, first.GetProperty("params").GetProperty("message").GetArrayLength());
            Assert.Equal(1, second.GetProperty("params").GetProperty("message").GetArrayLength());
            Assert.Equal("9999", split.Value!.PlatformMessageId);
        }
        finally
        {
            await adapter.StopAsync(default);
        }
    }

    [Fact]
    public async Task One_reply_becomes_several_messages_but_never_more_than_the_platform_style_allows()
    {
        var sink = new RecordingSink();
        var adapter = CreateAdapter(new Uri("ws://127.0.0.1:9"), sink);
        var started = await adapter.StartAsync(default);
        try
        {
            // The cap belongs to the Adapter, and the tail is joined back rather than dropped: losing the end of
            // an answer is worse than a longer last message.
            var parts = QqAdapter.SplitIntoMessages("一\n\n二\n\n三\n\n四\n\n五", limit: 3);
            Assert.Equal(["一", "二", "三\n\n四\n\n五"], parts);

            // A single newline is a line break inside one message, so a short list survives intact.
            var listing = QqAdapter.SplitIntoMessages("清单：\n1. 甲\n2. 乙", limit: 3);
            Assert.Equal(["清单：\n1. 甲\n2. 乙"], listing);

            // Nothing to split is still exactly one message, and it is the same text.
            Assert.Equal(["就一句"], QqAdapter.SplitIntoMessages("  就一句  ", limit: 3));

            Assert.Equal("3", adapter.ReadParameter(QqAdapter.MaxMessagesParameter));
            var capped = await adapter.ApplyParameterAsync(QqAdapter.MaxMessagesParameter, "11", default);
            Assert.False(capped.Ok);
            var set = await adapter.ApplyParameterAsync(QqAdapter.MaxMessagesParameter, "5", default);
            Assert.True(set.Ok, set.Error?.Message);
            Assert.Equal("5", adapter.ReadParameter(QqAdapter.MaxMessagesParameter));
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
            new AdapterDescriptor("qq", "10000", AdapterDirection.Both), new Dictionary<string, string>());

        var error = Assert.Throws<ArgumentException>(() => new QqAdapter(spec, new RecordingSink(), new NullLogger()));
        Assert.Contains("endpoint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spec_with_a_non_websocket_endpoint_is_refused()
    {
        var spec = new AdapterSpec(new Address("adapter", "qq", "qq-1"),
            new AdapterDescriptor("qq", "10000", AdapterDirection.Both),
            new Dictionary<string, string> { ["endpoint"] = "http://127.0.0.1:3001" });

        Assert.Throws<ArgumentException>(() => new QqAdapter(spec, new RecordingSink(), new NullLogger()));
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
                new AdapterDescriptor("qq", "10000", AdapterDirection.Both),
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
