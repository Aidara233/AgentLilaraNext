using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Archive;

namespace ALN.Archive;

/// <summary>
/// The recycle bin. It owns exactly three things: where things physically go, how a second entry for a taken
/// id is named, and what a restore is allowed to assume. It does not decide what is worth keeping — that is
/// each source's judgement, expressed as candidates — and it never deletes on its own: emptying the bin is a
/// separate decision nobody has made yet.
/// </summary>
public sealed class ArchiveManager : IArchiveRegistry, IArchiveSink
{
    public const string ServiceId = ArchiveService.Registry;

    /// <summary>The manifest written inside every entry: what it is, where it came from, why it went in.</summary>
    public const string ManifestName = ArchiveLayout.Manifest;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly List<IArchiveSource> _sources = [];
    private readonly object _gate = new();

    public ArchiveManager(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _root = Path.Combine(Path.GetFullPath(dataRoot), "archive");
    }

    /// <summary>Where the bin lives, so an operator can look without being told the layout twice.</summary>
    public string Root => _root;

    public Result Register(IArchiveSource source)
    {
        if (source is null) return Result.Failure(new(ErrorCode.Invalid, "Archive source is required."));
        if (!IsToken(source.Kind))
            return Result.Failure(new(ErrorCode.Invalid, $"Archive kind must be 1-128 ASCII letters, digits, '-', '_' or '.': {source.Kind}"));
        lock (_gate)
        {
            // One kind, one source: two sources writing one namespace would make "which of these is this id"
            // a question nobody can answer later.
            if (_sources.Any(existing => string.Equals(existing.Kind, source.Kind, StringComparison.Ordinal)))
                return Result.Failure(new(ErrorCode.Conflict, $"An archive source is already registered for kind: {source.Kind}"));
            _sources.Add(source);
            return Result.Success();
        }
    }

    public IReadOnlyList<string> Kinds
    {
        get { lock (_gate) return _sources.Select(source => source.Kind).ToArray(); }
    }

    public async ValueTask<Result<IReadOnlyList<ArchiveCandidate>>> CandidatesAsync(string? kind,
        CancellationToken cancellationToken)
    {
        var kindError = FindKind(kind, out var selected, allowNull: true);
        if (kindError is not null) return Result<IReadOnlyList<ArchiveCandidate>>.Failure(kindError);
        var found = new List<ArchiveCandidate>();
        foreach (var source in selected)
        {
            var listed = await source.ListAsync(cancellationToken).ConfigureAwait(false);
            // One broken source must not hide what the others have to offer.
            if (!listed.Ok || listed.Value is null) continue;
            found.AddRange(listed.Value.Where(candidate => IsToken(candidate.Id)));
        }
        return Result<IReadOnlyList<ArchiveCandidate>>.Success(found);
    }

    public ValueTask<Result<IReadOnlyList<ArchivedItem>>> ArchivedAsync(string? kind, CancellationToken cancellationToken)
    {
        var kindError = FindKind(kind, out var selected, allowNull: true);
        if (kindError is not null) return ValueTask.FromResult(Result<IReadOnlyList<ArchivedItem>>.Failure(kindError));
        var kinds = selected.Count > 0
            ? selected.Select(source => source.Kind).ToArray()
            : Directory.Exists(_root)
                ? Directory.GetDirectories(_root).Select(Path.GetFileName).OfType<string>().Where(IsToken).ToArray()
                : [];
        var items = new List<ArchivedItem>();
        foreach (var name in kinds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.AddRange(ReadEntries(name));
        }
        return ValueTask.FromResult(Result<IReadOnlyList<ArchivedItem>>.Success(items));
    }

    public async ValueTask<Result<ArchiveReport>> ArchiveAsync(string kind, IReadOnlyList<string>? ids, bool dryRun,
        CancellationToken cancellationToken)
    {
        var kindError = FindKind(kind, out var selected, allowNull: false);
        if (kindError is not null) return Result<ArchiveReport>.Failure(kindError);
        var source = selected[0];
        var listed = await source.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!listed.Ok || listed.Value is null)
            return Result<ArchiveReport>.Failure(listed.Error ?? new(ErrorCode.Unavailable, "Archive candidates could not be listed."));

        var wanted = ids is { Count: > 0 } ? ids.ToHashSet(StringComparer.Ordinal) : null;
        var candidates = listed.Value.Where(candidate => wanted is null || wanted.Contains(candidate.Id)).ToArray();
        var outcomes = new List<ArchiveOutcome>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dryRun)
            {
                outcomes.Add(new(source.Kind, candidate.Id, "archived", Done: false, candidate.Bytes, candidate.Reason));
                continue;
            }
            var archived = await source.ArchiveAsync(candidate, this, cancellationToken).ConfigureAwait(false);
            outcomes.Add(archived.Ok && archived.Value is not null
                ? new(source.Kind, candidate.Id, "archived", Done: true, archived.Value.Bytes)
                : new(source.Kind, candidate.Id, "archived", Done: false, candidate.Bytes,
                    archived.Error?.Message ?? "Archive failed."));
        }

        // Explicit ids are an operator's decision, not a machine verdict: a bin you may only fill with what the
        // system already considers garbage is not a bin. The source still decides whether it owns the id, and
        // says so if it does not.
        if (wanted is not null)
        {
            var offered = candidates.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in wanted.Where(id => !offered.Contains(id)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = new ArchiveCandidate(source.Kind, id, 0, Reason: "requested");
                if (dryRun)
                {
                    outcomes.Add(new(source.Kind, id, "archived", Done: false, 0, "Not offered by this source; would be taken on request."));
                    continue;
                }
                var archived = await source.ArchiveAsync(requested, this, cancellationToken).ConfigureAwait(false);
                outcomes.Add(archived.Ok && archived.Value is not null
                    ? new(source.Kind, id, "archived", Done: true, archived.Value.Bytes)
                    : new(source.Kind, id, "archived", Done: false, 0, archived.Error?.Message ?? "Archive failed."));
            }
        }
        return Result<ArchiveReport>.Success(new(dryRun, outcomes));
    }

    public async ValueTask<Result<ArchiveReport>> RestoreAsync(string kind, IReadOnlyList<string> ids, bool dryRun,
        CancellationToken cancellationToken)
    {
        if (ids is null || ids.Count == 0)
            return Result<ArchiveReport>.Failure(new(ErrorCode.Invalid, "Restore needs at least one id; restoring everything is a decision, not a default."));
        var kindError = FindKind(kind, out var selected, allowNull: false);
        if (kindError is not null) return Result<ArchiveReport>.Failure(kindError);
        var source = selected[0];
        var archived = (await ArchivedAsync(source.Kind, cancellationToken).ConfigureAwait(false)).Value ?? [];
        var byId = archived.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var outcomes = new List<ArchiveOutcome>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(id, out var item))
            {
                outcomes.Add(new(source.Kind, id, "restored", Done: false, 0, "Not in the archive."));
                continue;
            }
            if (dryRun)
            {
                outcomes.Add(new(source.Kind, id, "restored", Done: false, item.Bytes, item.Reason));
                continue;
            }
            var restored = await source.RestoreAsync(item, this, cancellationToken).ConfigureAwait(false);
            outcomes.Add(restored.Ok
                ? new(source.Kind, id, "restored", Done: true, item.Bytes)
                : new(source.Kind, id, "restored", Done: false, item.Bytes, restored.Error?.Message ?? "Restore failed."));
        }
        return Result<ArchiveReport>.Success(new(dryRun, outcomes));
    }

    public ValueTask<Result<ArchiveEntryClaim>> ClaimAsync(string kind, string id, string? owner, string? reason,
        IReadOnlyDictionary<string, string>? note, CancellationToken cancellationToken)
    {
        if (!IsToken(kind) || !IsToken(id))
            return ValueTask.FromResult(Result<ArchiveEntryClaim>.Failure(new(ErrorCode.Invalid, "Archive kind and id must be tokens.")));
        try
        {
            var (entryName, entryDirectory) = ClaimEntryDirectory(kind, id);
            Directory.CreateDirectory(entryDirectory);
            return ValueTask.FromResult(Result<ArchiveEntryClaim>.Success(
                new ArchiveEntryClaim(kind, id, entryName, entryDirectory, note, owner, reason)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Result<ArchiveEntryClaim>.Failure(
                new(ErrorCode.Unavailable, $"Could not claim an archive entry for {kind}/{id}.", ex.Message)));
        }
    }

    public ValueTask<Result<ArchivedItem>> CompleteAsync(ArchiveEntryClaim claim, CancellationToken cancellationToken)
    {
        if (claim is null) return ValueTask.FromResult(Result<ArchivedItem>.Failure(new(ErrorCode.Invalid, "Archive claim is required.")));
        if (!Directory.Exists(claim.Directory))
            return ValueTask.FromResult(Result<ArchivedItem>.Failure(new(ErrorCode.Invalid, $"Archive entry is gone: {claim.Directory}")));
        try
        {
            // The payload is what the source contributed; the entry's own bytes include the manifest written
            // right after, so the number a person reads is the number that will be freed by emptying the bin.
            var bytes = DirectoryBytes(Path.Combine(claim.Directory, ArchiveLayout.Payload));
            var item = new ArchivedItem(claim.Kind, claim.EntryName, claim.Id, claim.Directory, bytes,
                DateTimeOffset.UtcNow, claim.Owner, claim.Reason, claim.Note);
            WriteManifest(item);
            return ValueTask.FromResult(Result<ArchivedItem>.Success(item));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Result<ArchivedItem>.Failure(
                new(ErrorCode.Unavailable, $"Could not finish archiving {claim.Kind}/{claim.Id}.", ex.Message)));
        }
    }

    public ValueTask<Result> ReleaseAsync(ArchivedItem item, CancellationToken cancellationToken)
    {
        if (item is null) return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, "Archived item is required.")));
        try
        {
            if (!Directory.Exists(item.Path)) return ValueTask.FromResult(Result.Success());
            var manifest = Path.Combine(item.Path, ManifestName);
            if (File.Exists(manifest)) File.Delete(manifest);
            // Only an entry that gave everything back can disappear: a partly-restored entry stays visible,
            // because "it is still in the bin" is the honest answer for whatever is left.
            if (Directory.EnumerateFileSystemEntries(item.Path).Any())
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Conflict,
                    $"{item.Kind}/{item.Id} still holds files; the entry was kept.")));
            Directory.Delete(item.Path);
            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, $"Could not release {item.Kind}/{item.Id}.", ex.Message)));
        }
    }

    /// <summary>The sources a kind selects, or the reason it selects none. Null kind means "all of them", when
    /// the caller can answer that question (listing can, archiving a particular thing cannot).</summary>
    private Error? FindKind(string? kind, out IReadOnlyList<IArchiveSource> selected, bool allowNull)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                if (!allowNull)
                {
                    selected = [];
                    return new(ErrorCode.Invalid, "This operation needs a kind; see the registered kinds.");
                }
                selected = [.. _sources];
                return null;
            }
            selected = _sources.Where(source => string.Equals(source.Kind, kind, StringComparison.Ordinal)).ToArray();
            return selected.Count > 0
                ? null
                : new(ErrorCode.Invalid, $"No archive source is registered for kind '{kind}'. Known kinds: {string.Join(", ", Kinds)}.");
        }
    }

    /// <summary>
    /// Picks a free directory for a new entry: the id, then the id plus the moment, then a counter. Overwriting
    /// would be the one thing a bin must never do — the old entry would vanish without anybody deciding that.
    /// </summary>
    private (string EntryName, string Directory) ClaimEntryDirectory(string kind, string id)
    {
        var kindRoot = Path.Combine(_root, kind);
        var directory = Path.Combine(kindRoot, id);
        if (!Directory.Exists(directory)) return (id, directory);
        var stamped = $"{id}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
        if (!Directory.Exists(Path.Combine(kindRoot, stamped))) return (stamped, Path.Combine(kindRoot, stamped));
        for (var index = 2; index < 1000; index++)
        {
            var numbered = $"{stamped}-{index}";
            if (!Directory.Exists(Path.Combine(kindRoot, numbered))) return (numbered, Path.Combine(kindRoot, numbered));
        }
        throw new IOException($"No free archive entry name for {kind}/{id}.");
    }

    /// <summary>
    /// Reads entries from disk rather than from memory: the bin is a directory, and something an operator
    /// dropped in by hand (or a manifest written by an older build) must still be listable and restorable.
    /// </summary>
    private IEnumerable<ArchivedItem> ReadEntries(string kind)
    {
        var kindRoot = Path.Combine(_root, kind);
        if (!Directory.Exists(kindRoot)) return [];
        return Directory.GetDirectories(kindRoot)
            .Select(directory => ReadEntry(kind, directory))
            .OfType<ArchivedItem>()
            .OrderBy(item => item.ArchivedAt)
            .ToArray();
    }

    private static ArchivedItem? ReadEntry(string kind, string directory)
    {
        var entryName = Path.GetFileName(directory);
        if (!IsToken(entryName)) return null;
        var manifestPath = Path.Combine(directory, ManifestName);
        try
        {
            if (File.Exists(manifestPath))
            {
                var manifest = JsonSerializer.Deserialize<EntryManifest>(File.ReadAllBytes(manifestPath), JsonOptions);
                if (manifest is not null)
                    return new ArchivedItem(kind, entryName, manifest.Id, directory, manifest.Bytes, manifest.ArchivedAt,
                        manifest.Owner, manifest.Reason, manifest.Note);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable manifest must not hide the entry: it is still in the bin and still restorable.
        }
        // No manifest: the entry name is the only id there is, and a restore has to work with that.
        return new ArchivedItem(kind, entryName, entryName, directory, DirectoryBytes(directory),
            new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero));
    }

    private void WriteManifest(ArchivedItem item)
    {
        // The source's id goes in the manifest, the entry's own name stays the directory: re-archiving the same
        // id twice gives two entries that are still individually addressable.
        var manifest = new EntryManifest(item.SourceId, item.Bytes, item.ArchivedAt, item.Owner, item.Reason, item.Note);
        var path = Path.Combine(item.Path, ManifestName);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static long DirectoryBytes(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                : new FileInfo(directory).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool IsToken(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private sealed record EntryManifest(
        string Id,
        long Bytes,
        DateTimeOffset ArchivedAt,
        string? Owner = null,
        string? Reason = null,
        IReadOnlyDictionary<string, string>? Note = null);
}
