using System.Runtime.CompilerServices;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The two tools a Session uses to talk to other Sessions, and the roster that tells it who they are.
///
/// What is worth pinning: the roster answers "what is this conversation's job" (its own line first, with objective,
/// level and Channel) and "who else is here"; the message tool goes through the control plane, so the record it leaves
/// is the same one <c>actions_call</c> would leave; and the answer to a busy receiver says "it is waiting there" rather
/// than "it failed", because a model told "failed" sends the same message a second time.
/// </summary>
public sealed class SessionToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-mesh-" + Guid.NewGuid().ToString("N"));
    private readonly SessionExtensionRegistry _extensions = new();
    private readonly List<AuditRecord> _audit = [];
    private readonly ControlPlane _plane;
    private readonly SessionManager _sessions;

    public SessionToolsTests()
    {
        _sessions = new SessionManager(_root, new SilentModel(), _extensions);
        _plane = new ControlPlane(new CollectingAudit(_audit), SignalLog.Open(null));
        Assert.True(_sessions.RegisterActions(_plane).Ok);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static SessionSpec Spec(string objective, AccessLevel level = AccessLevel.Agent) =>
        new("default", objective, "1", Level: level);

    /// <summary>
    /// One tool as a round would see it: the offers are per round, and both tools read who is calling from the request.
    /// </summary>
    private static async Task<ISessionTool> ToolAsync(SessionTools tools, string name, string? sessionId,
        AccessLevel level = AccessLevel.Agent)
    {
        var offers = await tools.ToolsAsync(new RoundContextRequest(sessionId ?? "s-none", Guid.NewGuid(), 1,
            Level: level), default);
        Assert.True(offers.Ok, offers.Error?.Message);
        return offers.Value!.Single(offer => offer.Definition.Name == name).Tool;
    }

    private static string Text(Result<SessionToolResult> result)
    {
        Assert.True(result.Ok, result.Error?.Message);
        return result.Value!.Content;
    }

    [Fact]
    public async Task The_roster_says_what_the_reader_is_for_before_it_says_who_else_is_here()
    {
        Assert.True((await _sessions.OpenAsync("s-me", Spec("回答这个群里的问题。"), default)).Ok);
        Assert.True((await _sessions.OpenForChannelAsync("channel.conversation#chat-1", Spec("盯着那个频道的公告。"), default)).Ok);
        Assert.True((await _sessions.OpenAsync("s-deep", Spec("替人做决定。", AccessLevel.Operator), default)).Ok);

        var tools = new SessionTools(_sessions, _plane);
        var roster = Text(await (await ToolAsync(tools, SessionTools.RosterToolName, "s-me"))
            .ExecuteAsync(new("c1", SessionTools.RosterToolName, "{}"), default));

        // The reader's own line, first, and marked: objective, level, and "serving no Channel" said out loud rather
        // than left blank — a Session with no Channel cannot send anything outward, and that is worth knowing.
        Assert.StartsWith("- s-me   ← you", roster, StringComparison.Ordinal);
        Assert.Contains("objective: 回答这个群里的问题。", roster);
        Assert.Contains("L0 agent", roster);
        Assert.Contains("serving no Channel", roster);

        // Somebody else's job is in the same answer: that is the point of the tool.
        Assert.Contains("盯着那个频道的公告。", roster);
        Assert.Contains("channel.conversation#chat-1", roster);
        Assert.Contains("替人做决定。", roster);
        Assert.Contains("s-deep", roster);
        // The deep one is reported as deep rather than hidden.
        Assert.Contains("L1 operator", roster);
        // And exactly one line is the reader's.
        Assert.Equal(1, roster.Split("← you").Length - 1);
    }

    [Fact]
    public async Task A_session_with_nothing_on_disk_still_gets_an_honest_answer()
    {
        // The first turn of a Channel's Session: the directory may not exist yet when the roster is asked. Saying
        // "nothing about you is written down" is the truth, and it is better than a fabricated objective.
        var tools = new SessionTools(_sessions, _plane);
        var roster = Text(await (await ToolAsync(tools, SessionTools.RosterToolName, "s-not-yet"))
            .ExecuteAsync(new("c1", SessionTools.RosterToolName, "{}"), default));

        Assert.Contains("s-not-yet", roster);
        Assert.Contains("no directory on disk yet", roster);
        Assert.Contains("No other Session exists on this host", roster);
    }

    [Fact]
    public async Task A_message_reaches_the_other_session_and_says_which_of_the_two_things_happened()
    {
        Assert.True((await _sessions.OpenAsync("s-me", Spec("接线员。"), default)).Ok);
        Assert.True((await _sessions.OpenAsync("s-other", Spec("管数据的人。"), default)).Ok);

        var tools = new SessionTools(_sessions, _plane);
        var message = await ToolAsync(tools, SessionTools.MessageToolName, "s-me");

        // Stored, not woken: the default. The sentence has to say so, or the model waits for an answer that is not coming.
        var stored = Text(await message.ExecuteAsync(new("c1", SessionTools.MessageToolName,
            """{"session":"s-other","text":"把上个月的数字找出来"}"""), default));
        Assert.StartsWith("ok: handed to s-other", stored, StringComparison.Ordinal);
        Assert.Contains("will see this next time it runs", stored);

        var history = await File.ReadAllLinesAsync(Path.Combine(_root, "modules", "s-other", "history.jsonl"));
        // Read the JSON back rather than grepping the bytes: the file escapes non-ASCII, so a search for the sentence
        // would fail for a reason that has nothing to do with whether it landed.
        var entries = history.Select(line => JsonDocument.Parse(line).RootElement).ToArray();
        Assert.Contains(entries, entry => entry.TryGetProperty("content", out var content) &&
                                          content.ValueKind == JsonValueKind.String &&
                                          (content.GetString() ?? "").Contains("把上个月的数字找出来", StringComparison.Ordinal));
        // Attributed by the host, not by the text: the receiver has to be able to tell who asked.
        Assert.Contains(entries, entry => entry.TryGetProperty("relay", out var relay) &&
                                          relay.TryGetProperty("from", out var from) &&
                                          from.GetString() == "model:s-me");

        // Through the plane, so the record exists — the same one actions_call would have written.
        var record = Assert.Single(_audit, entry => entry.Action == SessionActions.Message);
        Assert.Equal("model:s-me", record.Actor);

        // And waking it really runs a turn: the answer says which, instead of leaving the caller to guess.
        var woken = Text(await message.ExecuteAsync(new("c2", SessionTools.MessageToolName,
            """{"session":"s-other","text":"现在就看","wake":true}"""), default));
        Assert.Contains("was woken and ran a turn now", woken);
    }

    [Fact]
    public async Task Talking_to_oneself_is_refused_and_writes_nothing()
    {
        Assert.True((await _sessions.OpenAsync("s-me", Spec("只跟自己说话。"), default)).Ok);
        var tools = new SessionTools(_sessions, _plane);
        var message = await ToolAsync(tools, SessionTools.MessageToolName, "s-me");

        var refused = Text(await message.ExecuteAsync(new("c1", SessionTools.MessageToolName,
            """{"session":"s-me","text":"提醒自己一下","wake":true}"""), default));
        Assert.Contains("That is this Session", refused);
        // Nothing was written: no relay in the history, and no audit line for a call that never happened.
        var historyPath = Path.Combine(_root, "modules", "s-me", "history.jsonl");
        if (File.Exists(historyPath))
            Assert.DoesNotContain("relay:", await File.ReadAllTextAsync(historyPath), StringComparison.Ordinal);
        Assert.DoesNotContain(_audit, entry => entry.Action == SessionActions.Message);
    }

    [Fact]
    public async Task An_id_nobody_answers_to_is_answered_as_that_and_points_at_the_roster()
    {
        Assert.True((await _sessions.OpenAsync("s-me", Spec("接线员。"), default)).Ok);
        var tools = new SessionTools(_sessions, _plane);
        var message = await ToolAsync(tools, SessionTools.MessageToolName, "s-me");

        var answer = Text(await message.ExecuteAsync(new("c1", SessionTools.MessageToolName,
            """{"session":"s-ghost","text":"在吗"}"""), default));

        Assert.StartsWith("error: sessions_message → s-ghost", answer, StringComparison.Ordinal);
        Assert.Contains("code: invalid", answer);
        // A refusal a caller cannot act on is a refusal that gets retried: name the tool that answers "who is there".
        Assert.Contains(SessionTools.RosterToolName, answer);
    }

    [Fact]
    public async Task The_tools_are_offered_by_a_module_and_read_the_caller_per_round()
    {
        // One source serves every Session in the process, so an id or a level captured in the constructor would be the
        // first Session's forever (docs/07 §9). Two rounds, two callers, two rosters.
        Assert.True((await _sessions.OpenAsync("s-a", Spec("甲的活。"), default)).Ok);
        Assert.True((await _sessions.OpenAsync("s-b", Spec("乙的活。"), default)).Ok);

        var tools = new SessionTools(_sessions, _plane);
        var forA = Text(await (await ToolAsync(tools, SessionTools.RosterToolName, "s-a"))
            .ExecuteAsync(new("c1", SessionTools.RosterToolName, "{}"), default));
        var forB = Text(await (await ToolAsync(tools, SessionTools.RosterToolName, "s-b"))
            .ExecuteAsync(new("c2", SessionTools.RosterToolName, "{}"), default));

        Assert.StartsWith("- s-a   ← you", forA, StringComparison.Ordinal);
        Assert.StartsWith("- s-b   ← you", forB, StringComparison.Ordinal);

        // The module is what puts the source on the round, and taking it out takes both tools away.
        var extensions = new SessionExtensionRegistry();
        var module = Assert.IsAssignableFrom<IKernelModule>(SessionToolsModule
            .FromManifest(new ModuleManifest(new ModuleId(SessionToolsModule.TypeId), SessionToolsModule.TypeId, true),
                _sessions, _plane, extensions).Value);
        Assert.Equal(new ModuleId(SessionToolsModule.TypeId), module.Id);
        Assert.True((await module.InitializeAsync(default)).Ok);
        var names = extensions.Tools.Single(source => source.Id == SessionTools.SourceId);
        var offered = await names.ToolsAsync(new RoundContextRequest("s-a", Guid.NewGuid(), 1), default);
        Assert.Equal([SessionTools.MessageToolName, SessionTools.RosterToolName],
            offered.Value!.Select(offer => offer.Definition.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.True((await module.StopAsync(default)).Ok);
        Assert.Empty(extensions.Tools);
    }

    /// <summary>A model that answers one line per step and nothing else: enough for a woken turn to finish.</summary>
    private sealed class SilentModel : IModelManager
    {
        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream()));

        private static async IAsyncEnumerable<ModelEvent> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        public ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public Result<IModelProvider> Get(Address provider) =>
            Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "No providers."));
        public IReadOnlyList<ModelProviderInfo> List() => [];
        public Result RegisterProfile(ModelProfile profile) => Result.Success();
        public Result RemoveProfile(string profileId) => Result.Success();
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }

    private sealed class CollectingAudit(List<AuditRecord> records) : IAuditSink
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            records.Add(record);
            return ValueTask.CompletedTask;
        }
    }
}
