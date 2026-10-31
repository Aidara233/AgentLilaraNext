using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>Breaks the startup cycle between instance managers and Graph bindings.</summary>
public sealed class InstanceBindingsProxy : IInstanceBindingsProxy
{
    private IInstanceBindings? _inner;
    private readonly object _gate = new();

    public void Bind(IInstanceBindings inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        lock (_gate)
        {
            if (_inner is not null && !ReferenceEquals(_inner, inner))
                throw new InvalidOperationException("Instance bindings are already bound.");
            _inner = inner;
        }
    }

    public ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken) =>
        GetInner().AttachAsync(target, cancellationToken);

    public ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken) =>
        GetInner().DetachAsync(target, cancellationToken);

    public ValueTask<Result> ForceDetachAsync(Address target, CancellationToken cancellationToken) =>
        GetInner() is IForceInstanceBindings forceBindings
            ? forceBindings.ForceDetachAsync(target, cancellationToken)
            : DetachAsync(target, cancellationToken);

    private IInstanceBindings GetInner()
    {
        lock (_gate) return _inner ?? throw new InvalidOperationException("Instance bindings are not initialized.");
    }
}
