using System.Text.Json;
using ALN.Api;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Graph.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class ControlPlaneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-control-" + Guid.NewGuid().ToString("N"));
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

    private static Actor User(params string[] capabilities) => new("alice", ActorKind.User, capabilities);

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
    public void Wildcards_grant_a_group_but_not_everything()
    {
        Assert.True(new Actor("ops", ActorKind.User, ["demo.*"]).Allows("demo.read"));
        Assert.False(new Actor("ops", ActorKind.User, ["demo.*"]).Allows("system.read"));
        Assert.True(new Actor("viewer", ActorKind.User, ["*.read"]).Allows("adapters.read"));
        Assert.False(new Actor("viewer", ActorKind.User, ["*.read"]).Allows("adapters.control"));
        // Only one wildcard is understood; anything else fails closed.
        Assert.False(new Actor("odd", ActorKind.User, ["*.*"]).Allows("demo.read"));
        Assert.True(Actor.System.Allows("anything.at.all"));
    }

    [Fact]
    public async Task Model_listing_never_leaks_configuration_or_keys()
    {
        var models = new FakeModels(new ModelProviderInfo(new Address("model", "openai-chat", "one"),
            new ModelProviderDescriptor("chat", "openai-chat", "OpenAI", ModelCapabilities.Streaming), 0));
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), models, _signals, new FakeGraph(), new FakeParameters()).Ok);

        var result = await plane.InvokeAsync(User("models.read"), CoreActions.ModelsList, default, default);

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
    public async Task Archive_actions_are_in_the_catalogue_and_need_more_than_read()
    {
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        var provider = new ModelProviderInfo(new Address("model", "openai-chat", "one"),
            new ModelProviderDescriptor("chat", "openai-chat", "OpenAI", ModelCapabilities.Streaming), 0);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), new FakeModels(provider),
            _signals, new FakeGraph(), new FakeParameters()).Ok);

        foreach (var id in new[] { CoreActions.ArchiveMove, CoreActions.ArchiveRestore })
        {
            var action = Assert.Single(plane.Catalog, candidate => candidate.Id == id);
            // Taking a conversation out of service must not be a read: a "*.read" viewer may look, not sweep.
            Assert.Equal(Capabilities.ArchiveControl, action.Capability);
            Assert.True(action.Mutating);
            Assert.False(User("*.read").Allows(action.Capability));
        }
        Assert.Equal(Capabilities.ArchiveRead, Assert.Single(plane.Catalog, action => action.Id == CoreActions.ArchiveList).Capability);
        Assert.DoesNotContain(plane.Catalog, action => action.Id == "sessions.gc");

        // Without an archive module the actions are registered but honest about being unavailable.
        var refused = await plane.InvokeAsync(Actor.System, CoreActions.ArchiveList, default, default);
        Assert.Equal(ErrorCode.Unavailable, refused.Error?.Code);
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
    public async Task Creating_an_adapter_names_it_after_its_type_and_refuses_what_it_cannot_build()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.pack", [new("adapter.file", "adapter")]).Ok);
        var adapters = new FakeAdapters();
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(plane, adapters, new FakeChannels(), new FakeModels(null),
            _signals, new FakeGraph(), new FakeParameters(), moduleTypes: types).Ok);

        var created = await plane.InvokeAsync(User(Capabilities.AdaptersControl), CoreActions.AdaptersCreate,
            JsonSerializer.SerializeToElement(new
            {
                type = "adapter.file", instance = "file-2", direction = "inbound",
                settings = new Dictionary<string, string> { ["mailbox"] = "io/file/file-2" }
            }), default);
        Assert.True(created.Ok, created.Error?.Message);
        var spec = Assert.Single(adapters.Created);
        // The address comes from type + instance, so the two can never disagree in the stored spec.
        Assert.Equal("adapter.file#file-2", spec.Address.ToString());
        Assert.Equal("file", spec.Descriptor.PlatformId);   // default: the type's own name
        Assert.Equal("file-2", spec.Descriptor.AccountId);  // default: the instance
        Assert.Equal(AdapterDirection.Inbound, spec.Descriptor.Direction);
        Assert.Equal("io/file/file-2", spec.Settings!["mailbox"]);
        Assert.Equal("adapter.file", spec.TypeId);

        // A direction typo is refused: defaulting it to "both" would quietly turn a reader into a sender.
        var typo = await plane.InvokeAsync(User(Capabilities.AdaptersControl), CoreActions.AdaptersCreate,
            JsonSerializer.SerializeToElement(new { type = "adapter.file", instance = "file-3", direction = "inboud" }), default);
        Assert.Equal(ErrorCode.Invalid, typo.Error!.Code);
        // A type nothing registered cannot be built, so the form must not be able to ask for it.
        var unknown = await plane.InvokeAsync(User(Capabilities.AdaptersControl), CoreActions.AdaptersCreate,
            JsonSerializer.SerializeToElement(new { type = "adapter.nope", instance = "x" }), default);
        Assert.Equal(ErrorCode.Invalid, unknown.Error!.Code);
        // A settings value that is not a string is refused rather than stringified into the file.
        var badSetting = await plane.InvokeAsync(User(Capabilities.AdaptersControl), CoreActions.AdaptersCreate,
            JsonSerializer.SerializeToElement(new { type = "adapter.file", instance = "file-4", settings = new { retry = 3 } }), default);
        Assert.Equal(ErrorCode.Invalid, badSetting.Error!.Code);
        Assert.Single(adapters.Created);

        // Creating changes things; reading the list does not.
        Assert.Equal(Capabilities.AdaptersControl, Assert.Single(plane.Catalog, a => a.Id == CoreActions.AdaptersCreate).Capability);
        Assert.True(Assert.Single(plane.Catalog, a => a.Id == CoreActions.AdaptersDestroy).Mutating);
        var denied = await plane.InvokeAsync(User(Capabilities.AdaptersRead), CoreActions.AdaptersDestroy,
            JsonSerializer.SerializeToElement(new { address = "adapter.file#file-2" }), default);
        Assert.Equal(ErrorCode.Denied, denied.Error!.Code);

        var destroyed = await plane.InvokeAsync(User(Capabilities.AdaptersControl), CoreActions.AdaptersDestroy,
            JsonSerializer.SerializeToElement(new { address = "adapter.file#file-2" }), default);
        Assert.True(destroyed.Ok, destroyed.Error?.Message);
        Assert.Equal("adapter.file#file-2", Assert.Single(adapters.Destroyed).ToString());
    }

    [Fact]
    public async Task Creating_a_channel_takes_its_bindings_and_refuses_half_of_one()
    {
        var types = new AdapterTypeCatalog();
        Assert.True(types.RegisterPackageTypes("test.pack", [new("channel.conversation", "channel")]).Ok);
        var channels = new FakeChannels();
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), channels, new FakeModels(null),
            _signals, new FakeGraph(), new FakeParameters(), moduleTypes: types).Ok);

        var created = await plane.InvokeAsync(User(Capabilities.ChannelsControl), CoreActions.ChannelsCreate,
            JsonSerializer.SerializeToElement(new
            {
                type = "channel.conversation", instance = "group-1", maxMessages = 25,
                bindings = new[] { new { adapter = "adapter.qq#qq-1", conversation = "group-123" } }
            }), default);
        Assert.True(created.Ok, created.Error?.Message);
        var spec = Assert.Single(channels.Created);
        Assert.Equal("channel.conversation#group-1", spec.Address.ToString());
        Assert.Equal("group-1", spec.Name);          // default: the instance name
        Assert.Equal(25, spec.MaxMessages);
        var binding = Assert.Single(spec.Bindings);
        Assert.Equal("adapter.qq#qq-1", binding.Adapter.ToString());
        Assert.Equal("group-123", binding.PlatformConversationId);

        // Half a binding is not a binding: an adapter without a conversation would bind to nothing.
        var half = await plane.InvokeAsync(User(Capabilities.ChannelsControl), CoreActions.ChannelsCreate,
            JsonSerializer.SerializeToElement(new
            {
                type = "channel.conversation", instance = "group-2",
                bindings = new[] { new { adapter = "adapter.qq#qq-1" } }
            }), default);
        Assert.Equal(ErrorCode.Invalid, half.Error!.Code);
        // Zero is refused rather than read as "unlimited".
        var zero = await plane.InvokeAsync(User(Capabilities.ChannelsControl), CoreActions.ChannelsCreate,
            JsonSerializer.SerializeToElement(new { type = "channel.conversation", instance = "group-3", maxMessages = 0 }), default);
        Assert.Equal(ErrorCode.Invalid, zero.Error!.Code);
        Assert.Single(channels.Created);

        var destroyed = await plane.InvokeAsync(User(Capabilities.ChannelsControl), CoreActions.ChannelsDestroy,
            JsonSerializer.SerializeToElement(new { address = "channel.conversation#group-1" }), default);
        Assert.True(destroyed.Ok, destroyed.Error?.Message);
        Assert.Equal("channel.conversation#group-1", Assert.Single(channels.Destroyed).ToString());
    }

    [Fact]
    public async Task A_saved_provider_is_written_to_disk_masked_on_the_way_back_and_enough_to_run()
    {
        var store = new FileModelInstanceStore(Path.Combine(_root, "models"));
        var models = new FakeModels(null);
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), models,
            _signals, new FakeGraph(), new FakeParameters(), modelInstances: store).Ok);

        var saved = await plane.InvokeAsync(User(Capabilities.ModelsControl), CoreActions.ModelsProviderSave,
            JsonSerializer.SerializeToElement(new
            {
                category = "chat", instance = "one",
                configuration = new { endpoint = "https://api.example.com", apiKey = "sk-real" },
                profiles = new[] { new { id = "default", model = "deepseek-flash" } }
            }), default);
        Assert.True(saved.Ok, saved.Error?.Message);
        // Nothing was running, so the provider is built now instead of at the next restart.
        Assert.Equal("now", saved.Value.GetProperty("applied").GetString());
        Assert.Equal("now", saved.Value.GetProperty("applied").GetString());
        Assert.Equal("chat/one", Assert.Single(models.Created));
        Assert.Equal("default", Assert.Single(models.Profiles).Id);

        // Reading it back hides the key but keeps the shape an editor needs.
        var read = await plane.InvokeAsync(User(Capabilities.ModelsRead), CoreActions.ModelsProviderGet,
            JsonSerializer.SerializeToElement(new { address = "model.openai-chat#one" }), default);
        Assert.True(read.Ok, read.Error?.Message);
        Assert.True(read.Value.GetProperty("found").GetBoolean());
        Assert.Equal(ModelSecrets.Placeholder, read.Value.GetProperty("configuration").GetProperty("apiKey").GetString());
        Assert.Equal("https://api.example.com", read.Value.GetProperty("configuration").GetProperty("endpoint").GetString());

        // Saving the masked form back must not replace the key with "***".
        models.Profiles.Clear();
        var resaved = await plane.InvokeAsync(User(Capabilities.ModelsControl), CoreActions.ModelsProviderSave,
            JsonSerializer.SerializeToElement(new
            {
                category = "chat", instance = "one",
                configuration = new { endpoint = "https://api.example.com", apiKey = "***" }
            }), default);
        Assert.True(resaved.Ok, resaved.Error?.Message);
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "models", "chat", "one.json")));
        Assert.Equal("sk-real", stored.RootElement.GetProperty("configuration").GetProperty("apiKey").GetString());
        // This one was already running: its configuration lives in memory, so the honest answer is "restart".
        Assert.Equal("restart", resaved.Value.GetProperty("applied").GetString());
    }

    [Fact]
    public async Task Saving_a_provider_is_not_a_read()
    {
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), new FakeModels(null),
            _signals, new FakeGraph(), new FakeParameters(),
            modelInstances: new FileModelInstanceStore(Path.Combine(_root, "models"))).Ok);

        foreach (var id in new[] { CoreActions.ModelsProviderSave, CoreActions.ModelsProviderDestroy,
                     CoreActions.ModelsProfileCreate, CoreActions.ModelsProfileRemove })
        {
            var action = Assert.Single(plane.Catalog, candidate => candidate.Id == id);
            Assert.Equal(Capabilities.ModelsControl, action.Capability);
            Assert.True(action.Mutating);
            Assert.False(User("*.read").Allows(action.Capability));
        }
        Assert.Equal(Capabilities.ModelsRead, Assert.Single(plane.Catalog, a => a.Id == CoreActions.ModelsProviderGet).Capability);
        // Without a store the actions are registered and say so, instead of half-working.
        var noStore = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(noStore, new FakeAdapters(), new FakeChannels(), new FakeModels(null),
            _signals, new FakeGraph(), new FakeParameters()).Ok);
        var refused = await noStore.InvokeAsync(Actor.System, CoreActions.ModelsProviderGet, default, default);
        Assert.Equal(ErrorCode.Unavailable, refused.Error?.Code);
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
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), new FakeModels(null),
            _signals, new FakeGraph(), new FakeParameters(), prompts: prompts).Ok);

        var read = await plane.InvokeAsync(User(Capabilities.PromptsRead), CoreActions.PromptsGet,
            JsonSerializer.SerializeToElement(new { group = "1身份", name = "标准" }), default);
        Assert.True(read.Ok, read.Error?.Message);
        Assert.Equal("运送来的原文", read.Value.GetProperty("text").GetString());
        // The path shown is the winning file, which is the shipped one until somebody edits it.
        Assert.StartsWith(shipped, read.Value.GetProperty("path").GetString(), StringComparison.OrdinalIgnoreCase);

        var written = await plane.InvokeAsync(User(Capabilities.PromptsWrite), CoreActions.PromptsSet,
            JsonSerializer.SerializeToElement(new { group = "1身份", name = "标准", text = "改过之后的话" }), default);
        Assert.True(written.Ok, written.Error?.Message);
        // An edit lands in the data root, because that is the root that wins. Writing to the shipped one would
        // produce an edit the loader never reads.
        Assert.StartsWith(data, written.Value.GetProperty("path").GetString(), StringComparison.OrdinalIgnoreCase);

        var edited = await plane.InvokeAsync(User(Capabilities.PromptsRead), CoreActions.PromptsGet,
            JsonSerializer.SerializeToElement(new { group = "1身份", name = "标准" }), default);
        Assert.Equal("改过之后的话", edited.Value.GetProperty("text").GetString());

        // Editing is not a read.
        Assert.Equal(Capabilities.PromptsWrite, Assert.Single(plane.Catalog, a => a.Id == CoreActions.PromptsSet).Capability);
        Assert.Equal(ErrorCode.Denied, (await plane.InvokeAsync(User(Capabilities.PromptsRead), CoreActions.PromptsSet,
            JsonSerializer.SerializeToElement(new { group = "1身份", name = "标准", text = "x" }), default)).Error!.Code);
        // An empty prompt is refused: it is almost always a half-finished edit, and the next turn would answer
        // with no instructions at all.
        Assert.Equal(ErrorCode.Invalid, (await plane.InvokeAsync(User(Capabilities.PromptsWrite), CoreActions.PromptsSet,
            JsonSerializer.SerializeToElement(new { group = "1身份", name = "标准", text = "  " }), default)).Error!.Code);
        // A name that is a path is refused rather than written through.
        Assert.Equal(ErrorCode.Invalid, (await plane.InvokeAsync(User(Capabilities.PromptsWrite), CoreActions.PromptsSet,
            JsonSerializer.SerializeToElement(new { group = "..", name = "x", text = "y" }), default)).Error!.Code);
    }

    [Fact]
    public async Task A_script_can_be_read_as_loaded()
    {
        var manifest = new ScriptManifest("channel.echo", 2, "channel", MountMode.Manual, ["channel.message.accepted"],
            "onEvent", ["session.activate"], TrustDomain.Trusted);
        var script = new ScriptPackage(manifest, "async function onEvent(event, ctx) { return; }", "abc123");
        var plane = new ControlPlane(new CollectingAudit(_audit), _signals);
        Assert.True(CoreActions.RegisterAll(plane, new FakeAdapters(), new FakeChannels(), new FakeModels(null),
            _signals, new FakeGraph(script), new FakeParameters()).Ok);

        var read = await plane.InvokeAsync(User(Capabilities.GraphRead), CoreActions.ScriptsGet,
            JsonSerializer.SerializeToElement(new { id = "channel.echo" }), default);
        Assert.True(read.Ok, read.Error?.Message);
        Assert.Equal("async function onEvent(event, ctx) { return; }", read.Value.GetProperty("source").GetString());
        Assert.Equal("onEvent", read.Value.GetProperty("entryPoint").GetString());
        Assert.Equal("manual", read.Value.GetProperty("mount").GetString());
        Assert.True(read.Value.GetProperty("enabled").GetBoolean());
        // Reading a script is not controlling the graph.
        Assert.False(User("*.read").Allows(Capabilities.GraphControl));
        Assert.Equal(ErrorCode.Invalid, (await plane.InvokeAsync(User(Capabilities.GraphRead), CoreActions.ScriptsGet,
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

    private sealed class StubProvider(Address address, ModelProviderDescriptor describe) : IModelProvider
    {
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
