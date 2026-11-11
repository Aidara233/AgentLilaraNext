using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ALN.Adapter.Qq;
using ALN.Contracts;
using ALN.Contracts.Adapters;
using ALN.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ALN.Tests;

/// <summary>
/// The QQ adapter over a real socket: one Kestrel that speaks OneBot v11 on a forward WebSocket and serves a picture
/// over HTTP, and the adapter reaching it the way it reaches NapCat.
///
/// This is the closest thing to a live bridge that runs without a QQ account, and it pins exactly the two things the
/// unit tests could not: a picture <b>arrives as bytes fetched over HTTP</b>, and a picture <b>leaves as a
/// <c>base64://</c> segment</b>. What it cannot say is whether a real NapCat accepts that segment — see docs/28 §8.
/// </summary>
public sealed class QqMediaWireTests : IDisposable
{
    private static readonly Address AdapterAddress = new("adapter", "qq", "wire");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-qq-wire-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A 33-byte PNG header: the only part of a picture any of this code interprets.</summary>
    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x03,
        0x08, 0x06, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    [Fact]
    public async Task A_picture_that_arrives_over_http_becomes_an_attachment()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            // The picture is somewhere on the bridge's own HTTP side; the segment only carries its url.
            await bridge.PushGroupMessageAsync(GroupText(123456, 20000, imageUrl: bridge.MediaUri("x.png").ToString()));

            var envelope = await sink.WaitForEnvelopeAsync(TimeSpan.FromSeconds(10));
            // The placeholder is still in the text: a model that cannot see pictures has something to read.
            Assert.Contains("[图片]", envelope.Text);
            Assert.True(envelope.Attachments is { Count: 1 },
                "attachments=" + (envelope.Attachments?.Count.ToString() ?? "null") +
                " warnings=" + string.Join(" | ", sink.Log.Warnings));
            var attachment = Assert.Single(envelope.Attachments!);
            Assert.Equal(MediaKind.Image, attachment.Kind);
            Assert.Equal("image/png", attachment.MediaType);
            Assert.Equal(Png(), attachment.Bytes);
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_picture_on_another_machine_still_leaves_as_bytes()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        // The picture the model wants to send lives in this host's workspace — not on the bridge's disk.
        Directory.CreateDirectory(Path.Combine(_root, FileScopes.Workspace));
        await File.WriteAllBytesAsync(Path.Combine(_root, FileScopes.Workspace, "shot.png"), Png());
        var files = new LocalFileSystemAccess(_root, [FileScopes.Workspace], binaryScopes: [FileScopes.Workspace]);
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var picture = new MediaRef(FileScopes.Workspace, "shot.png", MediaKind.Image, "image/png",
                Png().Length, 4, 3, null);
            var sent = await adapter.SendAsync(new OutboundMessage("group-123456", "看这个", null,
                [new TextBlock("看这个"), new ImageBlock(picture)]), default);
            Assert.True(sent.Ok, sent.Error?.Message);

            var segments = await bridge.WaitForSendAsync(TimeSpan.FromSeconds(10));
            var kinds = segments.EnumerateArray().Select(segment => segment.GetProperty("type").GetString()).ToArray();
            Assert.Equal(new[] { "text", "image" }, kinds);
            var file = segments[1].GetProperty("data").GetProperty("file").GetString()!;
            // base64:// and not a path: the bridge may be a container on another machine, where our paths mean nothing.
            Assert.StartsWith("base64://", file, StringComparison.Ordinal);
            Assert.Equal(Png(), Convert.FromBase64String(file["base64://".Length..]));
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_picture_with_a_url_the_bridge_cannot_reach_costs_the_picture_not_the_message()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            // A url on a port nobody listens on: this is what a bridge behind NAT looks like from here.
            await bridge.PushGroupMessageAsync(GroupText(123456, 20000, imageUrl: "http://127.0.0.1:1/gone.png"));

            var envelope = await sink.WaitForEnvelopeAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("看这个[图片]", envelope.Text.Trim());
            Assert.Null(envelope.Attachments);
            Assert.Contains(sink.Log.Warnings, warning => warning.Contains("could not be fetched", StringComparison.Ordinal));
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_segment_with_only_a_file_name_is_left_as_text_and_says_so()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            // Some bridges send only a file name, which get_image is the documented way to resolve. This bridge has
            // nothing to resolve it with (and no pathMap is configured), so the honest behaviour is text and a reason.
            await bridge.PushGroupMessageAsync(GroupText(123456, 20000, imageUrl: null));

            var envelope = await sink.WaitForEnvelopeAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("看这个[图片]", envelope.Text.Trim());
            Assert.Null(envelope.Attachments);
            Assert.Contains(sink.Log.Warnings, warning =>
                warning.Contains("get_image", StringComparison.Ordinal) ||
                warning.Contains("no url", StringComparison.Ordinal));
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_file_name_the_bridge_resolves_to_base64_becomes_bytes_without_a_second_fetch()
    {
        // The other half of get_image: an implementation that answers with the bytes. The same code path accepts a
        // data url in `file`, which is what NapCat-shaped bridges sometimes hand back.
        await using var bridge = await FakeBridge.StartAsync(Png(), resolveImageAs: "base64://" + Convert.ToBase64String(Png()));
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            await bridge.PushGroupMessageAsync(GroupText(123456, 20000, imageUrl: null));

            var envelope = await sink.WaitForEnvelopeAsync(TimeSpan.FromSeconds(15));
            var attachment = Assert.Single(envelope.Attachments!);
            Assert.Equal(Png(), attachment.Bytes);
            Assert.Equal("image/png", attachment.MediaType);
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_picture_too_big_to_send_still_arrives_as_its_description()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        Directory.CreateDirectory(Path.Combine(_root, FileScopes.Workspace));
        // One byte over the limit that exists because a bridge has to hold the whole segment in its own memory.
        var huge = new byte[QqAdapter.MaxOutboundImageBytes + 1];
        Png().CopyTo(huge, 0);
        await File.WriteAllBytesAsync(Path.Combine(_root, FileScopes.Workspace, "huge.png"), huge);
        var files = new LocalFileSystemAccess(_root, [FileScopes.Workspace], binaryScopes: [FileScopes.Workspace]);
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var picture = new MediaRef(FileScopes.Workspace, "huge.png", MediaKind.Image, "image/png", huge.Length, 4, 3, null);
            var sent = await adapter.SendAsync(new OutboundMessage("group-123456", "看这个", null,
                [new TextBlock("看这个"), new ImageBlock(picture)]), default);
            Assert.True(sent.Ok, sent.Error?.Message);

            var segments = await bridge.WaitForSendAsync(TimeSpan.FromSeconds(20));
            var kinds = segments.EnumerateArray().Select(segment => segment.GetProperty("type").GetString()).ToArray();
            // No image segment at all: the message went out, the picture did not, and the reader can see where it was.
            Assert.Equal(new[] { "text", "text" }, kinds);
            Assert.Equal("[图片：workspace/huge.png]",
                segments[1].GetProperty("data").GetProperty("text").GetString());
            Assert.Contains(sink.Log.Warnings, warning => warning.Contains("larger than this route sends", StringComparison.Ordinal));
            // And it is said out loud, on the way back: arriving as a sentence and saying nothing is the one outcome
            // worse than either (IOutboundAdapter.SendAsync). speak puts this in the model's tool result.
            Assert.NotNull(sent.Value!.Detail);
            Assert.Contains("workspace/huge.png", sent.Value.Detail!, StringComparison.Ordinal);
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task When_the_host_serves_a_url_the_picture_travels_as_that_url_and_not_as_base64()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        Directory.CreateDirectory(Path.Combine(_root, FileScopes.Workspace));
        await File.WriteAllBytesAsync(Path.Combine(_root, FileScopes.Workspace, "shot.png"), Png());
        var files = new LocalFileSystemAccess(_root, [FileScopes.Workspace], binaryScopes: [FileScopes.Workspace]);
        // No file port and no bytes at all: with a URL the adapter never reads the picture, which is the whole point —
        // the bridge fetches it, on whichever machine it lives.
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null, urls: new FixedUrls());
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var picture = new MediaRef(FileScopes.Workspace, "shot.png", MediaKind.Image, "image/png",
                Png().Length, 4, 3, null);
            var sent = await adapter.SendAsync(new OutboundMessage("group-123456", "看这个", null,
                [new TextBlock("看这个"), new ImageBlock(picture)]), default);
            Assert.True(sent.Ok, sent.Error?.Message);

            var segments = await bridge.WaitForSendAsync(TimeSpan.FromSeconds(10));
            var file = segments[1].GetProperty("data").GetProperty("file").GetString()!;
            Assert.StartsWith("http://host.local/m?", file, StringComparison.Ordinal);
            Assert.Contains("scope=workspace", file, StringComparison.Ordinal);
            Assert.DoesNotContain("base64:", file, StringComparison.Ordinal);
            // Nothing fell back, so nothing is reported: a Detail on every send would be noise the model learns to skip.
            Assert.Null(sent.Value!.Detail);
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task An_expired_url_is_replaced_by_asking_the_bridge_for_a_fresh_one()
    {
        // What a real NNapCat does: the url on the message expires, and get_image mints another one that works. The
        // adapter must not stop at the first 400 — that would be "pictures only when they are new".
        await using var bridge = await FakeBridge.StartAsync(Png(), resolveImageAs: "base64://" + Convert.ToBase64String(Png()));
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            // A url that answers 404, plus a file name get_image can resolve to the bytes.
            await bridge.PushGroupMessageAsync(GroupText(123456, 20000,
                imageUrl: bridge.MediaUri("missing.png").ToString(), imageFile: "abc.png"));

            var envelope = await sink.WaitForEnvelopeAsync(TimeSpan.FromSeconds(15));
            var attachment = Assert.Single(envelope.Attachments!);
            Assert.Equal(Png(), attachment.Bytes);
            Assert.Contains(sink.Log.Warnings, warning => warning.Contains("could not be fetched", StringComparison.Ordinal));
        }
        finally { await adapter.StopAsync(default); }
    }

    [Theory]
    [InlineData("base64", true)]
    [InlineData("auto", false)]
    public async Task The_delivery_setting_decides_whether_a_url_is_used_at_all(string delivery, bool expectBase64)
    {
        // Base64 is the escape hatch for a bridge that cannot reach this machine: with `base64` the adapter does not even
        // ask for a url — that is what makes it a real fallback rather than a preference.
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        Directory.CreateDirectory(Path.Combine(_root, FileScopes.Workspace));
        await File.WriteAllBytesAsync(Path.Combine(_root, FileScopes.Workspace, "shot.png"), Png());
        var files = new LocalFileSystemAccess(_root, [FileScopes.Workspace], binaryScopes: [FileScopes.Workspace]);
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files, urls: new FixedUrls(), imageDelivery: delivery);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var picture = new MediaRef(FileScopes.Workspace, "shot.png", MediaKind.Image, "image/png",
                Png().Length, 4, 3, null);
            Assert.True((await adapter.SendAsync(new OutboundMessage("group-123456", "看", null,
                [new TextBlock("看"), new ImageBlock(picture)]), default)).Ok);

            var file = (await bridge.WaitForSendAsync(TimeSpan.FromSeconds(10)))[1].GetProperty("data").GetProperty("file").GetString()!;
            if (expectBase64) Assert.StartsWith("base64://", file, StringComparison.Ordinal);
            else Assert.StartsWith("http://host.local/m?", file, StringComparison.Ordinal);
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task Url_only_never_falls_back_to_base64_so_a_deployment_can_be_checked()
    {
        // `imageDelivery: "url"` is for answering "is my bridge actually fetching from this host?". A silent fallback
        // would answer it with "probably".
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        Directory.CreateDirectory(Path.Combine(_root, FileScopes.Workspace));
        await File.WriteAllBytesAsync(Path.Combine(_root, FileScopes.Workspace, "shot.png"), Png());
        var files = new LocalFileSystemAccess(_root, [FileScopes.Workspace], binaryScopes: [FileScopes.Workspace]);
        // No url source at all: exactly the case where "url only" must not cheat.
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files, urls: null, imageDelivery: "url");
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var picture = new MediaRef(FileScopes.Workspace, "shot.png", MediaKind.Image, "image/png",
                Png().Length, 4, 3, null);
            Assert.True((await adapter.SendAsync(new OutboundMessage("group-123456", "看", null,
                [new TextBlock("看"), new ImageBlock(picture)]), default)).Ok);

            var segments = await bridge.WaitForSendAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("text", segments[1].GetProperty("type").GetString());
            Assert.Equal("[图片：workspace/shot.png]",
                segments[1].GetProperty("data").GetProperty("text").GetString());
        }
        finally { await adapter.StopAsync(default); }
    }

    // ---- files: a delivery of its own, not a second kind of message ---------------------------

    [Fact]
    public async Task A_file_leaves_as_an_upload_of_its_own_with_a_url_the_bridge_fetches()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, await WorkspaceFileAsync("report.bin", "hello"u8.ToArray()),
            urls: new FixedUrls());
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var file = FileBlockOf("report.bin", 5);

            var sent = await adapter.SendAsync(new OutboundMessage("group-123456", MessageBlock.Flatten([file]), null,
                [file]), default);

            Assert.True(sent.Ok, sent.Error?.Message);
            // One action, and it is not `send_group_msg`: a group file is not a message part, and pretending it is would
            // mean inventing a segment OneBot does not have.
            Assert.Empty(bridge.CallsOf("send_group_msg"));
            var upload = Assert.Single(bridge.CallsOf("upload_group_file"));
            var parameters = upload.GetProperty("params");
            Assert.Equal("123456", parameters.GetProperty("group_id").GetRawText());
            Assert.Equal("report.bin", parameters.GetProperty("name").GetString());
            Assert.StartsWith("http://host.local/m?scope=workspace", parameters.GetProperty("file").GetString(),
                StringComparison.Ordinal);
            // Nothing was lost, so there is nothing to report: a file on its own is not "a message plus a file".
            Assert.Null(sent.Value!.Detail);
            Assert.Contains(sink.Log.Infos, line => line.Message == "outbound file" &&
                line.Detail!["transport"] == "url" && line.Detail["file"] == "workspace/report.bin");
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task Words_and_a_file_go_as_two_calls_and_the_caller_is_told_which_came_second()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, await WorkspaceFileAsync("report.bin", "hello"u8.ToArray()),
            urls: new FixedUrls());
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var layout = new MessageBlock[] { new TextBlock("看这个"), FileBlockOf("report.bin", 5) };

            var sent = await adapter.SendAsync(new OutboundMessage("group-123456", MessageBlock.Flatten(layout), null,
                layout), default);

            Assert.True(sent.Ok, sent.Error?.Message);
            // The message first, and it carries only what OneBot can put in a message.
            var segments = await bridge.WaitForSendAsync(TimeSpan.FromSeconds(10));
            var kinds = segments.EnumerateArray().Select(s => s.GetProperty("type").GetString()).ToArray();
            Assert.Single(kinds);
            Assert.Equal("text", kinds[0]);
            Assert.Equal("看这个", segments[0].GetProperty("data").GetProperty("text").GetString());
            Assert.Single(bridge.CallsOf("upload_group_file"));
            // And the order the caller wrote in cannot be honoured here, which is said out loud — a model that believed it
            // said "look at this [file] what do you think" as one bubble would be wrong about what arrived.
            Assert.Contains("文件在消息之后", sent.Value!.Detail!, StringComparison.Ordinal);
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_file_too_big_for_base64_with_no_door_and_no_path_is_refused_by_name()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        // No url source and no pathMap: the two routes that scale are both missing.
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, await WorkspaceFileAsync("big.bin", "PK"u8.ToArray()),
            urls: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var file = FileBlockOf("big.bin", QqAdapter.MaxOutboundFileBase64Bytes + 1);

            var sent = await adapter.SendAsync(new OutboundMessage("group-123456", MessageBlock.Flatten([file]), null,
                [file]), default);

            // Nothing arrived, so this is a failure and not a note — and the failure names the two things to configure
            // rather than the fact that something went wrong.
            Assert.False(sent.Ok);
            Assert.Contains("没有可用的传输路", sent.Error!.Message, StringComparison.Ordinal);
            Assert.Contains("媒体门", sent.Error.Message, StringComparison.Ordinal);
            Assert.Contains("pathMap", sent.Error.Message, StringComparison.Ordinal);
            // And it did not try anyway: the route is decided before the call, because a failed upload may half arrive.
            Assert.Empty(bridge.CallsOf("upload_group_file"));
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task Without_a_door_a_small_file_still_goes_as_base64()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, await WorkspaceFileAsync("report.bin", "hello"u8.ToArray()),
            urls: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var file = FileBlockOf("report.bin", 5);

            var sent = await adapter.SendAsync(new OutboundMessage("user-1664093638", MessageBlock.Flatten([file]),
                null, [file]), default);

            Assert.True(sent.Ok, sent.Error?.Message);
            var upload = Assert.Single(bridge.CallsOf("upload_private_file"));
            var argument = upload.GetProperty("params").GetProperty("file").GetString()!;
            Assert.StartsWith("base64://", argument, StringComparison.Ordinal);
            Assert.Equal("hello"u8.ToArray(), Convert.FromBase64String(argument["base64://".Length..]));
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task Two_step_lets_a_bridge_that_takes_paths_only_fetch_the_file_itself()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        // The go-cqhttp shape: `file` is a local path, so the bridge has to be told to download our url first.
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, await WorkspaceFileAsync("report.bin", "hello"u8.ToArray()),
            urls: new FixedUrls(), fileDelivery: "two-step");
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var file = FileBlockOf("report.bin", 5);

            var sent = await adapter.SendAsync(new OutboundMessage("group-123456", MessageBlock.Flatten([file]), null,
                [file]), default);

            Assert.True(sent.Ok, sent.Error?.Message);
            // It fetched our url first, and then uploaded the path it was answered with — our url never reached `file`.
            var download = Assert.Single(bridge.CallsOf("download_file"));
            Assert.StartsWith("http://host.local/m?", download.GetProperty("params").GetProperty("url").GetString(),
                StringComparison.Ordinal);
            var upload = Assert.Single(bridge.CallsOf("upload_group_file"));
            Assert.Equal("/bridge/downloads/fetched.bin",
                upload.GetProperty("params").GetProperty("file").GetString());
            Assert.Contains(sink.Log.Infos, line => line.Message == "outbound file" &&
                line.Detail!["transport"] == "two-step");
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_path_can_be_handed_over_when_the_two_machines_share_a_filesystem()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        // pathMap read the other way: the bridge sees this workspace under /srv/aln/workspace, so no bytes move at all.
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, await WorkspaceFileAsync("report.bin", "hello"u8.ToArray()),
            urls: null, fileDelivery: "path", pathMap: "/srv/aln/workspace=workspace");
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var file = FileBlockOf("report.bin", 5);

            var sent = await adapter.SendAsync(new OutboundMessage("group-123456", MessageBlock.Flatten([file]), null,
                [file]), default);

            Assert.True(sent.Ok, sent.Error?.Message);
            var upload = Assert.Single(bridge.CallsOf("upload_group_file"));
            Assert.Equal("/srv/aln/workspace/report.bin", upload.GetProperty("params").GetProperty("file").GetString());
            Assert.Empty(bridge.CallsOf("download_file"));
        }
        finally { await adapter.StopAsync(default); }
    }

    private async Task<IBinaryFileAccess> WorkspaceFileAsync(string name, byte[] content)
    {
        Directory.CreateDirectory(Path.Combine(_root, FileScopes.Workspace));
        await File.WriteAllBytesAsync(Path.Combine(_root, FileScopes.Workspace, name), content);
        return new LocalFileSystemAccess(_root, [FileScopes.Workspace], binaryScopes: [FileScopes.Workspace]);
    }

    /// <summary>A file block pointing into the workspace, described the way the store describes one.</summary>
    private static FileBlock FileBlockOf(string name, long bytes) =>
        new(new MediaRef(FileScopes.Workspace, name, MediaKind.File, "application/octet-stream", bytes));

    // ---- the harness -------------------------------------------------------------------------------------------

    private QqAdapter NewAdapter(Uri endpoint, RecordingSink sink, IBinaryFileAccess? files,
        IMediaUrlSource? urls = null, string? imageDelivery = null, string? fileDelivery = null, string? pathMap = null)
    {
        var settings = new Dictionary<string, string> { ["endpoint"] = endpoint.ToString() };
        if (imageDelivery is not null) settings["imageDelivery"] = imageDelivery;
        if (fileDelivery is not null) settings["fileDelivery"] = fileDelivery;
        if (pathMap is not null) settings["pathMap"] = pathMap;
        return new(new AdapterSpec(AdapterAddress, new("qq", "wire", AdapterDirection.Both), "adapter.qq", settings),
            sink, sink.Log, files: files, urls: urls);
    }

    /// <summary>
    /// A group's file area: newest first, handle marked, and a partial listing that says it is partial.
    ///
    /// The handle is the only thing that crosses this boundary, and the two calls behind it carry what OneBot wants —
    /// the group for the listing, id plus busid for the bytes.
    /// </summary>
    [Fact]
    public async Task A_groups_file_area_is_listed_and_one_handle_fetches_the_bytes()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var catalog = (IFileCatalogAdapter)adapter;

            var listed = await catalog.ListAsync("group-123456", 50, default);
            Assert.True(listed.Ok, listed.Error?.Message);
            var files = listed.Value!.Files;
            Assert.Equal(new[] { "新的.zip", "旧的.zip" }, files.Select(file => file.Name));
            // A handle is opaque and marked, so nothing outside the adapter can read it as a path (docs/28 §6.7).
            Assert.All(files, file => Assert.StartsWith(FileHandles.Prefix, file.Handle, StringComparison.Ordinal));
            Assert.Equal(2048, files[0].Bytes);
            Assert.Equal("小林", files[0].From);
            // The root has a folder in it and this build does not open folders: saying so is the honest half-listing.
            Assert.Contains("归档", listed.Value.Note ?? "", StringComparison.Ordinal);

            var fetched = await catalog.FetchAsync("group-123456", files[0].Handle, 1 << 20, default);
            Assert.True(fetched.Ok, fetched.Error?.Message);
            Assert.Equal("新的.zip", fetched.Value!.Name);
            await using var content = fetched.Value.Content;
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory);
            Assert.Equal("zip-bytes", Encoding.UTF8.GetString(memory.ToArray()));

            var asked = Assert.Single(bridge.CallsOf("get_file"));
            Assert.Equal("file-1", asked.GetProperty("params").GetProperty("file_id").GetString());
            Assert.Equal(0, asked.GetProperty("params").GetProperty("busid").GetInt32());
        }
        finally { await adapter.StopAsync(default); }
    }

    /// <summary>
    /// The link before the bytes: on this deployment the bridge answers <c>get_file</c> with a path inside its own
    /// container, so the link is the only route this host can actually take (docs/28 §6.8).
    /// </summary>
    [Fact]
    public async Task A_group_file_comes_through_the_direct_link_without_asking_get_file()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            bridge.DirectLink = bridge.MediaUri("direct.bin").ToString();
            var catalog = (IFileCatalogAdapter)adapter;

            var fetched = await catalog.FetchAsync("group-123456", "f:0|file-1", 1 << 20, default);
            Assert.True(fetched.Ok, fetched.Error?.Message);
            var content = fetched.Value!.Content;
            await using (content)
            {
                using var memory = new MemoryStream();
                await content.CopyToAsync(memory);
                Assert.Equal(Png(), memory.ToArray());
            }

            var asked = Assert.Single(bridge.CallsOf("get_group_file_url"));
            Assert.Equal("123456", asked.GetProperty("params").GetProperty("group_id").GetString());
            Assert.Equal("file-1", asked.GetProperty("params").GetProperty("file_id").GetString());
            // One round trip was enough, so nothing had to move the file through the bridge.
            Assert.Empty(bridge.CallsOf("get_file"));
            // A link carries no name: the receipt is told that instead of being handed a file id dressed as one.
            Assert.Equal(string.Empty, fetched.Value.Name);
        }
        finally { await adapter.StopAsync(default); }
    }

    /// <summary>
    /// A private attachment's handle is spelled "&lt;busid&gt;|&lt;id&gt;" like a group file's, with a placeholder busid.
    /// Leaving that on would hand the bridge "0|pm-1" — an id nothing can resolve, and one that fails as "not found"
    /// rather than as "wrong shape" (docs/28 §6.8).
    /// </summary>
    [Fact]
    public async Task A_private_handle_is_split_before_the_id_reaches_the_bridge()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            bridge.DirectLink = bridge.MediaUri("direct.bin").ToString();
            var catalog = (IFileCatalogAdapter)adapter;

            var fetched = await catalog.FetchAsync("user-1664093638", "f:0|pm-1", 1 << 20, default);
            Assert.True(fetched.Ok, fetched.Error?.Message);
            await fetched.Value!.Content.DisposeAsync();

            var asked = Assert.Single(bridge.CallsOf("get_private_file_url"));
            Assert.Equal("pm-1", asked.GetProperty("params").GetProperty("file_id").GetString());
            Assert.Empty(bridge.CallsOf("get_group_file_url"));
        }
        finally { await adapter.StopAsync(default); }
    }

    /// <summary>
    /// A "successful" answer that is not a link: one bridge produces exactly this when asked for a url it cannot make, and
    /// following it would be a request to a host literally named "undefined" (docs/28 §6.8).
    /// </summary>
    [Fact]
    public async Task A_link_made_of_undefined_is_not_followed_and_get_file_still_answers()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            bridge.DirectLink = "http://undefined:undefinedundefined&isthumb=0";
            var catalog = (IFileCatalogAdapter)adapter;

            var fetched = await catalog.FetchAsync("group-123456", "f:0|file-1", 1 << 20, default);
            Assert.True(fetched.Ok, fetched.Error?.Message);
            var content = fetched.Value!.Content;
            await using (content)
            {
                using var memory = new MemoryStream();
                await content.CopyToAsync(memory);
                Assert.Equal("zip-bytes", Encoding.UTF8.GetString(memory.ToArray()));
            }
            Assert.Single(bridge.CallsOf("get_file"));
        }
        finally { await adapter.StopAsync(default); }
    }

    /// <summary>
    /// The answer that made this necessary: a path in the bridge's container comes first in the object, and the same bytes
    /// as a bare base64 string come second. Reading only the first non-empty field finds the one answer this host cannot
    /// use (docs/28 §6.8).
    /// </summary>
    [Fact]
    public async Task A_bare_base64_field_is_read_even_though_the_bridges_path_comes_first()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var catalog = (IFileCatalogAdapter)adapter;

            var fetched = await catalog.FetchAsync("group-123456", "f:0|bare-b64", 1 << 20, default);
            Assert.True(fetched.Ok, fetched.Error?.Message);
            var content = fetched.Value!.Content;
            await using (content)
            {
                using var memory = new MemoryStream();
                await content.CopyToAsync(memory);
                Assert.Equal("zip-bytes", Encoding.UTF8.GetString(memory.ToArray()));
            }
        }
        finally { await adapter.StopAsync(default); }
    }

    /// <summary>
    /// A bridge's own file url is a hash it resolves internally. Reporting it as "a path on the bridge's disk" sends
    /// somebody looking for a mount that was never the problem (docs/16 §12.6).
    /// </summary>
    [Fact]
    public async Task A_file_url_is_not_reported_as_a_path_on_the_bridges_disk()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var catalog = (IFileCatalogAdapter)adapter;

            var fetched = await catalog.FetchAsync("group-123456", "f:0|file-url", 1 << 20, default);
            Assert.False(fetched.Ok);
            var message = fetched.Error!.Message;
            Assert.Contains("文件标识", message, StringComparison.Ordinal);
            Assert.DoesNotContain("磁盘上：file://", message, StringComparison.Ordinal);
        }
        finally { await adapter.StopAsync(default); }
    }

    /// <summary>
    /// A base64 payload is refused by its length before it is decoded: a ceiling that only applies after the string has
    /// already become its bytes is not a ceiling, it is a way to run out of memory (docs/28 §6.8).
    /// </summary>
    [Fact]
    public async Task A_base64_too_large_for_the_ceiling_is_refused_before_it_is_decoded()
    {
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var catalog = (IFileCatalogAdapter)adapter;

            // Nine bytes of payload under a ceiling of four: the answer is over before Convert would ever run.
            var fetched = await catalog.FetchAsync("group-123456", "f:0|bare-b64", 4, default);
            Assert.False(fetched.Ok);
            Assert.Contains("超过", fetched.Error!.Message, StringComparison.Ordinal);
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_group_file_upload_reaches_the_channel_as_an_envelope_with_no_message_id()
    {
        // The whole point of handling the notice at all: on this platform a file in the group's file area never appears in
        // the chat, so the only way a Session hears about it is this event. It carries no message_id (there is none) and
        // no bytes (they are still on the platform), and the text has to say what arrived and how to fetch it.
        await using var bridge = await FakeBridge.StartAsync(Png());
        var sink = new RecordingSink();
        var adapter = NewAdapter(bridge.WebSocketUri!, sink, files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            await bridge.PushGroupMessageAsync("""
                {"post_type":"notice","notice_type":"group_upload","self_id":10000,"group_id":616565017,
                 "user_id":1664093638,"time":1730000000,
                 "file":{"id":"offline-1","name":"周报.zip","size":2411724,"busid":0}}
                """);

            var envelope = await sink.WaitForEnvelopeAsync(TimeSpan.FromSeconds(10));
            Assert.Null(envelope.PlatformMessageId);
            Assert.Equal("group-616565017", envelope.PlatformConversationId);
            Assert.Equal("1664093638", envelope.Sender);
            Assert.Contains("周报.zip", envelope.Text, StringComparison.Ordinal);
            Assert.Contains("f:0|offline-1", envelope.Text, StringComparison.Ordinal);
            // No download happened: a file on the platform is a pointer until somebody says where to put it.
            Assert.Null(envelope.Attachments);
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_private_chat_has_no_file_area_and_says_which_place_does()
    {
        // "Nothing is there" and "this is the wrong kind of conversation" are different sentences, and the second one is
        // the true one here: a private chat has no file area at all, and what somebody sent already arrived with the
        // message. An empty array would have said the first.
        await using var bridge = await FakeBridge.StartAsync(Png());
        var adapter = NewAdapter(bridge.WebSocketUri!, new RecordingSink(), files: null);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");

            var listed = await ((IFileCatalogAdapter)adapter).ListAsync("user-20000", 50, default);

            Assert.False(listed.Ok);
            Assert.Contains("私聊", listed.Error!.Message, StringComparison.Ordinal);
            // And nothing was asked of the bridge: a refusal that still sends the call is a refusal the platform pays for.
            Assert.Empty(bridge.CallsOf("get_group_root_files"));
        }
        finally { await adapter.StopAsync(default); }
    }

    [Fact]
    public async Task A_path_on_the_bridges_own_disk_needs_a_pathMap_and_then_streams_from_here()
    {
        // The one shape that cannot be fetched: NapCat answers get_file with a path on its own disk (docs/16 §12.5). That
        // is reachable only when the two machines share a volume and the operator said where — and then the bytes are read
        // through the host's own port, inside a scope, never from an arbitrary path a bridge printed.
        await using var bridge = await FakeBridge.StartAsync(Png());
        var files = new LocalFileSystemAccess(_root, ["workspace"], binaryScopes: ["workspace"], readOnlyScopes: []);
        await files.SaveAsync("workspace", "napcat/files/x.zip",
            new MemoryStream(Encoding.UTF8.GetBytes("mapped-bytes")), default);

        var plain = NewAdapter(bridge.WebSocketUri!, new RecordingSink(), files);
        Assert.True((await plain.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the adapter never connected");
            var refused = await ((IFileCatalogAdapter)plain).FetchAsync("group-123456", "f:0|on-disk", 1 << 20, default);
            Assert.False(refused.Ok);
            // The reason names both ways out: sharing a volume, or answering with bytes.
            Assert.Contains("pathMap", refused.Error!.Message, StringComparison.Ordinal);
        }
        finally { await plain.StopAsync(default); }

        var mapped = NewAdapter(bridge.WebSocketUri!, new RecordingSink(), files,
            pathMap: "/srv/napcat=workspace/napcat");
        Assert.True((await mapped.StartAsync(default)).Ok);
        try
        {
            Assert.True(await WaitAsync(() => bridge.IsConnected), "the mapped adapter never connected");
            var fetched = await ((IFileCatalogAdapter)mapped).FetchAsync("group-123456", "f:0|on-disk", 1 << 20, default);
            Assert.True(fetched.Ok, fetched.Error?.Message);
            await using var content = fetched.Value!.Content;
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory);
            Assert.Equal("mapped-bytes", Encoding.UTF8.GetString(memory.ToArray()));
        }
        finally { await mapped.StopAsync(default); }
    }

    /// <summary>A host that serves every picture it is asked about: what the media endpoint looks like from the adapter.</summary>
    private sealed class FixedUrls : IMediaUrlSource    {
        public Uri? UrlFor(string scope, string path) =>
            new($"http://host.local/m?scope={scope}&path={Uri.EscapeDataString(path)}&t=token");
    }

    /// <summary>The group message a bridge pushes: one text segment, and a picture with (or without) a url.</summary>
    private static string GroupText(long group, long user, string? imageUrl, string imageFile = "x.png")
    {
        // Built as data rather than as a template: the shape is a wire format, and a string with braces in it is a place
        // where "did I quote that right" is the only question anybody can ask.
        var text = new { type = "text", data = new { text = "看这个" } };
        object image = imageUrl is null
            ? new { type = "image", data = new { file = imageFile } }
            : new { type = "image", data = new { file = imageFile, url = imageUrl } };
        return JsonSerializer.Serialize(new
        {
            post_type = "message",
            message_type = "group",
            sub_type = "normal",
            self_id = 10000,
            group_id = group,
            user_id = user,
            message_id = 1,
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            sender = new { user_id = user, nickname = "测试人员", card = "测试人员" },
            message = new object[] { text, image }
        });
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private sealed class RecordingSink : IEventSink
    {
        private readonly TaskCompletionSource<AdapterEnvelope> _envelope =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CollectingLogger Log { get; } = new();

        public ValueTask<Result> PublishAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
        {
            if (routedEvent.Name == EventNames.AdapterEnvelope)
                _envelope.TrySetResult(JsonSerializer.Deserialize<AdapterEnvelope>(routedEvent.PayloadJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> SubscribeAsync(string name,
            Func<RoutedEvent, CancellationToken, ValueTask<Result>> handler, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> SubscribeAllAsync(Func<RoutedEvent, CancellationToken, ValueTask<Result>> handler,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public async Task<AdapterEnvelope> WaitForEnvelopeAsync(TimeSpan timeout)
        {
            var finished = await Task.WhenAny(_envelope.Task, Task.Delay(timeout));
            Assert.True(ReferenceEquals(finished, _envelope.Task), "no envelope was published");
            return await _envelope.Task;
        }
    }

    private sealed class CollectingLogger : IModuleLogger
    {
        public List<string> Warnings { get; } = [];
        /// <summary>Info lines with their fields: which route a file took is a field, not a sentence to parse.</summary>
        public List<(string Message, IReadOnlyDictionary<string, string>? Detail)> Infos { get; } = [];
        public void Info(string message, IReadOnlyDictionary<string, string>? detail = null) => Infos.Add((message, detail));
        public void Warn(string message, IReadOnlyDictionary<string, string>? detail = null) => Warnings.Add(message);
        public void Error(string message, IReadOnlyDictionary<string, string>? detail = null) { }
    }

    /// <summary>
    /// A OneBot v11 bridge on a real socket: accepts one forward-WebSocket, pushes whatever the test asks it to push,
    /// answers actions by echo, and serves one picture over HTTP. Not a simulator — a wire.
    /// </summary>
    private sealed class FakeBridge : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly byte[] _picture;
        private WebSocket? _socket;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly TaskCompletionSource<JsonElement> _sent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Every action the adapter sent, in the order it sent them.</summary>
        private readonly ConcurrentQueue<JsonElement> _calls = new();
        public IReadOnlyList<JsonElement> Calls => _calls.ToArray();
        public IReadOnlyList<JsonElement> CallsOf(string action) => _calls
            .Where(call => string.Equals(call.GetProperty("action").GetString(), action, StringComparison.Ordinal))
            .ToArray();

        private FakeBridge(WebApplication app, byte[] picture, string? resolveImageAs)
        {
            _app = app;
            _picture = picture;
            _resolveImageAs = resolveImageAs;
        }

        private readonly string? _resolveImageAs;

        public Uri? WebSocketUri { get; private set; }
        /// <summary>The same listener over http: that is where a picture lives.</summary>
        public Uri BaseUri { get; private set; } = null!;
        public bool IsConnected => _socket is { State: WebSocketState.Open };
        public Uri MediaUri(string name) => new(BaseUri, "/media/" + name);

        /// <summary>
        /// What the two url actions answer, when they are asked. Null means this bridge does not implement them, which is
        /// a different fact from "it answered with nothing usable" — and the adapter falls back in both cases
        /// (docs/28 §6.8).
        /// </summary>
        public string? DirectLink { get; set; }

        public static async Task<FakeBridge> StartAsync(byte[] picture, string? resolveImageAs = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();
            var bridge = new FakeBridge(app, picture, resolveImageAs);

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

            app.Map("/media/{name}", async context =>
            {
                // One name is deliberately missing: an expired url is the normal case on a real bridge, and the only way
                // to test the fallback is to fail the first fetch.
                if (string.Equals(context.Request.RouteValues["name"] as string, "missing.png", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                context.Response.ContentType = "image/png";
                context.Response.ContentLength = bridge._picture.Length;
                await context.Response.Body.WriteAsync(bridge._picture, context.RequestAborted);
            });

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.First();
            // Kestrel speaks http; OneBot's forward WebSocket is the same port with ws in front of it, and the pictures
            // stay on the http side.
            bridge.BaseUri = new Uri(addresses);
            bridge.WebSocketUri = new UriBuilder(bridge.BaseUri) { Scheme = "ws" }.Uri;
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
                // Every call is kept, in order: a file leaves as an action of its own, so "did the message go first" and
                // "which route did the file take" are questions about the sequence, not about one payload.
                _calls.Enqueue(root);
                var echo = root.TryGetProperty("echo", out var echoElement) ? echoElement.GetString() : null;
                switch (root.GetProperty("action").GetString())
                {
                    case "get_login_info":
                        await WriteAsync(socket, new { status = "ok", retcode = 0, echo,
                            data = new { user_id = 10000, nickname = "wire" } });
                        break;
                    case "get_image":
                        await WriteAsync(socket, new
                        {
                            status = "ok", retcode = 0, echo,
                            data = _resolveImageAs is null ? (object?)null : new { file = _resolveImageAs }
                        });
                        break;
                    case "send_group_msg":
                        // The whole payload is kept: the assertion is about the segments, not about the call.
                        _sent.TrySetResult(root.GetProperty("params").GetProperty("message").Clone());
                        await WriteAsync(socket, new { status = "ok", retcode = 0, echo,
                            data = new { message_id = 1 } });
                        break;
                    case "upload_group_file":
                    case "upload_private_file":
                        await WriteAsync(socket, new { status = "ok", retcode = 0, echo,
                            data = new { file_id = "f-1" } });
                        break;
                    case "get_group_file_url":
                    case "get_private_file_url":
                    {
                        var link = DirectLink;
                        if (link is null)
                            await WriteAsync(socket, new { status = "failed", retcode = 1404,
                                message = "this bridge has no url for it", echo });
                        else
                            await WriteAsync(socket, new { status = "ok", retcode = 0, echo, data = new { url = link } });
                        break;
                    }
                    case "download_file":
                        // What a bridge answers when it has fetched our url onto its own disk: a path it can open.
                        await WriteAsync(socket, new { status = "ok", retcode = 0, echo,
                            data = new { file = "/bridge/downloads/fetched.bin" } });
                        break;
                    case "get_group_root_files":
                        // Two files and a folder: the folder is the part this build does not open, and the listing has to
                        // say so instead of pretending the root is everything (docs/28 §6.7).
                        await WriteAsync(socket, new
                        {
                            status = "ok", retcode = 0, echo,
                            data = new
                            {
                                files = new object[]
                                {
                                    new { file_id = "file-2", file_name = "旧的.zip", file_size = 10, busid = 0,
                                        upload_time = 1_700_000_000, uploader_name = "小林" },
                                    new { file_id = "file-1", file_name = "新的.zip", file_size = 2048, busid = 0,
                                        upload_time = 1_760_000_000, uploader_name = "小林" }
                                },
                                folders = new object[] { new { folder_id = "f-9", folder_name = "归档", total_files = 3 } }
                            }
                        });
                        break;
                    case "get_file":
                    {
                        var asked = root.GetProperty("params").GetProperty("file_id").GetString();
                        switch (asked)
                        {
                            // The three shapes a real bridge answers with, one per id: none, bytes, and a path on its disk.
                            case "missing":
                                await WriteAsync(socket, new { status = "failed", retcode = -1, message = "文件不存在", echo });
                                break;
                            case "on-disk":
                                await WriteAsync(socket, new { status = "ok", retcode = 0, echo,
                                    data = new { file = "/srv/napcat/files/x.zip", file_name = "x.zip" } });
                                break;
                            // NapCat with enableLocalFile2Url on: a path inside its own container *and* the same bytes as
                            // a bare base64 string, no "base64://" in front of it. The path is the field that reads first,
                            // and it is the one this host cannot use.
                            case "bare-b64":
                                await WriteAsync(socket, new { status = "ok", retcode = 0, echo,
                                    data = new { file = "/app/.config/QQ/NapCat/temp/x.zip", file_name = "x.zip",
                                        base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("zip-bytes")) } });
                                break;
                            // Its own file url: a hash it resolves internally, not a path on any disk.
                            case "file-url":
                                await WriteAsync(socket, new { status = "ok", retcode = 0, echo,
                                    data = new { file = "file://1234567890", file_name = "x.zip" } });
                                break;
                            default:
                                await WriteAsync(socket, new { status = "ok", retcode = 0, echo,
                                    data = new
                                    {
                                        file = "base64://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("zip-bytes")),
                                        file_name = "新的.zip"
                                    }
                                });
                                break;
                        }
                        break;
                    }
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

        public Task PushGroupMessageAsync(string json) => PushAsync(json);

        private async Task PushAsync(string json)
        {
            if (_socket is not { State: WebSocketState.Open } socket) throw new InvalidOperationException("no socket");
            await _write.WaitAsync();
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { _write.Release(); }
        }

        public async Task<JsonElement> WaitForSendAsync(TimeSpan timeout)
        {
            var finished = await Task.WhenAny(_sent.Task, Task.Delay(timeout));
            Assert.True(ReferenceEquals(finished, _sent.Task), "the adapter never sent anything");
            return await _sent.Task;
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
