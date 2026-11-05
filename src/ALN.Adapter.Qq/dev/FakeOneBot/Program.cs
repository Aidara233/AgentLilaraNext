using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Fake OneBot v11 over a forward WebSocket. Enough to prove the adapter's wiring on a machine with no
// QQ account: accept one connection, push one group message, answer actions by echo, print what came
// back. It is deliberately not a simulator — it has no state, no rate limits and no personality.

var port = 3001;
long group = 123456;
long user = 20000;
long self = 10000;
var text = "你好，我在测试。";
var idle = TimeSpan.FromSeconds(60);
var exitAfterSend = true;

for (var index = 0; index < args.Length; index++)
{
    string? Next() => index + 1 < args.Length ? args[++index] : null;
    switch (args[index])
    {
        case "--port" when int.TryParse(Next(), out var parsedPort): port = parsedPort; break;
        case "--group" when long.TryParse(Next(), out var parsedGroup): group = parsedGroup; break;
        case "--user" when long.TryParse(Next(), out var parsedUser): user = parsedUser; break;
        case "--self" when long.TryParse(Next(), out var parsedSelf): self = parsedSelf; break;
        case "--text": text = Next() ?? text; break;
        case "--idle" when int.TryParse(Next(), out var seconds): idle = TimeSpan.FromSeconds(seconds); break;
        case "--keep-open": exitAfterSend = false; break;
        case "-h":
        case "--help":
            Console.WriteLine(Usage());
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[index]}'.");
            Console.Error.WriteLine(Usage());
            return 2;
    }
}

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, $"http://127.0.0.1:{port}");
var app = builder.Build();
app.UseWebSockets();

var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

app.Map("/", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("expected a WebSocket upgrade");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine($"connected: {context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}");
    var gate = new SemaphoreSlim(1, 1);

    async Task WriteAsync(object payload)
    {
        await gate.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    // One pushed message, shortly after the handshake: the adapter must turn this into an envelope and
    // the core must either have a Channel for it or drop it.
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        Console.WriteLine($"pushing group message: group={group} user={user} text=\"{text}\"");
        await WriteAsync(new
        {
            post_type = "message",
            message_type = "group",
            sub_type = "normal",
            self_id = self,
            group_id = group,
            user_id = user,
            message_id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            sender = new { user_id = user, nickname = "测试人员", card = "测试人员" },
            message = new object[] { new { type = "text", data = new { text } } }
        });
    });

    var buffer = new byte[8192];
    using var frame = new MemoryStream();
    using var idleTimer = new CancellationTokenSource(idle);

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            frame.SetLength(0);
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, idleTimer.Token);
                if (received.MessageType == WebSocketMessageType.Close) return;
                frame.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);

            await HandleAsync(Encoding.UTF8.GetString(frame.ToArray()));
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"idle for {idle.TotalSeconds:0}s; closing");
    }
    catch (WebSocketException ex)
    {
        Console.WriteLine($"socket closed: {ex.Message}");
    }
    finally
    {
        finished.TrySetResult(0);
    }

    async Task HandleAsync(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : null;
        var echo = root.TryGetProperty("echo", out var echoElement) ? echoElement.GetString() : null;
        switch (action)
        {
            case "send_group_msg":
                var parameters = root.GetProperty("params");
                var messageId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Console.WriteLine($"<- send_group_msg group={parameters.GetProperty("group_id")} " +
                                  $"message={parameters.GetProperty("message").GetRawText()}");
                await WriteAsync(new { status = "ok", retcode = 0, data = new { message_id = messageId }, echo });
                if (exitAfterSend) finished.TrySetResult(0);
                break;
            case "get_login_info":
                await WriteAsync(new { status = "ok", retcode = 0, data = new { user_id = self, nickname = "fake" }, echo });
                break;
            // The name actions: the sender of the pushed message is a friend whose nickname is "测试人员" and whose
            // remark is "测试备注", and inside this group their card is "群里的测试人员". This is what lets a person
            // reading the transcript see who said what instead of a number.
            case "get_friend_list":
                await WriteAsync(new
                {
                    status = "ok", retcode = 0, echo,
                    data = new[] { new { user_id = user, nickname = "测试人员", remark = "测试备注" } }
                });
                break;
            case "get_group_member_info":
                await WriteAsync(new
                {
                    status = "ok", retcode = 0, echo,
                    data = new { user_id = user, nickname = "测试人员", card = "群里的测试人员" }
                });
                break;
            case "get_stranger_info":
                await WriteAsync(new { status = "ok", retcode = 0, echo, data = new { user_id = user, nickname = "测试人员" } });
                break;
            default:
                Console.WriteLine($"<- {action} {root.GetRawText()}");
                await WriteAsync(new { status = "failed", retcode = 1404, data = (object?)null, echo });
                break;
        }
    }
});

Console.WriteLine($"FakeOneBot on ws://127.0.0.1:{port}/ (group {group}, user {user}, self {self}, idle {idle.TotalSeconds:0}s).");
await app.StartAsync();
await finished.Task;
await app.StopAsync(TimeSpan.FromSeconds(2));
return 0;

static string Usage() =>
    """
    FakeOneBot — a minimal OneBot v11 server over a forward WebSocket.

      --port <n>     listen port, default 3001
      --group <id>   group to push a message into, default 123456
      --user <id>    sender id, default 20000
      --self <id>    the account the adapter logs in as, default 10000
      --text <text>  text to push, default "你好，我在测试。"
      --idle <s>     close after this long without traffic, default 60
      --keep-open    do not exit after answering the first send_group_msg
    """;
