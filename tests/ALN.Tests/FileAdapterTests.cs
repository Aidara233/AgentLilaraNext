using System.Text.Json;
using ALN.Adapter.File;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Tests;

/// <summary>
/// The file adapter's contract, which is deliberately small: one folder per conversation, and every file in it is a
/// message whose text is the file. Nothing in the file says who sent it or which id it has — the folder is the person
/// (a private mailbox has no roster) and the id is assigned when the input is claimed.
/// </summary>
public sealed class FileAdapterTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aln-tests-" + Guid.NewGuid().ToString("N"));

    public FileAdapterTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Conversation_folders_route_input_and_output_independently()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "adapters", "instance-1"));
        var roomOne = Inbox(mailbox, "chat-1");
        var roomTwo = Inbox(mailbox, "chat-2");
        // The file is the message: no envelope, no fields to fill in, and its name means nothing.
        await File.WriteAllTextAsync(Path.Combine(roomOne, "a.ready.json"), "hello");
        await File.WriteAllTextAsync(Path.Combine(roomTwo, "b.txt"), "world\n");
        var sink = new RecordingSink(Result.Success());
        var adapter = NewAdapter(mailbox, sink);
        Assert.True((await adapter.StartAsync(default)).Ok);

        var polled = await adapter.PollOnceAsync();

        Assert.True(polled.Ok, polled.Error?.Message);
        Assert.Equal(2, polled.Value);
        var routed = sink.Events.Select(e => JsonSerializer.Deserialize<AdapterEnvelope>(e.PayloadJson, WebJson)!).ToArray();
        Assert.Equal(["chat-1", "chat-2"], routed.Select(e => e.PlatformConversationId).ToArray());
        Assert.Equal(["hello", "world"], routed.Select(e => e.Text).ToArray());
        // One folder is one person: the sender is the conversation, and the id came from the mailbox.
        Assert.All(routed, envelope => Assert.Equal(envelope.PlatformConversationId, envelope.Sender));
        Assert.All(routed, envelope => Assert.Equal(32, envelope.PlatformMessageId!.Length));
        Assert.Single(Directory.GetFiles(Path.Combine(mailbox.Root, "processed", "chat-1"), "*.json"));
        Assert.Single(Directory.GetFiles(Path.Combine(mailbox.Root, "processed", "chat-2"), "*.json"));

        var sent = await adapter.SendAsync(new("chat-2", "reply"), default);
        Assert.True(sent.Ok);
        Assert.True(File.Exists(Path.Combine(mailbox.Root, "outbox", "chat-2", sent.Value!.AttemptId + ".json")));
        Assert.False(Directory.Exists(Path.Combine(mailbox.Root, "outbox", "chat-1")));
    }

    [Fact]
    public async Task A_refused_publication_hands_the_input_back_and_an_empty_file_waits()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "instance"));
        var inbox = Inbox(mailbox, "room");
        await File.WriteAllTextAsync(Path.Combine(inbox, "retry.ready.json"), "t");
        await File.WriteAllTextAsync(Path.Combine(inbox, "empty.txt"), "   \n");
        var sink = new RecordingSink(Result.Failure(new(ErrorCode.Unavailable, "full", Retryable: true)));
        var adapter = NewAdapter(mailbox, sink);
        await adapter.StartAsync(default);

        var result = await adapter.PollOnceAsync();

        Assert.True(result.Ok);
        Assert.Equal(0, result.Value);
        // Claimed, refused by the queue, handed back under the id it was given: publishing is not handling.
        Assert.Single(Directory.GetFiles(inbox, "*.ready.json"));
        // The empty one was never claimed: a file still being saved looks exactly like this, and taking it now would
        // send half a message and delete the rest of what is coming.
        Assert.True(File.Exists(Path.Combine(inbox, "empty.txt")));
        Assert.False(Directory.Exists(Path.Combine(mailbox.Root, "rejected", "room")));
    }

    [Fact]
    public async Task Json_looking_text_is_text_and_an_oversized_file_is_rejected_with_its_reason()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "shape"));
        var inbox = Inbox(mailbox, "room");
        await File.WriteAllTextAsync(Path.Combine(inbox, "array.ready.json"), "[]");
        await File.WriteAllTextAsync(Path.Combine(inbox, "null.ready.json"), "null");
        await File.WriteAllTextAsync(Path.Combine(inbox, "large.txt"), new string('x', 70 * 1024));
        var adapter = NewAdapter(mailbox, new RecordingSink(Result.Success()));
        await adapter.StartAsync(default);

        var result = await adapter.PollOnceAsync();

        Assert.True(result.Ok, result.Error?.Message);
        // No longer parsed as JSON: "[]" and "null" are messages like any other text.
        Assert.Equal(2, result.Value);
        var rejected = Path.Combine(mailbox.Root, "rejected", "room");
        // The reason travels with the file, or somebody puts it back unchanged and it is rejected again.
        var reasonPath = Assert.Single(Directory.GetFiles(rejected, "*.error.txt"));
        Assert.Contains("65536", await File.ReadAllTextAsync(reasonPath), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(rejected, "*.txt"),
            path => !path.EndsWith(".error.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unsafe_conversation_name_is_refused_on_output_and_skipped_on_input()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "unsafe"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await mailbox.WriteOutputAsync(new("../outside", "no"), default));

        var inbox = Path.Combine(mailbox.Root, "inbox", "..outside");
        Directory.CreateDirectory(inbox);
        await File.WriteAllTextAsync(Path.Combine(inbox, "x.ready.json"), "hello");

        // Skipped, not thrown. One stray folder used to make every poll fail for as long as it sat there, which is a
        // mailbox that silently stops rather than a folder that is ignored.
        Assert.Empty(await mailbox.ReadPendingAsync(default));
        Assert.True(File.Exists(Path.Combine(inbox, "x.ready.json")));
    }

    [Fact]
    public async Task The_file_name_is_not_an_identity_so_a_name_with_spaces_and_chinese_is_fine()
    {
        var mailbox = new FileMailbox(Path.Combine(_directory, "names"));
        var inbox = Inbox(mailbox, "room");
        await File.WriteAllTextAsync(Path.Combine(inbox, "新建 文本文档.txt"), "你好");
        var sink = new RecordingSink(Result.Success());
        var adapter = NewAdapter(mailbox, sink);
        await adapter.StartAsync(default);

        var result = await adapter.PollOnceAsync();

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(1, result.Value);
        var envelope = JsonSerializer.Deserialize<AdapterEnvelope>(sink.Events.Single().PayloadJson, WebJson)!;
        Assert.Equal("你好", envelope.Text);
        Assert.NotEqual("新建 文本文档", envelope.PlatformMessageId);
    }

    [Fact]
    public async Task The_adapter_reads_its_inbox_on_its_own_beat()
    {
        // The whole point of the beat: nobody calls poll. Before this, an Adapter instance was started and read nothing,
        // because every beat was attached to kernel modules and an Adapter is not one.
        var mailbox = new FileMailbox(Path.Combine(_directory, "beat"));
        var inbox = Inbox(mailbox, "room");
        var sink = new RecordingSink(Result.Success());
        var adapter = NewAdapter(mailbox, sink);
        adapter.PollInterval = TimeSpan.FromMilliseconds(10);
        Assert.True((await adapter.StartAsync(default)).Ok);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(inbox, "later.txt"), "picked up without anybody polling");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (sink.Events.Count == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
            Assert.Single(sink.Events);
            Assert.Empty(Directory.GetFiles(inbox));
        }
        finally { await adapter.StopAsync(default); }
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
            await File.WriteAllTextAsync(Path.Combine(inbox, $"{i:D3}.txt"), "m");
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
    public async Task A_trigger_while_stopped_is_answered_and_a_trigger_while_running_polls()
    {
        // A schedule outlives a stop: the beat after a start will find the work, so a trigger against a stopped
        // adapter is answered instead of refused.
        var adapter = NewAdapter(new FileMailbox(Path.Combine(_directory, "adapters", "instance-1")),
            new RecordingSink(Result.Success()));
        Assert.True((await adapter.OnTriggerAsync(new TriggerSignal(TriggerReason.Schedule, "Clock", DateTimeOffset.UtcNow), default)).Ok);

        Assert.True((await adapter.StartAsync(default)).Ok);
        Assert.True((await adapter.OnTriggerAsync(new TriggerSignal(TriggerReason.Demand, "test", DateTimeOffset.UtcNow), default)).Ok);
        Assert.True((await adapter.StopAsync(default)).Ok);
        Assert.Equal(AdapterState.Stopped, adapter.State);
    }

    private static string Inbox(FileMailbox mailbox, string conversationId)
    {
        var path = Path.Combine(mailbox.Root, "inbox", conversationId);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A very slow beat by default, so a test that polls by hand is not racing the adapter's own timer; the beat test
    /// turns it down to milliseconds.
    /// </summary>
    private static FileAdapter NewAdapter(IFileMailbox mailbox, IEventSink sink) => new(
        new AdapterSpec(new Address("adapter", "file", "test"), new("file", "account", AdapterDirection.Both), "adapter.file"),
        mailbox, sink)
    {
        PollInterval = TimeSpan.FromMinutes(10)
    };

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
