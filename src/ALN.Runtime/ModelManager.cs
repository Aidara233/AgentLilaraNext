using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Runtime;

/// <summary>Core registry and router for provider kinds, provider instances and model profiles.</summary>
[Module(ModelManagerId, Category = "model", Role = ModuleRole.Manager, Singleton = true)]
public sealed class ModelManager(IArtifactStore? artifacts = null, ISignalLog? signalLog = null)
    : IModelManager, IModuleIndex, IModulePackageResourceRegistry, IModuleWiring, IInstanceNodeSink, IModule
{
    public const string ModelManagerId = ModuleNames.ModelManager;
    public ModuleId Id { get; } = new(ModelManagerId);

    public string Category => "model";

    /// <summary>
    /// The master side of the edge: a provider is created here and announced afterwards, so this only accepts what
    /// the manager already holds. A provider that is not in the dictionary is not this manager's to hand out.
    /// </summary>
    public Result WireSlave(ModuleId slaveId, IReadOnlyList<string> kinds, IKernelModule instance)
    {
        lock (_gate)
            return _providers.ContainsKey(slaveId)
                ? Result.Success()
                : Result.Failure(new(ErrorCode.Invalid, $"{slaveId} is not one of this manager's model providers."));
    }

    public void UseInstanceNodes(IInstanceNodeHost nodes) => _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));

    private static ModuleId IdOf(Address address) => address.ToModuleId();

    private readonly object _gate = new();
    private readonly Dictionary<string, (string PackageId, IModelProviderFactory Factory)> _factories = new(StringComparer.Ordinal);
    // Keyed by name; the address stays on the provider. The type is not kept separately either: the provider
    // descriptor carries it, and that descriptor came from the factory registered under that very type.
    private readonly Dictionary<ModuleId, (string PackageId, IModelProvider Provider, int InFlight)> _providers = new();
    private readonly Dictionary<string, ModelProfile> _profiles = new(StringComparer.Ordinal);
    private IInstanceNodeHost? _nodes;

    public Result RegisterFactory(string packageId, IModelProviderFactory factory)
    {
        // A provider factory is a model provider because it says which category it serves and what type it is —
        // not because its type id starts with anything in particular (C-04, C-06).
        if (string.IsNullOrWhiteSpace(packageId) || factory is null || string.IsNullOrWhiteSpace(factory.TypeId) ||
            factory.Describe is null || factory.Describe.TypeId != factory.TypeId || string.IsNullOrWhiteSpace(factory.Describe.Category))
            return Result.Failure(new(ErrorCode.Invalid, "Model provider factory registration is invalid."));
        lock (_gate)
        {
            if (_factories.ContainsKey(factory.TypeId))
                return Result.Failure(new(ErrorCode.Conflict, $"Model provider type is already registered: {factory.TypeId}"));
            if (_factories.Values.Any(x => StringComparer.Ordinal.Equals(x.Factory.Describe.Category, factory.Describe.Category)))
                return Result.Failure(new(ErrorCode.Conflict, $"Model provider category is already registered: {factory.Describe.Category}"));
            _factories.Add(factory.TypeId, (packageId, factory));
            return Result.Success();
        }
    }

    public Result RemovePackage(string packageId) => RemovePackageFactories(packageId);

    public Result RemovePackageFactories(string packageId)
    {
        lock (_gate)
        {
            var ownedTypes = _factories.Where(x => x.Value.PackageId == packageId).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            var activeInstances = _providers.Values
                .Where(x => ownedTypes.Contains(x.Provider.Describe.TypeId))
                .Select(x => x.Provider.Address).ToArray();
            if (activeInstances.Length > 0)
                return Result.Failure(new(ErrorCode.Conflict, $"Model provider instances must be destroyed before unloading their package: {string.Join(", ", activeInstances)}"));
            foreach (var type in ownedTypes) _factories.Remove(type);
            return Result.Success();
        }
    }

    public IReadOnlyList<ModelProviderTypeInfo> ListTypes()
    {
        lock (_gate)
            return _factories.Values.Select(x => new ModelProviderTypeInfo(x.Factory.TypeId,
                x.Factory.Describe.Category, x.Factory.Describe.DisplayName, x.Factory.Describe.Capabilities))
                .OrderBy(x => x.Category, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public string? TypeOf(Address address)
    {
        lock (_gate) return _providers.TryGetValue(IdOf(address), out var row) ? row.Provider.Describe.TypeId : null;
    }

    public async ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken)
    {
        if (!spec.Address.IsInstance || string.IsNullOrWhiteSpace(spec.TypeId) || string.IsNullOrWhiteSpace(spec.ConfigurationJson))
            return Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "Model provider instance configuration is invalid."));
        try { using var _ = JsonDocument.Parse(spec.ConfigurationJson); }
        catch (JsonException ex) { return Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "Model provider configuration is not valid JSON.", ex.Message)); }

        IModelProviderFactory factory;
        string packageId;
        lock (_gate)
        {
            if (_providers.ContainsKey(IdOf(spec.Address)))
                return Result<IModelProvider>.Failure(new(ErrorCode.Conflict, $"Model provider instance already exists: {spec.Address}"));
            if (!_factories.TryGetValue(spec.TypeId, out var registration))
                return Result<IModelProvider>.Failure(new(ErrorCode.Unavailable, $"Model provider type is not registered: {spec.TypeId}"));
            (packageId, factory) = registration;
        }

        Result<IModelProvider> created;
        try { created = await factory.CreateAsync(spec, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<IModelProvider>.Failure(new(ErrorCode.Internal, "Model provider creation failed.", ex.Message)); }
        if (!created.Ok || created.Value is null) return Result<IModelProvider>.Failure(created.Error ?? new(ErrorCode.Internal, "Model provider factory returned no instance."));
        if (created.Value.Address != spec.Address)
        {
            await created.Value.DisposeAsync().ConfigureAwait(false);
            return Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "Model provider factory returned a mismatched instance address."));
        }
        lock (_gate)
        {
            if (_providers.ContainsKey(IdOf(spec.Address)))
            {
                _ = created.Value.DisposeAsync();
                return Result<IModelProvider>.Failure(new(ErrorCode.Conflict, $"Model provider instance already exists: {spec.Address}"));
            }
            _providers.Add(created.Value.Id, (packageId, created.Value, 0));
        }
        // Now a node, so a mount that names this provider points at something the graph knows (docs/23 §4 step 1).
        // A provider that exists but is invisible is the shape nobody can reason about later, so a refusal here
        // rolls the creation back instead of leaving a working provider with no node.
        if (_nodes is not null)
        {
            var announced = await _nodes.AttachAsync(new ModuleId(ModelManagerId), created.Value.Id,
                created.Value.Describe.TypeId, null, cancellationToken).ConfigureAwait(false);
            if (!announced.Ok)
            {
                lock (_gate) _providers.Remove(created.Value.Id);
                await created.Value.DisposeAsync().ConfigureAwait(false);
                return Result<IModelProvider>.Failure(announced.Error!);
            }
        }
        return Result<IModelProvider>.Success(created.Value);
    }

    public ValueTask<Result<IModelProvider>> CreateAsync(string category, string instance, string configurationJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(instance))
            return ValueTask.FromResult(Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "Model category and instance name are required.")));
        string typeId;
        lock (_gate)
        {
            var factory = _factories.Values.Select(x => x.Factory)
                .SingleOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Describe.Category, category));
            if (factory is null)
                return ValueTask.FromResult(Result<IModelProvider>.Failure(new(ErrorCode.Unavailable, $"Model category is not registered: {category}")));
            typeId = factory.TypeId;
        }
        // "model.chat" → the address names the instance of that kind; the type stays the whole string, and the
        // spec carries it so nothing downstream has to put it back together.
        var kind = typeId["model.".Length..];
        return CreateAsync(new ModelProviderSpec(new Address("model", kind, instance), configurationJson, typeId), cancellationToken);
    }

    public async ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken)
    {
        IModelProvider instance;
        lock (_gate)
        {
            if (!_providers.TryGetValue(IdOf(provider), out var registration))
                return Result.Failure(new(ErrorCode.Invalid, $"Model provider instance not found: {provider}"));
            if (registration.InFlight > 0)
                return Result.Failure(new(ErrorCode.Conflict, $"Model provider has calls in flight: {provider}"));
            if (_profiles.Values.Any(p => p.Provider == provider))
                return Result.Failure(new(ErrorCode.Conflict, $"Model provider is referenced by a profile: {provider}"));
            instance = registration.Provider;
            _providers.Remove(IdOf(provider));
        }
        // Out of the graph before the provider is disposed: a node whose target is gone is worse than no node.
        // Data is not touched here — whether this provider's configuration is deleted is the caller's decision.
        if (_nodes is not null)
            await _nodes.ForgetAsync(new ModuleId(ModelManagerId), IdOf(provider), CancellationToken.None).ConfigureAwait(false);
        try { await instance.DisposeAsync().ConfigureAwait(false); return Result.Success(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Result.Failure(new(ErrorCode.Internal, "Model provider disposal failed.", ex.Message)); }
    }

    public Result<IModelProvider> Get(Address provider)
    {
        lock (_gate) return _providers.TryGetValue(IdOf(provider), out var registration)
            ? Result<IModelProvider>.Success(registration.Provider)
            : Result<IModelProvider>.Failure(new(ErrorCode.Invalid, $"Model provider instance not found: {provider}"));
    }

    public IReadOnlyList<ModelProviderInfo> List()
    {
        lock (_gate) return _providers.Values.Select(x => new ModelProviderInfo(x.Provider.Address, x.Provider.Describe, x.InFlight))
            .OrderBy(x => x.Address.ToString(), StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public LocatedModule? Locate(ModuleId id)
    {
        lock (_gate)
            return _providers.TryGetValue(id, out var row)
                ? new LocatedModule(row.Provider, new CatalogEntry(row.Provider.Id, row.Provider.Describe.TypeId, Category, ModuleRole.Instance))
                : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<CatalogEntry> Entries()
    {
        lock (_gate) return _providers.Values
            .Select(row => new CatalogEntry(row.Provider.Id, row.Provider.Describe.TypeId, Category, ModuleRole.Instance))
            .ToArray();
    }

    public Result RegisterProfile(ModelProfile profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.ModelId) || !profile.Provider.IsInstance)
            return Result.Failure(new(ErrorCode.Invalid, "Model profile is incomplete."));
        lock (_gate)
        {
            if (!_providers.ContainsKey(IdOf(profile.Provider)))
                return Result.Failure(new(ErrorCode.Invalid, $"Model profile provider is not instantiated: {profile.Provider}"));
            if (!_profiles.TryAdd(profile.Id, profile))
                return Result.Failure(new(ErrorCode.Conflict, $"Model profile already exists: {profile.Id}"));
            return Result.Success();
        }
    }

    public Result RemoveProfile(string profileId)
    { lock (_gate) return _profiles.Remove(profileId) ? Result.Success() : Result.Failure(new(ErrorCode.Invalid, $"Model profile not found: {profileId}")); }

    public IReadOnlyList<ModelProfile> ListProfiles()
    { lock (_gate) return _profiles.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray(); }

    public ValueTask<Result<IAsyncEnumerable<ModelEvent>>> CompleteAsync(string profileId, ModelRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.Messages is null || request.Messages.Count == 0)
            return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Failure(new(ErrorCode.Invalid, "Model request must contain at least one message.")));
        IModelProvider provider;
        ModelProfile profile;
        lock (_gate)
        {
            if (!_profiles.TryGetValue(profileId, out profile!))
                return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Failure(new(ErrorCode.Invalid, $"Model profile not found: {profileId}")));
            if (!_providers.TryGetValue(IdOf(profile.Provider), out var registration))
                return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Failure(new(ErrorCode.Unavailable, $"Model provider is unavailable: {profile.Provider}")));
            provider = registration.Provider;
            if (request.Tools is { Count: > 0 } && !provider.Describe.Capabilities.HasFlag(ModelCapabilities.ToolCalls))
                return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Failure(new(ErrorCode.Invalid, "Selected model provider does not support tool calls.")));
            _providers[IdOf(profile.Provider)] = (registration.PackageId, provider, registration.InFlight + 1);
        }
        var routedRequest = request with { ModelId = profile.ModelId, Sampling = request.Sampling ?? profile.DefaultSampling };
        return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(
            Track(provider, profile.Id, profile.Provider, profile.ModelId, routedRequest, cancellationToken)));
    }

    private async IAsyncEnumerable<ModelEvent> Track(IModelProvider provider, string profileId, Address address, string modelId,
        ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var callId = Guid.NewGuid().ToString("N");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var at = DateTimeOffset.UtcNow;
        var text = new System.Text.StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, System.Text.StringBuilder Arguments)>();
        var eventCount = 0;
        StopReason? stopReason = null;
        ModelUsage? usage = null;
        Error? failure = null;
        try
        {
            await foreach (var item in provider.CompleteAsync(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                eventCount++;
                switch (item)
                {
                    case TextDelta delta: text.Append(delta.Text); break;
                    case ToolCallDelta call:
                        if (!calls.TryGetValue(call.Index, out var partial))
                            calls[call.Index] = partial = (null, null, new System.Text.StringBuilder());
                        calls[call.Index] = (call.CallId ?? partial.Id, call.Name ?? partial.Name,
                            partial.Arguments.Append(call.ArgumentsJsonFragment));
                        break;
                    case ModelCompleted completed: stopReason = completed.Reason; usage = completed.Usage; break;
                    case ModelFailed failed: failure = failed.Error; break;
                }
                yield return item;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_providers.TryGetValue(IdOf(address), out var registration) && ReferenceEquals(registration.Provider, provider))
                    _providers[IdOf(address)] = (registration.PackageId, provider, Math.Max(0, registration.InFlight - 1));
            }
            await PersistCallAsync(callId, at, profileId, address, modelId, request, text.ToString(),
                calls.Select(pair => new ModelToolCall(pair.Value.Id ?? "", pair.Value.Name ?? "", pair.Value.Arguments.ToString()))
                    .Where(call => call.Name.Length > 0).ToArray(),
                stopReason, usage, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                eventCount, failure, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Model calls are large — full messages, tool schemas, responses — so they go to the storage
    /// module, not the signal log. Failure to store must never fail the call it describes.
    /// </summary>
    private async ValueTask PersistCallAsync(string callId, DateTimeOffset at, string profileId, Address provider,
        string modelId, ModelRequest request, string? responseText, IReadOnlyList<ModelToolCall> toolCalls,
        StopReason? stopReason, ModelUsage? usage, double durationMs, int eventCount, Error? failure,
        CancellationToken cancellationToken)
    {
        if (artifacts is null) return;
        try
        {
            var record = new ModelCallRecord(callId, at, profileId, provider, modelId, request, responseText, toolCalls,
                stopReason, usage, durationMs, eventCount, failure?.Message);
            var json = JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var stored = await artifacts.StoreAsync("model-calls", $"{at:HHmmssfff}-{callId}", json, cancellationToken).ConfigureAwait(false);
            if (!stored.Ok)
                signalLog?.Write(new()
                {
                    Group = "model",
                    Name = profileId,
                    Mechanism = "persist",
                    Outcome = "failed",
                    Level = SignalLevel.Warn,
                    Detail = new Dictionary<string, string>
                    { ["code"] = stored.Error?.Code.ToString() ?? "unknown", ["message"] = stored.Error?.Message ?? "" }
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            signalLog?.Write(new()
            {
                Group = "model",
                Name = profileId,
                Mechanism = "persist",
                Outcome = "failed",
                Level = SignalLevel.Warn,
                Detail = new Dictionary<string, string> { ["message"] = ex.Message }
            });
        }
    }
}
