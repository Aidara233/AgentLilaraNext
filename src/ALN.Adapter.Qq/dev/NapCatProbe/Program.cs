using System.Text.Json;
using ALN.Adapter.Qq;
using ALN.Contracts;
using ALN.Contracts.Adapters;
using ALN.Runtime;

// A real NapCat, on purpose: connect the adapter to a live bridge, log in, optionally send one message (text and/or a
// picture), optionally stay a while and print what arrives. It is the counterpart of FakeOneBot — that one proves the
// shape, this one proves the bridge.
//
//   dotnet run --project src/ALN.Adapter.Qq/dev/NapCatProbe -- \
//     --endpoint ws://debian:3003 --token <token> --to user-1664093638 --text "测试" --listen 60
//
//   --image <path>   also send a picture (the file on this machine, read through the host's byte port)
//   --listen <s>     keep the socket open and print incoming envelopes (that is how an arriving picture is checked)
//   --probe          connect and log in only
//
// --to alone does not speak: reading a group's file area with --list-files used to also say 探测 into the group. Something
// to send (--text / --image / --file) is what makes it talk.
//
// Nothing is written to disk and no history is kept: this is a wire, not a host.

var endpoint = (string?)null;
var token = (string?)null;
var to = (string?)null;
var text = "探测";
var said = false;
var image = (string?)null;
var filePath = (string?)null;
var fileDelivery = (string?)null;
var inspect = (string?)null;
var listFiles = false;
var getFile = (string?)null;
var listen = 0;
var probeOnly = false;
var mediaEndpointUrl = (string?)null;
var history = (string?)null;

for (var index = 0; index < args.Length; index++)
{
    string? Next() => index + 1 < args.Length ? args[++index] : null;
    switch (args[index])
    {
        case "--endpoint": endpoint = Next(); break;
        case "--token": token = Next(); break;
        case "--to": to = Next(); break;
        case "--text": text = Next() ?? text; said = true; break;
        case "--image": image = Next(); break;
        case "--file": filePath = Next(); break;
        case "--file-delivery": fileDelivery = Next(); break;
        case "--inspect": inspect = Next(); break;
        case "--list-files": listFiles = true; break;
        case "--get-file": getFile = Next(); break;
        case "--media-endpoint": mediaEndpointUrl = Next(); break;
        case "--history": history = Next(); break;
        case "--listen" when int.TryParse(Next(), out var seconds): listen = seconds; break;
        case "--probe": probeOnly = true; break;
        case "-h":
        case "--help":
            Console.WriteLine(Usage());
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[index]}'.");
            return 2;
    }
}

if (string.IsNullOrWhiteSpace(endpoint))
{
    Console.Error.WriteLine("--endpoint is required (ws://host:port).");
    return 2;
}

var sink = new PrintingSink();
// A throwaway workspace for the probe's own picture: the same port and the same scope a model's screenshot would use.
var root = Path.Combine(Path.GetTempPath(), "napcat-probe-" + Guid.NewGuid().ToString("N"));
var workspace = Path.Combine(root, FileScopes.Workspace);
Directory.CreateDirectory(workspace);
var files = new LocalFileSystemAccess(root, [FileScopes.Workspace, FileScopes.Media],
    binaryScopes: [FileScopes.Workspace, FileScopes.Media]);
// The same door the host would open, on the same code: this is how the URL half of sending is checked against a bridge.
ALN.Api.MediaEndpoint? mediaEndpoint = null;
if (!string.IsNullOrWhiteSpace(mediaEndpointUrl))
{
    var opened = await ALN.Api.MediaEndpoint.StartAsync(mediaEndpointUrl, files, CancellationToken.None,
        line => Console.WriteLine($"  media: {line}"));
    if (!opened.Ok)
    {
        Console.Error.WriteLine($"media endpoint failed: {opened.Error?.Message} {opened.Error?.Detail}");
        return 1;
    }
    mediaEndpoint = opened.Value;
    Console.WriteLine($"media endpoint: {mediaEndpoint!.BaseUrl}");
}

var adapter = new QqAdapter(
    new AdapterSpec(new Address("adapter", "qq", "probe"), new("qq", "probe", AdapterDirection.Both), "adapter.qq",
        Settings(endpoint, token, fileDelivery)),
    sink, new ConsoleLogger(), files: files, urls: mediaEndpoint);

var started = await adapter.StartAsync(CancellationToken.None);
if (!started.Ok)
{
    Console.Error.WriteLine($"start failed: {started.Error?.Message}");
    return 1;
}

Console.WriteLine($"connecting to {endpoint} …");
var connected = await WaitAsync(() => adapter.Connected, TimeSpan.FromSeconds(20));
// The socket being open is not the same as the bridge having told us who it is: get_login_info is a round trip of its
// own, and a bridge that refuses it leaves an account id of "?" — which is worth seeing rather than racing past.
if (connected) await WaitAsync(() => adapter.AccountId is not null, TimeSpan.FromSeconds(8));
Console.WriteLine(connected
    ? $"connected: account={adapter.AccountId ?? "?"} name={adapter.DisplayName ?? "?"}"
    : $"NOT connected ({adapter.HealthDetail})");
if (!connected) return 1;
if (probeOnly)
{
    await adapter.StopAsync(CancellationToken.None);
    return connected ? 0 : 1;
}

if (listFiles)
{
    // The group's file area, through the same code a Session's tool uses. Printed as the model would see it: handle,
    // name, size, date, uploader — plus whatever the listing says it is not showing.
    if (string.IsNullOrWhiteSpace(to))
    {
        Console.Error.WriteLine("--list-files needs --to <group-…>.");
        return 2;
    }
    var catalog = (IFileCatalogAdapter)adapter;
    var listed = await catalog.ListAsync(to!, 50, CancellationToken.None);
    if (!listed.Ok)
    {
        Console.WriteLine($"list failed: {listed.Error?.Code} {listed.Error?.Message} {listed.Error?.Detail}");
    }
    else
    {
        Console.WriteLine($"listed {listed.Value!.Files.Count} file(s)");
        foreach (var entry in listed.Value.Files)
            Console.WriteLine($"  {entry.Handle}  {entry.Name}  {entry.Bytes?.ToString() ?? "?"}B  " +
                $"{entry.At?.ToString("yyyy-MM-dd HH:mm") ?? "-"}  {entry.From ?? "-"}");
        if (listed.Value.Folders is { Count: > 0 } folders)
            Console.WriteLine($"  folders: {string.Join("、", folders)}");
        if (listed.Value.Note is { Length: > 0 } note) Console.WriteLine($"  note: {note}");
    }
}

if (!string.IsNullOrWhiteSpace(getFile))
{
    // The bytes behind one handle: which of the four shapes the bridge answered with is exactly what this run is for, so
    // the size and the type are printed and the payload is not.
    if (string.IsNullOrWhiteSpace(to))
    {
        Console.Error.WriteLine("--get-file needs --to <conversation>.");
        return 2;
    }
    var catalog = (IFileCatalogAdapter)adapter;
    var startedAt = DateTimeOffset.UtcNow;
    var fetched = await catalog.FetchAsync(to!, getFile!, IBinaryFileAccess.MaxTransferBytes, CancellationToken.None);
    var took = DateTimeOffset.UtcNow - startedAt;
    if (!fetched.Ok)
    {
        Console.WriteLine($"get_file failed after {took.TotalMilliseconds:0}ms: {fetched.Error?.Code} " +
            $"{fetched.Error?.Message} {fetched.Error?.Detail}");
    }
    else
    {
        var content = fetched.Value!;
        await using var stream = content.Content;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        var probed = MediaProbe.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, MediaProbe.HeaderBytes)));
        Console.WriteLine($"get_file ok in {took.TotalMilliseconds:0}ms: name={content.Name} " +
            $"type={content.MediaType ?? "-"} declared={content.Bytes?.ToString() ?? "?"}B " +
            $"actual={bytes.Length}B probe={probed.Kind}/{probed.MediaType}");
    }
}

if (!string.IsNullOrWhiteSpace(inspect))
{
    // What the bridge actually says about a message and its picture: get_msg gives the segments back, get_image is what
    // a segment with only a file name has to be resolved with. Printed whole — the point is to see the real shape.
    using var id = JsonDocument.Parse($$"""{"message_id":{{inspect}}}""");
    var fetched = await adapter.RawAsync("get_msg", id.RootElement, CancellationToken.None);
    Console.WriteLine(fetched.Ok
        ? $"get_msg: {fetched.Value.GetRawText()}"
        : $"get_msg failed: {fetched.Error?.Message} {fetched.Error?.Detail}");

    // Then ask the bridge to resolve the first picture it just described, the way a segment with no url would.
    if (fetched.Ok && fetched.Value.TryGetProperty("data", out var data) &&
        data.TryGetProperty("message", out var segments) && segments.ValueKind == JsonValueKind.Array)
    {
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object) continue;
            if (!segment.TryGetProperty("type", out var type) || type.GetString() != "image") continue;
            if (!segment.TryGetProperty("data", out var picture)) continue;
            var parameters = new Dictionary<string, object>();
            if (picture.TryGetProperty("file", out var file) && file.GetString() is { Length: > 0 } name) parameters["file"] = name;
            if (picture.TryGetProperty("file_id", out var fileId) && fileId.GetString() is { Length: > 0 } resolved)
                parameters["file_id"] = resolved;
            using var request = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
            var resolvedImage = await adapter.RawAsync("get_image", request.RootElement, CancellationToken.None);
            Console.WriteLine(resolvedImage.Ok
                ? $"get_image {JsonSerializer.Serialize(parameters)}: {Summarize(resolvedImage.Value)}"
                : $"get_image failed: {resolvedImage.Error?.Message} {resolvedImage.Error?.Detail}");
            // And then try the url it just gave, with a plain client: this is the question that decides whether an
            // inbound picture is reachable at all when the bridge is another machine.
            // RawAsync hands back the whole envelope (ok / retcode / message / data): the url lives one level down.
            if (resolvedImage.Ok && resolvedImage.Value.TryGetProperty("data", out var payload) &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("url", out var link) &&
                link.GetString() is { Length: > 0 } urlText && Uri.TryCreate(urlText, UriKind.Absolute, out var target))
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    using var response = await client.GetAsync(target);
                    var bytes = response.IsSuccessStatusCode ? (await response.Content.ReadAsByteArrayAsync()).Length : 0;
                    Console.WriteLine($"get_image url: code={(int)response.StatusCode} bytes={bytes} " +
                        $"type={response.Content.Headers.ContentType} host={target.Host}");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    Console.WriteLine($"get_image url failed: {ex.GetType().Name} {ex.Message}");
                }
            }
            break;
        }
    }
}

if (!string.IsNullOrWhiteSpace(history))
{
    // What actually arrived: the newest message from that person, its segments, and whether a picture in it is
    // reachable at all. `get_friend_msg_history` is how a bridge that only pushes while connected can still be checked
    // for a message it pushed a minute ago.
    using var request = JsonDocument.Parse($$"""{"user_id":{{history}},"count":20}""");
    var page = await adapter.RawAsync("get_friend_msg_history", request.RootElement, CancellationToken.None);
    Console.WriteLine(page.Ok ? $"history: {Summarize(page.Value)}" : $"history failed: {page.Error?.Message}");
    if (page.Ok)
    {
        var messages = page.Value.TryGetProperty("data", out var payload) &&
                       payload.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array
            ? list
            : default;
        if (messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in messages.EnumerateArray().Reverse())
            {
                if (!entry.TryGetProperty("message", out var segments) || segments.ValueKind != JsonValueKind.Array) continue;
                var picture = segments.EnumerateArray().FirstOrDefault(segment =>
                    segment.TryGetProperty("type", out var type) && type.GetString() == "image" &&
                    segment.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object);
                if (picture.ValueKind != JsonValueKind.Object) continue;

                Console.WriteLine($"newest picture: message_id={entry.GetProperty("message_id")} " +
                    $"data={Summarize(picture.GetProperty("data"))}");
                var pictureData = picture.GetProperty("data");
                var arguments = new Dictionary<string, string>();
                if (pictureData.TryGetProperty("file", out var name) && name.GetString() is { Length: > 0 } fileName)
                    arguments["file"] = fileName;
                if (pictureData.TryGetProperty("file_id", out var fileId) && fileId.GetString() is { Length: > 0 } id)
                    arguments["file_id"] = id;
                using var resolve = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
                var resolved = await adapter.RawAsync("get_image", resolve.RootElement, CancellationToken.None);
                Console.WriteLine(resolved.Ok ? $"get_image: {Summarize(resolved.Value)}" : $"get_image failed: {resolved.Error?.Message}");
                if (resolved.Ok && resolved.Value.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("url", out var link) && link.GetString() is { Length: > 0 } urlText &&
                    Uri.TryCreate(urlText, UriKind.Absolute, out var target))
                {
                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                        using var response = await client.GetAsync(target);
                        var bytes = response.IsSuccessStatusCode ? (await response.Content.ReadAsByteArrayAsync()).Length : 0;
                        Console.WriteLine($"url: code={(int)response.StatusCode} bytes={bytes} type={response.Content.Headers.ContentType}");
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        Console.WriteLine($"url failed: {ex.GetType().Name} {ex.Message}");
                    }
                }
                break;
            }
        }
    }
}

// Speaking is now something the caller asks for. `--to` used to be enough, which meant that reading a group's file area
// also said 探测 into the group — a probe should not talk to people because it was pointed at them.
if (!string.IsNullOrWhiteSpace(to) && (said || filePath is not null || image is not null))
{
    OutboundMessage message;
    if (!string.IsNullOrWhiteSpace(filePath))
    {
        // A file takes the road a picture takes — read through the host's byte port out of a scope — but it is delivered
        // as an upload of its own, so the probe prints the result of that call rather than a message id.
        var bytes = await File.ReadAllBytesAsync(filePath);
        var name = Path.GetFileName(filePath);
        await File.WriteAllBytesAsync(Path.Combine(workspace, name), bytes);
        var info = MediaProbe.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, MediaProbe.HeaderBytes)));
        var described = new MediaRef(FileScopes.Workspace, name, info.Kind, info.MediaType, bytes.Length, null, null, null);
        var blocks = new List<MessageBlock> { new TextBlock(text), new FileBlock(described, name) };
        message = new OutboundMessage(to, text, null, blocks);
        Console.WriteLine($"sending {bytes.Length} bytes as a file ({info.MediaType}, delivery={fileDelivery ?? "auto"}) …");
    }
    else if (string.IsNullOrWhiteSpace(image))
    {
        message = new OutboundMessage(to, text);
    }
    else
    {
        // Read the picture through the same port the host gives the adapter, out of a throwaway workspace: this is the
        // path a model's own screenshot takes (docs/28 §6.5).
        var bytes = await File.ReadAllBytesAsync(image);
        await File.WriteAllBytesAsync(Path.Combine(workspace, "probe.png"), bytes);
        var picture = new MediaRef(FileScopes.Workspace, "probe.png", MediaKind.Image, "image/png", bytes.Length, null, null, null);
        message = new OutboundMessage(to, text, null, [new TextBlock(text), new ImageBlock(picture)]);
        Console.WriteLine($"sending {bytes.Length} bytes as a picture …");
    }

    var startedAt = DateTimeOffset.UtcNow;
    var sent = await adapter.SendAsync(message, CancellationToken.None);
    var took = DateTimeOffset.UtcNow - startedAt;
    Console.WriteLine(sent.Ok
        ? $"sent ok in {took.TotalMilliseconds:0}ms: attempt={sent.Value!.AttemptId} platformMessageId={sent.Value.PlatformMessageId ?? "-"}"
        : $"send failed after {took.TotalMilliseconds:0}ms: {sent.Error?.Code} {sent.Error?.Message} {sent.Error?.Detail}");
}

if (listen > 0)
{
    Console.WriteLine($"listening {listen}s for incoming messages …");
    await Task.Delay(TimeSpan.FromSeconds(listen));
}

await adapter.StopAsync(CancellationToken.None);
return 0;

static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout){
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (condition()) return true;
        await Task.Delay(100);
    }
    return condition();
}

/// <summary>
/// A picture's answer, without the payload: whether it came as a url, a path, or bytes, and how many. Printing the whole
/// base64 of a 5 MB picture would drown the one line that matters.
/// </summary>
static string Summarize(JsonElement root)
{
    // Key names and shapes, not values: the question is which fields a bridge answers with (url? base64? a path?), and a
    // 40 MB base64 would answer it much too loudly.
    var output = new List<string>();
    foreach (var property in root.EnumerateObject())
    {
        var shape = property.Value.ValueKind switch
        {
            JsonValueKind.String => "string(" + (property.Value.GetString()?.Length ?? 0) + ")",
            JsonValueKind.Object => "object{" + string.Join(",", property.Value.EnumerateObject().Select(inner => inner.Name)) + "}",
            _ => property.Value.GetRawText()[..Math.Min(60, property.Value.GetRawText().Length)]
        };
        output.Add($"{property.Name}:{shape}");
    }
    return string.Join(" ", output);
}

static string Usage() =>
    """
    NapCatProbe — the real adapter against a live NapCat.

      --endpoint <ws://host:port>  required
      --token <accessToken>        empty when the bridge has none
      --to <conversation>          e.g. user-1664093638 or group-123456
      --text <text>                what to say (default "探测")
      --image <path>               also send this file as a picture
      --file <path>                send this file as an upload of its own (docs/28 §6.6)
      --file-delivery <mode>       auto | url | path | base64 | two-step (adapter setting `fileDelivery`)
      --list-files                 print the group's file area through IFileCatalogAdapter (docs/28 §6.7)
      --get-file <handle>          fetch one f: handle and report which shape the bridge answered with
      --listen <seconds>           stay connected and print what arrives
      --probe                      connect and log in only
    """;

static Dictionary<string, string> Settings(string endpoint, string? token, string? fileDelivery)
{
    var settings = new Dictionary<string, string>
    {
        ["endpoint"] = endpoint,
        ["accessToken"] = token ?? string.Empty
    };
    if (!string.IsNullOrWhiteSpace(fileDelivery)) settings["fileDelivery"] = fileDelivery;
    return settings;
}

/// <summary>Every envelope the bridge pushes, printed one line each: enough to see whether a picture came with it.</summary>
internal sealed class PrintingSink : IEventSink
{
    public ValueTask<Result> PublishAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
    {
        if (routedEvent.Name == EventNames.AdapterEnvelope)
        {
            var envelope = JsonSerializer.Deserialize<AdapterEnvelope>(routedEvent.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (envelope is not null)
                Console.WriteLine($"inbound: from={envelope.Sender} name={envelope.SenderName} " +
                    $"text=\"{envelope.Text}\" attachments=" +
                    (envelope.Attachments is { Count: > 0 } attachments
                        ? string.Join(",", attachments.Select(attachment =>
                            $"{attachment.MediaType}/{attachment.Bytes.Length}B"))
                        : "none"));
            else
                Console.WriteLine($"inbound: {routedEvent.PayloadJson}");
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> SubscribeAsync(string name,
        Func<RoutedEvent, CancellationToken, ValueTask<Result>> handler, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());

    public ValueTask<Result> SubscribeAllAsync(Func<RoutedEvent, CancellationToken, ValueTask<Result>> handler,
        CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
}

internal sealed class ConsoleLogger : IModuleLogger
{
    public void Info(string message, IReadOnlyDictionary<string, string>? detail = null) =>
        Console.WriteLine($"  info: {message} {Join(detail)}");

    public void Warn(string message, IReadOnlyDictionary<string, string>? detail = null) =>
        Console.WriteLine($"  warn: {message} {Join(detail)}");

    public void Error(string message, IReadOnlyDictionary<string, string>? detail = null) =>
        Console.WriteLine($"  error: {message} {Join(detail)}");

    private static string Join(IReadOnlyDictionary<string, string>? detail) =>
        detail is null ? string.Empty : string.Join(" ", detail.Select(entry => $"{entry.Key}={entry.Value}"));
}
