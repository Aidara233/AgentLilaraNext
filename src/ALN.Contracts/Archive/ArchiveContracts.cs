namespace ALN.Contracts.Archive;

/// <summary>
/// How the rest of the process finds the bin. The id is in the contract so a component can register what it
/// has to offer without referencing the archive package — and so the package can be loaded first, without the
/// host having to hold its type.
/// </summary>
public static class ArchiveService
{
    public const string Registry = "archive.manager";
}

/// <summary>
/// The kinds this build knows how to put away. A name, not a class: an action, a script or an operator types
/// it, so it has to live where they can see it.
/// </summary>
public static class ArchiveKinds
{
    public const string Session = "session";
}

/// <summary>
/// One thing that could be put away, seen where it lives now. <paramref name="Kind"/> is the namespace the
/// source registered under ("session", "model-calls"), and <paramref name="Id"/> means whatever that kind
/// says it means — a session id, a date, a file name.
/// </summary>
/// <param name="Owner">Who the thing belongs to, when that is a meaningful thing to say (a Channel address).</param>
/// <param name="Reason">Why it is being offered, in words the source owns; the archive never interprets it.</param>
public sealed record ArchiveCandidate(
    string Kind,
    string Id,
    long Bytes,
    DateTimeOffset? LastUsedAt = null,
    string? Owner = null,
    string? Reason = null);

/// <summary>
/// Something already in the recycle bin. Two identities on purpose:
/// <paramref name="Id"/> is the entry's name inside the bin — unique, and possibly suffixed because the same
/// thing was put away twice — while <paramref name="SourceId"/> is what the source called it, which is what a
/// restore has to use. Merging them would make a second archiving of one id unaddressable.
/// <paramref name="Path"/> is the directory the archive gave it: the only handle a source needs to take it out.
/// </summary>
public sealed record ArchivedItem(
    string Kind,
    string Id,
    string SourceId,
    string Path,
    long Bytes,
    DateTimeOffset ArchivedAt,
    string? Owner = null,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Note = null);

/// <summary>
/// A kind of thing that can be put away. The source decides what "put away" physically means — a directory
/// move, an export followed by row deletion, a compression — so table rows and directories can share one
/// interface. What a source must not decide is where the archive lives: that is the sink's job, done once.
/// </summary>
public interface IArchiveSource
{
    /// <summary>Stable namespace for this kind; it becomes the directory name under the archive root.</summary>
    string Kind { get; }

    /// <summary>What this source can offer right now. Never includes something already archived.</summary>
    ValueTask<Result<IReadOnlyList<ArchiveCandidate>>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Moves one candidate out of its living place and hands it over. The source owns the claim behind the id
    /// ("this id is mine, and it is here"); the sink owns where it lands.
    /// </summary>
    ValueTask<Result<ArchiveReceipt>> ArchiveAsync(ArchiveCandidate candidate, IArchiveSink sink, CancellationToken cancellationToken);

    /// <summary>
    /// Puts one archived item back where it came from, and refuses when that place is occupied: a restore must
    /// never merge two things into one.
    /// </summary>
    ValueTask<Result> RestoreAsync(ArchivedItem item, IArchiveSink sink, CancellationToken cancellationToken);
}

/// <summary>
/// The archive's own side of the deal: where things physically go, and how they are named when they get
/// there. Implemented once, so every source inherits the same collision rule and the same manifest — and a
/// source only has to know how to put its own material into the place it is given.
/// </summary>
public interface IArchiveSink
{
    /// <summary>
    /// Reserves a free entry directory for <paramref name="kind"/>/<paramref name="id"/>. Never reuses a taken
    /// name: a second entry for the same id gets a suffix, because the one thing a bin must not do is replace
    /// what is already inside it without anybody deciding that.
    /// </summary>
    /// <param name="owner">Whose it was, when the source can say. Written into the manifest, never interpreted.</param>
    /// <param name="reason">Why the source offered it. The only place the bin records a judgement it did not make.</param>
    ValueTask<Result<ArchiveEntryClaim>> ClaimAsync(string kind, string id, string? owner, string? reason,
        IReadOnlyDictionary<string, string>? note, CancellationToken cancellationToken);

    /// <summary>Measures the payload and writes the entry's manifest, once the source has put its material in.</summary>
    ValueTask<Result<ArchivedItem>> CompleteAsync(ArchiveEntryClaim claim, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the entry after its contents were taken back out (or never arrived), manifest included. Refuses
    /// while files are still inside: a half-restored entry must stay visible, because it is still in the bin.
    /// </summary>
    ValueTask<Result> ReleaseAsync(ArchivedItem item, CancellationToken cancellationToken);
}

/// <summary>An entry directory that is reserved for one item but not yet filled in.</summary>
/// <param name="Id">The source's own id, kept as the manifest's record of where this came from.</param>
/// <param name="EntryName">The name the entry actually got — the same id unless it was taken.</param>
public sealed record ArchiveEntryClaim(string Kind, string Id, string EntryName, string Directory,
    IReadOnlyDictionary<string, string>? Note, string? Owner = null, string? Reason = null)
{
    /// <summary>Where the source puts its material: a directory it moves, or files it writes into it.</summary>
    public string PayloadPath => Path.Combine(Directory, ArchiveLayout.Payload);
}

/// <summary>
/// The shape of one entry on disk. Named here rather than in the archive module because a source has to put
/// its material somewhere, and that "somewhere" is part of the deal, not an implementation detail.
/// </summary>
public static class ArchiveLayout
{
    /// <summary>Everything the source contributes goes under this name inside the entry directory.</summary>
    public const string Payload = "payload";

    /// <summary>The entry's own manifest: kind, id, size, when, why, and whatever the source wanted remembered.</summary>
    public const string Manifest = "archive.json";
}

public sealed record ArchiveReceipt(string Kind, string Id, long Bytes);

/// <summary>
/// The one interface an action, a script or a test talks to. Sources register themselves; the registry
/// aggregates and applies dry runs, and never decides on its own that something is worth keeping or dropping.
/// </summary>
public interface IArchiveRegistry
{
    Result Register(IArchiveSource source);

    /// <summary>Kinds that have a source in this process, in registration order.</summary>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>Everything that could be put away right now, across all sources or one kind.</summary>
    ValueTask<Result<IReadOnlyList<ArchiveCandidate>>> CandidatesAsync(string? kind, CancellationToken cancellationToken);

    /// <summary>Everything currently in the bin.</summary>
    ValueTask<Result<IReadOnlyList<ArchivedItem>>> ArchivedAsync(string? kind, CancellationToken cancellationToken);

    /// <summary>Puts candidates away: all of a kind, or the named ids. <paramref name="dryRun"/> first.</summary>
    ValueTask<Result<ArchiveReport>> ArchiveAsync(string kind, IReadOnlyList<string>? ids, bool dryRun,
        CancellationToken cancellationToken);

    /// <summary>Takes items back out of the bin. <paramref name="dryRun"/> first.</summary>
    ValueTask<Result<ArchiveReport>> RestoreAsync(string kind, IReadOnlyList<string> ids, bool dryRun,
        CancellationToken cancellationToken);
}

/// <param name="Action"><c>archived</c> or <c>restored</c>: what was done, or what a dry run would have done.</param>
public sealed record ArchiveOutcome(string Kind, string Id, string Action, bool Done, long Bytes, string? Message = null);

public sealed record ArchiveReport(bool DryRun, IReadOnlyList<ArchiveOutcome> Items)
{
    public int Count => Items.Count;
    public int Done => Items.Count(item => item.Done);
    public long Bytes => Items.Sum(item => item.Bytes);
}
