using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class ArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-artifacts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Stores_content_under_a_daily_folder()
    {
        var store = new FileArtifactStore(_root);
        var stored = await store.StoreAsync("model-calls", "call-1", Encoding.UTF8.GetBytes("{\"x\":1}"), default);

        Assert.True(stored.Ok, stored.Error?.Message);
        var absolute = Path.Combine(_root, stored.Value!.RelativePath);
        Assert.True(File.Exists(absolute));
        Assert.StartsWith(Path.Combine("model-calls", DateTime.UtcNow.ToString("yyyyMMdd")), stored.Value.RelativePath);
        Assert.Equal("{\"x\":1}", await File.ReadAllTextAsync(absolute));
        // No partial file is left behind after a successful write.
        Assert.False(File.Exists(absolute + ".partial"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("")]
    public async Task Rejects_path_like_names(string name)
    {
        var store = new FileArtifactStore(_root);
        var stored = await store.StoreAsync("model-calls", name, "x"u8.ToArray(), default);

        Assert.False(stored.Ok);
        Assert.Equal(ErrorCode.Invalid, stored.Error?.Code);
    }

    [Fact]
    public async Task Rejects_oversized_content_instead_of_writing_a_partial_file()
    {
        var store = new FileArtifactStore(_root, maxBytes: 8);
        var stored = await store.StoreAsync("model-calls", "big", new byte[9], default);

        Assert.False(stored.Ok);
        Assert.False(Directory.Exists(Path.Combine(_root, "model-calls")));
    }

    [Fact]
    public async Task Model_calls_are_persisted_through_the_store_not_the_signal_log()
    {
        var signals = SignalLog.Open(null);
        var store = new FileArtifactStore(_root);
        var models = new ModelManager(store, signals);
        Assert.True(models.RegisterFactory("test.models", new StubFactory()).Ok);
        var providerAddress = new Address("model", "stub", "one");
        Assert.True((await models.CreateAsync(new(providerAddress, "{}", "model.stub"), default)).Ok);
        Assert.True(models.RegisterProfile(new("chat", providerAddress, "stub-1")).Ok);

        var started = await models.CompleteAsync("chat",
            new ModelRequest([new("user", "hello from the test")]), default);
        Assert.True(started.Ok, started.Error?.Message);
        await foreach (var _ in started.Value!) { }

        var stored = Assert.Single(Directory.GetFiles(Path.Combine(_root, "model-calls"), "*.json", SearchOption.AllDirectories));
        var record = JsonSerializer.Deserialize<ModelCallRecord>(await File.ReadAllBytesAsync(stored),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(record);
        Assert.Equal("chat", record!.ProfileId);
        Assert.Equal("stub-1", record.ModelId);
        Assert.Equal("hello from the test", record.Request.Messages[0].Content);
        Assert.Equal("ok", record.ResponseText);
        Assert.Equal(StopReason.EndTurn, record.StopReason);
        Assert.Equal(1, record.Usage!.InputTokens);
        // Large payloads must not be smuggled into the signal log.
        Assert.DoesNotContain(signals.Snapshot(), r => r.Detail?.Values.Any(v => v.Contains("hello from the test", StringComparison.Ordinal)) == true);
        await signals.DisposeAsync();
    }

    private sealed class StubFactory : IModelProviderFactory
    {
        public string TypeId => "model.stub";
        public ModelProviderDescriptor Describe { get; } = new("model.stub", "chat", "Stub", ModelCapabilities.Streaming);
        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<IModelProvider>.Success(new StubProvider(spec.Address)));
    }

    private sealed class StubProvider(Address address) : IModelProvider
    {
        public Address Address { get; } = address;
        public ModelProviderDescriptor Describe { get; } = new("model.stub", "chat", "Stub", ModelCapabilities.Streaming);
        public async IAsyncEnumerable<ModelEvent> CompleteAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new TextDelta("ok");
            yield return new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1), null);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
