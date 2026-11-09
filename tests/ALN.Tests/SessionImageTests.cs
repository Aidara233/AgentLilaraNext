using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// A picture that reaches the model through a tool: <c>fs_read</c> on an image answers with a sentence and a reference,
/// and the Session is what turns that reference into an image part — or into a receipt, when this model cannot see.
///
/// The three things pinned here are the ones that are easy to get wrong and expensive to discover in production:
/// the bytes that arrive are the bytes that were stored (not a re-encoded or truncated copy), the words the tool said
/// travel with the picture rather than instead of it, and a model that cannot see pictures is told so <b>in the message
/// it reads</b>. That last one is not politeness: a tool result that says "here is a picture" with no picture and no
/// reason is a model that will try again, forever.
/// </summary>
public sealed class SessionImageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-image-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A 33-byte PNG header — the only part of a picture this build ever interprets (docs/28 §5).</summary>
    private static byte[] Png(int width, int height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        0x08, 0x06, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    private LocalFileSystemAccess Access() =>
        new(_root, [FileScopes.Media, FileScopes.Workspace], binaryScopes: [FileScopes.Media, FileScopes.Workspace],
            readOnlyScopes: [FileScopes.Media]);

    /// <summary>A scripted model: one canned step per call. <paramref name="capabilities"/> is what its profile declares.</summary>
    private sealed class ScriptedModel(ModelCapabilities? capabilities, params ModelEvent[][] steps) : IModelManager
    {
        public List<ModelRequest> Requests { get; } = [];

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var step = steps[Math.Min(Requests.Count - 1, steps.Length - 1)];
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream(step)));
        }

        private static async IAsyncEnumerable<ModelEvent> Stream(ModelEvent[] events,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var item in events) yield return item;
        }

        public Result RegisterFactory(string packageId, IModelProviderFactory factory) => Result.Success();
        public Result RemovePackageFactories(string packageId) => Result.Success();
        public IReadOnlyList<ModelProviderTypeInfo> ListTypes() => [];
        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<Result<IModelProvider>> CreateAsync(string category, string instance, string configurationJson,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public Result<IModelProvider> Get(Address provider) => Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "No providers."));
        public IReadOnlyList<ModelProviderInfo> List() => [];
        public Result RegisterProfile(ModelProfile profile) => Result.Success();
        public Result RemoveProfile(string profileId) => Result.Success();

        public IReadOnlyList<ModelProfile> ListProfiles() =>
        [
            new("default", new Address("model.openai-chat#one"), "scripted", Capabilities: capabilities)
        ];
    }

    private sealed class Offers(params SessionToolOffer[] offers) : ISessionToolbox
    {
        public IReadOnlyList<string> Names { get; } = offers.Select(offer => offer.Definition.Name).ToArray();

        public Result<ISessionTool> Find(string name) => offers.FirstOrDefault(offer => offer.Tool.Name == name) is { } found
            ? Result<ISessionTool>.Success(found.Tool)
            : Result<ISessionTool>.Failure(new(ErrorCode.Invalid, $"No tool named {name}."));
    }

    private static ModelToolCall ReadCall(string path) =>
        new("call-1", "fs_read", JsonSerializer.Serialize(new { scope = FileScopes.Media, path }));

    private static ModelToolCall Wait() => new("call-2", "wait", "{}");

    /// <summary>Store one picture and hand back how a tool would name it.</summary>
    private async Task<(MediaRef Ref, SessionToolOffer[] Offers)> StoredPictureAsync()
    {
        var access = Access();
        var store = new FileMediaStore(access);
        var ingested = await store.IngestAsync(MediaKind.Image, new MemoryStream(Png(4, 3)), default);
        Assert.True(ingested.Ok, ingested.Error?.Message);
        var tools = new FileSystemTools(access, [FileScopes.Media, FileScopes.Workspace], store);
        var offered = await tools.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default);
        Assert.True(offered.Ok, offered.Error?.Message);
        return (ingested.Value!, offered.Value!.ToArray());
    }

    private SessionManager Manager(IModelManager model) => new(_root, model, files: Access());

    private static SessionSpec Spec(IReadOnlyList<SessionToolOffer> offers) =>
        new("default", "Answer briefly.", "1",
            Tools: offers.Where(offer => offer.Definition.Name == "fs_read").Select(offer => offer.Definition).ToArray());

    // ---- the conversation's own pictures -------------------------------------------------------------------------

    /// <summary>A conversation message that arrived as a picture, with whatever the platform sent as its text.</summary>
    private static SessionChannelMessage Said(MediaRef picture, string text = "") =>
        new("m-1", "user", "小明", text, DateTimeOffset.UtcNow, [picture]);

    [Fact]
    public async Task A_picture_in_the_conversation_reaches_a_model_that_can_see_it()
    {
        var (stored, _) = await StoredPictureAsync();
        var model = new ScriptedModel(ModelCapabilities.Streaming | ModelCapabilities.Vision,
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = Manager(model);

        var turn = await sessions.TurnAsync("s-1", Spec([]), Guid.NewGuid(),
            new SessionTurnInput([Said(stored, "看这个")], null, null), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var context = model.Requests[^1].Messages.Single(message => message.Role == "user" && message.HasImage);
        // The words the picture came with are still there, and so is the receipt naming the file.
        var text = string.Concat(context.Parts!.OfType<TextPart>().Select(part => part.Text));
        Assert.Contains("看这个", text, StringComparison.Ordinal);
        Assert.Contains(stored.Path, text, StringComparison.Ordinal);
        Assert.StartsWith("<channel_context>", text, StringComparison.Ordinal);
        Assert.EndsWith("</channel_context>", text, StringComparison.Ordinal);
        Assert.Equal(text, context.Content);
        // And the picture rides beside it, after the message it belongs to, unchanged from what was stored.
        var picture = Assert.IsType<ImagePart>(context.Parts![^2]);
        Assert.Equal(Png(4, 3), picture.Bytes.ToArray());
        Assert.Equal("image/png", picture.MediaType);
    }

    [Fact]
    public async Task A_conversation_without_pictures_renders_exactly_as_before()
    {
        // The invariant the whole change rests on: a request with no picture in it must be byte-identical to what this
        // build sent before pictures existed — same text, and no parts at all (a parts array is a different wire shape
        // for every provider, and it buys nothing when it holds one text block).
        var model = new ScriptedModel(ModelCapabilities.Vision,
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = Manager(model);

        var turn = await sessions.TurnAsync("s-1", Spec([]), Guid.NewGuid(),
            new SessionTurnInput([new("m-1", "user", "小明", "在吗", DateTimeOffset.UtcNow)], null, null), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var context = model.Requests[^1].Messages.Single(message => message.Role == "user" && message.Content?.Contains("在吗", StringComparison.Ordinal) == true);
        Assert.Null(context.Parts);
        Assert.DoesNotContain("图片", context.Content!, StringComparison.Ordinal);
        Assert.Contains("<message id=\"m-1\" source=\"user\" sender=\"小明\"", context.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_cannot_see_reads_a_receipt_where_the_picture_would_have_been()
    {
        var (stored, _) = await StoredPictureAsync();
        var model = new ScriptedModel(null,
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = Manager(model);

        var turn = await sessions.TurnAsync("s-1", Spec([]), Guid.NewGuid(),
            new SessionTurnInput([Said(stored)], null, null), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var context = model.Requests[^1].Messages.Single(message => message.Role == "user" && message.Content?.Contains("【图片", StringComparison.Ordinal) == true);
        Assert.False(context.HasImage);
        Assert.Contains("本模型看不到图像", context.Content!, StringComparison.Ordinal);
        Assert.Contains(stored.Path, context.Content!, StringComparison.Ordinal);
        // The pointer is in the text, so "look at it yourself" is a move it can actually make.
        Assert.Contains("fs_read", context.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_policy_that_says_off_keeps_the_picture_out_and_says_why()
    {
        // The operator's knob, not the model's: with autoView=off a picture is recorded and referenced, never shown
        // unasked — and the receipt names the policy, so "why can I not see it" has an answer in the request itself.
        var (stored, _) = await StoredPictureAsync();
        var model = new ScriptedModel(ModelCapabilities.Vision,
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = new SessionManager(_root, model, media: new OffPolicy(), files: Access());

        var turn = await sessions.TurnAsync("s-1", Spec([]), Guid.NewGuid(),
            new SessionTurnInput([Said(stored)], null, null), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var context = model.Requests[^1].Messages.Single(message => message.Role == "user" && message.Content?.Contains("【图片", StringComparison.Ordinal) == true);
        Assert.False(context.HasImage);
        Assert.Contains("off", context.Content!, StringComparison.Ordinal);
    }

    /// <summary>An operator who said "off": everything else is the shipped default.</summary>
    private sealed class OffPolicy : IMediaPolicy
    {
        public MediaViewPolicy AutoView => MediaViewPolicy.Off;
        public long MaxImageBytes => DefaultMediaPolicy.Instance.MaxImageBytes;
        public int MaxAutoViewsPerTurn => DefaultMediaPolicy.Instance.MaxAutoViewsPerTurn;
        public int MaxImagesPerRequest => DefaultMediaPolicy.Instance.MaxImagesPerRequest;
        public long MaxImageBytesPerRequest => DefaultMediaPolicy.Instance.MaxImageBytesPerRequest;
        public bool Allows(string mediaType) => DefaultMediaPolicy.Instance.Allows(mediaType);
        public MediaView Decide(int index, string mediaType, long bytes, int alreadyShown) =>
            MediaView.No("看图策略是 off，收到图片只登记不自动看");
    }

    [Fact]
    public async Task A_picture_the_model_can_see_arrives_as_one_message_carrying_both_the_words_and_the_bytes()
    {
        var (stored, offers) = await StoredPictureAsync();
        var model = new ScriptedModel(ModelCapabilities.Streaming | ModelCapabilities.Vision,
            [new ToolCallDelta(0, "call-1", "fs_read", JsonSerializer.Serialize(new { scope = FileScopes.Media, path = stored.Path })),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))],
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = Manager(model);

        var turn = await sessions.TurnAsync("s-1", Spec(offers), Guid.NewGuid(),
            new SessionTurnInput([], null, null), new Offers(offers), default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        // Every request a scripted model records sees the same list the loop is still building, so the last one is the
        // finished turn — and the answer to the call is found by its id, not by position.
        var answer = model.Requests[^1].Messages.Single(message => message.ToolCallId == "call-1");
        Assert.Equal("call-1", answer.ToolCallId);
        Assert.True(answer.HasImage);
        // Words first, then the picture: the sentence says what it is, so a model reading only text still knows.
        var text = Assert.IsType<TextPart>(answer.Parts![0]);
        Assert.Contains("这是图片", text.Text, StringComparison.Ordinal);
        Assert.Contains(stored.Path, text.Text, StringComparison.Ordinal);
        var picture = Assert.IsType<ImagePart>(answer.Parts![1]);
        Assert.Equal("image/png", picture.MediaType);
        Assert.Equal(4, picture.Width);
        Assert.Equal(3, picture.Height);
        // The bytes are the ones that were stored, unchanged — no re-encode, no truncation, nothing invented.
        Assert.Equal(Png(4, 3), picture.Bytes.ToArray());
        // And the plain-text view of the message is the words, which is what a provider that ignores parts would send.
        Assert.Equal(text.Text, answer.Content);
    }

    [Fact]
    public async Task A_model_that_cannot_see_is_told_so_in_the_message_it_reads()
    {
        var (stored, offers) = await StoredPictureAsync();
        // No declaration at all: the absence is read as "no", which is the safe direction (docs/14 §capabilities).
        var model = new ScriptedModel(null,
            [new ToolCallDelta(0, "call-1", "fs_read", JsonSerializer.Serialize(new { scope = FileScopes.Media, path = stored.Path })),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))],
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = Manager(model);

        var turn = await sessions.TurnAsync("s-1", Spec(offers), Guid.NewGuid(),
            new SessionTurnInput([], null, null), new Offers(offers), default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        var answer = model.Requests[^1].Messages.Single(message => message.ToolCallId == "call-1");
        Assert.False(answer.HasImage);
        // The tool's sentence survives, and the receipt is there: "not shown" and "why" are both in the text, and the
        // path is in it too, so the model has something to do instead of asking again.
        Assert.Contains("这是图片", answer.Content!, StringComparison.Ordinal);
        Assert.Contains("看不到图像", answer.Content!, StringComparison.Ordinal);
        Assert.Contains(stored.Path, answer.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain(answer.Parts ?? [], part => part is ImagePart);
    }

    [Fact]
    public async Task The_timeline_keeps_the_reference_and_never_the_bytes()
    {
        var (stored, offers) = await StoredPictureAsync();
        var model = new ScriptedModel(ModelCapabilities.Vision,
            [new ToolCallDelta(0, "call-1", "fs_read", JsonSerializer.Serialize(new { scope = FileScopes.Media, path = stored.Path })),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))],
            [new ToolCallDelta(0, "call-2", "wait", "{}"), new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = Manager(model);

        Assert.True((await sessions.TurnAsync("s-1", Spec(offers), Guid.NewGuid(),
            new SessionTurnInput([], null, null), new Offers(offers), default)).Ok);

        var lines = await File.ReadAllLinesAsync(Path.Combine(_root, "modules", "s-1", "history.jsonl"));
        var entries = lines.Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<SessionHistoryEntry>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        var result = Assert.Single(entries, entry => entry.ToolCallId == "call-1");
        var attachment = Assert.Single(result.Attachments!);
        Assert.Equal(stored.Path, attachment.Path);
        Assert.Equal(MediaKind.Image, attachment.Kind);

        // The picture itself is on disk once and in the journal never: a history that kept the bytes could not be read
        // cheaply again, and would hold a second copy of every image anybody ever sent (docs/28 §3).
        var base64 = Convert.ToBase64String(Png(4, 3));
        Assert.DoesNotContain(lines, line => line.Contains(base64, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.True(Encoding.UTF8.GetByteCount(line) < 4096));
        Assert.True(File.Exists(Path.Combine(_root, "media", stored.Path.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Reading_a_file_that_is_not_text_and_not_a_picture_says_what_it_is_instead_of_failing()
    {
        // A receipt is still better than "not UTF-8": the model learns the file exists and what it is, which is the
        // difference between "I cannot read this" and "this is missing" (docs/24 §fs_read).
        var access = Access();
        var store = new FileMediaStore(access);
        // An MP3 by its header. This build never decodes it — it only knows what kind of thing it is looking at, which
        // is exactly as much as it needs to say "I cannot read this, and here is what it is".
        var ingested = await store.IngestAsync(MediaKind.Voice, new MemoryStream("ID3\u0004\u0000\u0000\u0000\u0000\u0000\u0000"u8.ToArray()), default);
        Assert.True(ingested.Ok, ingested.Error?.Message);
        Assert.Equal(MediaKind.Voice, ingested.Value!.Kind);
        var tools = new FileSystemTools(access, [FileScopes.Media], store);
        var offered = (await tools.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default)).Value!;

        var read = await offered.Single(offer => offer.Definition.Name == "fs_read").Tool
            .ExecuteAsync(ReadCall(ingested.Value.Path), default);

        Assert.True(read.Ok, read.Error?.Message);
        Assert.Contains("不是文本", read.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("audio/mpeg", read.Value.Content, StringComparison.Ordinal);
        Assert.Null(read.Value.Attachments);
    }

    [Fact]
    public async Task Bytes_this_build_cannot_identify_are_reported_as_not_text_and_nothing_more()
    {
        // The honest limit: an unidentifiable file is not guessed at. It stays a plain "not UTF-8 text" failure rather
        // than becoming an invented description of what it might have been (docs/28 §5).
        var access = Access();
        var store = new FileMediaStore(access);
        var ingested = await store.IngestAsync(MediaKind.File, new MemoryStream([0x00, 0x01, 0x02, 0x03]), default);
        var tools = new FileSystemTools(access, [FileScopes.Media], store);
        var offered = (await tools.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default)).Value!;

        var read = await offered.Single(offer => offer.Definition.Name == "fs_read").Tool
            .ExecuteAsync(ReadCall(ingested.Value!.Path), default);

        Assert.False(read.Ok);
        Assert.Contains("not UTF-8", read.Error!.Message, StringComparison.Ordinal);
    }
}
