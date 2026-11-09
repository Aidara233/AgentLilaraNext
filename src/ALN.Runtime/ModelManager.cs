using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Runtime;

/// <summary>Core registry and router for provider kinds, provider instances and model profiles.</summary>
[Module(ModelManagerId, Category = "model", Role = ModuleRole.Manager, Singleton = true)]
public sealed partial class ModelManager(IArtifactStore? artifacts = null, ISignalLog? signalLog = null,
    /// <summary>Where a provider announces that it is usable. Optional: a test fake need not have a queue, and a
    /// provider that cannot announce is still a provider.</summary>
    IEventSink? events = null)
    : IModelManager, IModuleIndex, IModulePackageResourceRegistry, IModuleWiring, IInstanceSink, IModule
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

    public void UseInstanceHost(IInstanceHost instances) => _instances = instances ?? throw new ArgumentNullException(nameof(instances));

    private static ModuleId IdOf(Address address) => address.ToModuleId();

    private readonly object _gate = new();
    private readonly Dictionary<string, (string PackageId, IModelProviderFactory Factory)> _factories = new(StringComparer.Ordinal);
    // Keyed by name; the address stays on the provider. The type is not kept separately either: the provider
    // descriptor carries it, and that descriptor came from the factory registered under that very type.
    private readonly Dictionary<ModuleId, (string PackageId, IModelProvider Provider, int InFlight)> _providers = new();
    private readonly Dictionary<string, ModelProfile> _profiles = new(StringComparer.Ordinal);
    private IInstanceHost? _instances;

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
            // Instances that are calling right now are the only thing worth refusing for: a call that started under this
            // factory is a request in progress, and pulling the package out from under it is the case nobody can reason
            // about afterwards. Idle instances are not a reason — they hold the old assembly open and keep working until
            // they are destroyed, which is what makes a version switch possible at all.
            var busy = _providers.Values
                .Where(x => ownedTypes.Contains(x.Provider.Describe.TypeId) && x.InFlight > 0)
                .Select(x => x.Provider.Address).ToArray();
            if (busy.Length > 0)
                return Result.Failure(new(ErrorCode.Conflict,
                    $"Model provider instances have calls in flight before unloading their package: {string.Join(", ", busy)}"));
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

    /// <summary>
    /// Hands new settings to a provider that is already running. Refused when the provider cannot take them, which is
    /// the honest answer for a provider that reads its configuration once: applying half of it silently is how a
    /// deployment ends up with a key from one file and an endpoint from another.
    /// </summary>
    public Result ReconfigureProvider(Address address, string configurationJson)
    {
        IModelProvider provider;
        lock (_gate)
        {
            if (!_providers.TryGetValue(IdOf(address), out var registration))
                return Result.Failure(new(ErrorCode.Invalid, $"Model provider instance not found: {address}"));
            provider = registration.Provider;
        }
        if (provider is not IModelProviderReconfigurable reconfigurable)
            return Result.Failure(new(ErrorCode.Unavailable,
                $"{address} keeps the configuration it was built with; a restart applies the new one."));
        // Outside the lock: applying settings is the provider's business and must not hold the registry while it does.
        return reconfigurable.ApplyConfiguration(configurationJson);
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
        // Now a instance, so a mount that names this provider points at something the graph knows (docs/23 §4 step 1).
        // A provider that exists but is invisible is the shape nobody can reason about later, so a refusal here
        // rolls the creation back instead of leaving a working provider with no instance.
        if (_instances is not null)
        {
            var announced = await _instances.AttachAsync(new ModuleId(ModelManagerId), created.Value.Id,
                created.Value.Describe.TypeId, null, cancellationToken).ConfigureAwait(false);
            if (!announced.Ok)
            {
                lock (_gate) _providers.Remove(created.Value.Id);
                await created.Value.DisposeAsync().ConfigureAwait(false);
                return Result<IModelProvider>.Failure(announced.Error!);
            }
        }
        // Usable is a fact worth announcing: a script mounted on a provider can now be told when it arrives, the same
        // way it is told when a Channel or a Session does. A refusal from the queue does not undo the creation — the
        // provider works, and a graph that could not be told so is a warning, not a reason to have no provider.
        await AnnounceAsync(created.Value.Address, EventNames.LifecycleStart, LifecyclePhase.Start, cancellationToken)
            .ConfigureAwait(false);
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

    public ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken) =>
        DestroyAsync(provider, TimeSpan.Zero, cancellationToken);

    /// <summary>
    /// Destroys one provider instance, optionally waiting for its in-flight calls to finish first.
    ///
    /// Refusing outright is right when the caller cannot wait, and useless when it can: a turn that is one request away
    /// from ending would otherwise block a switch for as long as somebody keeps talking to it. Waiting turns "not now"
    /// into "in a moment", which is the answer an operator who just changed a key actually wants.
    /// </summary>
    public async ValueTask<Result> DestroyAsync(Address provider, TimeSpan drain, CancellationToken cancellationToken)
    {
        if (drain > TimeSpan.Zero)
        {
            var deadline = DateTime.UtcNow + drain;
            while (true)
            {
                int inFlight;
                lock (_gate)
                    inFlight = _providers.TryGetValue(IdOf(provider), out var row) ? row.InFlight : 0;
                if (inFlight == 0) break;
                if (DateTime.UtcNow >= deadline)
                    return Result.Failure(new(ErrorCode.Conflict,
                        $"{provider} still has {inFlight} call(s) in flight after {drain.TotalSeconds:0.#}s.",
                        Retryable: true));
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

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
        // Out of the graph before the provider is disposed: a instance whose target is gone is worse than no instance.
        // Data is not touched here — whether this provider's configuration is deleted is the caller's decision.
        // The stop goes out first, while the provider is still disposed-but-present: a listener that answers by trying
        // to use it gets an honest "not found" rather than a message about something already gone.
        await AnnounceAsync(provider, EventNames.LifecycleStop, LifecyclePhase.Stop, CancellationToken.None)
            .ConfigureAwait(false);
        if (_instances is not null)
            await _instances.ForgetAsync(new ModuleId(ModelManagerId), IdOf(provider), CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Replaces a profile in place. The difference from remove-then-create is the window between the two: every model
    /// step looks its profile up by id, so a session that was mid-turn during the gap failed with "profile not found" —
    /// an error about the host's bookkeeping, arriving as if the conversation had broken.
    /// </summary>
    public Result UpdateProfile(ModelProfile profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.ModelId) || !profile.Provider.IsInstance)
            return Result.Failure(new(ErrorCode.Invalid, "Model profile is incomplete."));
        lock (_gate)
        {
            if (!_profiles.ContainsKey(profile.Id))
                return Result.Failure(new(ErrorCode.Invalid, $"Model profile not found: {profile.Id}"));
            if (!_providers.ContainsKey(IdOf(profile.Provider)))
                return Result.Failure(new(ErrorCode.Invalid, $"Model profile provider is not instantiated: {profile.Provider}"));
            // One write of one whole profile: a step either reads the old one or the new one, never half of each.
            _profiles[profile.Id] = profile;
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
    /// <summary>
    /// The request as it is worth keeping: everything except the pixels.
    ///
    /// A stored model call is a copy of a request that has already been answered, kept so a person can see what was
    /// actually sent. For a picture, "what was actually sent" is a media type, a size and a byte count — the pixels
    /// themselves are four thirds of their size in base64, per call, forever, in a store that exists to be pruned. So
    /// an image becomes a sentence in its place and the record stays readable instead of becoming unopenable.
    /// </summary>
    private static ModelRequest Redact(ModelRequest request)
    {
        if (request.Messages.All(message => !message.HasImage)) return request;
        var messages = new List<ModelMessage>(request.Messages.Count);
        foreach (var message in request.Messages)
        {
            if (!message.HasImage || message.Parts is not { } parts)
            {
                messages.Add(message);
                continue;
            }
            var redacted = new List<ModelContentPart>(parts.Count);
            foreach (var part in parts)
                redacted.Add(part is ImagePart image
                    ? new TextPart($"[图片 {image.MediaType}"
                                   + (image.Width is > 0 && image.Height is > 0 ? $" {image.Width}×{image.Height}" : "")
                                   + $" {image.Bytes.Length} 字节；内容未存进调用记录]")
                    : part);
            messages.Add(message with { Parts = redacted });
        }
        return request with { Messages = messages };
    }

    private async ValueTask PersistCallAsync(string callId, DateTimeOffset at, string profileId, Address provider,
        string modelId, ModelRequest request, string? responseText, IReadOnlyList<ModelToolCall> toolCalls,
        StopReason? stopReason, ModelUsage? usage, double durationMs, int eventCount, Error? failure,
        CancellationToken cancellationToken)
    {
        if (artifacts is null) return;
        try
        {
            var record = new ModelCallRecord(callId, at, profileId, provider, modelId, Redact(request), responseText, toolCalls,
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

    /// <summary>
    /// Tells the graph a provider is usable, or is going away — the same shape as every other lifecycle announcement
    /// (the signal is the payload; the address is both source and target, because a provider has no other address).
    ///
    /// A failed publish is a warning, never a failed creation or a failed destruction: a provider that works and could
    /// not be announced is still better than no provider, and this is the one place that decision is made.
    /// </summary>
    private async ValueTask AnnounceAsync(Address provider, string name, LifecyclePhase phase,
        CancellationToken cancellationToken)
    {
        if (events is null || !provider.IsInstance) return;
        var published = await events.PublishAsync(new(name, provider, Guid.NewGuid(),
            JsonSerializer.Serialize(new LifecycleSignal(phase, DateTimeOffset.UtcNow).For(provider),
                LifecycleJson.Options), provider), cancellationToken).ConfigureAwait(false);
        if (!published.Ok)
            signalLog?.Write(new()
            {
                Group = "model",
                Name = provider.ToString(),
                Mechanism = "model.announce",
                Outcome = "failed",
                Level = SignalLevel.Warn,
                Detail = new Dictionary<string, string>(StringComparer.Ordinal)
                { ["error"] = published.Error?.Message ?? "refused" }
            });
    }
}
