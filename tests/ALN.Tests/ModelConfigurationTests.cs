using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The model provider files, and the rule that lets a settings page edit a key it is not allowed to see.
///
/// The failure this is built around is quiet and expensive: mask a value for display, save the form back, and the
/// real key is now the string "***". Every save would silently log the deployment out, and the symptom (auth errors
/// after a settings change) points anywhere but at the form.
/// </summary>
public sealed class ModelConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Masking_hides_credentials_and_leaves_everything_else_alone()
    {
        var configuration = Json("""
            {"endpoint":"https://api.example.com","apiKey":"sk-real","nested":{"token":"abc","model":"x"},"retries":3}
            """);

        var masked = ModelSecrets.Mask(configuration);

        Assert.Equal("https://api.example.com", masked.GetProperty("endpoint").GetString());
        Assert.Equal(ModelSecrets.Placeholder, masked.GetProperty("apiKey").GetString());
        Assert.Equal(ModelSecrets.Placeholder, masked.GetProperty("nested").GetProperty("token").GetString());
        // A key that does not look like a credential is not touched; a mask that hides everything is not an editor.
        Assert.Equal("x", masked.GetProperty("nested").GetProperty("model").GetString());
        Assert.Equal(3, masked.GetProperty("retries").GetInt32());
        // What was stored is untouched: masking is a rendering decision.
        Assert.Equal("sk-real", configuration.GetProperty("apiKey").GetString());
    }

    [Fact]
    public void A_placeholder_means_keep_the_stored_value_so_saving_does_not_erase_the_key()
    {
        var stored = Json("""{"endpoint":"https://old.example.com","apiKey":"sk-real","nested":{"token":"abc"}}""");
        var fromForm = Json("""{"endpoint":"https://new.example.com","apiKey":"***","nested":{"token":"***"}}""");

        var merged = ModelSecrets.Restore(fromForm, stored);

        Assert.Equal("https://new.example.com", merged.GetProperty("endpoint").GetString());
        Assert.Equal("sk-real", merged.GetProperty("apiKey").GetString());
        Assert.Equal("abc", merged.GetProperty("nested").GetProperty("token").GetString());

        // A real new value is taken, and a placeholder with nothing stored stays a placeholder rather than inventing
        // a credential.
        var replaced = ModelSecrets.Restore(Json("""{"apiKey":"sk-new"}"""), stored);
        Assert.Equal("sk-new", replaced.GetProperty("apiKey").GetString());
        var orphan = ModelSecrets.Restore(Json("""{"apiKey":"***"}"""), Json("""{"endpoint":"x"}"""));
        Assert.Equal(ModelSecrets.Placeholder, orphan.GetProperty("apiKey").GetString());
    }

    [Fact]
    public async Task The_store_writes_reads_and_deletes_one_module_directory_per_provider()
    {
        var store = new FileModelInstanceStore(_root);
        var spec = new ModelInstanceSpec(Json("""{"endpoint":"https://api.example.com","apiKey":"sk-real"}"""),
            [new ModelProfile("default", new Address("model", "openai-chat", "one"), "deepseek-flash")]);

        Assert.True((await store.SaveAsync("model.openai-chat#one", spec, default)).Ok);
        // The directory is the node's id, and the file is the one the kernel would also read — that is the point of
        // moving it here: a provider is not the one module whose directory says nothing (docs/22 §2).
        var manifest = Path.Combine(_root, "modules", "model.openai-chat#one", "module.json");
        Assert.True(File.Exists(manifest));

        var read = await store.ReadAsync("model.openai-chat#one", default);
        Assert.True(read.Error is null, read.Error?.Message);
        Assert.Equal("deepseek-flash", Assert.Single(read.Value!.Profiles!).ModelId);

        // The file is the shape the loader reads: id, type, and the domain's payload under config.
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
        Assert.Equal("model.openai-chat#one", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("model.openai-chat", document.RootElement.GetProperty("type").GetString());
        var config = document.RootElement.GetProperty("config");
        Assert.True(config.GetProperty("enabled").GetBoolean());
        Assert.True(config.TryGetProperty("configuration", out _));
        Assert.True(config.TryGetProperty("profiles", out _));

        var loaded = (await store.LoadAllAsync(default)).Value!;
        var entry = Assert.Single(loaded);
        Assert.Equal("model.openai-chat", entry.TypeId);
        Assert.Equal("model.openai-chat#one", entry.Provider.ToString());

        Assert.True((await store.DeleteAsync("model.openai-chat#one", default)).Ok);
        Assert.True((await store.DeleteAsync("model.openai-chat#one", default)).Ok);
        Assert.Empty((await store.LoadAllAsync(default)).Value!);
        // The configuration was the only thing in it, so the directory goes with it. A provider that left something
        // else behind keeps its directory.
        Assert.False(Directory.Exists(Path.Combine(_root, "modules", "model.openai-chat#one")));
        Assert.Null((await store.ReadAsync("model.openai-chat#one", default)).Value);
    }

    [Fact]
    public async Task Another_kind_of_module_in_the_same_tree_is_not_read_as_a_provider()
    {
        // The store looks at every module directory, and most of them are not providers. Reading one of those as
        // model configuration would decide whose file it is by guessing, which is the same as being wrong sometimes.
        var other = Path.Combine(_root, "modules", "channel.conversation#chat");
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(other, "module.json"),
            """{ "id": "channel.conversation#chat", "type": "channel.conversation" }""");
        var store = new FileModelInstanceStore(_root);

        var loaded = await store.LoadAllAsync(default);

        Assert.True(loaded.Error is null, loaded.Error?.Message);
        Assert.Empty(loaded.Value!);
    }

    [Fact]
    public async Task A_name_that_is_a_path_is_refused_rather_than_written_through()
    {
        var store = new FileModelInstanceStore(_root);

        foreach (var provider in new[] { "model.openai-chat#../escape", "model.openai-chat#a/b", "chat#one", "", "..", "model.openai-chat" })
        {
            var saved = await store.SaveAsync(provider, new ModelInstanceSpec(Json("{}")), default);
            Assert.False(saved.Ok, $"{provider} should not be writable");
            Assert.Equal(ErrorCode.Invalid, saved.Error!.Code);
        }
        Assert.False(Directory.Exists(Path.Combine(_root, "escape")));
    }

    [Fact]
    public async Task Configuration_that_is_not_an_object_is_refused_by_the_store()
    {
        var store = new FileModelInstanceStore(_root);
        var saved = await store.SaveAsync("model.openai-chat#one", new ModelInstanceSpec(Json("\"just a string\"")), default);
        Assert.Equal(ErrorCode.Invalid, saved.Error!.Code);
    }

    [Fact]
    public async Task A_disabled_instance_is_loaded_but_not_meant_to_run()
    {
        var store = new FileModelInstanceStore(_root);
        Assert.True((await store.SaveAsync("model.openai-chat#off", new ModelInstanceSpec(Json("{}"), null, Enabled: false), default)).Ok);

        var entry = Assert.Single((await store.LoadAllAsync(default)).Value!);
        Assert.False(entry.Spec.Enabled);
    }

    [Fact]
    public async Task A_provider_is_a_node_while_it_is_held_and_stops_being_one_when_it_is_destroyed()
    {
        // A mount that names a provider points at a node; a provider that only exists in the manager's dictionary is
        // a target the graph cannot draw an edge to (docs/23 §4 step 1).
        var models = new ModelManager();
        Assert.True(models.RegisterFactory("test.models", new NodeStubFactory()).Ok);
        var kernel = new Kernel(_root, new ModuleFactoryRegistry());
        Assert.True(kernel.Declare(new PassiveNode(models.Id, models), typeId: ModuleNames.ModelManager).Ok);
        models.UseInstanceNodes(new InstanceNodeHost(kernel, models.Id));
        Assert.True((await kernel.StartAsync(default)).Ok);

        var address = new Address("model", "stub", "one");
        var created = await models.CreateAsync(new(address, "{}", "model.stub"), default);
        Assert.True(created.Ok, created.Error?.Message);

        var node = kernel.Nodes.Single(candidate => candidate.Id.Value == "model.stub#one");
        Assert.Equal(ModulePhase.Ready, node.Phase);
        Assert.Equal("model.stub", node.Type);
        // Accepted, so nothing about this edge is in the "never became a call" list.
        Assert.Empty(kernel.Status.BadEdges);

        var destroyed = await models.DestroyAsync(address, default);
        Assert.True(destroyed.Ok, destroyed.Error?.Message);
        Assert.Null(kernel.Find(new ModuleId("model.stub#one")));
    }

    private sealed class NodeStubFactory : IModelProviderFactory
    {
        public string TypeId => "model.stub";
        public ModelProviderDescriptor Describe { get; } = new("model.stub", "chat", "Stub", ModelCapabilities.Streaming);
        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<IModelProvider>.Success(new NodeStubProvider(spec.Address)));
    }

    private sealed class NodeStubProvider(Address address) : IModelProvider
    {
        public Address Address { get; } = address;
        public ModuleId Id => new(Address.ToString());
        public ModelProviderDescriptor Describe { get; } = new("model.stub", "chat", "Stub", ModelCapabilities.Streaming);
        public IAsyncEnumerable<ModelEvent> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            Empty();

        private static async IAsyncEnumerable<ModelEvent> Empty() { await Task.Yield(); yield break; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
