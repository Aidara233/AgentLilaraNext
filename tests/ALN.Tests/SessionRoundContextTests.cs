using System.Runtime.CompilerServices;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class SessionRoundContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-round-context-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedModel _model = new();
    private readonly RoundContextContributorRegistry _contributors = new();
    private readonly SessionManager _sessions;
    private static readonly Address Channel = new("channel", "conversation", "chat-1");
    private static readonly ChannelBinding Binding = new(new("adapter", "file", "file-1"), "chat-1");

    public SessionRoundContextTests()
    {
        Assert.True(_contributors.Register(new TimeContextContributor(new FixedClock())).Ok);
        Assert.True(_contributors.Register(new ChannelInfoContributor()).Ok);
        _sessions = new SessionManager(Path.Combine(_root, "sessions"), _model, _contributors);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Time_is_rendered_each_round_but_channel_info_enters_history_only_once()
    {
        var spec = new SessionSpec("default", "Answer briefly.", "1");
        var first = await _sessions.TurnAsync("s-chat-1", spec, Guid.NewGuid(),
            new SessionTurnInput([new("m-1", "user", "alice", "hello", DateTimeOffset.UtcNow)], Channel, Binding),
            null, default);
        Assert.True(first.Ok, first.Error?.Detail ?? first.Error?.Message);

        var second = await _sessions.TurnAsync("s-chat-1", spec, Guid.NewGuid(),
            new SessionTurnInput([new("m-2", "user", "alice", "again", DateTimeOffset.UtcNow)], Channel, Binding),
            null, default);
        Assert.True(second.Ok, second.Error?.Detail ?? second.Error?.Message);

        Assert.Equal(2, _model.Requests.Count);
        var channelAddress = $"频道地址：{Channel}";
        foreach (var request in _model.Requests)
        {
            var block = Assert.Single(request.Messages, m => m.Role == "user" &&
                m.Content?.Contains("<channel_context>", StringComparison.Ordinal) == true);
            var content = block.Content ?? string.Empty;
            Assert.Contains("<floating_context>", content, StringComparison.Ordinal);
            Assert.Contains("当前时间：", content, StringComparison.Ordinal);
            Assert.Contains(channelAddress, content, StringComparison.Ordinal);
            // Injected once, so it must not be repeated inside the block.
            Assert.Equal(1, CountOccurrences(content, "频道地址："));
        }

        var history = File.ReadAllLines(Path.Combine(_root, "sessions", "s-chat-1", "history.jsonl"));
        // Floating time never becomes timeline: it would be a stale timestamp in every later request.
        Assert.DoesNotContain(history, line => line.Contains("当前时间", StringComparison.Ordinal));
        var channelInfoEntries = history.Where(line => line.Contains("\"id\":\"channel-info\"", StringComparison.Ordinal)).ToArray();
        Assert.Single(channelInfoEntries);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private sealed class FixedClock : TimeProvider
    {
        private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Records every request and answers with one deterministic text turn.</summary>
    private sealed class ScriptedModel : IModelManager
    {
        public List<ModelRequest> Requests { get; } = [];

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream()));
        }

        private static async IAsyncEnumerable<ModelEvent> Stream([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new TextDelta("ok");
            yield return new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1), null);
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
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }
}
