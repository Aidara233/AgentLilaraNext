using System.Globalization;
using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The runtime knob panel: components register themselves, values are validated by the component that owns
/// them, applied immediately, and persisted through the state store so a restart keeps the setting.
///
/// The order matters and is deliberate: <b>apply, then persist</b>. If the write fails the value is still
/// live and the caller gets an error saying it would not survive a restart — whereas persisting first and
/// failing to apply would leave the file promising something the process is not doing.
/// </summary>
public sealed class ParameterRegistry(IStateStore state) : IParameterRegistry
{
    private readonly Dictionary<string, IParameterTarget> _targets = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Result Register(IParameterTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ParameterScope))
            return Result.Failure(new(ErrorCode.Invalid, "Parameter target needs a scope."));
        var duplicate = target.Parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != target.Parameters.Count;
        if (duplicate) return Result.Failure(new(ErrorCode.Conflict, $"Duplicate parameter names in {target.ParameterScope}."));
        lock (_gate)
        {
            if (!_targets.TryAdd(target.ParameterScope, target))
                return Result.Failure(new(ErrorCode.Conflict, $"Parameter scope already registered: {target.ParameterScope}"));
        }
        return Result.Success();
    }

    public Result Unregister(string scope)
    {
        lock (_gate)
            return _targets.Remove(scope)
                ? Result.Success()
                : Result.Failure(new(ErrorCode.Invalid, $"Parameter scope is not registered: {scope}"));
    }

    public IReadOnlyList<ParameterView> List()
    {
        IParameterTarget[] targets;
        lock (_gate) targets = _targets.Values.ToArray();
        return targets
            .SelectMany(target => target.Parameters.Select(descriptor => new ParameterView(
                target.ParameterScope,
                descriptor.Name,
                descriptor.Kind,
                // A secret is settable and never readable: this is the one place that decides it.
                descriptor.Secret ? null : target.ReadParameter(descriptor.Name),
                descriptor.Secret ? null : descriptor.DefaultValue,
                descriptor.Apply,
                descriptor.Secret,
                descriptor.Minimum,
                descriptor.Maximum,
                descriptor.Description)))
            .OrderBy(view => view.Scope, StringComparer.Ordinal)
            .ThenBy(view => view.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<Result> SetAsync(string scope, string name, string value, CancellationToken cancellationToken)
    {
        IParameterTarget? target;
        lock (_gate) _targets.TryGetValue(scope, out target);
        if (target is null) return Result.Failure(new(ErrorCode.Invalid, $"Parameter scope is not registered: {scope}"));

        var descriptor = target.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (descriptor is null) return Result.Failure(new(ErrorCode.Invalid, $"Unknown parameter: {scope}/{name}"));
        if (CheckValue(descriptor, value) is { } rejected) return Result.Failure(rejected);

        var applied = await target.ApplyParameterAsync(name, value, cancellationToken).ConfigureAwait(false);
        if (!applied.Ok) return applied;

        var json = JsonSerializer.Serialize(new StoredParameter(value), JsonOptions);
        var persisted = await state.WriteAsync(ScopeKey(scope), name, json, cancellationToken).ConfigureAwait(false);
        return persisted.Ok
            ? Result.Success()
            : Result.Failure(new(ErrorCode.Unavailable,
                $"{scope}/{name} was applied but could not be persisted; it will not survive a restart.",
                persisted.Error?.Message));
    }

    public async ValueTask<Result> RestoreAsync(CancellationToken cancellationToken)
    {
        IParameterTarget[] targets;
        lock (_gate) targets = _targets.Values.ToArray();
        foreach (var target in targets)
        {
            foreach (var descriptor in target.Parameters)
            {
                var stored = await state.ReadAsync(ScopeKey(target.ParameterScope), descriptor.Name, cancellationToken).ConfigureAwait(false);
                // Note: for a nullable value, Result<T>.Ok is false when the value is null — "missing" must be
                // read off Error, or every unset parameter looks like a failed read.
                if (stored.Error is { } readError) return Result.Failure(readError);
                if (stored.Value is null) continue;

                StoredParameter? parameter;
                try { parameter = JsonSerializer.Deserialize<StoredParameter>(stored.Value, JsonOptions); }
                catch (JsonException ex)
                {
                    return Result.Failure(new(ErrorCode.Invalid,
                        $"Persisted parameter is not readable: {target.ParameterScope}/{descriptor.Name}", ex.Message));
                }
                if (parameter is null) continue;

                var applied = await target.ApplyParameterAsync(descriptor.Name, parameter.Value, cancellationToken)
                    .ConfigureAwait(false);
                // A value that is no longer valid is reported rather than silently dropped: the file says one
                // thing and the process is doing another, which is exactly what a person needs to be told.
                if (!applied.Ok)
                    return Result.Failure(new(ErrorCode.Invalid,
                        $"Persisted parameter is no longer accepted: {target.ParameterScope}/{descriptor.Name} = {parameter.Value}",
                        applied.Error?.Message));
            }
        }
        return Result.Success();
    }

    /// <summary>Type and range only; meaning belongs to the component.</summary>
    private static Error? CheckValue(ParameterDescriptor descriptor, string value)
    {
        switch (descriptor.Kind)
        {
            case ParameterKind.Integer:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    return new(ErrorCode.Invalid, $"{descriptor.Name} must be an integer.");
                return InRange(descriptor, integer);
            case ParameterKind.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return new(ErrorCode.Invalid, $"{descriptor.Name} must be a number.");
                return InRange(descriptor, number);
            case ParameterKind.Boolean:
                return bool.TryParse(value, out _) ? null : new(ErrorCode.Invalid, $"{descriptor.Name} must be true or false.");
            default:
                return value.Length <= 4096 ? null : new(ErrorCode.Invalid, $"{descriptor.Name} is too long.");
        }
    }

    private static Error? InRange(ParameterDescriptor descriptor, double value)
    {
        if (descriptor.Minimum is { } minimum && value < minimum)
            return new(ErrorCode.Invalid, $"{descriptor.Name} must be at least {minimum}.");
        if (descriptor.Maximum is { } maximum && value > maximum)
            return new(ErrorCode.Invalid, $"{descriptor.Name} must be at most {maximum}.");
        return null;
    }

    /// <summary>
    /// A scope is an address ("channel.conversation#test"), a key in the state store is a file name. Encoding
    /// here keeps that store strict about what it will write, instead of widening its rules for everybody.
    /// The rule itself lives in <see cref="StateName"/> because the graph needs the same one.
    /// </summary>
    public static string ScopeKey(string scope) => StateName.Encode(scope);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record StoredParameter(string Value);
}
