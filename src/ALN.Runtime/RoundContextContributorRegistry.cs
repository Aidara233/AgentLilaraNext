using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>Thread-safe registry for request-scoped context contributors.</summary>
public sealed class RoundContextContributorRegistry : IRoundContextContributorRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IRoundContextContributor> _contributors = new(StringComparer.Ordinal);

    public Result Register(IRoundContextContributor contributor)
    {
        if (contributor is null || !IsValidId(contributor.Id))
            return Result.Failure(new(ErrorCode.Invalid, "Round context contributor id is invalid."));
        lock (_gate)
        {
            if (!_contributors.TryAdd(contributor.Id, contributor))
                return Result.Failure(new(ErrorCode.Conflict, $"Round context contributor is already registered: {contributor.Id}"));
        }
        return Result.Success();
    }

    public Result Unregister(string contributorId)
    {
        if (!IsValidId(contributorId)) return Result.Failure(new(ErrorCode.Invalid, "Round context contributor id is invalid."));
        lock (_gate) _contributors.Remove(contributorId);
        return Result.Success();
    }

    public IReadOnlyList<IRoundContextContributor> Snapshot()
    {
        lock (_gate) return _contributors.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    private static bool IsValidId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
