using System.Text;
using ALN.Contracts;
using ALN.Contracts.Adapters;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// <c>files_get</c> as a model meets it: three shapes chosen by what the argument looks like, and a destination it has to
/// name itself.
///
/// The two rules pinned here are the ones that are cheap to get wrong and expensive to live with. <b>The shape decides,
/// never a failure</b> — a fetch that fails must not quietly become a search, or a model asking for bytes gets a
/// listing. And <b>there is no default destination</b> — a file that lands where nobody said is a file the next turn has
/// to go looking for (docs/28 §6.8).
/// </summary>
public sealed class FilesGetToolsTests : IDisposable
{
    private static readonly Address ChannelAddress = new("channel", "conversation", "c1");
    private static readonly Address AdapterAddress = new("adapter", "qq", "wire");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-filesget-" + Guid.NewGuid().ToString("N"));

    public FilesGetToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private LocalFileSystemAccess Files() =>
        new(_root, [FileScopes.Workspace, FileScopes.Media], binaryScopes: [FileScopes.Workspace], readOnlyScopes: [FileScopes.Media]);

    private static FilesGetTools Tools(IChatAdapter? adapter, LocalFileSystemAccess files,
        string conversation = "group-123456", bool channelBound = true, bool adapterPresent = true,
        IJobRegistry? jobs = null) =>
        new(new FakeChannels(channelBound, conversation, adapterPresent),
            new FakeAdapters(adapter),
            files, jobs);

    private static async Task<Result<SessionToolResult>> CallAsync(FilesGetTools tools, string arguments,
        bool noChannel = false)
    {
        var request = new RoundContextRequest("s-1", Guid.NewGuid(), 1, noChannel ? null : ChannelAddress);
        var offers = (await tools.ToolsAsync(request, default)).Value!;
        var offer = Assert.Single(offers);
        return await offer.Tool.ExecuteAsync(new ModelToolCall("call-1", "files_get", arguments), default);
    }

    private static FakeCatalog Catalog() => new()
    {
        Entries =
        [
            new PlatformFile(FileHandles.Wrap("0|new"), "新的成片.mp4", 2048, DateTimeOffset.UtcNow, "小林"),
            new PlatformFile(FileHandles.Wrap("0|old"), "旧的分镜.zip", 10, DateTimeOffset.UtcNow.AddDays(-3), "小林")
        ]
    };

    [Fact]
    public async Task With_no_target_it_lists_what_the_platform_is_holding()
    {
        var catalog = Catalog();
        var answered = await CallAsync(Tools(catalog, Files()), "{}");

        Assert.True(answered.Ok, answered.Error?.Message);
        Assert.Contains("新的成片.mp4", answered.Value!.Content, StringComparison.Ordinal);
        Assert.Contains(FileHandles.Prefix, answered.Value.Content, StringComparison.Ordinal);
        Assert.Contains("2 个文件", answered.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_target_that_is_not_a_handle_searches_names()
    {
        var catalog = Catalog();

        var hit = await CallAsync(Tools(catalog, Files()), """{"target":"成片"}""");
        Assert.True(hit.Ok, hit.Error?.Message);
        Assert.Contains("新的成片.mp4", hit.Value!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("旧的分镜", hit.Value.Content, StringComparison.Ordinal);

        var miss = await CallAsync(Tools(catalog, Files()), """{"target":"预算表"}""");
        Assert.True(miss.Ok, miss.Error?.Message);
        // "Nothing matched" is not "nothing is there": the answer says which one it is, and how to see everything.
        Assert.Contains("没有名字里带", miss.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("不带 target", miss.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fetch_without_a_destination_is_refused_before_anything_is_downloaded()
    {
        var catalog = Catalog();
        var answered = await CallAsync(Tools(catalog, Files()), $$"""{"target":"{{FileHandles.Wrap("0|new")}}"}""");

        Assert.False(answered.Ok);
        Assert.Contains("scope", answered.Error!.Message, StringComparison.Ordinal);
        // The scopes that would have worked are named: a refusal that does not say what would work is a dead end.
        Assert.Contains(FileScopes.Workspace, answered.Error.Message, StringComparison.Ordinal);
        // And nothing was asked of the platform: an incomplete call must not cost a download.
        Assert.Empty(catalog.Fetched);
    }

    [Fact]
    public async Task A_handle_puts_the_bytes_exactly_where_the_caller_said()
    {
        var registry = new FileJobRegistry();
        var catalog = Catalog();
        var files = Files();
        var answered = await CallAsync(Tools(catalog, files, jobs: registry),
            $$"""{"target":"{{FileHandles.Wrap("0|new")}}","scope":"workspace","path":"downloads/成片.mp4"}""");

        Assert.True(answered.Ok, answered.Error?.Message);
        Assert.Contains("workspace/downloads/成片.mp4", answered.Value!.Content, StringComparison.Ordinal);

        var opened = Assert.Single((await registry.ListAsync("s-1", default)).Value!);
        var settled = await WaitForJobAsync(registry, opened.Id);
        Assert.Equal(JobState.Done, settled.State);

        var saved = await files.ReadAsync(FileScopes.Workspace, "downloads/成片.mp4", default);
        Assert.True(saved.Ok, saved.Error?.Message);
        Assert.Equal("bytes of 新的成片.mp4", saved.Value!.Text);
        Assert.Equal("downloads/成片.mp4", saved.Value.Path);
        // The size comes from the bytes that actually arrived, not from what the platform claimed about them: the listing
        // said 2048 and this fake hands over a few dozen, and the number on the row has to be the true one.
        Assert.Contains($"（{saved.Value.Bytes} B）", settled.Result ?? "", StringComparison.Ordinal);
        Assert.Equal(new[] { FileHandles.Wrap("0|new") }, catalog.Fetched);
    }

    /// <summary>
    /// A file on a platform is not a transfer the tool chose: it is as big as whoever put it there made it. So the
    /// ceiling is four gigabytes, not the sixty-four that answer "how much may one tool move through this process"
    /// (docs/28 §6.8).
    /// </summary>
    [Fact]
    public async Task A_platform_file_may_be_larger_than_one_transfer_and_the_tool_says_so()
    {
        var registry = new FileJobRegistry();
        var catalog = Catalog();
        var answered = await CallAsync(Tools(catalog, Files(), jobs: registry),
            $$"""{"target":"{{FileHandles.Wrap("0|new")}}","scope":"workspace","path":"big.mp4"}""");

        Assert.True(answered.Ok, answered.Error?.Message);
        var opened = Assert.Single((await registry.ListAsync("s-1", default)).Value!);
        Assert.Equal(JobState.Done, (await WaitForJobAsync(registry, opened.Id)).State);
        // The ceiling the job carried, not the one a tool moving its own tarball would use.
        Assert.Equal(new[] { FilesGetTools.MaxFetchBytes }, catalog.Ceilings);
        Assert.Equal(4L * 1024 * 1024 * 1024, FilesGetTools.MaxFetchBytes);
        Assert.True(FilesGetTools.MaxFetchBytes > IBinaryFileAccess.MaxTransferBytes);
    }

    [Fact]
    public async Task The_destination_is_the_byte_ports_decision_and_its_refusal_is_passed_on()
    {
        // Media is the read-only half of the file system: bytes somebody else sent, which the model may look at but not
        // write. "Does this scope take bytes at all" is answered before anything is in flight, because a job aimed at a
        // scope that never accepts writes is a failed row somebody waits for. A path that escapes its scope is a
        // different question, and the port answers that one when the write happens (I-37).
        var answered = await CallAsync(Tools(Catalog(), Files(), jobs: new FileJobRegistry()),
            $$"""{"target":"{{FileHandles.Wrap("0|new")}}","scope":"media","path":"file/x.mp4"}""");

        Assert.False(answered.Ok);
        Assert.Contains("media", answered.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fetch_that_fails_lands_on_the_ledger_and_is_not_retried_as_a_search()
    {
        // The one mistake that makes three shapes worse than one: "it did not work, so it must have been the other
        // shape". A model asking for bytes would get a listing, and would have no way to tell that nothing arrived.
        var registry = new FileJobRegistry();
        var catalog = Catalog();
        catalog.Failure = new(ErrorCode.Unavailable, "平台那边没有这个文件。");
        var answered = await CallAsync(Tools(catalog, Files(), jobs: registry),
            $$"""{"target":"{{FileHandles.Wrap("0|gone")}}","scope":"workspace","path":"x.mp4"}""");

        // The call is the scheduling step, so it succeeds: what failed is the fetch, and that belongs on the row.
        Assert.True(answered.Ok, answered.Error?.Message);
        var opened = Assert.Single((await registry.ListAsync("s-1", default)).Value!);
        var settled = await WaitForJobAsync(registry, opened.Id);
        Assert.Equal(JobState.Failed, settled.State);
        Assert.Contains("没有这个文件", settled.Error ?? "", StringComparison.Ordinal);
        // Not a listing: the search was never run.
        Assert.Empty(catalog.Listed);
    }

    [Fact]
    public async Task A_session_with_no_platform_says_that_instead_of_looking()
    {
        var answered = await CallAsync(Tools(Catalog(), Files()), "{}", noChannel: true);

        Assert.False(answered.Ok);
        Assert.Contains("没有连着任何平台会话", answered.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_adapter_that_cannot_do_this_is_one_sentence_not_an_empty_list()
    {
        // Optional interface (I-37): an adapter that does not implement it has no file area, and saying so is the whole
        // answer — there is no capability declaration to consult first, and no empty array to mistake for "nothing there".
        var answered = await CallAsync(Tools(new FakeSender(), Files()), "{}");

        Assert.False(answered.Ok);
        Assert.Contains("不提供文件目录", answered.Error!.Message, StringComparison.Ordinal);
    }

    // ---- a fetch is never this turn's work ---------------------------------------------------------

    [Fact]
    public async Task Every_fetch_becomes_a_job_that_still_lands_where_it_was_aimed()
    {
        // A turn that blocks reads as a hang, and "will this one be slow" is a question about a network nobody can answer
        // before the first byte moves — so every fetch is written down and done in the background, with the destination
        // decided *now*, because a job that picked its own path would write a file nobody finds.
        var registry = new FileJobRegistry();
        var catalog = new FakeCatalog
        {
            Entries =
            [
                new PlatformFile(FileHandles.Wrap("0|big"), "成片.mp4", 12L * 1024 * 1024, DateTimeOffset.UtcNow, "小林")
            ]
        };
        var files = Files();
        var tools = Tools(catalog, files, jobs: registry);

        var answered = await CallAsync(tools,
            $$"""{"target":"{{FileHandles.Wrap("0|big")}}","scope":"workspace","path":"downloads/成片.mp4"}""");

        Assert.True(answered.Ok, answered.Error?.Message);
        Assert.Contains("后台", answered.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("jobs", answered.Value.Content, StringComparison.Ordinal);
        // It came back without the bytes: the row exists and the file does not (yet).
        var opened = Assert.Single((await registry.ListAsync("s-1", default)).Value!);
        Assert.Equal("file.fetch", opened.Kind);
        Assert.Contains("workspace/downloads/成片.mp4", opened.Label, StringComparison.Ordinal);

        // And then it finishes by itself, on the row, which is what wakes the Session.
        var settled = await WaitForJobAsync(registry, opened.Id);
        Assert.Equal(JobState.Done, settled.State);
        Assert.Contains("workspace/downloads/成片.mp4", settled.Result ?? "", StringComparison.Ordinal);
        var saved = await files.ReadAsync(FileScopes.Workspace, "downloads/成片.mp4", default);
        Assert.True(saved.Ok, saved.Error?.Message);
        Assert.Equal("bytes of 成片.mp4", saved.Value!.Text);
    }

    [Fact]
    public async Task Without_a_ledger_a_fetch_is_refused_rather_than_held_open()
    {
        // Every fetch goes through the ledger now, so a host without one cannot fetch at all — and says so. Falling back
        // to doing it in the turn would put the wait back exactly where this change took it out of, decided by a
        // configuration the caller cannot see.
        var catalog = Catalog();
        var answered = await CallAsync(Tools(catalog, Files()),
            $$"""{"target":"{{FileHandles.Wrap("0|new")}}","scope":"workspace","path":"x.mp4"}""");

        Assert.False(answered.Ok);
        Assert.Contains("账本", answered.Error!.Message, StringComparison.Ordinal);
        // And nothing was asked of the platform.
        Assert.Empty(catalog.Fetched);
    }

    private static async Task<JobSnapshot> WaitForJobAsync(IJobRegistry registry, string jobId, int seconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var row = (await registry.GetAsync("s-1", jobId, default)).Value!;
            if (row.IsOver) return row;
            await Task.Delay(20);
        }
        Assert.Fail($"job {jobId} never ended");
        return null!;
    }

    // ---- doubles -----------------------------------------------------------------------------------

    /// <summary>An adapter with a file area: the two verbs, and nothing else to get in the way.</summary>
    private sealed class FakeCatalog : AdapterBase, IFileCatalogAdapter
    {
        public FakeCatalog() : base(AdapterAddress, new("qq", "wire", AdapterDirection.Both)) { }

        public IReadOnlyList<PlatformFile> Entries { get; init; } = [];

        public string? Note { get; init; }

        /// <summary>What a fetch should fail with, when the test is about a failure.</summary>
        public Error? Failure { get; set; }

        public List<string> Listed { get; } = [];
        public List<string> Fetched { get; } = [];

        /// <summary>What the tool said it would accept, per fetch: the ceiling belongs to the caller, not to the port.</summary>
        public List<long> Ceilings { get; } = [];

        public ValueTask<Result<PlatformFileListing>> ListAsync(string conversationId, int limit,
            CancellationToken cancellationToken)
        {
            Listed.Add(conversationId);
            return ValueTask.FromResult(Failure is { } failure
                ? Result<PlatformFileListing>.Failure(failure)
                : Result<PlatformFileListing>.Success(new PlatformFileListing(Entries.Take(limit).ToArray(), null, Note)));
        }

        public ValueTask<Result<PlatformFileContent>> FetchAsync(string conversationId, string handle, long maxBytes,
            CancellationToken cancellationToken)
        {
            Fetched.Add(handle);
            Ceilings.Add(maxBytes);
            if (Failure is { } failure) return ValueTask.FromResult(Result<PlatformFileContent>.Failure(failure));
            var file = Entries.FirstOrDefault(entry => entry.Handle == handle);
            if (file is null)
                return ValueTask.FromResult(Result<PlatformFileContent>.Failure(
                    new(ErrorCode.Invalid, $"没有这个 handle：{handle}")));
            var bytes = Encoding.UTF8.GetBytes("bytes of " + file.Name);
            return ValueTask.FromResult(Result<PlatformFileContent>.Success(
                new PlatformFileContent(new MemoryStream(bytes), file.Name, bytes.Length)));
        }

        public override ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SendResult>.Failure(new(ErrorCode.Unavailable, "Not a sender in these tests.")));
    }

    /// <summary>An adapter with no file area at all: the shape of "this platform cannot do that", not of an error.</summary>
    private sealed class FakeSender : AdapterBase
    {
        public FakeSender() : base(AdapterAddress, new("qq", "wire", AdapterDirection.Both)) { }

        public override ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SendResult>.Failure(new(ErrorCode.Unavailable, "Not a sender in these tests.")));
    }

    /// <summary>A Channel bound to one platform conversation — or to nothing, when the test is about an unbound Session.</summary>
    private sealed class FakeChannels(bool bound, string conversation, bool adapterPresent) : IChannelManager
    {
        public Result<ChannelInfo> Get(Address address) => bound
            ? Result<ChannelInfo>.Success(new ChannelInfo(ChannelAddress, "c1",
                [new ChannelBinding(adapterPresent ? AdapterAddress : new Address("adapter", "qq", "gone"), conversation)],
                true, "channel.conversation"))
            : Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "No such Channel."));

        public IReadOnlyList<ChannelInfo> List() => [];
        public bool IsBound(Address channel, Address adapter, string platformConversationId) => false;
        public Result<ChannelInfo> FindBound(Address adapter, string platformConversationId) =>
            Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "none"));
        public ValueTask<Result<ChannelInfo>> CreateAsync(ChannelSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<Result> StartAsync(Address address, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DestroyAsync(Address channel, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result<int>> AcceptAsync(AdapterEnvelope envelope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> CompleteAcceptAsync(Address channel, Guid messageId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> RecordReplyAsync(Address channel, Guid answeredMessageId, string platformConversationId,
            string text, string? platformMessageId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result<int>> ReplayPendingAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<int>.Success(0));
    }

    /// <summary>The adapter table, holding at most the one fake.</summary>
    private sealed class FakeAdapters(IChatAdapter? adapter) : IAdapterManager
    {
        public Result<IChatAdapter> Get(Address address) =>
            adapter is not null && address == AdapterAddress
                ? Result<IChatAdapter>.Success(adapter)
                : Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, "No such Adapter."));

        public IReadOnlyList<AdapterInfo> List() => [];
        public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, Func<AdapterSpec, IChatAdapter> factory,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result<IReadOnlyList<AdapterInfo>>> SnapshotByTypeAsync(string concreteTypeId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result> DestroyByTypeAsync(string concreteTypeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> ForceDestroyByTypeAsync(string concreteTypeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StartAsync(Address address, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DestroyAsync(Address address, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
    }
}
