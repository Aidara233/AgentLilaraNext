using System.Runtime.CompilerServices;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// When a Session asks to be woken, and what the host makes of the answer.
///
/// Two things are being pinned. First, an absolute time is only a time with a zone: "08:00" without one is refused
/// rather than guessed, because guessing produces a wake-up at the wrong hour that nobody can explain afterwards.
/// Second, the refusal has to say which part is wrong — a deadline that silently becomes "sleep forever" is the
/// failure mode this whole tool exists to avoid.
/// </summary>
public sealed class SessionWaitTimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-wait-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ScriptedModel(params ModelEvent[][] steps) : IModelManager
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
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }

    private static SessionSpec Spec() => new("default", "Answer briefly.", "1");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One turn whose only step is a <c>wait</c> with the given arguments, and what the tool answered.</summary>
    private async Task<(string Text, DateTimeOffset? WaitUntil, bool Ok)> WaitAsync(string argumentsJson)
    {
        var model = new ScriptedModel(
            [new ToolCallDelta(0, "call-1", "wait", argumentsJson),
             new ModelCompleted(StopReason.ToolUse, new ModelUsage(1, 1))]);
        var sessions = new SessionManager(_root, model);
        var turn = await sessions.TurnAsync("s-wait", Spec(), Guid.NewGuid(),
            new SessionTurnInput([new("m-1", "user", "alice", "hello", DateTimeOffset.UtcNow)]), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);

        // The tool's own answer is the last tool entry in the timeline: refused arguments come back as the tool's
        // failure text, and the turn ends either way, so this is where a refusal is visible to the model.
        var directory = Path.Combine(_root, "modules", "s-wait");
        var entries = (await File.ReadAllLinesAsync(Path.Combine(directory, "history.jsonl")))
            .Select(line => JsonSerializer.Deserialize<SessionHistoryEntry>(line, Json)!)
            .Where(entry => entry.Role == "tool" && entry.ToolCallId == "call-1")
            .ToArray();
        var text = entries.Length == 0 ? "" : entries[^1].Content ?? "";

        var state = JsonSerializer.Deserialize<SessionState>(await File.ReadAllTextAsync(Path.Combine(directory, "state.json")), Json);
        return (text, state?.WaitUntil, text.StartsWith("{\"waited\":true", StringComparison.Ordinal));
    }

    /// <summary>The <c>until</c> the tool answered with, read out of its own JSON — what the model is told.</summary>
    private static DateTimeOffset? Iso(string text)
    {
        if (!text.StartsWith("{\"waited\"", StringComparison.Ordinal)) return null;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.TryGetProperty("until", out var until) && until.ValueKind == JsonValueKind.String
            ? until.GetDateTimeOffset()
            : null;
    }

    [Fact]
    public async Task A_clock_time_without_a_zone_is_refused_and_says_so()
    {
        var (text, until, _) = await WaitAsync("""{"at":"08:00"}""");
        Assert.Null(until);
        Assert.Contains("wait.at needs wait.timeZone", text, StringComparison.Ordinal);
        Assert.Contains("There is no default", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clock_time_with_a_zone_lands_on_the_next_time_it_reads_that()
    {
        var (text, until, ok) = await WaitAsync("""{"at":"08:00","timeZone":"Asia/Shanghai"}""");
        Assert.True(ok, text);
        Assert.NotNull(until);
        Assert.Equal(TimeSpan.FromHours(8), until!.Value.Offset);
        Assert.Equal(new TimeOnly(8, 0), TimeOnly.FromDateTime(until.Value.DateTime));
        // The answer repeats the instant with its offset: a zone mistake is visible there instead of at the wake-up.
        Assert.Equal(until.Value, Iso(text));
    }

    [Fact]
    public async Task An_instant_with_an_offset_stands_alone_and_is_used_as_written()
    {
        var wanted = DateTimeOffset.UtcNow.AddHours(3);
        var (text, until, ok) = await WaitAsync(
            $$"""{"until":"{{wanted:yyyy-MM-dd'T'HH:mm:sszzz}}"}""");
        Assert.True(ok, text);
        Assert.NotNull(until);
        // Whole seconds: the value is written to the second, so that is what comes back.
        Assert.Equal(wanted.ToUniversalTime().ToUnixTimeSeconds(), until!.Value.ToUnixTimeSeconds());
        Assert.Equal(until.Value, Iso(text));
    }

    [Fact]
    public async Task An_instant_without_an_offset_needs_a_zone_and_is_refused_without_one()
    {
        var (text, until, _) = await WaitAsync("""{"until":"2030-01-01T09:00:00"}""");
        Assert.Null(until);
        Assert.Contains("carries no offset", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_instant_without_an_offset_is_read_in_the_zone_it_names()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddDays(2), zone);
        var (text, until, ok) = await WaitAsync(
            $$"""{"until":"{{local:yyyy-MM-dd'T'HH:mm:ss}}","timeZone":"Asia/Shanghai"}""");
        Assert.True(ok, text);
        Assert.NotNull(until);
        Assert.Equal(TimeSpan.FromHours(8), until!.Value.Offset);
        // The same wall-clock reading, placed in the zone it named: the value carried no offset and did not need one.
        Assert.Equal(new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second),
            until.Value.DateTime);
        Assert.Equal(until.Value, Iso(text));
    }

    [Fact]
    public async Task A_time_that_has_already_passed_is_refused_rather_than_waking_at_once()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-1);
        var (text, until, _) = await WaitAsync(
            $$"""{"until":"{{past:yyyy-MM-dd'T'HH:mm:sszzz}}"}""");
        Assert.Null(until);
        Assert.Contains("has already passed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_time_further_than_a_year_is_refused()
    {
        var (text, until, _) = await WaitAsync("""{"until":"2099-01-01T09:00:00+08:00"}""");
        Assert.Null(until);
        Assert.Contains("days away", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_deadlines_at_once_are_refused()
    {
        var (text, until, _) = await WaitAsync("""{"timeout":60,"at":"08:00","timeZone":"Asia/Shanghai"}""");
        Assert.Null(until);
        Assert.Contains("one deadline", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_zone_is_refused_by_name()
    {
        var (text, until, _) = await WaitAsync("""{"at":"08:00","timeZone":"Mars/Olympus"}""");
        Assert.Null(until);
        Assert.Contains("does not name a known zone", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zone_without_a_time_means_nothing_and_is_refused()
    {
        var (text, until, _) = await WaitAsync("""{"timeZone":"Asia/Shanghai"}""");
        Assert.Null(until);
        Assert.Contains("only means something with at or until", text, StringComparison.Ordinal);
    }
}
