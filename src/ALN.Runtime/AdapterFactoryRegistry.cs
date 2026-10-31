using ALN.Contracts;

namespace ALN.Runtime;

public sealed class AdapterFactoryRegistry : IAdapterFactoryRegistry
{
    private readonly Dictionary<string, (string PackageId, IAdapterFactory Factory)> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _packages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _draining = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Result Register(string packageId, IAdapterFactory factory)
    {
        if (string.IsNullOrWhiteSpace(packageId) || factory is null || string.IsNullOrWhiteSpace(factory.TypeId))
            return Result.Failure(new(ErrorCode.Invalid, "Adapter factory registration is incomplete."));
        lock (_gate)
        {
            if (_draining.Contains(packageId)) return Result.Failure(new(ErrorCode.Unavailable, $"Adapter package is draining: {packageId}"));
            if (_factories.ContainsKey(factory.TypeId))
                return Result.Failure(new(ErrorCode.Conflict, $"Adapter type already has a factory: {factory.TypeId}"));
            _factories.Add(factory.TypeId, (packageId, factory));
            if (!_packages.TryGetValue(packageId, out var types)) _packages.Add(packageId, types = new(StringComparer.Ordinal));
            types.Add(factory.TypeId);
            return Result.Success();
        }
    }

    public Result BeginDrain(string packageId)
    {
        lock (_gate)
        {
            if (!_packages.ContainsKey(packageId)) return Result.Failure(new(ErrorCode.Invalid, $"Adapter package has no factories: {packageId}"));
            _draining.Add(packageId);
            return Result.Success();
        }
    }

    public Result Resume(string packageId)
    {
        lock (_gate)
        {
            if (!_packages.ContainsKey(packageId)) return Result.Failure(new(ErrorCode.Invalid, $"Adapter package has no factories: {packageId}"));
            _draining.Remove(packageId);
            return Result.Success();
        }
    }

    public Result RemovePackage(string packageId)
    {
        lock (_gate)
        {
            if (!_packages.Remove(packageId, out var types))
            {
                _draining.Remove(packageId);
                return Result.Success();
            }
            foreach (var type in types) _factories.Remove(type);
            _draining.Remove(packageId);
            return Result.Success();
        }
    }

    public Result<IChatAdapter> Create(string typeId, AdapterSpec spec)
    {
        lock (_gate)
        {
            if (!_factories.TryGetValue(typeId, out var registration))
                return Result<IChatAdapter>.Failure(new(ErrorCode.Invalid, $"No Adapter factory is registered for {typeId}."));
            if (_draining.Contains(registration.PackageId))
                return Result<IChatAdapter>.Failure(new(ErrorCode.Unavailable, $"Adapter package is draining: {registration.PackageId}", Retryable: true));
            try
            {
                var adapter = registration.Factory.Create(spec);
                return adapter is null
                    ? Result<IChatAdapter>.Failure(new(ErrorCode.Internal, "Adapter factory returned null."))
                    : Result<IChatAdapter>.Success(adapter);
            }
            catch (Exception ex)
            {
                return Result<IChatAdapter>.Failure(new(ErrorCode.Internal, "Adapter factory failed.", ex.Message));
            }
        }
    }
}
