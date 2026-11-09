using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// Changing where a conversation's words go, while it is running.
///
/// The thing being protected is a half-finished conversation: its history, its state and the fact that the next request
/// goes somewhere else. What must not happen is any of it being rebuilt, restarted, or silently left pointing at the old
/// endpoint because a file was written and the held record was not.
/// </summary>
public sealed class ModelReconfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-reconfig-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly SessionSpec Spec = new("alpha", "Answer briefly.", "1");

    [Fact]
    public async Task A_session_moves_to_another_profile_and_the_next_turn_uses_it()
    {
        var model = new RecordingModel();
        var sessions = new SessionManager(_root, model);
        var first = await sessions.TurnAsync("s-1", Spec, Guid.NewGuid(), new SessionTurnInput([], null, null));
        Assert.True(first.Ok, first.Error?.Detail ?? first.Error?.Message);
        Assert.Equal(["alpha"], model.Profiles);

        var rebound = await sessions.RebindAsync("s-1", "beta", default);

        Assert.True(rebound.Ok, rebound.Error?.Message);
        Assert.True(rebound.Value!.Changed);
        Assert.Equal("alpha", rebound.Value.PreviousProfile);
        Assert.Equal("beta", rebound.Value.Profile);
        // The history is not rebuilt and the conversation is not restarted: the same Session keeps going.
        var second = await sessions.TurnAsync("s-1", Spec, Guid.NewGuid(), new SessionTurnInput([], null, null));
        Assert.True(second.Ok, second.Error?.Detail ?? second.Error?.Message);
        Assert.Equal(["alpha", "beta"], model.Profiles);
        var history = await File.ReadAllTextAsync(Path.Combine(_root, "modules", "s-1", "history.jsonl"));
        Assert.Contains("answer one", history, StringComparison.Ordinal);
        Assert.Contains("answer two", history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_for_the_profile_it_already_uses_changes_nothing()
    {
        var sessions = new SessionManager(_root, new RecordingModel());
        Assert.True((await sessions.TurnAsync("s-1", Spec, Guid.NewGuid(), new SessionTurnInput([], null, null))).Ok);

        var rebound = await sessions.RebindAsync("s-1", "alpha", default);

        Assert.True(rebound.Ok, rebound.Error?.Message);
        Assert.False(rebound.Value!.Changed);
    }

    [Fact]
    public async Task A_profile_that_does_not_exist_is_refused_before_anything_is_written()
    {
        var sessions = new SessionManager(_root, new RecordingModel());
        Assert.True((await sessions.TurnAsync("s-1", Spec, Guid.NewGuid(), new SessionTurnInput([], null, null))).Ok);

        var refused = await sessions.RebindAsync("s-1", "nowhere", default);

        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);
        // A profile nobody can serve would move the conversation to a name that fails at its next turn with a stranger
        // error — the refusal is what keeps that from being written down.
        Assert.Contains("models.list", refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_in_the_middle_of_a_turn_is_not_moved_under_itself()
    {
        var model = new RecordingModel { Hold = new TaskCompletionSource() };
        var sessions = new SessionManager(_root, model);
        var turn = sessions.TurnAsync("s-1", Spec, Guid.NewGuid(), new SessionTurnInput([], null, null));
        await model.Started.Task;

        var refused = await sessions.RebindAsync("s-1", "beta", default);

        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Conflict, refused.Error!.Code);
        Assert.Contains("turn boundary", refused.Error.Message, StringComparison.Ordinal);

        model.Hold.SetResult();
        var finished = await turn;
        Assert.True(finished.Ok, finished.Error?.Detail ?? finished.Error?.Message);
        // Still on the profile it started with: a refused rebind must not be half-applied.
        Assert.Equal(["alpha"], model.Profiles);
    }

    [Fact]
    public async Task A_running_provider_takes_new_settings_and_says_so()
    {
        var manager = new ModelManager();
        Assert.True(manager.RegisterFactory("test", new RecordingFactory()).Ok);
        var created = await manager.CreateAsync("chat", "main", """{"apiKey":"k1","endpoint":"http://one"}""", default);
        Assert.True(created.Ok, created.Error?.Message);

        var applied = manager.ReconfigureProvider(new Address("model", "recording", "main"), """{"apiKey":"k2","endpoint":"http://two"}""");

        Assert.True(applied.Ok, applied.Error?.Message);
        // The provider is the thing that received them; what it does with them is its business, but it must have been
        // handed the whole configuration in one piece.
        Assert.Equal("""{"apiKey":"k2","endpoint":"http://two"}""", ReconfigurableRecordingProvider.LastConfiguration);
    }

    [Fact]
    public async Task A_provider_that_cannot_take_new_settings_is_told_so_rather_than_silently_ignored()
    {
        var manager = new ModelManager();
        Assert.True(manager.RegisterFactory("test", new RecordingFactory { Reconfigurable = false }).Ok);
        var created = await manager.CreateAsync("chat", "main", """{"apiKey":"k1"}""", default);
        Assert.True(created.Ok, created.Error?.Message);

        var applied = manager.ReconfigureProvider(new Address("model", "recording", "main"), """{"apiKey":"k2"}""");

        Assert.False(applied.Ok);
        Assert.Equal(ErrorCode.Unavailable, applied.Error!.Code);
        Assert.Contains("restart", applied.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_a_profile_in_place_is_what_the_next_step_uses()
    {
        var manager = new ModelManager();
        Assert.True(manager.RegisterFactory("test", new RecordingFactory()).Ok);
        Assert.True((await manager.CreateAsync("chat", "main", """{"apiKey":"k1"}""", default)).Ok);
        var plane = new ControlPlane(new QuietAudit(), new QuietSignals());
        Assert.True(manager.RegisterActions(plane).Ok);
        Assert.True((await plane.InvokeAsync(Actor.System, ModelActions.ProfileCreate,
            JsonSerializer.SerializeToElement(new { id = "p", provider = "model.recording#main", model = "small" }), default)).Ok);

        var changed = await plane.InvokeAsync(Actor.System, ModelActions.ProfileSet,
            JsonSerializer.SerializeToElement(new { id = "p", model = "big" }), default);

        Assert.True(changed.Ok, $"{changed.Error?.Message} {changed.Error?.Detail}");
        Assert.Equal("big", changed.Value.GetProperty("model").GetString());
        Assert.Equal("next step", changed.Value.GetProperty("applies").GetString());
        // Read back through the registry, which is what a model step consults, not through the reply.
        Assert.Equal("big", Assert.Single(manager.ListProfiles()).ModelId);
        // And a field nobody named kept what it had.
        Assert.Equal("model.recording#main", Assert.Single(manager.ListProfiles()).Provider.ToString());
    }

    // ---- fixtures ----------------------------------------------------------------------------------------------

    /// <summary>Answers every request with text, and remembers which profile each request was routed through.</summary>
    private sealed class RecordingModel : IModelManager
    {
        public List<string> Profiles { get; } = [];

        /// <summary>When set, the call blocks until it is completed: the only way to hold a turn open on purpose.</summary>
        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request,
            CancellationToken cancellationToken)
        {
            Profiles.Add(profileId);
            Started.TrySetResult();
            if (Hold is not null) await Hold.Task;
            var text = profileId == "alpha" ? "answer one" : "answer two";
            return Result<IAsyncEnumerable<ModelEvent>>.Success(Stream(
                [new TextDelta(text), new ModelCompleted(StopReason.EndTurn, new ModelUsage(1, 1), null)]));
        }

        private static async IAsyncEnumerable<ModelEvent> Stream(IReadOnlyList<ModelEvent> events,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var item in events) yield return item;
        }

        public Result RegisterFactory(string packageId, IModelProviderFactory factory) => Result.Success();
        public Result RemovePackageFactories(string packageId) => Result.Success();
        public IReadOnlyList<ModelProviderTypeInfo> ListTypes() => [];
        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result<IModelProvider>> CreateAsync(string category, string instance, string configurationJson,
            CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Result> DestroyAsync(Address provider, CancellationToken ct) => ValueTask.FromResult(Result.Success());
        public Result<IModelProvider> Get(Address provider) => Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "No providers."));
        public IReadOnlyList<ModelProviderInfo> List() => [];
        public Result RegisterProfile(ModelProfile profile) => Result.Success();
        public Result RemoveProfile(string profileId) => Result.Success();
        public IReadOnlyList<ModelProfile> ListProfiles() => [new("alpha", new Address("model", "recording", "main"), "m1"),
            new("beta", new Address("model", "recording", "main"), "m2")];
    }

    private sealed class RecordingFactory : IModelProviderFactory
    {
        public bool Reconfigurable { get; init; } = true;

        public string TypeId => "model.recording";
        public ModelProviderDescriptor Describe => new(TypeId, "chat", "Recording", ModelCapabilities.None);

        public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken ct) =>
            ValueTask.FromResult(Result<IModelProvider>.Success(Reconfigurable
                ? new ReconfigurableRecordingProvider(spec.Address, Describe)
                : new RecordingProvider(spec.Address, Describe)));
    }

    private class RecordingProvider(Address address, ModelProviderDescriptor describe) : IModelProvider
    {
        public Address Address { get; } = address;
        public ModuleId Id => new(Address.ToString());
        public ModelProviderDescriptor Describe { get; } = describe;

        public async IAsyncEnumerable<ModelEvent> CompleteAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new ModelCompleted(StopReason.EndTurn, new ModelUsage(0, 0), null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A provider that took the interface seriously: it can be handed new settings while it runs.</summary>
    private sealed class ReconfigurableRecordingProvider(Address address, ModelProviderDescriptor describe)
        : RecordingProvider(address, describe), IModelProviderReconfigurable
    {
        public static string? LastConfiguration { get; private set; }

        public Result ApplyConfiguration(string configurationJson)
        {
            LastConfiguration = configurationJson;
            return Result.Success();
        }
    }

    private sealed class QuietAudit : IAuditSink
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class QuietSignals : ISignalLog
    {
        public SignalLevel MinLevel => SignalLevel.Debug;
        public bool IsEnabled(SignalLevel level) => true;
        public void Write(SignalRecord record) { }
        public IReadOnlyList<SignalRecord> Snapshot() => [];
    }
}
