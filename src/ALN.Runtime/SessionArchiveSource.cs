using ALN.Contracts;
using ALN.Contracts.Archive;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// What Sessions put in the bin. A Session is the one thing the host mints by itself — a script hands over a
/// spec and a hash becomes a directory — so it is the one thing that can pile up unseen. There are four ways
/// it stops being useful, and each of them is a fact this source can check, not a guess about value:
/// nobody knows whose it is, its Channel is gone, its spec can never match again, or nobody has spoken to it
/// for a long time.
/// </summary>
public sealed class SessionArchiveSource(ISessionManager sessions, IChannelManager channels, int idleDays = 30)
    : IArchiveSource
{
    public string Kind => ArchiveKinds.Session;

    public async ValueTask<Result<IReadOnlyList<ArchiveCandidate>>> ListAsync(CancellationToken cancellationToken)
    {
        var listed = await sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!listed.Ok || listed.Value is null)
            return Result<IReadOnlyList<ArchiveCandidate>>.Failure(
                listed.Error ?? new(ErrorCode.Unavailable, "Sessions could not be listed."));

        var live = channels.List().Select(channel => channel.Address.ToString()).ToHashSet(StringComparer.Ordinal);
        var cutoff = idleDays > 0 ? DateTimeOffset.UtcNow.AddDays(-idleDays) : (DateTimeOffset?)null;
        var candidates = new List<ArchiveCandidate>();
        foreach (var session in listed.Value)
        {
            var reason = session.Channel is null ? SessionArchiveReasons.ChannelUnknown
                : !live.Contains(session.Channel) ? SessionArchiveReasons.ChannelGone
                : cutoff is not null && session.LastActiveAt is { } lastActive && lastActive < cutoff
                    ? SessionArchiveReasons.Idle
                    : null;
            if (reason is null) continue;
            candidates.Add(new(Kind, session.Id, session.Bytes, session.LastActiveAt, session.Channel, reason));
        }
        return Result<IReadOnlyList<ArchiveCandidate>>.Success(candidates);
    }

    public async ValueTask<Result<ArchiveReceipt>> ArchiveAsync(ArchiveCandidate candidate, IArchiveSink sink,
        CancellationToken cancellationToken)
    {
        var id = candidate.Id;
        // The note is what makes a hand-recovery possible: the manifest says where the material came from, so
        // an operator who has lost the database (or the source) still knows what belongs where.
        var note = new Dictionary<string, string> { ["origin"] = $"sessions/{id}" };
        var claim = await sink.ClaimAsync(Kind, id, candidate.Owner, candidate.Reason, note, cancellationToken)
            .ConfigureAwait(false);
        if (!claim.Ok || claim.Value is null)
            return Result<ArchiveReceipt>.Failure(claim.Error ?? new(ErrorCode.Unavailable, "Archive entry was not claimed."));

        var moved = await sessions.ArchiveAsync(id, claim.Value.PayloadPath, cancellationToken).ConfigureAwait(false);
        if (!moved.Ok)
        {
            // Nothing went in, so nothing may stay behind looking like it did.
            await sink.ReleaseAsync(new ArchivedItem(Kind, id, id, claim.Value.Directory, 0, DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);
            return Result<ArchiveReceipt>.Failure(moved.Error!);
        }

        var completed = await sink.CompleteAsync(claim.Value, cancellationToken).ConfigureAwait(false);
        return completed.Ok && completed.Value is not null
            ? Result<ArchiveReceipt>.Success(new(Kind, id, completed.Value.Bytes))
            : Result<ArchiveReceipt>.Failure(completed.Error ?? new(ErrorCode.Unavailable, "Archived entry was not finished."));
    }

    public async ValueTask<Result> RestoreAsync(ArchivedItem item, IArchiveSink sink, CancellationToken cancellationToken)
    {
        var payload = Path.Combine(item.Path, ArchiveLayout.Payload);
        var restored = await sessions.RestoreAsync(item.SourceId, payload, cancellationToken).ConfigureAwait(false);
        if (!restored.Ok) return restored;
        // Only after the material is back in place: an entry that disappears while its payload is still in the
        // bin would be a session that exists nowhere.
        return await sink.ReleaseAsync(item, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Why a Session is offered. Stable strings: an operator reads them in a report, a script may branch on them.
/// </summary>
public static class SessionArchiveReasons
{
    /// <summary>Written before the Channel was recorded, so nobody can say which Channel owns it.</summary>
    public const string ChannelUnknown = "channel-unknown";

    /// <summary>Its Channel no longer exists.</summary>
    public const string ChannelGone = "channel-gone";

    /// <summary>Nothing has talked to it for longer than the idle window.</summary>
    public const string Idle = "idle";
}
