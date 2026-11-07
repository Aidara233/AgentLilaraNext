using System.Runtime.CompilerServices;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// Tools are interfaces too. A name in the tool list is an invitation, so a tool deeper than the caller is not offered
/// at all — and on top of that ceiling a Session may ask for fewer, which is a different question with a different
/// answer ("I don't need it" rather than "you may not have it").
/// </summary>
public sealed class SessionToolLevelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-tool-level-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedModel _model = new();
    private readonly SessionExtensionRegistry _extensions = new();

    public SessionToolLevelTests() => Assert.True(_extensions.Register(new ProbeSource()).Ok);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<IReadOnlyList<string>> OfferedAsync(SessionSpec spec, string sessionId)
    {
        var sessions = new SessionManager(_root, _model, _extensions);
        var turn = await sessions.TurnAsync(sessionId, spec, Guid.NewGuid(),
            new SessionTurnInput([new("m-1", "user", "alice", "hello", DateTimeOffset.UtcNow)]), null, default);
        Assert.True(turn.Ok, turn.Error?.Detail ?? turn.Error?.Message);
        return _model.Requests[^1].Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }

    private static SessionSpec Spec(AccessLevel level = AccessLevel.Agent, SessionToolPolicy? policy = null) =>
        new("default", "Answer briefly.", "1", Level: level, ToolPolicy: policy);

    [Fact]
    public async Task A_tool_above_the_sessions_level_is_not_offered_at_all()
    {
        var agent = await OfferedAsync(Spec(), "s-agent");
        Assert.Contains(ProbeSource.Agent, agent);
        Assert.DoesNotContain(ProbeSource.Operator, agent);
        // The loop's own two are always there: waiting and compacting are how a turn ends and how it breathes.
        Assert.Contains("wait", agent);

        var raised = await OfferedAsync(Spec(AccessLevel.Operator), "s-operator");
        Assert.Contains(ProbeSource.Agent, raised);
        Assert.Contains(ProbeSource.Operator, raised);
    }

    [Fact]
    public async Task A_policy_takes_away_what_the_level_would_allow()
    {
        var narrowed = await OfferedAsync(Spec(policy: new SessionToolPolicy(Deny: [ProbeSource.Agent])), "s-narrow");
        Assert.DoesNotContain(ProbeSource.Agent, narrowed);
        // The ceiling still applies to whatever survives the policy.
        Assert.DoesNotContain(ProbeSource.Operator, narrowed);

        var only = await OfferedAsync(Spec(policy: new SessionToolPolicy(Only: [ProbeSource.Operator])), "s-only");
        // "Only" cannot lift the ceiling: it narrows what the level already allows, it does not grant.
        Assert.DoesNotContain(ProbeSource.Agent, only);
        Assert.DoesNotContain(ProbeSource.Operator, only);
    }

    /// <summary>Two tools at different depths, and nothing else: what survives is decided by the level and the policy.</summary>
    private sealed class ProbeSource : ISessionToolSource
    {
        public const string Agent = "probe.agent";
        public const string Operator = "probe.operator";

        public string Id => "test.probe-tools";

        public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<SessionToolOffer> offers =
            [
                new(new ProbeTool(Agent), new ModelToolDefinition(Agent, "An agent's tool.", """{"type":"object"}"""), false),
                new(new ProbeTool(Operator), new ModelToolDefinition(Operator, "An operator's tool.", """{"type":"object"}"""),
                    false, AccessLevel.Operator)
            ];
            return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
        }
    }

    private sealed class ProbeTool(string name) : ISessionTool
    {
        public string Name => name;
        public bool EndsTurn => false;

        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SessionToolResult>.Success(new SessionToolResult("ok", null)));
    }

    private sealed class ScriptedModel : IModelManager
    {
        public List<ModelRequest> Requests { get; } = [];

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Stream()));
        }

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
        public ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public Result<IModelProvider> Get(Address provider) => Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "No providers."));
        public IReadOnlyList<ModelProviderInfo> List() => [];
        public Result RegisterProfile(ModelProfile profile) => Result.Success();
        public Result RemoveProfile(string profileId) => Result.Success();
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }
}
