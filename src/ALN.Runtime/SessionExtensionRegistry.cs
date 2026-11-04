using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// Every module that extends a session round, kept in three lists (docs/22, C1).
///
/// Ids are unique per kind, not across kinds: the memory module is one module that feeds a floating line and a set
/// of tools, and forcing it to invent two names for itself would be bookkeeping with no reader. Unregistering takes
/// all three at once, because a module that is going away is going away everywhere.
/// </summary>
public sealed class SessionExtensionRegistry : ISessionExtensionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IFloatingContextSource> _floating = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITimelineSource> _timeline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ISessionToolSource> _tools = new(StringComparer.Ordinal);

    public Result Register(IFloatingContextSource source) =>
        Add(_floating, source?.Id, source, "Floating context source");

    public Result Register(ITimelineSource source) =>
        Add(_timeline, source?.Id, source, "Timeline source");

    public Result Register(ISessionToolSource source) =>
        Add(_tools, source?.Id, source, "Session tool source");

    public Result Unregister(string id)
    {
        if (!IsValidId(id)) return Result.Failure(new(ErrorCode.Invalid, "Session extension id is invalid."));
        lock (_gate)
        {
            var removed = _floating.Remove(id) | _timeline.Remove(id) | _tools.Remove(id);
            return removed ? Result.Success() : Result.Failure(new(ErrorCode.Invalid, $"No session extension answers to \"{id}\"."));
        }
    }

    public IReadOnlyList<IFloatingContextSource> Floating
    {
        get { lock (_gate) return _floating.Values.OrderBy(source => source.Id, StringComparer.Ordinal).ToArray(); }
    }

    public IReadOnlyList<ITimelineSource> Timeline
    {
        get { lock (_gate) return _timeline.Values.OrderBy(source => source.Id, StringComparer.Ordinal).ToArray(); }
    }

    public IReadOnlyList<ISessionToolSource> Tools
    {
        get { lock (_gate) return _tools.Values.OrderBy(source => source.Id, StringComparer.Ordinal).ToArray(); }
    }

    private Result Add<T>(Dictionary<string, T> into, string? id, T? source, string kind) where T : class
    {
        if (source is null || !IsValidId(id))
            return Result.Failure(new(ErrorCode.Invalid, $"{kind} id is invalid."));
        lock (_gate)
        {
            if (!into.TryAdd(id!, source))
                return Result.Failure(new(ErrorCode.Conflict, $"{kind} is already registered: {id}"));
        }
        return Result.Success();
    }

    private static bool IsValidId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
