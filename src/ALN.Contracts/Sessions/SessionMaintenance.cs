namespace ALN.Contracts.Sessions;

/// <summary>
/// One Session as maintenance sees it. The Session id is a hash, so everything a keep-or-archive decision
/// needs — whose it is, when it last spoke, how much room it takes — has to be carried here.
/// </summary>
/// <param name="Steps">Committed steps: one per model call, so a turn with tool calls counts more than once.</param>
/// <param name="LastActiveAt">The recorded time, or — for a Session that never recorded one — its newest file.
/// Never null for a directory that exists, because "no answer" would read as "idle since forever".</param>
/// <param name="SupersededAt">When a run was refused because the caller's spec had moved on, if that happened.</param>
public sealed record SessionSummary(
    string Id,
    string? Channel,
    DateTimeOffset? LastActiveAt,
    int Steps,
    long Bytes,
    DateTimeOffset? SupersededAt = null);
