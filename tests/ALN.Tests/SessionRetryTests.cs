using System.Runtime.CompilerServices;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// What one turn does when the model step does not come back.
///
/// The rules this pins are the ones in <c>docs/08 §9</c>: a retry is only ever spent on a step where nothing else has
/// happened (no tool has run, so nothing can run twice), it is bounded, it does not touch a refusal that will still be
/// true a second later, and every failed attempt leaves the model a sentence it can act on. That last part is the
/// difference between "the framework gave up silently" and "the model was told what went wrong".
/// </summary>
public sealed class SessionRetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-retry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly SessionSpec Spec = new("default", "Answer briefly.", "1");

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_model_reads_the_error_in_the_next_attempt()
    {
        var model = new FlakyModel(Transient(), Transient(), Turn());
        var sessions = new SessionManager(_root, model, retryDelay: _ => TimeSpan.Zero);

        var ran = await sessions.TurnAsync("s-1", Spec, Guid.NewGuid(), new SessionTurnInput([], null, null));
        Assert.True(ran.Ok, ran.Error?.Detail ?? ran.Error?.Message);

        // Three attempts: the two that failed, then the one that answered.
        Assert.Equal(3, model.Requests.Count);
        // Both failures are in front of the model by the third attempt, numbered, so it can see this is a flap rather
        // than something it did wrong.
        var notes = model.Requests[2].Messages
            .Where(message => message.Content?.Contains("上一次请求没有成功", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(2, notes.Length);
        Assert.Contains("第 1 次", notes[0].Content, StringComparison.Ordinal);
        Assert.Contains("第 2 次", notes[1].Content, StringComparison.Ordinal);
        Assert.Contains("HTTP 503", notes[1].Content, StringComparison.Ordinal);
        Assert.Contains("不是谁在对你说话", notes[1].Content, StringComparison.Ordinal);
        // …and it is request-only: a failed attempt is not a thing anybody said, so it must not reach the timeline.
        var history = await File.ReadAllTextAsync(Path.Combine(_root, "modules", "s-1", "history.jsonl"));
        Assert.DoesNotContain("上一次请求没有成功", history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_retries_are_bounded_and_the_turn_still_fails()
    {
        var model = new FlakyModel(Enumerable.Repeat(Turn(), SessionManager.ModelRetries + 1).ToArray())
        {
            // Every attempt fails, and there is a success queued that must never be reached.
            AlwaysFail = Transient()
        };
        var sessions = new SessionManager(_root, model, retryDelay: _ => TimeSpan.Zero);

        var ran = await sessions.TurnAsync("s-1", Spec, Guid.NewGuid(), new SessionTurnInput([], null, null));
        Assert.False(ran.Ok);
        Assert.Equal(ErrorCode.Unavailable, ran.Error!.Code);
        // First attempt plus five retries, and then it stops: a broken endpoint must not hold a Session forever.
        Assert.Equal(SessionManager.ModelRetries + 1, model.Requests.Count);
    }

    [Fact]
    public async Task A_failure_that_will_still_be_true_next_second_is_not_retried()
    {
        // A 422 is the provider saying "this request is wrong", not "try again": repeating it wastes the turn and
        // buries the one line somebody has to act on.
        var model = new FlakyModel(Turn(), Transient()) { AlwaysFail = Permanent() };
        var sessions = new SessionManager(_root, model, retryDelay: _ => TimeSpan.Zero);

        var ran = await sessions.TurnAsync("s-1", Spec, Guid.NewGuid(), new SessionTurnInput([], null, null));
        Assert.False(ran.Ok);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task A_provider_that_throws_is_treated_as_a_transport_failure_and_retried()
    {
        // A network stack throws instead of answering. Nothing in the loop should see that as the end of the turn.
        var model = new FlakyModel(Turn()) { ThrowOnce = true };
        var sessions = new SessionManager(_root, model, retryDelay: _ => TimeSpan.Zero);

        var ran = await sessions.TurnAsync("s-1", Spec, Guid.NewGuid(), new SessionTurnInput([], null, null));
        Assert.True(ran.Ok, ran.Error?.Detail ?? ran.Error?.Message);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(model.Requests[1].Messages,
            message => message.Content?.Contains("The model request threw.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_tool_that_was_never_offered_is_reported_to_the_model_and_the_step_is_retried()
    {
        // The one failure the model itself can fix. Retrying it without saying what was wrong would just ask the same
        // question again; the note names what it could have called.
        var model = new FlakyModel(UnofferedTool(), Turn());
        var sessions = new SessionManager(_root, model, retryDelay: _ => TimeSpan.Zero);

        var ran = await sessions.TurnAsync("s-1", Spec, Guid.NewGuid(), new SessionTurnInput([], null, null));
        Assert.True(ran.Ok, ran.Error?.Detail ?? ran.Error?.Message);
        Assert.Equal(2, model.Requests.Count);
        var note = Assert.Single(model.Requests[1].Messages,
            message => message.Content?.Contains("did not offer", StringComparison.Ordinal) == true);
        Assert.Contains("Offered:", note.Content, StringComparison.Ordinal);
    }

    private static ModelEvent[] Turn() =>
    [
        new TextDelta("ok"), new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1), null)
    ];

    private static ModelEvent[] Transient() =>
        [new ModelFailed(new Error(ErrorCode.Unavailable, "OpenAI Chat returned HTTP 503.", null, Retryable: true))];

    private static ModelEvent[] Permanent() =>
        [new ModelFailed(new Error(ErrorCode.Unavailable, "OpenAI Chat returned HTTP 422.", null))];

    private static ModelEvent[] UnofferedTool() =>
    [
        new ToolCallDelta(0, "call-1", "speak", """{"text":"hi"}"""),
        new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1), null)
    ];

    /// <summary>
    /// Answers its script in order, one entry per attempt, and then fails. <see cref="AlwaysFail"/> and
    /// <see cref="ThrowOnce"/> are the two ways a provider misbehaves outside the script.
    /// </summary>
    private sealed class FlakyModel(params ModelEvent[][] script) : IModelManager
    {
        public List<ModelRequest> Requests { get; } = [];

        public ModelEvent[]? AlwaysFail { get; init; }

        public bool ThrowOnce { get; set; }

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (ThrowOnce)
            {
                ThrowOnce = false;
                throw new HttpRequestException("connection reset");
            }
            var events = AlwaysFail ?? (Requests.Count <= script.Length ? script[Requests.Count - 1] : Turn());
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream(events)));
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
        public ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public Result<IModelProvider> Get(Address provider) =>
            Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "No providers."));
        public IReadOnlyList<ModelProviderInfo> List() => [];
        public Result RegisterProfile(ModelProfile profile) => Result.Success();
        public Result RemoveProfile(string profileId) => Result.Success();
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }
}
