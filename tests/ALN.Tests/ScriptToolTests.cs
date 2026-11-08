using System.Runtime.CompilerServices;
using ALN.Channel;
using ALN.Clock;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Graph;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;
using ALN.Scripting.Jint;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// A script that says it can be called, offered to Sessions as a tool.
///
/// Two promises are what this is worth having. The definition is settled when the script loads — one shape, one name, one
/// owner — because a tool name is global to every Session and a duplicate would fail every turn in the house. And a call
/// runs as the script: its own folder's level and capabilities, never the calling Session's, so a tool written by the
/// model cannot become a way for a deeper Session to run it with deeper reach.
/// </summary>
public sealed class ScriptToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-script-tool-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _audit;
    private readonly SignalLog _signals;
    private readonly GraphManager _graph;

    public ScriptToolTests()
    {
        Directory.CreateDirectory(_root);
        _audit = new SqliteStore(Path.Combine(_root, "aln.db"));
        _signals = SignalLog.Open(null);
        var events = new EventQueue(8);
        var adapterTypes = new AdapterTypeCatalog();
        var bindings = new Proxy();
        var adapters = new AdapterManager(adapterTypes, bindings);
        var catalog = new ModuleCatalog();
        catalog.Attach(adapters);
        var channels = new ChannelManager(events, bindings, catalog);
        var sessions = new SessionManager(_root, new QuietModel());
        var graph = new GraphManager(ModuleWiring.With(adapters, channels, new ClockSource(events), sessions), adapterTypes,
            new JintScriptExecutor(), _audit, _signals, state: null);
        bindings.Bind(graph);
        _graph = graph;
    }

    public void Dispose()
    {
        _signals.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _audit.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ScriptPackage Callable(string id, string name, string source,
        AccessLevel level = AccessLevel.Agent, string? description = null) => new(
        new(id, 1, "channel", MountMode.Manual, ["channel.message.accepted"], "onEvent",
            [ScriptCapabilities.ChannelReply], TrustDomain.Trusted, Call: "onCall",
            Tool: new(name, description ?? "Say how many characters you were given.", """{"type":"object"}""")),
        source, Level: level);

    private const string Echo =
        """
        async function onEvent(event, ctx) { }
        async function onCall(args, ctx) {
          await ctx.log.info("called with " + JSON.stringify(args));
          return { ok: true, length: String(args.text ?? "").length };
        }
        """;

    [Fact]
    public async Task A_callable_script_becomes_a_tool_with_the_name_and_the_folder_it_has()
    {
        Assert.True((await _graph.LoadAsync([Callable("tools.length", "tool.length", Echo)], default)).Ok);

        var tool = Assert.Single(_graph.ToolScripts);
        Assert.Equal("tool.length", tool.Tool!.Name);

        var called = await _graph.InvokeToolAsync("tools.length", """{"text":"早安"}""", default);
        Assert.True(called.Ok, called.Error?.Message);
        Assert.Contains("\"length\":2", called.Value);

        // The call is on the record: which script, how big the arguments were, whether it came back.
        Assert.Contains(_audit.ReadAudit(), entry => entry.Action == GraphManager.ToolCallEvent && entry.Outcome == "allowed");
    }

    [Fact]
    public async Task A_script_that_is_not_callable_is_not_a_tool()
    {
        var eventsOnly = new ScriptPackage(
            new("tools.plain", 1, "channel", MountMode.Manual, ["channel.message.accepted"], "onEvent",
                [ScriptCapabilities.ChannelReply], TrustDomain.Trusted), Echo);
        Assert.True((await _graph.LoadAsync([eventsOnly], default)).Ok);

        Assert.Empty(_graph.ToolScripts);
        var called = await _graph.InvokeToolAsync("tools.plain", "{}", default);
        Assert.Equal(ErrorCode.Invalid, called.Error!.Code);
    }

    [Fact]
    public async Task A_name_that_could_collide_is_a_refused_script_not_a_broken_turn()
    {
        // No dot: the name is in the same space as wait, compact and every module's tools, where a duplicate makes
        // ResolveTools fail and takes every turn in every Session down with it. Refusing the file is the only safe answer.
        var unnamed = Callable("tools.bare", "summarise", Echo);
        Assert.True((await _graph.LoadAsync([unnamed], default)).Ok);
        Assert.Empty(_graph.ToolScripts);
        Assert.Contains(_graph.RejectedScripts, issue => issue.Reason.Contains("namespaced", StringComparison.Ordinal));

        // Two scripts, one name: the second is refused and the first keeps working.
        var first = Callable("tools.one", "tool.same", Echo);
        var second = Callable("tools.two", "tool.same", Echo);
        Assert.True((await _graph.LoadAsync([first, second], default)).Ok);
        Assert.Equal("tools.one", Assert.Single(_graph.ToolScripts).Manifest.Id);
        Assert.Contains(_graph.RejectedScripts, issue => issue.Reason.Contains("already offered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_callable_script_with_no_definition_is_refused_and_says_which_of_the_two_ways_is_missing()
    {
        var noDefinition = new ScriptPackage(
            new("tools.silent", 1, "channel", MountMode.Manual, ["channel.message.accepted"], "onEvent",
                [ScriptCapabilities.ChannelReply], TrustDomain.Trusted, Call: "onCall"),
            "async function onEvent(event, ctx) { }\nasync function onCall(args, ctx) { return 1; }");
        Assert.True((await _graph.LoadAsync([noDefinition], default)).Ok);
        Assert.Empty(_graph.ToolScripts);
        Assert.Contains(_graph.RejectedScripts, issue => issue.Reason.Contains("describe()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_definition_can_come_from_the_script_itself()
    {
        var described = new ScriptPackage(
            new("tools.self", 1, "channel", MountMode.Manual, ["channel.message.accepted"], "onEvent",
                [ScriptCapabilities.ChannelReply], TrustDomain.Trusted, Call: "onCall"),
            """
            async function onEvent(event, ctx) { }
            function describe() {
              return { name: "tool.self", description: "Defined next to the code.", parametersJson: "{\"type\":\"object\"}" };
            }
            async function onCall(args, ctx) { return { ok: true }; }
            """);
        Assert.True((await _graph.LoadAsync([described], default)).Ok);

        var tool = Assert.Single(_graph.ToolScripts).Tool!;
        Assert.Equal("tool.self", tool.Name);
        Assert.Equal("Defined next to the code.", tool.Description);
    }

    [Fact]
    public async Task A_tool_that_runs_out_of_time_is_reported_as_maybe_done_rather_than_failed()
    {
        // A script whose one call never finishes. What matters is the wording of the result: the call may already have
        // reached the host, so "failed" would teach the model to retry something that may have happened.
        var slow = new ScriptPackage(
            new("tools.slow", 1, "channel", MountMode.Manual, ["channel.message.accepted"], "onEvent",
                [ScriptCapabilities.ChannelReply], TrustDomain.Trusted, MaxStatements: 50_000,
                TimeoutMilliseconds: 120, Call: "onCall",
                Tool: new("tool.slow", "Never finishes.", """{"type":"object"}""")),
            "async function onEvent(event, ctx) { }\nasync function onCall(args, ctx) { while (true) { } }");
        Assert.True((await _graph.LoadAsync([slow], default)).Ok);

        var called = await _graph.InvokeToolAsync("tools.slow", "{}", default);
        Assert.Equal(ErrorCode.Timeout, called.Error!.Code);

        // And the text the model is shown says so, because that is the only place it can find out.
        var source = new ScriptCallTools(_graph);
        var offers = (await source.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1, Level: AccessLevel.Full),
            default)).Value!;
        var tool = Assert.Single(offers).Tool;
        var result = await tool.ExecuteAsync(new ModelToolCall("c-1", "tool.slow", "{}"), default);
        Assert.True(result.Ok);
        Assert.Contains("可能已经做了", result.Value!.Content);
    }

    [Fact]
    public async Task A_tool_is_offered_with_its_provenance_and_only_to_sessions_deep_enough()
    {
        var trusted = Callable("tools.trusted", "tool.trusted", Echo, AccessLevel.Full);
        var own = Callable("tools.own", "tool.own", Echo, AccessLevel.Agent);
        Assert.True((await _graph.LoadAsync([trusted, own], default)).Ok);

        var source = new ScriptCallTools(_graph);
        var agent = (await source.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1, Level: AccessLevel.Agent),
            default)).Value!;
        // A full-level tool runs with full reach, so an agent-level Session is not offered it — same rule as mounting.
        Assert.Equal(["tool.own"], agent.Select(offer => offer.Definition.Name).ToArray());
        Assert.Contains("来自", agent[0].Definition.Description);
        Assert.Contains("L0 agent", agent[0].Definition.Description);

        var full = (await source.ToolsAsync(new RoundContextRequest("s-2", Guid.NewGuid(), 1, Level: AccessLevel.Full),
            default)).Value!;
        Assert.Equal(["tool.own", "tool.trusted"], full.Select(offer => offer.Definition.Name).Order().ToArray());
    }

    private sealed class Proxy : IInstanceBindings
    {
        private IInstanceBindings? _inner;
        public void Bind(IInstanceBindings inner) => _inner = inner;
        public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) => _inner!.AttachAsync(target, ct);
        public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) => _inner!.DetachAsync(target, ct);
    }

    private sealed class QuietModel : IModelManager
    {
        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Result RegisterFactory(string packageId, IModelProviderFactory factory) => Result.Success();
        public Result RemovePackageFactories(string packageId) => Result.Success();
        public IReadOnlyList<ModelProviderTypeInfo> ListTypes() => [];
        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<Result<IModelProvider>> CreateAsync(string category, string instance, string configurationJson,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
        public Result<IModelProvider> Get(Address provider) => Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "No providers."));
        public IReadOnlyList<ModelProviderInfo> List() => [];
        public Result RegisterProfile(ModelProfile profile) => Result.Success();
        public Result RemoveProfile(string profileId) => Result.Success();
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }
}
