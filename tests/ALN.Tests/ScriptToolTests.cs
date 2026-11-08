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
        // A control plane with real session verbs on it: the authority a tool runs at is only observable through an
        // action that would refuse a shallower caller, so the table has to exist here.
        var plane = new ControlPlane(_audit, _signals);
        Assert.True(sessions.RegisterActions(plane).Ok);
        Assert.True(graph.RegisterActions(plane).Ok);
        _graph = graph;
    }

    public void Dispose()
    {
        _signals.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _audit.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ScriptPackage Callable(string id, string name, string source,
        AccessLevel level = AccessLevel.Agent, string? description = null, AccessLevel? access = null) => new(
        new(id, 1, "channel", MountMode.Manual, ["channel.message.accepted"], "onEvent",
            [ScriptCapabilities.ChannelReply], TrustDomain.Trusted, Call: "onCall",
            Tool: new(name, description ?? "Say how many characters you were given.", """{"type":"object"}""",
                access ?? AccessLevel.Agent)),
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
    public async Task A_tool_is_opened_to_whoever_its_author_opened_it_to()
    {
        // Two tools from one person: the basic one open to everybody, the deep one kept for callers at least as deep as
        // its own level. Both are the author's decision — that is the knob, not the caller's level.
        var openToAll = Callable("tools.open", "tool.open", Echo, AccessLevel.Full, access: AccessLevel.Agent);
        var keptDeep = Callable("tools.kept", "tool.kept", Echo, AccessLevel.Full, access: AccessLevel.Full);
        var own = Callable("tools.own", "tool.own", Echo, AccessLevel.Agent);
        Assert.True((await _graph.LoadAsync([openToAll, keptDeep, own], default)).Ok);

        var source = new ScriptCallTools(_graph);
        var atAgent = (await source.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1, Level: AccessLevel.Agent),
            default)).Value!;
        Assert.Equal(["tool.open", "tool.own"], atAgent.Select(offer => offer.Definition.Name).Order().ToArray());

        var atFull = (await source.ToolsAsync(new RoundContextRequest("s-2", Guid.NewGuid(), 1, Level: AccessLevel.Full),
            default)).Value!;
        Assert.Equal(["tool.kept", "tool.open", "tool.own"],
            atFull.Select(offer => offer.Definition.Name).Order().ToArray());

        // And the definition says who is doing the work and who may ask — the two facts a caller needs.
        var deep = atAgent.Single(offer => offer.Definition.Name == "tool.open");
        Assert.Contains("来自", deep.Definition.Description);
        Assert.Contains("L2 full", deep.Definition.Description);
        Assert.Contains("不按调用它的会话", deep.Definition.Description);
        Assert.Contains("可调用者", deep.Definition.Description);
    }

    [Fact]
    public async Task A_maker_cannot_open_a_tool_further_than_its_own_level()
    {
        // Narrowing the audience is not granting anything — but the tool's own permission is capped by its maker in both
        // directions, and a file that says otherwise is a file to fix, not a ceiling to guess at.
        var overreaching = Callable("tools.cheeky", "tool.cheeky", Echo, AccessLevel.Agent, access: AccessLevel.Full);
        Assert.True((await _graph.LoadAsync([overreaching], default)).Ok);

        Assert.Empty(_graph.ToolScripts);
        Assert.Contains(_graph.RejectedScripts, issue => issue.Reason.Contains("never above", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tool_runs_at_the_level_of_whoever_made_it_and_that_level_bounds_what_it_is_made_of()
    {
        // sessions.setLevel is an operator's verb. A tool built by a person reaches it — the call gets past the gate and
        // fails on its own arguments ("invalid", not "denied"). A tool built by the model cannot even be built out of
        // that capability: the loader refuses the file, which is why "the tool's interfaces never exceed its maker's" is
        // a property of the catalogue rather than a check at call time.
        const string Ask = """
            async function onEvent(event, ctx) { }
            async function onCall(args, ctx) {
              return await ctx.actions.call("sessions.setLevel", { id: "s-no-such-session", level: "operator" });
            }
            """;
        static ScriptPackage MadeBy(string id, AccessLevel level) => new(
            new(id, 1, "channel", MountMode.Manual, ["channel.message.accepted"], "onEvent",
                [ScriptCapabilities.ChannelReply, Capabilities.SessionsControl], TrustDomain.Trusted, Call: "onCall",
                Tool: new("tool." + id, "Ask for a level change.", """{"type":"object"}""")),
            Ask, Level: level);

        Assert.True((await _graph.LoadAsync([MadeBy("deep", AccessLevel.Full)], default)).Ok);
        var deep = await _graph.InvokeToolAsync("deep", "{}", default);
        Assert.True(deep.Ok, deep.Error?.Message);
        Assert.Contains("invalid", deep.Value, StringComparison.OrdinalIgnoreCase);

        Assert.True((await _graph.LoadAsync([MadeBy("shallow", AccessLevel.Agent)], default)).Ok);
        Assert.DoesNotContain(_graph.ToolScripts, package => package.Manifest.Id == "shallow");
        Assert.Contains(_graph.RejectedScripts, issue => issue.Reason.Contains("folder it came from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manifest_file_reaches_the_graph_with_its_tool_fields_intact()
    {
        // The live check found this one: the loader read `call` and `tool` out of the JSON and then built the manifest
        // without them, so every callable script quietly loaded as an ordinary one. Nothing failed — which is exactly why
        // the test walks the file path rather than a hand-built package.
        var directory = Path.Combine(_root, "scripts", "trusted", "tools.fromfile");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), """
            {
              "id": "tools.fromfile",
              "version": 1,
              "targetType": "channel",
              "mount": "manual",
              "events": ["channel.message.accepted"],
              "entryPoint": "onEvent",
              "capabilities": [],
              "trustDomain": "trusted",
              "call": "onCall",
              "tool": { "name": "tool.fromfile", "description": "From a file.", "parametersJson": "{\"type\":\"object\"}" }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(directory, "main.js"),
            "async function onEvent(event, ctx) { }\nasync function onCall(args, ctx) { return { ok: true }; }");

        var read = await new JsonScriptCatalog(_root, _root).LoadAsync(default);
        Assert.True(read.Ok, read.Error?.Message);
        var package = Assert.Single(read.Value!, entry => entry.Manifest.Id == "tools.fromfile");
        Assert.Equal("onCall", package.Manifest.Call);
        Assert.Equal("tool.fromfile", package.Manifest.Tool!.Name);

        // And the graph takes it the rest of the way: resolved, offered, callable.
        Assert.True((await _graph.LoadAsync([package], default)).Ok);
        Assert.Equal("tool.fromfile", Assert.Single(_graph.ToolScripts).Tool!.Name);
        var called = await _graph.InvokeToolAsync("tools.fromfile", "{}", default);
        Assert.True(called.Ok, called.Error?.Message);
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
