using System.Runtime.CompilerServices;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The replay rules, as a turn actually assembles its request.
///
/// <see cref="SessionReplayTests"/> pins what folding means; this pins that the loop uses it — the difference matters,
/// because the loop had been handing the whole timeline to the provider and nothing in the pure rules would notice.
/// </summary>
public sealed class SessionReplayWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-replay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const int ResultChars = 5_000;

    private static readonly ModelToolDefinition ReadShape = new("read", "Read one file.",
        """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");

    [Fact]
    public async Task A_long_session_sends_an_outline_of_old_work_and_the_full_text_of_the_newest()
    {
        const int turns = 20;
        var model = new ReplayModel(Turn());
        var read = new ReadingTool(new string('z', ResultChars));
        var sessions = new SessionManager(_root, model);
        var spec = new SessionSpec("default", "Keep reading.", "1", Tools: [ReadShape]);
        var toolbox = new ReplayToolbox(read);

        for (var turn = 0; turn < turns; turn++)
        {
            var ran = await sessions.TurnAsync("s-1", spec, Guid.NewGuid(),
                new SessionTurnInput([], null, null), toolbox, default);
            Assert.True(ran.Ok, ran.Error?.Detail ?? ran.Error?.Message);
        }

        var messages = model.Requests.Last().Messages;
        var answered = messages.Where(message => message.Role == "tool").Select(message => message.ToolCallId!).ToArray();
        var called = messages.SelectMany(message => message.ToolCalls ?? []).Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        // Same rule as at the unit level, now from the loop's own request: no result without its call.
        Assert.NotEmpty(answered);
        Assert.All(answered, id => Assert.Contains(id, called));

        // Fifteen of twenty turns are older than the window, and each of them left two results behind (the read and the
        // wait that ended the turn), so thirty of forty become outlines; the newest five turns are still whole.
        var folded = messages.Count(message => message.Content?.Contains("已折叠", StringComparison.Ordinal) == true);
        Assert.Equal((turns - SessionReplay.KeepRecentActivations) * 2, folded);
        var characters = messages.Sum(message => message.Content?.Length ?? 0);
        Assert.True(characters < turns * ResultChars / 3,
            $"{characters} characters for {turns} results of {ResultChars}: folding did not take");

        // The newest result is still readable in full: the model is in the middle of a turn, not reading history.
        Assert.Contains(messages, message => message.Content?.Length == ResultChars);

        // And the folding is a view: the timeline on disk still holds every result it ever got.
        var history = await File.ReadAllLinesAsync(Path.Combine(_root, "modules", "s-1", "history.jsonl"));
        Assert.Equal(turns, history.Count(line => line.Contains(new string('z', 64), StringComparison.Ordinal)));
        Assert.DoesNotContain(history, line => line.Contains("已折叠", StringComparison.Ordinal));
    }

    /// <summary>One turn's script: read a file, then stop.</summary>
    private static ModelEvent[][] Turn()
    {
        var call = 0;
        return
        [
            [new ToolCallDelta(0, $"call_r{call++}", "read", """{"path":"memory/x/block.md"}"""),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(10, 10))],
            [new ToolCallDelta(0, $"call_w{call++}", "wait", "{}"),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(10, 10))]
        ];
    }

    private sealed class ReadingTool(string result) : ISessionTool
    {
        public string Name => "read";
        public bool EndsTurn => false;

        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SessionToolResult>.Success(new SessionToolResult(result, null)));
    }

    private sealed class ReplayToolbox(params ISessionTool[] tools) : ISessionToolbox
    {
        public IReadOnlyList<string> Names { get; } = tools.Select(tool => tool.Name).ToArray();

        public Result<ISessionTool> Find(string name) => tools.FirstOrDefault(tool => tool.Name == name) is { } found
            ? Result<ISessionTool>.Success(found)
            : Result<ISessionTool>.Failure(new(ErrorCode.Invalid, $"No tool named {name}."));
    }

    /// <summary>A model that answers with the same two steps forever and keeps every request it was given.</summary>
    private sealed class ReplayModel(ModelEvent[][] script) : IModelManager
    {
        public List<ModelRequest> Requests { get; } = [];

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var step = script[Requests.Count % 2 == 1 ? 0 : 1];
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
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }
}
