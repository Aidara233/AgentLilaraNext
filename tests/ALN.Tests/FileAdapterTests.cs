using System.Text.Json;
using ALN.Adapter.File;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Tests;

public sealed class FileAdapterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aln-tests-" + Guid.NewGuid().ToString("N"));

    public FileAdapterTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Conversation_folders_route_input_and_output_independently()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "adapters", "instance-1"));
        var roomOne = Inbox(mailbox, "chat-1");
        var roomTwo = Inbox(mailbox, "chat-2");
        await File.WriteAllTextAsync(Path.Combine(roomOne, "a.ready.json"), "{\"text\":\"hello\",\"messageId\":\"m-1\"}");
        await File.WriteAllTextAsync(Path.Combine(roomTwo, "b.ready.json"), "{\"text\":\"world\",\"messageId\":\"m-2\"}");
        var sink = new RecordingSink(Result.Success());
        var adapter = NewAdapter(mailbox, sink);
        Assert.True((await adapter.StartAsync(default)).Ok);
        var polled = await adapter.PollOnceAsync();
        Assert.True(polled.Ok);
        Assert.Equal(2, polled.Value);
        var routed = sink.Events.Select(e => JsonSerializer.Deserialize<AdapterEnvelope>(e.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        Assert.Equal(new[] { "chat-1", "chat-2" }, routed.Select(e => e.PlatformConversationId).ToArray());
        Assert.True(File.Exists(Path.Combine(mailbox.Root, "processed", "chat-1", "a.json")));
        Assert.True(File.Exists(Path.Combine(mailbox.Root, "processed", "chat-2", "b.json")));

        var sent = await adapter.SendAsync(new("chat-2", "reply"), default);
        Assert.True(sent.Ok);
        Assert.True(File.Exists(Path.Combine(mailbox.Root, "outbox", "chat-2", sent.Value!.AttemptId + ".json")));
        Assert.False(Directory.Exists(Path.Combine(mailbox.Root, "outbox", "chat-1")));
    }

    [Fact]
    public async Task Invalid_json_is_rejected_and_publish_failure_remains_pending()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "instance"));
        var inbox = Inbox(mailbox, "room");
        await File.WriteAllTextAsync(Path.Combine(inbox, "bad.ready.json"), "{");
        await File.WriteAllTextAsync(Path.Combine(inbox, "retry.ready.json"), "{\"text\":\"t\"}");
        var sink = new RecordingSink(Result.Failure(new(ErrorCode.Unavailable, "full", Retryable: true)));
        var adapter = NewAdapter(mailbox, sink);
        await adapter.StartAsync(default);
        var result = await adapter.PollOnceAsync();
        Assert.True(result.Ok);
        Assert.Equal(0, result.Value);
        Assert.True(File.Exists(Path.Combine(mailbox.Root, "rejected", "room", "bad.json")));
        Assert.True(File.Exists(Path.Combine(inbox, "retry.ready.json")));
    }

    [Fact]
    public async Task Non_object_and_oversized_inputs_are_rejected_without_crashing_poller()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "shape"));
        var inbox = Inbox(mailbox, "room");
        await File.WriteAllTextAsync(Path.Combine(inbox, "array.ready.json"), "[]");
        await File.WriteAllTextAsync(Path.Combine(inbox, "null.ready.json"), "null");
        await File.WriteAllTextAsync(Path.Combine(inbox, "large.ready.json"), new string('x', 70 * 1024));
        var adapter = NewAdapter(mailbox, new RecordingSink(Result.Success()));
        await adapter.StartAsync(default);
        var result = await adapter.PollOnceAsync();
        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(0, result.Value);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(mailbox.Root, "rejected", "room"), "*.json").Length);
    }

    [Fact]
    public async Task Input_and_output_reject_unsafe_conversation_directory_names()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "unsafe"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await mailbox.WriteOutputAsync(new("../outside", "no"), default));
        var inbox = Path.Combine(mailbox.Root, "inbox", "..outside");
        Directory.CreateDirectory(inbox);
        await File.WriteAllTextAsync(Path.Combine(inbox, "x.ready.json"), "{}");
        await Assert.ThrowsAsync<ArgumentException>(async () => await mailbox.ReadPendingAsync(default));
    }

    [Fact]
    public async Task Send_writes_real_attempt_and_stop_disables_io()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "instance"));
        var adapter = NewAdapter(mailbox, new RecordingSink(Result.Success()));
        Assert.False((await adapter.SendAsync(new("c", "before"), default)).Ok);
        await adapter.StartAsync(default);
        var sent = await adapter.SendAsync(new("c", "hello"), default);
        Assert.True(sent.Ok);
        Assert.True(sent.Value!.Sent);
        var path = Path.Combine(mailbox.Root, "outbox", "c", sent.Value.AttemptId + ".json");
        Assert.True(File.Exists(path));
        using (var output = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            Assert.Equal("hello", output.RootElement.GetProperty("Text").GetString());
        await adapter.StopAsync(default);
        Assert.False((await adapter.SendAsync(new("c", "after"), default)).Ok);
        Assert.False((await adapter.PollOnceAsync()).Ok);
    }

    [Fact]
    public async Task Mailbox_rejects_path_tokens_and_limits_pending_batch()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "instance"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await mailbox.CompleteAsync("room", "../outside", true, null, default));
        var inbox = Inbox(mailbox, "room");
        for (var i = 0; i < 70; i++)
            await File.WriteAllTextAsync(Path.Combine(inbox, $"{i:D3}.ready.json"), "{}");
        Assert.Equal(64, (await mailbox.ReadPendingAsync(default)).Count);
    }

    [Fact]
    public void Data_directory_resolves_relative_path_from_current_directory()
    {
        var current = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _directory;
            var data = DataDirectory.Open("relative-data");
            Assert.Equal(Path.Combine(_directory, "relative-data"), data.Root);
            Assert.True(Directory.Exists(data.Root));
        }
        finally { Environment.CurrentDirectory = current; }
    }

    [Fact]
    public async Task Stop_still_stops_when_the_clock_subscription_is_already_gone()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "adapters", "instance-1"));
        var sink = new RecordingSink(Result.Success());
        var clock = new ALN.Clock.ClockSource(sink);
        var adapter = new FileAdapter(
            new AdapterSpec(new Address("adapter", "file", "test"), new("file", "account", AdapterDirection.Both), "adapter.file"),
            mailbox, sink, clock);
        Assert.True((await adapter.StartAsync(default)).Ok);
        // What the owner used to do first: clear the target's subscriptions before telling it to stop.
        Assert.True((await clock.UnregisterTargetAsync(adapter.Address, default)).Ok);

        var stopped = await adapter.StopAsync(default);

        Assert.True(stopped.Ok, stopped.Error?.Detail ?? stopped.Error?.Message);
        Assert.Equal(AdapterState.Stopped, adapter.State);
        Assert.False((await adapter.PollOnceAsync()).Ok);
    }

    private static string Inbox(FileMailbox mailbox, string conversationId)
    {
        var path = Path.Combine(mailbox.Root, "inbox", conversationId);
        Directory.CreateDirectory(path);
        return path;
    }

    private static FileAdapter NewAdapter(IFileMailbox mailbox, IEventSink sink) =>
        new(new AdapterSpec(new Address("adapter", "file", "test"), new("file", "account", AdapterDirection.Both), "adapter.file"), mailbox, sink, new ALN.Clock.ClockSource(sink));

    private sealed class RecordingSink(Result result) : IEventSink
    {
        public List<RoutedEvent> Events { get; } = [];
        public ValueTask<Result> PublishAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
        {
            Events.Add(routedEvent);
            return ValueTask.FromResult(result);
        }
    }
}
