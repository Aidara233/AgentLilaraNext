using System.Text.Json;
using ALN.Api;
using ALN.Channel;
using ALN.Contracts;
using ALN.Graph;
using ALN.Scripting.Jint;
using ALN.Contracts.Models;
using ALN.Graph.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class ControlPlaneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-control-" + Guid.NewGuid().ToString("N"));

    /// <summary>The type tree the model store asks "is this record a provider's" — the store never guesses by name.</summary>
    private static IModuleTypeCatalog ModelTypes()
    {
        var catalog = new AdapterTypeCatalog();
        catalog.RegisterPackageTypes("test.pack", [new("model.openai-chat", "model"), new("channel.conversation", "channel")]);
        return catalog;
    }
    private readonly List<AuditRecord> _audit = [];
    private readonly SignalLog _signals = SignalLog.Open(null);
    private readonly ControlPlane _plane;

    public ControlPlaneTests()
    {
        _plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(_plane.Register(new("demo.read", "Read demo state.", "demo.read", false),
            (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new { ok = 1 })))).Ok);
        Assert.True(_plane.Register(new("demo.write", "Mutate demo state.", "demo.write", true),
            (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new { ok = 1 })))).Ok);
        Assert.True(_plane.Register(new("demo.explode", "Throws.", "demo.read", false),
            (_, _) => throw new InvalidOperationException("boom")).Ok);
    }

    public void Dispose()
    {
        _signals.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// A person at the API. <see cref="AccessLevel.Full"/> on purpose: these tests are about grants and about the
    /// plane's behaviour, and pinning them to a level would make all of them fail the day a verb's level moves.
    /// The level's own behaviour is tested where it belongs — see the level tests further down.
    /// </summary>
    private static Actor User(params string[] capabilities) =>
        new("alice", ActorKind.User, capabilities, AccessLevel.Full);

    [Fact]
    public async Task Unknown_action_is_invalid_not_denied()
    {
        var result = await _plane.InvokeAsync(Actor.System, "nope", default, default);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.Invalid, result.Error?.Code);
        Assert.Contains(_signals.Snapshot(), r => r.Group == "control" && r.Outcome == "unknown");
    }

    [Fact]
    public async Task Missing_capability_is_denied_and_audited_before_the_handler_runs()
    {
        var result = await _plane.InvokeAsync(User("demo.read"), "demo.write", default, default);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.Denied, result.Error?.Code);
        var record = Assert.Single(_audit);
        Assert.Equal("denied", record.Outcome);
        Assert.Equal("alice", record.Actor);
        Assert.Contains(_signals.Snapshot(), r => r.Group == "control" && r.Outcome == "denied");
    }

    [Fact]
    public async Task Allowed_calls_and_throwing_handlers_are_both_audited()
    {
        var allowed = await _plane.InvokeAsync(User("demo.read"), "demo.read", default, default);
        var failed = await _plane.InvokeAsync(User("demo.read"), "demo.explode", default, default);

        Assert.True(allowed.Ok);
        Assert.False(failed.Ok);
        Assert.Equal(ErrorCode.Unavailable, failed.Error?.Code);
        Assert.Equal(["allowed", "failed"], _audit.Select(a => a.Outcome));
    }

    [Fact]
    public async Task The_subject_is_recorded_and_the_arguments_are_not()
    {
        // "Who changed what" has to survive the call, and an argument object is where a credential lives. So the
        // descriptor names one argument and only that one is written down.
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(plane.Register(new("demo.patch", "Patch a thing.", "demo.write", true,
            ParamsJson: """{"type":"object","properties":{"id":{"type":"string"},"key":{"type":"string"}},"required":["id"]}""",
            Subject: "id"),
            (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new { ok = 1 })))).Ok);

        var ok = await plane.InvokeAsync(User("demo.write"), "demo.patch",
            JsonSerializer.SerializeToElement(new { id = "thing-1", key = "sk-must-not-be-logged" }), default);

        Assert.True(ok.Ok, ok.Error?.Message);
        var signal = Assert.Single(_signals.Snapshot(), r => r.Name == "demo.patch" && r.Outcome == "ok");
        Assert.Equal("thing-1", signal.Detail!["subject"]);
        Assert.DoesNotContain("sk-must-not-be-logged", JsonSerializer.Serialize(signal.Detail));
        Assert.Contains("subject=thing-1", Assert.Single(_audit).Detail);

        // An action that does not say which argument is the subject gets no subject, rather than a guess from the
        // first field that happens to be a string.
        _audit.Clear();
        Assert.True(plane.Register(new("demo.opaque", "No subject.", "demo.write", true),
            (_, _) => ValueTask.FromResult(Result<JsonElement>.Failure(new(ErrorCode.Invalid, "nope")))).Ok);
        Assert.False((await plane.InvokeAsync(User("demo.write"), "demo.opaque",
            JsonSerializer.SerializeToElement(new { id = "thing-2" }), default)).Ok);
        Assert.DoesNotContain("thing-2", JsonSerializer.Serialize(_audit));
    }

    [Fact]
    public void Wildcards_grant_a_group_but_not_everything()
    {
        // Deep on purpose: this test is about the shape of a grant, so the level must not be the reason for a "no".
        static Actor Deep(string id, params string[] capabilities) =>
            new(id, ActorKind.User, capabilities, AccessLevel.Full);

        Assert.True(Deep("ops", "demo.*").Allows("demo.read"));
        Assert.False(Deep("ops", "demo.*").Allows("system.read"));
        Assert.True(Deep("viewer", "*.read").Allows("adapters.read"));
        Assert.False(Deep("viewer", "*.read").Allows("adapters.control"));
        // Only one wildcard is understood; anything else fails closed.
        Assert.False(Deep("odd", "*.*").Allows("demo.read"));
        Assert.True(Actor.System.Allows("anything.at.all"));
    }

    [Fact]
    public async Task Model_listing_never_leaks_configuration_or_keys()
    {
        // A real registry with one live provider: the shape of the answer is the manager's, so asking a fake would pin
        // a second implementation of it.
        var models = NewModelManager();
        Assert.True((await models.CreateAsync("chat", "one", """{"endpoint":"https://api.example.com","apiKey":"sk-real"}""", default)).Ok);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(models.RegisterActions(plane).Ok);

        var result = await plane.InvokeAsync(User("models.read"), ModelActions.List, default, default);

        Assert.True(result.Ok, result.Error?.Message);
        using var document = JsonDocument.Parse(result.Value.GetRawText());
        // The field list is pinned: configuration (which can hold an api key) is not part of
        // ModelProviderInfo at all, and adding it later would fail this test. typeId and inFlight were
        // added for the settings page — neither is a secret, and both are things a list has to show.
        var provider = Assert.Single(document.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Equal(["address", "typeId", "inFlight"], provider.EnumerateObject().Select(p => p.Name));
        Assert.Equal("model.openai-chat#one", provider.GetProperty("address").GetString());
    }

    [Fact]
    public async Task System_info_reports_the_live_graph_in_the_same_words_as_the_receipt()
    {
        var status = new ModuleGraphStatus(9, 8, 1,
            [new(new ModuleId("adapter.file#one"), TimeSpan.FromMilliseconds(200), "Clock")],
            [new(new ModuleId("channel.conversation#chat"), new ModuleId("adapter.file#gone"), BadEdgeReason.MissingModule, "No module answers to it.")]);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), new FakeModels(null), _signals,
            new FakeGraph(), new FakeParameters(), graphStatus: () => status).Ok);

        var info = await Raw(plane);
        Assert.Equal(9, info.GetProperty("instances").GetInt32());
        Assert.Equal(8, info.GetProperty("ready").GetInt32());
        // The number a health check can look at, when nobody is reading the startup log any more.
        Assert.Equal(1, info.GetProperty("degraded").GetInt32());
        // The line, not a re-formatted number: the schedule already decided how to say this.
        Assert.Equal("adapter.file#one 200ms by Clock",
            Assert.Single(info.GetProperty("rhythm").EnumerateArray()).GetString());
        var edge = Assert.Single(info.GetProperty("badEdges").EnumerateArray());
        Assert.Equal("missingModule", edge.GetProperty("reason").GetString());
        Assert.Equal("adapter.file#gone", edge.GetProperty("slave").GetString());

        // Nothing scheduled and a healthy graph is an empty list and a zero, never a missing property: a client must
        // not have to special-case "the core forgot to say".
        status = new(3, 3, 0, [], []);
        var quiet = await Raw(plane);
        Assert.Empty(quiet.GetProperty("rhythm").EnumerateArray());
        Assert.Equal(0, quiet.GetProperty("degraded").GetInt32());
    }

    /// <summary>
    /// A level is a comparison, not a switch. Two refusals stay two answers: "not granted" is a role that needs
    /// wiring, "too shallow" is a job for somebody else, and a caller can only act on the difference.
    /// </summary>
    [Fact]
    public async Task A_level_is_a_comparison_and_the_refusal_says_which_half_failed()
    {
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(plane.Register(new("demo.operator", "Needs an operator.", Capabilities.ModelsControl, true),
            (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new { ok = 1 })))).Ok);
        // One verb may sit deeper than its family: modules.read is an agent's, this one is the kernel's.
        Assert.True(plane.Register(new("demo.kernel", "Needs the kernel.", Capabilities.ModulesRead, true,
                MinLevel: AccessLevel.Full),
            (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new { ok = 1 })))).Ok);

        var agent = new Actor("model", ActorKind.Model, [Actor.Any], AccessLevel.Agent);
        var api = new Actor("alice", ActorKind.User, [Actor.Any], AccessLevel.Operator);

        var refused = await plane.InvokeAsync(agent, "demo.operator", default, default);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Contains("L1 operator", refused.Error.Message);
        Assert.DoesNotContain("lacks capability", refused.Error.Message);

        // The same grant at the right depth goes through.
        Assert.True((await plane.InvokeAsync(api, "demo.operator", default, default)).Ok);

        var kernel = await plane.InvokeAsync(api, "demo.kernel", default, default);
        Assert.Equal(ErrorCode.Denied, kernel.Error!.Code);
        Assert.Contains("L2 full", kernel.Error.Message);
        Assert.True((await plane.InvokeAsync(Actor.System, "demo.kernel", default, default)).Ok);

        // Never granted at all stays the other answer, whatever the level.
        var stranger = new Actor("viewer", ActorKind.User, ["demo.read"], AccessLevel.Full);
        Assert.Contains("lacks capability",
            (await plane.InvokeAsync(stranger, "demo.operator", default, default)).Error!.Message);
    }

    [Fact]
    public void An_interface_nobody_registered_counts_as_the_deepest_there_is()
    {
        Assert.Equal(AccessLevel.Full, AccessLevels.Of("nothing.registered.this"));
        Assert.Equal(AccessLevel.Full, AccessLevels.Of(null));
        Assert.Equal(AccessLevel.Agent, AccessLevels.Of(Capabilities.ChannelsRead));
        Assert.Equal(AccessLevel.Operator, AccessLevels.Of(Capabilities.ModelsControl));
    }

    [Fact]
    public void Every_script_capability_has_a_level()
    {
        // The table spells these names out instead of referencing them (ScriptCapabilities lives one assembly up), so
        // something has to hold the two lists together: a capability the runner accepts but the table never names
        // would silently be kernel-deep — the kind of failure that looks like "it works" until an agent tries it.
        string[] names =
        [
            ScriptCapabilities.SessionRun, ScriptCapabilities.SessionActivate, ScriptCapabilities.ModelComplete,
            ScriptCapabilities.SelfAdapterSend, ScriptCapabilities.SelfAdapterPoll, ScriptCapabilities.ChannelAccept,
            ScriptCapabilities.ChannelRoute, ScriptCapabilities.ChannelReply
        ];
        foreach (var name in names) Assert.Contains(name, AccessLevels.Known);
    }

    private async Task<JsonElement> Raw(ControlPlane plane)
    {
        var result = await plane.InvokeAsync(User("system.read"), CoreActions.SystemInfo, default, default);
        Assert.True(result.Ok, result.Error?.Message);
        using var document = JsonDocument.Parse(result.Value.GetRawText());
        return document.RootElement.Clone();
    }

    [Fact]
    public void A_grant_written_as_graph_still_reaches_the_module_actions()
    {
        // Role files in the wild say graph.control, and the actions those grants guarded are modules.* now. Losing
        // mount rights because nobody edited a json file is exactly the kind of silent downgrade this vocabulary has
        // to not do.
        // An operator's role file, which is the level the grant was written for.
        static Actor Ops(params string[] capabilities) =>
            new("ops", ActorKind.User, capabilities, AccessLevel.Operator);

        Assert.True(Ops("graph.control").Allows(Capabilities.ModulesControl));
        Assert.True(Ops("graph.read").Allows(Capabilities.ModulesRead));
        // One-way on purpose: the new name does not buy the old one, so the forwarding can be deleted later.
        Assert.False(Ops("modules.control").Allows(Capabilities.GraphControl));
    }

    [Fact]
    public async Task The_edge_actions_are_called_modules_edge_now_and_the_old_names_still_answer()
    {
        // The graph registers its own mount verbs, and both names go to the same handler: a client that says graph.list
        // gets the list, not a lesson in naming.
        var graph = new GraphManager(new ModuleCatalog(), new AdapterTypeCatalog(), new JintScriptExecutor(),
            new CollectingAudit(_audit), _signals);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(graph.RegisterActions(plane).Ok);

        var viaOld = await plane.InvokeAsync(Actor.System, GraphActions.DeprecatedList, default, default);
        var viaNew = await plane.InvokeAsync(Actor.System, GraphActions.EdgeList, default, default);

        Assert.True(viaOld.Ok, viaOld.Error?.Message);
        Assert.True(viaNew.Ok, viaNew.Error?.Message);
        // Same handler, not a second implementation that will drift: the answers are identical.
        Assert.Equal(viaNew.Value.GetRawText(), viaOld.Value.GetRawText());
    }

    [Fact]
    public async Task Unloading_a_module_from_the_control_plane_puts_its_directory_in_the_bin_by_default()
    {
        var root = Path.Combine(_root, "graph");
        var types = new ModuleFactoryRegistry();
        Assert.True(types.Register("test.plain", manifest => Result<IKernelModule>.Success(new NoopModule(manifest.Id))).Ok);
        WriteModule(root, "thing");
        var kernel = new Kernel(root, types);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), new FakeModels(null), _signals,
            new FakeGraph(), new FakeParameters(), kernel: kernel).Ok);

        var unloaded = await plane.InvokeAsync(Actor.System, CoreActions.ModulesUnload,
            JsonSerializer.SerializeToElement(new { id = "thing" }), default);

        Assert.True(unloaded.Ok, unloaded.Error?.Message);
        Assert.Null(kernel.Find(new ModuleId("thing")));
        // Archive is the default because "gone" has meant the bin everywhere else here.
        Assert.False(Directory.Exists(Path.Combine(root, "modules", "thing")));
        Assert.NotEmpty(Directory.GetDirectories(Path.Combine(root, "archive", "modules")));

        // Keeping the directory is the explicit choice.
        WriteModule(root, "kept");
        Assert.True((await plane.InvokeAsync(Actor.System, CoreActions.ModulesLoad,
            JsonSerializer.SerializeToElement(new { id = "kept" }), default)).Ok);
        var kept = await plane.InvokeAsync(Actor.System, CoreActions.ModulesUnload,
            JsonSerializer.SerializeToElement(new { id = "kept", archive = false }), default);
        Assert.True(kept.Ok, kept.Error?.Message);
        Assert.True(Directory.Exists(Path.Combine(root, "modules", "kept")));
    }

    [Fact]
    public async Task Listing_modules_lists_the_graph_and_listing_packages_lists_the_packages()
    {
        var root = Path.Combine(_root, "graph");
        var types = new ModuleFactoryRegistry();
        Assert.True(types.Register("test.plain", manifest => Result<IKernelModule>.Success(new NoopModule(manifest.Id))).Ok);
        WriteModule(root, "thing");
        var kernel = new Kernel(root, types);
        Assert.True((await kernel.StartAsync(default)).Ok);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), new FakeModels(null), _signals,
            new FakeGraph(), new FakeParameters(), kernel: kernel).Ok);

        var graph = await plane.InvokeAsync(Actor.System, CoreActions.ModulesList, default, default);
        var packages = await plane.InvokeAsync(Actor.System, CoreActions.PackagesList, default, default);

        Assert.True(graph.Ok, graph.Error?.Message);
        Assert.Equal("thing", Assert.Single(graph.Value.GetProperty("instances").EnumerateArray()).GetProperty("id").GetString());
        Assert.True(packages.Ok, packages.Error?.Message);
        // Two different questions, two different answers: the graph is what runs, the package list is what was loaded.
        Assert.True(packages.Value.TryGetProperty("modules", out _));
        Assert.False(packages.Value.TryGetProperty("instances", out _));
    }

    /// <summary>Writes one module's own file, which is all the kernel reads.</summary>
    private static void WriteModule(string root, string id)
    {
        var directory = Path.Combine(root, "modules", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "module.json"),
            JsonSerializer.Serialize(new { id, type = "test.plain" }));
    }

    private sealed class NoopModule(ModuleId id) : IKernelModule
    {
        public ModuleId Id { get; } = id;
        public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }

    [Fact]
    public void Api_settings_authenticate_by_digest_and_never_store_the_token()
    {
        Directory.CreateDirectory(Path.Combine(_root, "api"));
        var plaintext = "dev-token-123";
        var settings = new ApiSettings(["http://127.0.0.1:5199"],
            new() { ["admin"] = ["*"], ["viewer"] = ["*.read"] },
            [new("alice", ApiSettings.Hash(plaintext), ["viewer"])]);
        File.WriteAllText(Path.Combine(_root, "api", "server.json"), JsonSerializer.Serialize(settings, ApiSettings.JsonOptions));

        var stored = File.ReadAllText(Path.Combine(_root, "api", "server.json"));
        Assert.DoesNotContain(plaintext, stored, StringComparison.Ordinal);

        var loaded = ApiSettings.Load(_root);
        Assert.NotNull(loaded);
        var actor = loaded!.Authenticate(plaintext);
        Assert.NotNull(actor);
        Assert.Equal("alice", actor!.Id);
        Assert.True(actor.Allows("adapters.read"));
        Assert.False(actor.Allows("adapters.control"));
        Assert.Null(loaded.Authenticate("wrong-token"));
        Assert.Null(loaded.Authenticate(null));
    }

    [Fact]
    public void No_settings_file_means_no_listener()
    {
        Assert.Null(ApiSettings.Load(_root));
    }

    [Fact]
    public void Archive_actions_are_in_the_catalogue_and_need_more_than_read()
    {
        // The bin registers its own verbs: same two for every kind, and both writes are dry-run by default.
        var archive = new ALN.Archive.ArchiveManager(_root);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(ALN.Archive.ArchiveActions.Register(plane, archive).Ok);

        foreach (var id in new[] { ALN.Archive.ArchiveActions.Move, ALN.Archive.ArchiveActions.Restore })
        {
            var action = Assert.Single(plane.Catalog, candidate => candidate.Id == id);
            // Taking a conversation out of service must not be a read: a "*.read" viewer may look, not sweep.
            Assert.Equal(Capabilities.ArchiveControl, action.Capability);
            Assert.True(action.Mutating);
            Assert.False(User("*.read").Allows(action.Capability));
            Assert.False(string.IsNullOrWhiteSpace(action.ParamsJson));
        }
        Assert.Equal(Capabilities.ArchiveRead,
            Assert.Single(plane.Catalog, action => action.Id == ALN.Archive.ArchiveActions.List).Capability);
        Assert.DoesNotContain(plane.Catalog, action => action.Id == "sessions.gc");
    }

    [Fact]
    public async Task System_types_lists_what_a_create_form_can_offer()
    {
        var catalog = new AdapterTypeCatalog();
        Assert.True(catalog.RegisterPackageTypes("test.pack", [
            new("adapter.file", "adapter"),
            new("channel.conversation", "channel")
        ]).Ok);

        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), new FakeModels(null),
            _signals, new FakeGraph(), new FakeParameters(), moduleTypes: catalog).Ok);

        var result = await plane.InvokeAsync(User(Capabilities.SystemRead), CoreActions.ModuleTypes, default, default);
        Assert.True(result.Ok, result.Error?.Message);
        var types = result.Value.GetProperty("types").EnumerateArray()
            .Select(item => (Id: item.GetProperty("typeId").GetString(), Parent: item.GetProperty("parent").GetString(),
                Abstract: item.GetProperty("abstract").GetBoolean()))
            .ToList();
        Assert.Contains(types, type => type is { Id: "channel.conversation", Parent: "channel", Abstract: false });
        Assert.Contains(types, type => type is { Id: "adapter", Parent: null, Abstract: true });
        // Sessions are in the catalog too: a mount rule has to be able to name them.
        Assert.Contains(types, type => type.Id == "session.chat");
        // Listing types is a read: a viewer may open the create form, and still be refused the create itself.
        Assert.True(User("*.read").Allows(Capabilities.SystemRead));
    }

    [Fact]
    public void A_type_catalog_answers_what_it_registered()
    {
        var catalog = new AdapterTypeCatalog();
        Assert.Contains(catalog.ListTypes(), type => type is { TypeId: "adapter", IsAbstract: true, ParentTypeId: null });
        Assert.Contains(catalog.ListTypes(), type => type is { TypeId: "session.chat", ParentTypeId: "session", IsAbstract: false });
        Assert.True(catalog.RegisterPackageTypes("test.pack", [new("adapter.file", "adapter"), new("channel.conversation", "channel")]).Ok);
        Assert.Contains(catalog.ListTypes(), type => type is { TypeId: "adapter.file", ParentTypeId: "adapter", IsAbstract: false });
        // Removing the package takes its types back out: a listing that keeps them would offer a type nothing can build.
        Assert.True(catalog.RemovePackageTypes("test.pack").Ok);
        Assert.DoesNotContain(catalog.ListTypes(), type => type.TypeId == "adapter.file");
        Assert.Contains(catalog.ListTypes(), type => type.TypeId == "adapter");
    }

    [Fact]
    public async Task An_instance_the_caller_does_not_name_is_named_after_its_type()
    {
        // C-01: an instance is named when it is created. The caller may name it; otherwise the name is the type's
        // own prefix plus a fresh suffix — two instances never collide, and nothing derives the name from a hash of
        // what the instance happens to be.
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.pack", [new("adapter.file", "adapter")]).Ok);
        // The verbs belong to the Adapter manager now, so this builds the real one over a factory that records what it
        // was asked to make: the naming rule is the manager's, and a fake would be a second implementation of it.
        var factories = new RecordingAdapterFactories();
        var adapters = new AdapterManager(types, new QuietBindings(), factories);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(adapters.RegisterActions(plane).Ok);
        Assert.True(CoreActions.RegisterAll(plane, adapters, new FakeChannels(), new FakeModels(null),
            _signals, new FakeGraph(), new FakeParameters()).Ok);

        var unnamed = JsonSerializer.SerializeToElement(new { type = "adapter.file", direction = "inbound" });
        Assert.True((await plane.InvokeAsync(User(Capabilities.AdaptersControl), AdapterActions.Create, unnamed, default)).Ok);
        Assert.True((await plane.InvokeAsync(User(Capabilities.AdaptersControl), AdapterActions.Create, unnamed, default)).Ok);

        Assert.Equal(2, factories.Created.Count);
        Assert.All(factories.Created, spec => Assert.StartsWith("adapter.file#", spec.Address.ToString(), StringComparison.Ordinal));
        Assert.NotEqual(factories.Created[0].Address, factories.Created[1].Address);

        // A name the caller gives is used as given: "instance" is a name, not a suggestion to be reformatted.
        Assert.True((await plane.InvokeAsync(User(Capabilities.AdaptersControl), AdapterActions.Create,
            JsonSerializer.SerializeToElement(new { type = "adapter.file", instance = "main" }), default)).Ok);
        Assert.Equal("adapter.file#main", factories.Created[2].Address.ToString());
    }

    [Fact]
    public async Task Creating_an_adapter_names_it_after_its_type_and_refuses_what_it_cannot_build()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.pack", [new("adapter.file", "adapter")]).Ok);
        var factories = new RecordingAdapterFactories();
        var adapters = new AdapterManager(types, new QuietBindings(), factories);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(adapters.RegisterActions(plane).Ok);
        Assert.True(CoreActions.RegisterAll(plane, adapters, new FakeChannels(), new FakeModels(null),
            _signals, new FakeGraph(), new FakeParameters()).Ok);

        var created = await plane.InvokeAsync(User(Capabilities.AdaptersControl), AdapterActions.Create,
            JsonSerializer.SerializeToElement(new
            {
                type = "adapter.file", instance = "file-2", direction = "inbound",
                settings = new Dictionary<string, string> { ["mailbox"] = "io/file/file-2" }
            }), default);
        Assert.True(created.Ok, created.Error?.Message);
        var spec = Assert.Single(factories.Created);
        // The address comes from type + instance, so the two can never disagree in the stored spec.
        Assert.Equal("adapter.file#file-2", spec.Address.ToString());
        Assert.Equal("file", spec.Descriptor.PlatformId);   // default: the type's own name
        Assert.Equal("file-2", spec.Descriptor.AccountId);  // default: the instance
        Assert.Equal(AdapterDirection.Inbound, spec.Descriptor.Direction);
        Assert.Equal("io/file/file-2", spec.Settings!["mailbox"]);
        Assert.Equal("adapter.file", spec.TypeId);
        // A freshly created adapter is stopped, and the answer says so rather than leaving the caller to guess.
        using (var answer = JsonDocument.Parse(created.Value.GetRawText()))
            Assert.Contains("adapters.start", answer.RootElement.GetProperty("note").GetString());

        // A direction typo is refused: defaulting it to "both" would quietly turn a reader into a sender.
        var typo = await plane.InvokeAsync(User(Capabilities.AdaptersControl), AdapterActions.Create,
            JsonSerializer.SerializeToElement(new { type = "adapter.file", instance = "file-3", direction = "inboud" }), default);
        Assert.Equal(ErrorCode.Invalid, typo.Error!.Code);
        // A type nothing registered cannot be built, so the form must not be able to ask for it.
        var unknown = await plane.InvokeAsync(User(Capabilities.AdaptersControl), AdapterActions.Create,
            JsonSerializer.SerializeToElement(new { type = "adapter.nope", instance = "x" }), default);
        Assert.Equal(ErrorCode.Invalid, unknown.Error!.Code);
        // A settings value that is not a string is refused rather than stringified into the file.
        var badSetting = await plane.InvokeAsync(User(Capabilities.AdaptersControl), AdapterActions.Create,
            JsonSerializer.SerializeToElement(new { type = "adapter.file", instance = "file-4", settings = new { retry = 3 } }), default);
        Assert.Equal(ErrorCode.Invalid, badSetting.Error!.Code);
        Assert.Single(factories.Created);

        // Creating changes things; reading the list does not.
        Assert.Equal(Capabilities.AdaptersControl, Assert.Single(plane.Catalog, a => a.Id == AdapterActions.Create).Capability);
        Assert.True(Assert.Single(plane.Catalog, a => a.Id == AdapterActions.Destroy).Mutating);
        Assert.All(plane.Catalog.Where(a => a.Id.StartsWith("adapters.", StringComparison.Ordinal)),
            action => Assert.False(string.IsNullOrWhiteSpace(action.ParamsJson)));
        var denied = await plane.InvokeAsync(User(Capabilities.AdaptersRead), AdapterActions.Destroy,
            JsonSerializer.SerializeToElement(new { address = "adapter.file#file-2" }), default);
        Assert.Equal(ErrorCode.Denied, denied.Error!.Code);

        var destroyed = await plane.InvokeAsync(User(Capabilities.AdaptersControl), AdapterActions.Destroy,
            JsonSerializer.SerializeToElement(new { address = "adapter.file#file-2" }), default);
        Assert.True(destroyed.Ok, destroyed.Error?.Message);
        Assert.False(adapters.Get(new Address("adapter", "file", "file-2")).Ok);
    }

    [Fact]
    public async Task Creating_a_channel_takes_its_bindings_and_refuses_half_of_one()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.pack", [new("channel.conversation", "channel")]).Ok);
        // The verb belongs to the Channel module now, so this asks the real manager for it: parsing the arguments is
        // the module's job, and a fake would be a second implementation of the thing being tested.
        var channels = new ChannelManager(new EventQueue(8), new QuietBindings(), new ModuleCatalog(), types: types);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(channels.RegisterActions(plane).Ok);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), channels, new FakeModels(null),
            _signals, new FakeGraph(), new FakeParameters()).Ok);

        var created = await plane.InvokeAsync(User(Capabilities.ChannelsControl), ChannelActions.Create,
            JsonSerializer.SerializeToElement(new
            {
                type = "channel.conversation", instance = "group-1", maxMessages = 25,
                bindings = new[] { new { adapter = "adapter.qq#qq-1", conversation = "group-123" } }
            }), default);
        Assert.True(created.Ok, created.Error?.Message);
        var channel = channels.Get(new Address("channel", "conversation", "group-1"));
        Assert.True(channel.Ok, channel.Error?.Message);
        Assert.Equal("group-1", channel.Value!.Name);          // default: the instance name
        Assert.Equal(25, channel.Value.MaxMessages);
        var binding = Assert.Single(channel.Value.Bindings);
        Assert.Equal("adapter.qq#qq-1", binding.Adapter.ToString());
        Assert.Equal("group-123", binding.PlatformConversationId);

        // Half a binding is not a binding: an adapter without a conversation would bind to nothing.
        var half = await plane.InvokeAsync(User(Capabilities.ChannelsControl), ChannelActions.Create,
            JsonSerializer.SerializeToElement(new
            {
                type = "channel.conversation", instance = "group-2",
                bindings = new[] { new { adapter = "adapter.qq#qq-1" } }
            }), default);
        Assert.Equal(ErrorCode.Invalid, half.Error!.Code);
        // Zero is refused rather than read as "unlimited".
        var zero = await plane.InvokeAsync(User(Capabilities.ChannelsControl), ChannelActions.Create,
            JsonSerializer.SerializeToElement(new { type = "channel.conversation", instance = "group-3", maxMessages = 0 }), default);
        Assert.Equal(ErrorCode.Invalid, zero.Error!.Code);
        Assert.False(channels.Get(new Address("channel", "conversation", "group-2")).Ok);

        var destroyed = await plane.InvokeAsync(User(Capabilities.ChannelsControl), ChannelActions.Destroy,
            JsonSerializer.SerializeToElement(new { address = "channel.conversation#group-1" }), default);
        Assert.True(destroyed.Ok, destroyed.Error?.Message);
        Assert.False(channels.Get(new Address("channel", "conversation", "group-1")).Ok);
    }

    [Fact]
    public async Task A_channel_serves_one_conversation_and_a_second_binding_is_refused_up_front()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.pack", [new("channel.conversation", "channel")]).Ok);
        var channels = new ChannelManager(new EventQueue(8), new QuietBindings(), new ModuleCatalog(), types: types);

        var two = await channels.CreateAsync(new ChannelSpec(new Address("channel", "conversation", "two"), "two",
            [new ChannelBinding(new Address("adapter", "qq", "one"), "a"), new ChannelBinding(new Address("adapter", "qq", "one"), "b")],
            "channel.conversation"), default);

        // Refused here rather than accepted and then unusable: accepting a message had to pick a binding, and sending
        // refused anything but exactly one, so a two-binding channel was a channel that half worked.
        Assert.Equal(ErrorCode.Invalid, two.Error!.Code);
        Assert.Contains("one conversation", two.Error!.Message, StringComparison.OrdinalIgnoreCase);

        // And the ids are the module's own: registered with it, not by the host.
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(channels.RegisterActions(plane).Ok);
        Assert.Equal(
            [ChannelActions.Connect, ChannelActions.Create, ChannelActions.Destroy, ChannelActions.Disconnect,
                ChannelActions.List, ChannelActions.Send, ChannelActions.Start, ChannelActions.Stop],
            plane.Catalog.Select(action => action.Id).ToArray());
        Assert.All(plane.Catalog, action => Assert.False(string.IsNullOrWhiteSpace(action.ParamsJson)));
    }


    [Fact]
    public async Task A_saved_provider_is_written_to_disk_masked_on_the_way_back_and_enough_to_run()
    {
        var store = new FileModelInstanceStore(_root, ModelTypes());
        // The verbs belong to the model manager now, so this asks the real one over a factory that builds a stub
        // provider: what the test is about (the file, the masking, "applied now" vs "restart") is all in the manager.
        var models = NewModelManager();
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(models.RegisterActions(plane, store).Ok);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), models,
            _signals, new FakeGraph(), new FakeParameters()).Ok);

        var saved = await plane.InvokeAsync(User(Capabilities.ModelsControl), ModelActions.ProviderSave,
            JsonSerializer.SerializeToElement(new
            {
                category = "chat", instance = "one",
                configuration = new { endpoint = "https://api.example.com", apiKey = "sk-real" },
                profiles = new[] { new { id = "default", model = "deepseek-flash" } }
            }), default);
        Assert.True(saved.Ok, saved.Error?.Message);
        // Nothing was running, so the provider is built now instead of at the next restart.
        Assert.Equal("now", saved.Value.GetProperty("applied").GetString());
        Assert.Equal("model.openai-chat#one", Assert.Single(models.List()).Address.ToString());
        Assert.Equal("default", Assert.Single(models.ListProfiles()).Id);

        // Reading it back hides the key but keeps the shape an editor needs.
        var read = await plane.InvokeAsync(User(Capabilities.ModelsRead), ModelActions.ProviderGet,
            JsonSerializer.SerializeToElement(new { address = "model.openai-chat#one" }), default);
        Assert.True(read.Ok, read.Error?.Message);
        Assert.True(read.Value.GetProperty("found").GetBoolean());
        Assert.Equal(ModelSecrets.Placeholder, read.Value.GetProperty("configuration").GetProperty("apiKey").GetString());
        Assert.Equal("https://api.example.com", read.Value.GetProperty("configuration").GetProperty("endpoint").GetString());

        // Saving the masked form back must not replace the key with "***".
        var resaved = await plane.InvokeAsync(User(Capabilities.ModelsControl), ModelActions.ProviderSave,
            JsonSerializer.SerializeToElement(new
            {
                category = "chat", instance = "one",
                configuration = new { endpoint = "https://api.example.com", apiKey = "***" }
            }), default);
        Assert.True(resaved.Ok, resaved.Error?.Message);
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "modules", "model.openai-chat#one", "module.json")));
        Assert.Equal("sk-real", stored.RootElement.GetProperty("config").GetProperty("configuration").GetProperty("apiKey").GetString());
        // This one was already running: its configuration lives in memory, so the honest answer is "restart".
        Assert.Equal("restart", resaved.Value.GetProperty("applied").GetString());
    }

    [Fact]
    public async Task Saving_a_provider_is_not_a_read()
    {
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        var models = NewModelManager();
        Assert.True(models.RegisterActions(plane, new FileModelInstanceStore(_root, ModelTypes())).Ok);

        foreach (var id in new[] { ModelActions.ProviderSave, ModelActions.ProviderDestroy,
                     ModelActions.ProfileCreate, ModelActions.ProfileRemove })
        {
            var action = Assert.Single(plane.Catalog, candidate => candidate.Id == id);
            Assert.Equal(Capabilities.ModelsControl, action.Capability);
            Assert.True(action.Mutating);
            Assert.False(User("*.read").Allows(action.Capability));
            Assert.False(string.IsNullOrWhiteSpace(action.ParamsJson));
        }
        Assert.Equal(Capabilities.ModelsRead, Assert.Single(plane.Catalog, a => a.Id == ModelActions.ProviderGet).Capability);
        // Without a store the actions are registered and say so, instead of half-working.
        var noStore = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(NewModelManager().RegisterActions(noStore).Ok);
        var refused = await noStore.InvokeAsync(Actor.System, ModelActions.ProviderGet, default, default);
        Assert.Equal(ErrorCode.Unavailable, refused.Error?.Code);
    }

    /// <summary>A real model registry with one provider kind, which is what every action here needs to resolve a name.</summary>
    private static ModelManager NewModelManager()
    {
        var models = new ModelManager();
        Assert.True(models.RegisterFactory("test.pack", new StubProviderFactory()).Ok);
        return models;
    }

    [Fact]
    public async Task A_prompt_can_be_read_edited_and_the_edit_is_the_copy_that_wins()
    {
        var shipped = Path.Combine(_root, "prompts-shipped");
        var data = Path.Combine(_root, "data", "prompts");
        Directory.CreateDirectory(Path.Combine(shipped, "1身份"));
        File.WriteAllText(Path.Combine(shipped, "1身份", "标准.md"), "运送来的原文");
        var prompts = new PromptLibrary([shipped, data]);

        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(PromptActions.Register(plane, prompts).Ok);

        var read = await plane.InvokeAsync(User(Capabilities.PromptsRead), PromptActions.Get,
            JsonSerializer.SerializeToElement(new { group = "1身份", name = "标准" }), default);
        Assert.True(read.Ok, read.Error?.Message);
        Assert.Equal("运送来的原文", read.Value.GetProperty("text").GetString());
        // The path shown is the winning file, which is the shipped one until somebody edits it.
        Assert.StartsWith(shipped, read.Value.GetProperty("path").GetString(), StringComparison.OrdinalIgnoreCase);

        var written = await plane.InvokeAsync(User(Capabilities.PromptsWrite), PromptActions.Set,
            JsonSerializer.SerializeToElement(new { group = "1身份", name = "标准", text = "改过之后的话" }), default);
        Assert.True(written.Ok, written.Error?.Message);
        // An edit lands in the data root, because that is the root that wins. Writing to the shipped one would
        // produce an edit the loader never reads.
        Assert.StartsWith(data, written.Value.GetProperty("path").GetString(), StringComparison.OrdinalIgnoreCase);

        var edited = await plane.InvokeAsync(User(Capabilities.PromptsRead), PromptActions.Get,
            JsonSerializer.SerializeToElement(new { group = "1身份", name = "标准" }), default);
        Assert.Equal("改过之后的话", edited.Value.GetProperty("text").GetString());

        // Editing is not a read.
        Assert.Equal(Capabilities.PromptsWrite, Assert.Single(plane.Catalog, a => a.Id == PromptActions.Set).Capability);
        Assert.Equal(ErrorCode.Denied, (await plane.InvokeAsync(User(Capabilities.PromptsRead), PromptActions.Set,
            JsonSerializer.SerializeToElement(new { group = "1身份", name = "标准", text = "x" }), default)).Error!.Code);
        // An empty prompt is refused: it is almost always a half-finished edit, and the next turn would answer
        // with no instructions at all.
        Assert.Equal(ErrorCode.Invalid, (await plane.InvokeAsync(User(Capabilities.PromptsWrite), PromptActions.Set,
            JsonSerializer.SerializeToElement(new { group = "1身份", name = "标准", text = "  " }), default)).Error!.Code);
        // A name that is a path is refused rather than written through.
        Assert.Equal(ErrorCode.Invalid, (await plane.InvokeAsync(User(Capabilities.PromptsWrite), PromptActions.Set,
            JsonSerializer.SerializeToElement(new { group = "..", name = "x", text = "y" }), default)).Error!.Code);
    }

    [Fact]
    public async Task A_script_can_be_read_as_loaded()
    {
        var manifest = new ScriptManifest("channel.echo", 2, "channel", MountMode.Manual, ["channel.message.accepted"],
            "onEvent", ["session.activate"], TrustDomain.Trusted);
        var script = new ScriptPackage(manifest, "async function onEvent(event, ctx) { return; }", "abc123");
        // The script verbs live with the script library now: same table, same execution point, registered by the
        // code that owns the catalogue.
        var graph = new FakeGraph(script);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(ScriptActions.Register(plane, graph, catalog: null).Ok);

        var read = await plane.InvokeAsync(User(Capabilities.GraphRead), ScriptActions.Get,
            JsonSerializer.SerializeToElement(new { id = "channel.echo" }), default);
        Assert.True(read.Ok, read.Error?.Message);
        Assert.Equal("async function onEvent(event, ctx) { return; }", read.Value.GetProperty("source").GetString());
        Assert.Equal("onEvent", read.Value.GetProperty("entryPoint").GetString());
        Assert.Equal("manual", read.Value.GetProperty("mount").GetString());
        Assert.True(read.Value.GetProperty("enabled").GetBoolean());
        // Reading a script is not controlling the graph.
        Assert.False(User("*.read").Allows(Capabilities.GraphControl));
        Assert.Equal(ErrorCode.Invalid, (await plane.InvokeAsync(User(Capabilities.GraphRead), ScriptActions.Get,
            JsonSerializer.SerializeToElement(new { id = "nope" }), default)).Error!.Code);
    }

    private sealed class CollectingAudit(List<AuditRecord> records) : IAuditSink
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeParameters : IParameterRegistry
    {
        public Result Register(IParameterTarget target) => Result.Success();
        public Result Unregister(string scope) => Result.Success();
        public IReadOnlyList<ParameterView> List() => [];
        public ValueTask<Result> SetAsync(string scope, string name, string value, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> RestoreAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
    }

    /// <summary>Just enough graph to keep the action catalogue registrable; mounting has its own tests.</summary>
    private sealed class FakeGraph(ScriptPackage? script = null) : IGraphRuntime
    {
        public ValueTask<Result> LoadAsync(IEnumerable<ScriptPackage> packages, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> MountAsync(string scriptId, Address target, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> UnmountAsync(string scriptId, Address target, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public IReadOnlyList<MountedScript> Mounted => [];
        public IReadOnlyList<MountRule> Rules => [];
        public IReadOnlyList<MountRule> PendingRules => [];
        public IReadOnlyList<(ScriptPackage Package, bool Enabled)> Scripts => script is null ? [] : [(script, true)];
        public IReadOnlyList<Address> Instances => [];
        public IReadOnlyList<ScriptIssue> RejectedScripts => [];
        public ValueTask<Result> SetScriptEnabledAsync(string target, bool enabled, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<MountRule>> AddRuleAsync(MountRequest request, CancellationToken ct) =>
            ValueTask.FromResult(Result<MountRule>.Failure(new(ErrorCode.Invalid, "Fake graph does not keep rules.")));
        public ValueTask<Result> RemoveRuleAsync(string ruleId, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DispatchAsync(RoutedEvent routedEvent, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> AttachAsync(Address target, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken ct) => ValueTask.FromResult(Result.Success());
    }

    private sealed class FakeAdapters : IAdapterManager
    {
        public List<AdapterSpec> Created { get; } = [];
        public List<Address> Destroyed { get; } = [];

        public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, CancellationToken ct)
        {
            Created.Add(spec);
            return ValueTask.FromResult(Result<IChatAdapter>.Success(new StubAdapter(spec)));
        }
        public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, Func<AdapterSpec, IChatAdapter> factory, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result> DestroyAsync(Address address, CancellationToken ct)
        {
            Destroyed.Add(address);
            return ValueTask.FromResult(Result.Success());
        }
        public ValueTask<Result> DestroyByTypeAsync(string typeId, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> ForceDestroyByTypeAsync(string typeId, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StartAsync(Address address, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(Address address, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public Result<IChatAdapter> Get(Address address) => Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, "none"));
        public IReadOnlyList<AdapterInfo> List() => [];
        public ValueTask<Result<IReadOnlyList<AdapterInfo>>> SnapshotByTypeAsync(string typeId, CancellationToken ct) =>
            ValueTask.FromResult(Result<IReadOnlyList<AdapterInfo>>.Success([]));
    }

    /// <summary>
    /// A factory registry that makes the adapter asked for and writes down the spec, so a test can read what the
    /// manager decided instead of what a fake manager was told.
    /// </summary>
    private sealed class RecordingAdapterFactories : IAdapterFactoryRegistry
    {
        public List<AdapterSpec> Created { get; } = [];
        private IAdapterFactory? _factory = new StubAdapterFactory("adapter.file");

        public Result Register(string packageId, IAdapterFactory factory)
        {
            _factory = factory;
            return Result.Success();
        }

        public Result BeginDrain(string packageId) => Result.Success();
        public Result Resume(string packageId) => Result.Success();
        public Result RemovePackage(string packageId)
        {
            _factory = null;
            return Result.Success();
        }

        public Result<IChatAdapter> Create(string typeId, AdapterSpec spec)
        {
            if (_factory is null || !string.Equals(_factory.TypeId, typeId, StringComparison.Ordinal))
                return Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, $"No factory for adapter type {typeId}."));
            Created.Add(spec);
            return Result<IChatAdapter>.Success(new StubAdapter(spec));
        }
    }

    private sealed class StubAdapterFactory(string typeId) : IAdapterFactory
    {
        public string TypeId { get; } = typeId;
        public IChatAdapter Create(AdapterSpec spec) => new StubAdapter(spec);
    }

    private sealed class StubAdapter(AdapterSpec spec) : IChatAdapter
    {
        public Address Address => spec.Address;
        public AdapterDescriptor Descriptor => spec.Descriptor;
        public AdapterState State => AdapterState.Stopped;
        public ModuleId Id => new(spec.Address.ToString());
        public ValueTask<Result> StartAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken ct) =>
            ValueTask.FromResult(Result<SendResult>.Success(new(true, "attempt")));
    }

    private sealed class FakeChannels : IChannelManager
    {
        public List<ChannelSpec> Created { get; } = [];
        public List<Address> Destroyed { get; } = [];

        public ValueTask<Result<ChannelInfo>> CreateAsync(ChannelSpec spec, CancellationToken ct)
        {
            Created.Add(spec);
            return ValueTask.FromResult(Result<ChannelInfo>.Success(new ChannelInfo(spec.Address, spec.Name, spec.Bindings,
                true, spec.TypeId, spec.MaxMessages)));
        }
        public ValueTask<Result> StartAsync(Address address, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(Address address, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DestroyAsync(Address address, CancellationToken ct)
        {
            Destroyed.Add(address);
            return ValueTask.FromResult(Result.Success());
        }
        public ValueTask<Result<int>> AcceptAsync(AdapterEnvelope envelope, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result> CompleteAcceptAsync(Address channel, Guid messageId, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> RecordReplyAsync(Address channel, Guid answeredMessageId, string platformConversationId, string text, string? platformMessageId, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<int>> ReplayPendingAsync(CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(0));
        public Result<ChannelInfo> Get(Address address) => Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "none"));
        public IReadOnlyList<ChannelInfo> List() => [];
        public bool IsBound(Address channel, Address adapter, string conversation) => false;
        public Result<ChannelInfo> FindBound(Address adapter, string conversation) => Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "none"));
    }

    private sealed class FakeModels(ModelProviderInfo? provider) : IModelManager
    {
        private readonly List<ModelProviderInfo> _providers = provider is null ? [] : [provider];
        public List<string> Created { get; } = [];
        public List<ModelProfile> Profiles { get; } = [];

        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Result RegisterFactory(string packageId, IModelProviderFactory factory) => Result.Success();
        public Result RemovePackageFactories(string packageId) => Result.Success();
        public IReadOnlyList<ModelProviderTypeInfo> ListTypes() => [new("model.openai-chat", "chat", "OpenAI Chat", ModelCapabilities.Streaming)];
        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result<IModelProvider>> CreateAsync(string category, string instance, string configurationJson, CancellationToken ct)
        {
            Created.Add($"{category}/{instance}");
            var address = new Address("model", "openai-chat", instance);
            var descriptor = new ModelProviderDescriptor("model.openai-chat", category, "OpenAI Chat", ModelCapabilities.Streaming);
            _providers.Add(new ModelProviderInfo(address, descriptor, 0));
            return ValueTask.FromResult(Result<IModelProvider>.Success(new StubProvider(address, descriptor)));
        }
        public ValueTask<Result> DestroyAsync(Address provider, CancellationToken ct)
        {
            _providers.RemoveAll(row => row.Address == provider);
            return ValueTask.FromResult(Result.Success());
        }
        public Result<IModelProvider> Get(Address provider) => Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "none"));
        public IReadOnlyList<ModelProviderInfo> List() => _providers;
        public Result RegisterProfile(ModelProfile profile)
        {
            Profiles.Add(profile);
            return Result.Success();
        }
        public Result RemoveProfile(string profileId)
        {
            Profiles.RemoveAll(profile => profile.Id == profileId);
            return Result.Success();
        }
        public IReadOnlyList<ModelProfile> ListProfiles() => Profiles;
    }

    /// <summary>
    /// The graph port, doing nothing: these tests are about what a manager does before an instance reaches the graph.
    /// </summary>
    private sealed class QuietBindings : IInstanceBindings
    {
        public ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }

    /// <summary>One provider kind, so the registry can turn "chat" into a type id. Builds a provider that never streams.</summary>
    private sealed class StubProviderFactory : IModelProviderFactory
    {
        // The type id is the provider's own ("model.openai-chat"), and the address's "kind" is what follows the dot.
        // Registering the short name here is what a real factory would get wrong first.
        public string TypeId => "model.openai-chat";
        public ModelProviderDescriptor Describe { get; } = new("model.openai-chat", "chat", "Stub", ModelCapabilities.Streaming);

        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<IModelProvider>.Success(new StubProvider(spec.Address, Describe)));
    }

    private sealed class StubProvider(Address address, ModelProviderDescriptor describe) : IModelProvider    {
        public Address Address => address;
        public ModelProviderDescriptor Describe => describe;
        public ModuleId Id => new(address.ToString());
        public IAsyncEnumerable<ModelEvent> CompleteAsync(ModelRequest request, CancellationToken ct) => Empty();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<ModelEvent> Empty()
        {
            await Task.Yield();
            yield break;
        }
    }
}
