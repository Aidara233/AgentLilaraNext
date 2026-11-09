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
    private readonly SessionExtensionRegistry _contributors = new();
    private readonly SessionManager _sessions;
    private static readonly Address Channel = new("channel", "conversation", "chat-1");
    private static readonly ChannelBinding Binding = new(new("adapter", "file", "file-1"), "chat-1");

    public SessionRoundContextTests()
    {
        Assert.True(_contributors.Register(new TimeContextSource(new FixedClock())).Ok);
        Assert.True(_contributors.Register(new ChannelInfoSource()).Ok);
        _sessions = new SessionManager(_root, _model, _contributors);
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

        var history = File.ReadAllLines(Path.Combine(_root, "modules", "s-chat-1", "history.jsonl"));
        // Floating time never becomes timeline: it would be a stale timestamp in every later request.
        Assert.DoesNotContain(history, line => line.Contains("当前时间", StringComparison.Ordinal));
        var channelInfoEntries = history.Where(line => line.Contains("\"id\":\"channel-info\"", StringComparison.Ordinal)).ToArray();
        Assert.Single(channelInfoEntries);
    }

    [Fact]
    public async Task A_broken_contributor_is_left_out_of_the_turn_instead_of_failing_it()
    {
        Assert.True(_contributors.Register(new ThrowingSource("broken")).Ok);
        var spec = new SessionSpec("default", "Answer briefly.", "1");

        var turn = await _sessions.TurnAsync("s-broken", spec, Guid.NewGuid(),
            new SessionTurnInput([new("m-1", "user", "alice", "hello", DateTimeOffset.UtcNow)], Channel, Binding),
            null, default);

        // A hook may contribute or observe, never veto: a broken one used to fail the whole step.
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        var request = Assert.Single(_model.Requests);
        var block = Assert.Single(request.Messages, m => m.Role == "user" &&
            m.Content?.Contains("<channel_context>", StringComparison.Ordinal) == true);
        // What the healthy contributors had to say still arrives.
        Assert.Contains("当前时间：", block.Content ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Self_says_which_session_it_is_how_deep_it_is_trusted_and_what_policy_narrowed_it()
    {
        var policy = new SessionToolPolicy(Only: ["actions_call"], Deny: ["read_file", "write_file"]);
        var spec = new SessionSpec("default", "Answer briefly.", "1", ToolPolicy: policy);
        var first = await _sessions.TurnAsync("s-self", spec, Guid.NewGuid(),
            new SessionTurnInput([new("m-1", "user", "alice", "hello", DateTimeOffset.UtcNow)], Channel, Binding),
            null, default);
        Assert.True(first.Ok, first.Error?.Detail ?? first.Error?.Message);

        var block = Assert.Single(_model.Requests[0].Messages, m => m.Role == "user" &&
            m.Content?.Contains("<channel_context>", StringComparison.Ordinal) == true).Content ?? string.Empty;
        Assert.Contains("<section name=\"self\"", block, StringComparison.Ordinal);
        Assert.Contains("会话：s-self。", block, StringComparison.Ordinal);
        Assert.Contains($"你答的是频道 {Channel}", block, StringComparison.Ordinal);
        Assert.Contains("等级：L0 agent", block, StringComparison.Ordinal);
        // The sentence that keeps "depth" from being read as "rank".
        Assert.Contains("不是谁有资格命令你", block, StringComparison.Ordinal);
        Assert.Contains("只要 actions_call", block, StringComparison.Ordinal);
        Assert.Contains("另有 2 个永远不给", block, StringComparison.Ordinal);

        // The level is one of the few things an operator can change mid-flight: the very next round has to know.
        Assert.True((await _sessions.SetLevelAsync("s-self", AccessLevel.Operator, default)).Ok);
        var second = await _sessions.TurnAsync("s-self", spec, Guid.NewGuid(),
            new SessionTurnInput([new("m-2", "user", "alice", "again", DateTimeOffset.UtcNow)], Channel, Binding),
            null, default);
        Assert.True(second.Ok, second.Error?.Detail ?? second.Error?.Message);

        var after = Assert.Single(_model.Requests[1].Messages, m => m.Role == "user" &&
            m.Content?.Contains("<channel_context>", StringComparison.Ordinal) == true).Content ?? string.Empty;
        Assert.Contains("等级：L1 operator", after, StringComparison.Ordinal);
        Assert.DoesNotContain("等级：L0 agent", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Self_still_says_which_channel_after_the_opening_line_falls_out_of_the_window()
    {
        // The opening channel-info line is one message among the last N that get re-rendered, so a conversation longer
        // than that window drops the one entry that said where this Session lives. Nothing else in the request replaced
        // it — which is why the self section carries the same identity every round.
        var spec = new SessionSpec("default", "Answer briefly.", "1");
        var opening = await _sessions.TurnAsync("s-long", spec, Guid.NewGuid(),
            new SessionTurnInput([new("m-open", "user", "alice", "hello", DateTimeOffset.UtcNow)], Channel, Binding),
            null, default);
        Assert.True(opening.Ok, opening.Error?.Detail ?? opening.Error?.Message);

        var burst = Enumerable.Range(0, SessionManager.RecentChannelMessages)
            .Select(index => new SessionChannelMessage($"m-{index:D3}", "channel.delta", "alice", $"第 {index} 句",
                DateTimeOffset.UtcNow.AddSeconds(index)))
            .ToArray();
        var later = await _sessions.TurnAsync("s-long", spec, Guid.NewGuid(),
            new SessionTurnInput(burst, Channel, Binding), null, default);
        Assert.True(later.Ok, later.Error?.Detail ?? later.Error?.Message);

        var block = Assert.Single(_model.Requests[1].Messages, m => m.Role == "user" &&
            m.Content?.Contains("<channel_context>", StringComparison.Ordinal) == true).Content ?? string.Empty;
        // The opening note is gone from the request…
        Assert.DoesNotContain("频道地址：", block, StringComparison.Ordinal);
        Assert.Contains("<omitted count=", block, StringComparison.Ordinal);
        // …but the identity is not.
        Assert.Contains($"你答的是频道 {Channel}", block, StringComparison.Ordinal);
    }

    private sealed class ThrowingSource(string id) : IFloatingContextSource
    {
        public string Id => id;
        public ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(
            RoundContextRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Result<IReadOnlyList<RoundContextContribution>>>(
                new InvalidOperationException("source is broken"));
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
