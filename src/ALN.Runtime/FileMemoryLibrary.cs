using System.Globalization;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Memory;

namespace ALN.Runtime;

/// <summary>
/// The memory library on disk: one directory per group, a body in <c>block.md</c>, fragments in <c>*.jsonl</c>.
///
/// It never touches <see cref="System.IO"/> itself — every read and write goes through <see cref="IFileSystemAccess"/>,
/// which is what makes "which directories this process may reach" one decision instead of a convention, and what lets
/// a model tidy the same files with the file tools.
///
/// The whole reading side is defensive on purpose. The files are editable by a language model, which means they will
/// eventually be edited wrongly: a hand-mangled line, a block deleted to "start over", a stray file dropped in the
/// library root. None of that is allowed to turn into "the memory is gone" — a broken line is skipped and counted, a
/// missing block is an empty body, a stray file is ignored, and each of those is reported rather than silently
/// repaired (a library that fixes itself quietly teaches nobody anything).
/// </summary>
public sealed class FileMemoryLibrary(IFileSystemAccess access, TimeProvider? timeProvider = null) : IMemoryLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IFileSystemAccess _access = access;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private string Scope => FileScopes.Memory;

    public async ValueTask<Result<IReadOnlyList<MemoryGroup>>> GroupsAsync(CancellationToken cancellationToken)
    {
        var listing = await _access.ListAsync(Scope, "", cancellationToken).ConfigureAwait(false);
        if (!listing.Ok || listing.Value is null) return Result<IReadOnlyList<MemoryGroup>>.Failure(listing.Error!);

        var groups = new List<MemoryGroup>();
        foreach (var entry in listing.Value)
        {
            // A file in the library root is not a group. It is ignored rather than promoted: the root is a shelf of
            // groups, and turning a stray file into a group would let a typo become a memory.
            if (!entry.IsDirectory) continue;
            var described = await DescribeAsync(entry.Name, cancellationToken).ConfigureAwait(false);
            if (described.Error is { } error) return Result<IReadOnlyList<MemoryGroup>>.Failure(error);
            if (described.Value is { } group) groups.Add(group);
        }
        groups.Sort((left, right) => right.ModifiedAt.CompareTo(left.ModifiedAt));
        return Result<IReadOnlyList<MemoryGroup>>.Success(groups);
    }

    public async ValueTask<Result<MemoryGroup?>> GroupAsync(string name, CancellationToken cancellationToken)
    {
        if (MemoryGroupNames.Validate(name).Error is { } invalid) return Result<MemoryGroup?>.Failure(invalid);
        var stat = await _access.StatAsync(Scope, name, cancellationToken).ConfigureAwait(false);
        if (stat.Error is { } error) return Result<MemoryGroup?>.Failure(error);
        if (stat.Value is null || !stat.Value.IsDirectory) return Result<MemoryGroup?>.Success(null);
        return await DescribeAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<IReadOnlyList<FileNode>>> TreeAsync(string name, CancellationToken cancellationToken)
    {
        if (MemoryGroupNames.Validate(name).Error is { } invalid) return Result<IReadOnlyList<FileNode>>.Failure(invalid);
        var tree = await _access.TreeAsync(Scope, name, cancellationToken).ConfigureAwait(false);
        return tree.Error is { } error
            ? Result<IReadOnlyList<FileNode>>.Failure(error)
            : Result<IReadOnlyList<FileNode>>.Success(tree.Value ?? []);
    }

    public async ValueTask<Result<MemoryBlock?>> ReadBlockAsync(string name, CancellationToken cancellationToken)
    {
        if (MemoryGroupNames.Validate(name).Error is { } invalid) return Result<MemoryBlock?>.Failure(invalid);
        var stat = await _access.StatAsync(Scope, name, cancellationToken).ConfigureAwait(false);
        if (stat.Error is { } statError) return Result<MemoryBlock?>.Failure(statError);
        if (stat.Value is null || !stat.Value.IsDirectory) return Result<MemoryBlock?>.Success(null);

        var block = await _access.ReadAsync(Scope, BlockPath(name), cancellationToken).ConfigureAwait(false);
        if (block.Error is { } readError) return Result<MemoryBlock?>.Failure(readError);
        // A group without a block is a group that has only fragments so far: a normal state, and not "gone".
        return Result<MemoryBlock?>.Success(block.Value is null
            ? new MemoryBlock(name, "", stat.Value.ModifiedAt, Exists: false)
            : new MemoryBlock(name, block.Value.Text, block.Value.ModifiedAt, Exists: true));
    }

    public async ValueTask<Result<MemoryBlock>> WriteBlockAsync(string name, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (MemoryGroupNames.Validate(name).Error is { } invalid) return Result<MemoryBlock>.Failure(invalid);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MemoryLimits.MaxBlockBytes)
            return Result<MemoryBlock>.Failure(new(ErrorCode.Invalid,
                $"The block is larger than {MemoryLimits.MaxBlockBytes} bytes; a body that big is a document, not a conclusion."));

        var existed = await ExistsAsync(name, cancellationToken).ConfigureAwait(false);
        if (existed.Error is { } probeError) return Result<MemoryBlock>.Failure(probeError);
        if (!existed.Value)
        {
            var groups = await CountGroupsAsync(cancellationToken).ConfigureAwait(false);
            if (groups.Error is { } countError) return Result<MemoryBlock>.Failure(countError);
            if (groups.Value >= MemoryLimits.MaxGroups)
                return Result<MemoryBlock>.Failure(new(ErrorCode.Invalid,
                    $"The library already holds {groups.Value} groups (limit {MemoryLimits.MaxGroups}). " +
                    "Merge or drop a group before adding another."));
        }

        var written = await _access.WriteAsync(Scope, BlockPath(name), text, cancellationToken).ConfigureAwait(false);
        if (written.Error is { } writeError) return Result<MemoryBlock>.Failure(writeError);
        return Result<MemoryBlock>.Success(new MemoryBlock(name, text, written.Value!.ModifiedAt, Exists: true));
    }

    public async ValueTask<Result<MemoryAppend>> AppendLineAsync(string name, string text, string? who, string? source,
        CancellationToken cancellationToken)
    {
        if (MemoryGroupNames.Validate(name).Error is { } invalid) return Result<MemoryAppend>.Failure(invalid);
        var normalized = MemoryText.Normalize(text);
        if (normalized.Length == 0)
            return Result<MemoryAppend>.Failure(new(ErrorCode.Invalid, "A fragment needs some text."));
        if (normalized.Length > MemoryLimits.MaxLineText)
            return Result<MemoryAppend>.Failure(new(ErrorCode.Invalid,
                $"A fragment is longer than {MemoryLimits.MaxLineText} characters; write the whole thing as a block instead."));

        var existed = await ExistsAsync(name, cancellationToken).ConfigureAwait(false);
        if (existed.Error is { } probeError) return Result<MemoryAppend>.Failure(probeError);
        if (!existed.Value)
        {
            var groups = await CountGroupsAsync(cancellationToken).ConfigureAwait(false);
            if (groups.Error is { } countError) return Result<MemoryAppend>.Failure(countError);
            if (groups.Value >= MemoryLimits.MaxGroups)
                return Result<MemoryAppend>.Failure(new(ErrorCode.Invalid,
                    $"The library already holds {groups.Value} groups (limit {MemoryLimits.MaxGroups}). " +
                    "Merge or drop a group before adding another."));
        }

        var line = new MemoryLine(MemoryQuotes.NewId(), normalized, _time.GetUtcNow(),
            string.IsNullOrWhiteSpace(who) ? null : who.Trim(),
            string.IsNullOrWhiteSpace(source) ? null : source.Trim());
        var json = JsonSerializer.Serialize(new LineRecord(line.Id, line.Text, line.At, line.Who, line.Source), JsonOptions);
        var appended = await _access.AppendAsync(Scope, LinesPath(name), json + "\n", cancellationToken).ConfigureAwait(false);
        if (appended.Error is { } appendError) return Result<MemoryAppend>.Failure(appendError);

        var described = await DescribeAsync(name, cancellationToken).ConfigureAwait(false);
        if (described.Error is { } describeError) return Result<MemoryAppend>.Failure(describeError);
        var lines = described.Value?.Lines ?? 0;
        return Result<MemoryAppend>.Success(new MemoryAppend(line, CreatedGroup: !existed.Value,
            Note: ConsolidationNote(name, lines)));
    }

    public async ValueTask<Result<MemorySearch>> SearchLinesAsync(string name, string query, int limit,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (MemoryGroupNames.Validate(name).Error is { } invalid) return Result<MemorySearch>.Failure(invalid);
        var terms = MemoryRanking.Terms(query);
        var block = await ReadBlockAsync(name, cancellationToken).ConfigureAwait(false);
        if (block.Error is { } blockError) return Result<MemorySearch>.Failure(blockError);
        var quoted = block.Value is { Exists: true } body
            ? new HashSet<string>(MemoryQuotes.Find(body.Text), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var scan = await ScanLinesAsync(name, cancellationToken).ConfigureAwait(false);
        if (scan.Error is { } scanError) return Result<MemorySearch>.Failure(scanError);
        var (lines, skipped) = scan.Value;

        var hits = lines
            .Select(line => new MemoryLineHit(line, MemoryLineRanking.Score(line, terms, now, quoted.Contains(line.Id))))
            // A query that matches nothing gets an empty answer rather than the newest lines dressed up as a match;
            // only "no query at all" means "whatever is recent".
            .Where(hit => terms.Count == 0 || hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Line.At)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
        return Result<MemorySearch>.Success(new MemorySearch(hits, skipped));
    }

    public async ValueTask<Result<IReadOnlyList<MemoryLine>>> ReadLinesAsync(string name, IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        if (MemoryGroupNames.Validate(name).Error is { } invalid) return Result<IReadOnlyList<MemoryLine>>.Failure(invalid);
        var wanted = new HashSet<string>(ids ?? [], StringComparer.Ordinal);
        if (wanted.Count == 0) return Result<IReadOnlyList<MemoryLine>>.Success([]);

        var scan = await ScanLinesAsync(name, cancellationToken).ConfigureAwait(false);
        if (scan.Error is { } scanError) return Result<IReadOnlyList<MemoryLine>>.Failure(scanError);
        // Order follows the caller's list, so a block's quotes read in the order they were written.
        var found = scan.Value.Lines
            .Where(line => wanted.Contains(line.Id))
            .GroupBy(line => line.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var ordered = (ids ?? []).Where(found.ContainsKey).Select(id => found[id]).ToList();
        return Result<IReadOnlyList<MemoryLine>>.Success(ordered);
    }

    public async ValueTask<Result<bool>> DropLineAsync(string name, string id, CancellationToken cancellationToken)
    {
        if (MemoryGroupNames.Validate(name).Error is { } invalid) return Result<bool>.Failure(invalid);
        if (string.IsNullOrWhiteSpace(id))
            return Result<bool>.Failure(new(ErrorCode.Invalid, "DropLine needs the id of the fragment to remove."));

        var tree = await _access.TreeAsync(Scope, name, cancellationToken).ConfigureAwait(false);
        if (tree.Error is { } treeError) return Result<bool>.Failure(treeError);
        var files = (tree.Value ?? [])
            .Where(node => !node.IsDirectory)
            .Where(node => node.Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var dropped = false;
        foreach (var file in files)
        {
            var path = $"{name}/{file.Path}";
            var read = await _access.ReadAsync(Scope, path, cancellationToken).ConfigureAwait(false);
            // A file that cannot be read is left exactly as it is: not deleting what we could not look at is the only
            // safe answer, and the caller is told nothing was removed.
            if (read.Error is not null || read.Value is null) continue;

            var kept = new List<string>();
            var hit = false;
            foreach (var raw in read.Value.Text.Split('\n'))
            {
                if (raw.Trim().Length == 0) continue;
                // Lines that do not parse are kept verbatim: this call removes one fragment, it does not tidy the file.
                if (Parse(raw.Trim()) is { } line && string.Equals(line.Id, id, StringComparison.Ordinal))
                {
                    hit = true;
                    continue;
                }
                kept.Add(raw);
            }
            if (!hit) continue;

            var rewritten = kept.Count == 0 ? "" : string.Join('\n', kept) + "\n";
            var written = await _access.WriteAsync(Scope, path, rewritten, cancellationToken).ConfigureAwait(false);
            if (written.Error is { } writeError) return Result<bool>.Failure(writeError);
            dropped = true;
        }
        return Result<bool>.Success(dropped);
    }

    public async ValueTask<Result> DropGroupAsync(string name, CancellationToken cancellationToken)
    {
        if (MemoryGroupNames.Validate(name).Error is { } invalid) return Result.Failure(invalid);
        // Recursive because a group is a directory with files in it; dropping what is not there is a success, so
        // "forget this group" needs no read first.
        return await _access.DeleteAsync(Scope, name, recursive: true, cancellationToken).ConfigureAwait(false);
    }

    // ---- internals ------------------------------------------------------------------------------

    private static string BlockPath(string name) => $"{name}/{IMemoryLibrary.BlockFileName}";

    private static string LinesPath(string name) => $"{name}/{IMemoryLibrary.LinesFileName}";

    private async ValueTask<Result<bool>> ExistsAsync(string name, CancellationToken cancellationToken)
    {
        var stat = await _access.StatAsync(Scope, name, cancellationToken).ConfigureAwait(false);
        if (stat.Error is { } error) return Result<bool>.Failure(error);
        return Result<bool>.Success(stat.Value is { IsDirectory: true });
    }

    private async ValueTask<Result<int>> CountGroupsAsync(CancellationToken cancellationToken)
    {
        var listing = await _access.ListAsync(Scope, "", cancellationToken).ConfigureAwait(false);
        if (listing.Error is { } error) return Result<int>.Failure(error);
        return Result<int>.Success(listing.Value?.Count(entry => entry.IsDirectory) ?? 0);
    }

    /// <summary>One group's header numbers. Walks the group, because "how much is in here" is not a property of its
    /// directory entry.</summary>
    private async ValueTask<Result<MemoryGroup?>> DescribeAsync(string name, CancellationToken cancellationToken)
    {
        var tree = await _access.TreeAsync(Scope, name, cancellationToken).ConfigureAwait(false);
        if (tree.Error is { } treeError) return Result<MemoryGroup?>.Failure(treeError);
        var nodes = tree.Value ?? [];
        if (nodes.Count == 0)
        {
            // An empty directory is still a group: it was made, and saying it is not there would hide the mess.
            var stat = await _access.StatAsync(Scope, name, cancellationToken).ConfigureAwait(false);
            return stat.Error is { } statError
                ? Result<MemoryGroup?>.Failure(statError)
                : Result<MemoryGroup?>.Success(stat.Value is null
                    ? null
                    : new MemoryGroup(name, stat.Value.ModifiedAt, 0, 0));
        }

        var scan = await ScanLinesAsync(name, cancellationToken).ConfigureAwait(false);
        if (scan.Error is { } scanError) return Result<MemoryGroup?>.Failure(scanError);
        var modified = nodes.Max(node => node.ModifiedAt);
        return Result<MemoryGroup?>.Success(new MemoryGroup(name, modified, scan.Value.Lines.Count,
            nodes.Where(node => !node.IsDirectory).Sum(node => node.Bytes)));
    }

    /// <summary>
    /// Every line of a group, active and archived, plus how many could not be read. Files are visited newest first so
    /// the ceiling keeps the recent ones when a group was never consolidated.
    /// </summary>
    private async ValueTask<Result<(List<MemoryLine> Lines, int Skipped)>> ScanLinesAsync(string name,
        CancellationToken cancellationToken)
    {
        var lines = new List<MemoryLine>();
        var skipped = 0;
        var tree = await _access.TreeAsync(Scope, name, cancellationToken).ConfigureAwait(false);
        if (tree.Error is { } treeError) return Result<(List<MemoryLine>, int)>.Failure(treeError);

        var files = (tree.Value ?? [])
            .Where(node => !node.IsDirectory)
            .Where(node => node.Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(node => node.ModifiedAt)
            .ToList();

        foreach (var file in files)
        {
            if (lines.Count >= MemoryLimits.SearchCeiling) break;
            var read = await _access.ReadAsync(Scope, $"{name}/{file.Path}", cancellationToken).ConfigureAwait(false);
            // A file that cannot be read is skipped, not fatal: the rest of the group is still the group.
            if (read.Error is not null) { skipped++; continue; }
            if (read.Value is null) continue;
            foreach (var raw in read.Value.Text.Split('\n'))
            {
                if (lines.Count >= MemoryLimits.SearchCeiling) break;
                var text = raw.Trim();
                if (text.Length == 0) continue;
                if (Parse(text) is { } line) lines.Add(line);
                else skipped++;
            }
        }
        return Result<(List<MemoryLine>, int)>.Success((lines, skipped));
    }

    private static MemoryLine? Parse(string json)
    {
        try
        {
            var record = JsonSerializer.Deserialize<LineRecord>(json, JsonOptions);
            if (record is null || string.IsNullOrWhiteSpace(record.Id) || string.IsNullOrWhiteSpace(record.Text)) return null;
            // A hand-written line with no usable time cannot be ranked, and defaulting it to "now" would quietly
            // promote it to the top of every recall; default(DateTimeOffset) is how "no time" arrives here.
            if (record.At == default) return null;
            return new MemoryLine(record.Id, record.Text, record.At, record.Who, record.Source);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The sentence a writer gets back when a group has grown past what one body can rest on. It names the two moves
    /// that actually work with the tools at hand, because "please consolidate" without them is advice nobody can take.
    /// </summary>
    private static string? ConsolidationNote(string name, int lines)
    {
        if (lines < MemoryLimits.LineWarning) return null;
        var heavy = lines >= MemoryLimits.MaxLinesPerGroup;
        var lead = heavy
            ? $"{name} 已经有 {lines} 条碎片，超过单组上限 {MemoryLimits.MaxLinesPerGroup}。"
            : $"{name} 已经有 {lines} 条碎片。";
        return lead + $"把要留下的折叠进 {IMemoryLibrary.BlockFileName}（引用 [l-…]），" +
               $"再把 {IMemoryLibrary.LinesFileName} 用 fs_move 搬到 " +
               $"{IMemoryLibrary.HistoryDirectory}/lines-<年月>.jsonl——归档的行照样能被引用，不会消失。";
    }

    /// <summary>One line as it is stored. Kept private and minimal: the file is the format, and its shape is a contract
    /// only with the model that writes it by hand.</summary>
    private sealed record LineRecord(string Id, string Text, DateTimeOffset At, string? Who, string? Source);
}
