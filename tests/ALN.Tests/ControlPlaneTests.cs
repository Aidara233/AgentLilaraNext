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
        // Exactly one field per provider: configuration (which can hold an api key) is not part of
        // ModelProviderInfo at all, and adding it later would fail this test.
        var provider = Assert.Single(document.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Equal(["address"], provider.EnumerateObject().Select(p => p.Name));
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
    private sealed class FakeGraph : IGraphRuntime
    {
        public ValueTask<Result> LoadAsync(IEnumerable<ScriptPackage> packages, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> MountAsync(string scriptId, Address target, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> UnmountAsync(string scriptId, Address target, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public IReadOnlyList<MountedScript> Mounted => [];
        public IReadOnlyList<MountRule> Rules => [];
        public IReadOnlyList<MountRule> PendingRules => [];
        public IReadOnlyList<(ScriptPackage Package, bool Enabled)> Scripts => [];
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
        public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result<IChatAdapter>> CreateAsync(AdapterSpec spec, Func<AdapterSpec, IChatAdapter> factory, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result> DestroyAsync(Address address, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result> DestroyByTypeAsync(string typeId, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> ForceDestroyByTypeAsync(string typeId, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StartAsync(Address address, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(Address address, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public Result<IChatAdapter> Get(Address address) => Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, "none"));
        public IReadOnlyList<AdapterInfo> List() => [];
        public ValueTask<Result<IReadOnlyList<AdapterInfo>>> SnapshotByTypeAsync(string typeId, CancellationToken ct) =>
            ValueTask.FromResult(Result<IReadOnlyList<AdapterInfo>>.Success([]));
    }

    private sealed class FakeChannels : IChannelManager
    {
        public ValueTask<Result<ChannelInfo>> CreateAsync(ChannelSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result> StartAsync(Address address, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(Address address, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DestroyAsync(Address address, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<int>> AcceptAsync(AdapterEnvelope envelope, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result> CompleteAcceptAsync(Address channel, Guid messageId, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> RecordReplyAsync(Address channel, Guid answeredMessageId, string platformConversationId, string text, string? platformMessageId, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<int>> ReplayPendingAsync(CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(0));
        public Result<ChannelInfo> Get(Address address) => Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "none"));
        public IReadOnlyList<ChannelInfo> List() => [];
        public bool IsBound(Address channel, Address adapter, string conversation) => false;
        public Result<ChannelInfo> FindBound(Address adapter, string conversation) => Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "none"));
    }

    private sealed class FakeModels(ModelProviderInfo provider) : IModelManager
    {
        public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Result RegisterFactory(string packageId, IModelProviderFactory factory) => Result.Success();
        public Result RemovePackageFactories(string packageId) => Result.Success();
        public IReadOnlyList<ModelProviderTypeInfo> ListTypes() => [];
        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result<IModelProvider>> CreateAsync(string category, string instance, string configurationJson, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result> DestroyAsync(Address provider, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public Result<IModelProvider> Get(Address provider) => Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "none"));
        public IReadOnlyList<ModelProviderInfo> List() => [provider];
        public Result RegisterProfile(ModelProfile profile) => Result.Success();
        public Result RemoveProfile(string profileId) => Result.Success();
        public IReadOnlyList<ModelProfile> ListProfiles() => [];
    }
}
