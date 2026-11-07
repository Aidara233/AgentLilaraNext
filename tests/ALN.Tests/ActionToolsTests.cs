using System.Text.Json;
using ALN.Channel;
using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The two tools that put the action table in front of a model.
///
/// What is worth pinning: the model can find a verb and its arguments without reading source, an id that does not
/// exist is a different answer from one it may not call, and the call really goes through the control plane — because
/// that is where the audit and the signal are, and a second execution point would quietly lose one of them.
/// </summary>
public sealed class ActionToolsTests : IDisposable
{
    private readonly List<AuditRecord> _audit = [];
    private readonly SignalLog _signals = SignalLog.Open(null);

    public void Dispose() => _signals.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class QuietBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }

    private ControlPlane PlaneWithChannels(out ChannelManager channels)
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.pack", [new("channel.conversation", "channel")]).Ok);
        channels = new ChannelManager(new EventQueue(8), new QuietBindings(), new ModuleCatalog(), types: types);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(channels.RegisterActions(plane).Ok);
        return plane;
    }

    private static async Task<ISessionTool> ToolAsync(ActionTools tools, string name)
    {
        var offers = await tools.ToolsAsync(new RoundContextRequest("s", Guid.NewGuid(), 1), default);
        Assert.True(offers.Ok, offers.Error?.Message);
        return offers.Value!.Single(offer => offer.Definition.Name == name).Tool;
    }

    private static string Text(Result<SessionToolResult> result)
    {
        Assert.True(result.Ok, result.Error?.Message);
        return result.Value!.Content;
    }

    [Fact]
    public async Task The_list_names_every_action_with_the_arguments_it_takes()
    {
        var plane = PlaneWithChannels(out _);
        var tools = new ActionTools(plane);
        var list = await ToolAsync(tools, ActionTools.ListToolName);

        var all = Text(await list.ExecuteAsync(new("c1", ActionTools.ListToolName, "{}"), default));
        // The ids are the table's, and the arguments come from each action's own schema — one description, not two.
        Assert.Contains(ChannelActions.Create, all);
        Assert.Contains("args: type, bindings", all);
        Assert.Contains("changes state", all);
        Assert.Contains(ChannelActions.List, all);
        Assert.Contains("read-only", all);

        // A prefix narrows it; a prefix nothing matches says so instead of pretending the table is empty.
        var narrowed = Text(await list.ExecuteAsync(new("c2", ActionTools.ListToolName, """{"prefix":"channels."}"""), default));
        Assert.DoesNotContain("system.", narrowed);
        Assert.Contains(ChannelActions.Destroy, narrowed);
        var none = Text(await list.ExecuteAsync(new("c3", ActionTools.ListToolName, """{"prefix":"nope."}"""), default));
        Assert.Contains("No action starts with", none);
        Assert.Contains("list them all with no prefix", none);

        var schemas = Text(await list.ExecuteAsync(new("c4", ActionTools.ListToolName,
            """{"prefix":"channels.create","schemas":true}"""), default));
        Assert.Contains("\"maxMessages\"", schemas);
    }

    [Fact]
    public async Task The_list_names_the_level_of_the_actions_this_caller_cannot_reach()
    {
        // An agent holds the whole table and still cannot reach all of it. The list has to say which lines are somebody
        // else's, or the model spends rounds discovering that one denial at a time.
        var plane = PlaneWithChannels(out _);
        Assert.True(plane.Register(new("test.deep", "Guarded.", Capabilities.ModelsControl, true,
                MinLevel: AccessLevel.Operator),
            (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new { ok = 1 })))).Ok);

        var agent = await ToolAsync(new ActionTools(plane, AccessLevel.Agent), ActionTools.ListToolName);
        var all = Text(await agent.ExecuteAsync(new("c1", ActionTools.ListToolName, "{}"), default));
        Assert.Contains("needs L1 operator", all);
        // A verb that is an agent's own says nothing about levels: the marker has to be rare to be read.
        var mine = Text(await agent.ExecuteAsync(new("c2", ActionTools.ListToolName, """{"prefix":"channels.list"}"""), default));
        Assert.DoesNotContain("needs L", mine);

        // One level up, nothing is out of reach, so nothing is labelled — the same table, read by a different caller.
        var deeper = await ToolAsync(new ActionTools(plane, AccessLevel.Operator), ActionTools.ListToolName);
        var wider = Text(await deeper.ExecuteAsync(new("c3", ActionTools.ListToolName, "{}"), default));
        Assert.DoesNotContain("needs L1 operator", wider);
        Assert.Contains("test.deep", wider);
    }

    [Fact]
    public async Task Calling_goes_through_the_control_plane_and_reports_what_happened()
    {
        var plane = PlaneWithChannels(out var channels);
        var tools = new ActionTools(plane);
        var call = await ToolAsync(tools, ActionTools.CallToolName);

        var created = Text(await call.ExecuteAsync(new("c1", ActionTools.CallToolName,
            """{"action":"ACTION","args":{"type":"channel.conversation","instance":"probe-a","bindings":[{"adapter":"adapter.file#file-1","conversation":"probe-a"}]}}"""
                .Replace("ACTION", ChannelActions.Create, StringComparison.Ordinal)),
            default));

        Assert.StartsWith("ok: " + ChannelActions.Create, created);
        Assert.True(channels.Get(new Address("channel", "conversation", "probe-a")).Ok);
        // The record is the half that has to survive: who called it, and against what.
        var record = Assert.Single(_audit, entry => entry.Action == ChannelActions.Create);
        Assert.Equal(ActionTools.ActorId, record.Actor);
        Assert.Contains("subject=probe-a", record.Detail);
        var signal = Assert.Single(_signals.Snapshot(), row => row.Name == ChannelActions.Create && row.Outcome == "ok");
        Assert.Equal(ActionTools.ActorId, signal.From);
        Assert.Equal("probe-a", signal.Detail!["subject"]);
    }

    [Fact]
    public async Task Unknown_denied_and_invalid_are_three_different_answers()
    {
        var plane = PlaneWithChannels(out _);
        // An action the model may not call: a read grant is not the whole table.
        Assert.True(plane.Register(new("test.control", "Guarded.", Capabilities.ModulesControl, true),
            (_, _) => ValueTask.FromResult(Result<JsonElement>.Failure(new(ErrorCode.Invalid, "never reached")))).Ok);
        var restricted = new ActionTools(new ControlPlaneWithGrant(plane, [Capabilities.ChannelsControl, Capabilities.ChannelsRead]));
        var call = await ToolAsync(restricted, ActionTools.CallToolName);

        var unknown = Text(await call.ExecuteAsync(new("c1", ActionTools.CallToolName, """{"action":"nope.nothing"}"""), default));
        Assert.StartsWith("error: nope.nothing", unknown);
        Assert.Contains("code: invalid", unknown);

        var denied = Text(await call.ExecuteAsync(new("c2", ActionTools.CallToolName,
            """{"action":"test.control","args":{}}"""), default));
        Assert.Contains("code: denied", denied);
        Assert.Contains("lacks capability", denied);

        var invalid = Text(await call.ExecuteAsync(new("c3", ActionTools.CallToolName,
            """{"action":"channels.create","args":{"type":"channel.conversation"}}"""), default));
        Assert.Contains("code: invalid", invalid);
        Assert.Contains("actions_list", invalid);
    }

    /// <summary>A plane with the same table but a narrower actor, for the one test that needs a refusal.</summary>
    private sealed class ControlPlaneWithGrant(IControlPlane inner, IReadOnlyList<string> granted) : IControlPlane
    {
        public IReadOnlyList<ActionDescriptor> Catalog => inner.Catalog;
        public Result Register(ActionDescriptor descriptor, ActionHandler handler) => inner.Register(descriptor, handler);

        public ValueTask<Result<JsonElement>> InvokeAsync(Actor actor, string action, JsonElement arguments,
            CancellationToken cancellationToken) =>
            // The real plane decides with whatever actor it is handed; this hands it a smaller one, so the refusal and
            // its record are produced by the same code the model would meet.
            inner.InvokeAsync(actor with { Capabilities = granted }, action, arguments, cancellationToken);
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
