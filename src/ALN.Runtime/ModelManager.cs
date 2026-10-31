using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Runtime;

/// <summary>Core registry and router for provider kinds, provider instances and model profiles.</summary>
public sealed class ModelManager : IModelManager, IModulePackageResourceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string PackageId, IModelProviderFactory Factory)> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<Address, (string PackageId, IModelProvider Provider, int InFlight)> _providers = new();
    private readonly Dictionary<string, ModelProfile> _profiles = new(StringComparer.Ordinal);

    public Result RegisterFactory(string packageId, IModelProviderFactory factory)
    {
        if (string.IsNullOrWhiteSpace(packageId) || factory is null || string.IsNullOrWhiteSpace(factory.TypeId) ||
            !factory.TypeId.StartsWith("model.", StringComparison.Ordinal) || factory.TypeId == "model" ||
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
            var activeInstances = _providers.Values.Where(x => ownedTypes.Contains(x.Provider.Address.TypeId)).Select(x => x.Provider.Address).ToArray();
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

    public async ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken)
    {
        if (!spec.Address.IsInstance || spec.Address.Module != "model" || string.IsNullOrWhiteSpace(spec.ConfigurationJson))
            return Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "Model provider instance configuration is invalid."));
        try { using var _ = JsonDocument.Parse(spec.ConfigurationJson); }
        catch (JsonException ex) { return Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "Model provider configuration is not valid JSON.", ex.Message)); }

        IModelProviderFactory factory;
        string packageId;
        lock (_gate)
        {
            if (_providers.ContainsKey(spec.Address))
                return Result<IModelProvider>.Failure(new(ErrorCode.Conflict, $"Model provider instance already exists: {spec.Address}"));
            if (!_factories.TryGetValue(spec.Address.TypeId, out var registration))
                return Result<IModelProvider>.Failure(new(ErrorCode.Unavailable, $"Model provider type is not registered: {spec.Address.TypeId}"));
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
            if (_providers.ContainsKey(spec.Address))
            {
                _ = created.Value.DisposeAsync();
                return Result<IModelProvider>.Failure(new(ErrorCode.Conflict, $"Model provider instance already exists: {spec.Address}"));
            }
            _providers.Add(spec.Address, (packageId, created.Value, 0));
        }
        return Result<IModelProvider>.Success(created.Value);
    }

    public ValueTask<Result<IModelProvider>> CreateAsync(string category, string instance, string configurationJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(instance))
            return ValueTask.FromResult(Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "Model category and instance name are required.")));
        string? kind;
        lock (_gate)
        {
            var factory = _factories.Values.Select(x => x.Factory)
                .SingleOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Describe.Category, category));
            if (factory is null)
                return ValueTask.FromResult(Result<IModelProvider>.Failure(new(ErrorCode.Unavailable, $"Model category is not registered: {category}")));
            kind = factory.TypeId["model.".Length..];
        }
        return CreateAsync(new ModelProviderSpec(new Address("model", kind, instance), configurationJson), cancellationToken);
    }

    public async ValueTask<Result> DestroyAsync(Address provider, CancellationToken cancellationToken)
    {
        IModelProvider instance;
        lock (_gate)
        {
            if (!_providers.TryGetValue(provider, out var registration))
                return Result.Failure(new(ErrorCode.Invalid, $"Model provider instance not found: {provider}"));
            if (registration.InFlight > 0)
                return Result.Failure(new(ErrorCode.Conflict, $"Model provider has calls in flight: {provider}"));
            if (_profiles.Values.Any(p => p.Provider == provider))
                return Result.Failure(new(ErrorCode.Conflict, $"Model provider is referenced by a profile: {provider}"));
            instance = registration.Provider;
            _providers.Remove(provider);
        }
        try { await instance.DisposeAsync().ConfigureAwait(false); return Result.Success(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Result.Failure(new(ErrorCode.Internal, "Model provider disposal failed.", ex.Message)); }
    }

    public Result<IModelProvider> Get(Address provider)
    {
        lock (_gate) return _providers.TryGetValue(provider, out var registration)
            ? Result<IModelProvider>.Success(registration.Provider)
            : Result<IModelProvider>.Failure(new(ErrorCode.Invalid, $"Model provider instance not found: {provider}"));
    }

    public IReadOnlyList<ModelProviderInfo> List()
    {
        lock (_gate) return _providers.Values.Select(x => new ModelProviderInfo(x.Provider.Address, x.Provider.Describe, x.InFlight))
            .OrderBy(x => x.Address.ToString(), StringComparer.Ordinal).ToArray();
    }

    public Result RegisterProfile(ModelProfile profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.ModelId) || !profile.Provider.IsInstance)
            return Result.Failure(new(ErrorCode.Invalid, "Model profile is incomplete."));
        lock (_gate)
        {
            if (!_providers.ContainsKey(profile.Provider))
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
            if (!_providers.TryGetValue(profile.Provider, out var registration))
                return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Failure(new(ErrorCode.Unavailable, $"Model provider is unavailable: {profile.Provider}")));
            provider = registration.Provider;
            if (request.Tools is { Count: > 0 } && !provider.Describe.Capabilities.HasFlag(ModelCapabilities.ToolCalls))
                return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Failure(new(ErrorCode.Invalid, "Selected model provider does not support tool calls.")));
            _providers[profile.Provider] = (registration.PackageId, provider, registration.InFlight + 1);
        }
        var routedRequest = request with { ModelId = profile.ModelId, Sampling = request.Sampling ?? profile.DefaultSampling };
        return ValueTask.FromResult(Result<IAsyncEnumerable<ModelEvent>>.Success(Track(provider, profile.Provider, routedRequest, cancellationToken)));
    }

    private async IAsyncEnumerable<ModelEvent> Track(IModelProvider provider, Address address, ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in provider.CompleteAsync(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            lock (_gate)
            {
                if (_providers.TryGetValue(address, out var registration) && ReferenceEquals(registration.Provider, provider))
                    _providers[address] = (registration.PackageId, provider, Math.Max(0, registration.InFlight - 1));
            }
        }
    }
}
