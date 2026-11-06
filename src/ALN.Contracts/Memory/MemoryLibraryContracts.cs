using System.Text;

namespace ALN.Contracts.Memory;

/// <summary>
/// One memory group: a directory under the memory scope, a <c>block.md</c> that is its body, and <c>*.jsonl</c> files
/// that are its fragments.
///
/// The levels are deliberately lopsided. The block is a few hundred lines a reader can take in; the lines are an
/// append-only pile that is allowed to be large, wrong, and repetitive. Trying to keep them in one file or one shape
/// is how a memory store turns into either "a pile of fragments nobody folded" or "a summary nobody can check".
/// </summary>
/// <param name="Lines">Fragments in the group, active and archived together: "how much have I been told about this".</param>
/// <param name="Bytes">Everything the group holds, so a reader can see which group is getting heavy.</param>
public sealed record MemoryGroup(string Name, DateTimeOffset ModifiedAt, int Lines, long Bytes)
{
    /// <summary>Whether this group's fragments have outgrown what one block can reasonably rest on (docs/21 §6).</summary>
    public bool NeedsConsolidation => Lines >= MemoryLimits.LineWarning;
}

/// <summary>
/// One fragment: what was said or seen, written down as a line. <see cref="Id"/> is what a block quotes when it wants
/// to stand on this line — the line never changes after it is written, so the quote cannot go stale by being edited.
/// </summary>
/// <param name="Who">How the platform named the speaker when it happened, e.g. <c>小林(同事)</c>. Recorded here because
/// a name cannot be looked up later, and the line outlives the conversation it came from.</param>
/// <param name="Source">Where it came from — a Channel address, a session id, a tool name. Free text on purpose: this
/// is a note to a reader, not a join key.</param>
public sealed record MemoryLine(string Id, string Text, DateTimeOffset At, string? Who = null, string? Source = null);

/// <summary>One group's body. <see cref="Exists"/> is false when the group has no <c>block.md</c> yet — which is a
/// normal state (a group starts as a pile of fragments), and must not be confused with "the group is gone".</summary>
public sealed record MemoryBlock(string Name, string Text, DateTimeOffset ModifiedAt, bool Exists);

/// <summary>What came of writing one line: the line, and the one thing a writer sometimes needs to be told.</summary>
/// <param name="CreatedGroup">True when this write is what made the group. A caller reporting "记下了" should know it
/// also made a place, because that is how a library fills up with one-line groups.</param>
/// <param name="Note">A sentence the caller should pass on — a group that has grown past what one block can hold. Null
/// when there is nothing to say; a warning that nobody needs is how warnings stop being read.</param>
public sealed record MemoryAppend(MemoryLine Line, bool CreatedGroup, string? Note = null);

/// <summary>A fragment that matched, and the number that put it where it is.</summary>
public sealed record MemoryLineHit(MemoryLine Line, double Score);

/// <summary>What a search found, and what it could not read. A skipped line is reported rather than hidden: a file
/// somebody edited by hand is a fact about the library, not a line that quietly disappeared.</summary>
public sealed record MemorySearch(IReadOnlyList<MemoryLineHit> Hits, int Skipped);

/// <summary>
/// The limits, in the contract because two callers must agree on them: the library refuses what it cannot store, and
/// the tools warn at the same numbers. Nothing here deletes anything — a limit is where the store says "this is
/// getting heavy", and a person or the model decides what to do about it (docs/09: retention is forever by default,
/// with an interface to prune, never an automatic expiry).
/// </summary>
public static class MemoryLimits
{
    /// <summary>Groups one library may hold. Past this, adding one is refused: a library with 500 topics is a library
    /// nobody consults, and refusing is the only feedback that says so.</summary>
    public const int MaxGroups = 64;

    /// <summary>Where a group is considered heavy enough to mention. Not a refusal — the line still gets written.</summary>
    public const int LineWarning = 400;

    /// <summary>Fragments one group may hold. Past this an append is still accepted but says the group needs folding,
    /// because the alternative is a caller that loses what somebody just said.</summary>
    public const int MaxLinesPerGroup = 2000;

    /// <summary>Longest text one fragment may carry.</summary>
    public const int MaxLineText = 4000;

    /// <summary>Longest block. A body that large is not a conclusion any more, it is a document with a different owner.</summary>
    public const int MaxBlockBytes = 256 * 1024;

    /// <summary>How many fragments one search may look at, so a group nobody ever folded cannot turn a recall into an
    /// unbounded read.</summary>
    public const int SearchCeiling = 5000;
}

/// <summary>
/// A group's name is a directory name, so it is checked once, here, and refused rather than rewritten — the same
/// policy as a module id and for the same reason: rewriting "a/b" into "a_b" is how two groups become one.
/// </summary>
public static class MemoryGroupNames
{
    /// <summary>Names that would collide with the files inside a group, since a group name is also a directory name and
    /// a reader sees them in the same tree.</summary>
    private static readonly string[] Taken = [FileScopes.Memory];

    public static Result Validate(string? name)
    {
        var split = FilePaths.Split(name);
        if (!split.Ok) return Result.Failure(split.Error!);
        if (split.Value!.Count != 1)
            return Result.Failure(new(ErrorCode.Invalid,
                $"A memory group name is one segment, not a path: '{name}'."));
        if (Taken.Contains(split.Value[0], StringComparer.OrdinalIgnoreCase))
            return Result.Failure(new(ErrorCode.Invalid, $"'{name}' is the library itself, not a group."));
        return Result.Success();
    }

    public static bool IsValid(string? name) => Validate(name).Ok;
}

/// <summary>
/// How a fragment is ranked. The three primitives live in <see cref="MemoryRanking"/> and are shared with the old
/// ordering, because "term overlap, time decay" is one idea however it is applied; what is specific to a fragment is
/// the bonus for one the group's block already leans on.
///
/// Why the bonus: a quoted line is one a conclusion rests on, so when a query could return either that line or an
/// equally-matching one nobody used, the quoted one is the better answer. It never hides the other one — it only
/// decides the order.
/// </summary>
public static class MemoryLineRanking
{
    /// <summary>Multiplier for a fragment the block quotes. Same reasoning as the note bonus it replaces.</summary>
    public const double QuotedBonus = 1.25;

    public static double Score(MemoryLine line, IReadOnlyList<string> terms, DateTimeOffset now, bool quoted)
    {
        ArgumentNullException.ThrowIfNull(line);
        var decay = MemoryRanking.Decay(line.At, now);
        var bonus = quoted ? QuotedBonus : 1.0;
        if (terms.Count == 0) return decay * bonus;
        return MemoryRanking.Coverage(line.Text, terms) * (0.4 + 0.6 * decay) * bonus;
    }
}

/// <summary>
/// The memory library: groups of fragments, one body each, all of it on disk under one scope.
///
/// Deliberately not a store of rows. A group is a directory, a block is a file, fragments are lines — that shape is
/// the contract, not an implementation detail, because it is what lets a model tidy memory with the file tools it
/// already has (move the active fragments into <c>history/</c>, rewrite a block, merge two groups). A library that
/// could not be tidied by moving a file would need one tool per tidying operation forever.
///
/// Nothing here decides what is true, and nothing here deletes on a schedule: the only removals are the ones a caller
/// asks for.
/// </summary>
public interface IMemoryLibrary
{
    /// <summary>The body of every group. The name is the only fixed file name here; everything else is the model's business.</summary>
    const string BlockFileName = "block.md";

    /// <summary>The active fragment file. Append-only; when it is folded away it becomes a file under <see cref="HistoryDirectory"/>.</summary>
    const string LinesFileName = "lines.jsonl";

    /// <summary>Where folded fragments go. A file moved here still answers a <c>[l-…]</c> quote from the block.</summary>
    const string HistoryDirectory = "history";

    /// <summary>
    /// Every group, newest write first — all of them, never a selection. Choosing which groups a round sees is a
    /// decision the caller makes with the whole list; a library that silently dropped the tail would be hiding its own
    /// size, and "there are too many groups" is a fact somebody has to act on, not one to paper over.
    /// </summary>
    ValueTask<Result<IReadOnlyList<MemoryGroup>>> GroupsAsync(CancellationToken cancellationToken);

    /// <summary>Null when there is no such group — absence is not a failure.</summary>
    ValueTask<Result<MemoryGroup?>> GroupAsync(string name, CancellationToken cancellationToken);

    /// <summary>Everything inside one group, relative to the group, so the answer can be shown as the group's tree.</summary>
    ValueTask<Result<IReadOnlyList<FileNode>>> TreeAsync(string name, CancellationToken cancellationToken);

    /// <summary>Null when there is no such group. A group that exists without a block answers with
    /// <see cref="MemoryBlock.Exists"/> false — an empty body is a state, not a missing group.</summary>
    ValueTask<Result<MemoryBlock?>> ReadBlockAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the group's whole body, creating the group if needed. One call, one body: a block is a conclusion
    /// somebody stands behind, and editing it in pieces is how a document ends up disagreeing with itself.
    /// </summary>
    ValueTask<Result<MemoryBlock>> WriteBlockAsync(string name, string text, CancellationToken cancellationToken);

    /// <summary>
    /// Adds one fragment to the group's active file, creating the group if needed. Append is the only write: it is the
    /// one operation that cannot lose a line that somebody else wrote at the same moment, and "he said it again" is a
    /// second line rather than an edit of the first.
    /// </summary>
    ValueTask<Result<MemoryAppend>> AppendLineAsync(string name, string text, string? who, string? source,
        CancellationToken cancellationToken);

    /// <summary>
    /// Best fragments for a query, from the active file and everything archived under <see cref="HistoryDirectory"/>.
    /// An empty query means "the recent ones" — the shape of a group without having to invent a keyword.
    /// </summary>
    ValueTask<Result<MemorySearch>> SearchLinesAsync(string name, string query, int limit, DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Specific fragments by id, wherever they now live — this is what makes a <c>[l-…]</c> quote in a block resolve
    /// after the fragments were folded away. An id that is no longer in the group is simply absent from the answer.
    /// </summary>
    ValueTask<Result<IReadOnlyList<MemoryLine>>> ReadLinesAsync(string name, IReadOnlyList<string> ids,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes one fragment by id, from whichever file now holds it. True when it was there, false when it was already
    /// gone — "forget this" is a statement, not a request to check.
    ///
    /// The line is what a block may quote, so deleting it makes that quote unanswerable; the reader says "that line is
    /// gone" rather than silently dropping the sentence. That is the honest failure: an entry that was wrong should be
    /// deletable, and a conclusion resting on it should stop looking backed.
    /// </summary>
    ValueTask<Result<bool>> DropLineAsync(string name, string id, CancellationToken cancellationToken);

    /// <summary>Removes a whole group, block and fragments together. A name that is not there is a success.</summary>
    ValueTask<Result> DropGroupAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// The ids a block quotes. Kept in the contract because two sides must agree on what a quote looks like: the tool that
/// renders a block has to find them, and a model writing a block has to copy them.
/// </summary>
public static class MemoryQuotes
{
    public const string IdPrefix = "l-";

    /// <summary>Every fragment id quoted in <paramref name="text"/>, in order of first appearance, without duplicates.</summary>
    public static IReadOnlyList<string> Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + IdPrefix.Length < text.Length; index++)
        {
            if (text[index] != '[' || !text.AsSpan(index + 1).StartsWith(IdPrefix, StringComparison.Ordinal)) continue;
            var start = index + 1;
            var end = start;
            while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '-' || text[end] == '_')) end++;
            if (end >= text.Length || text[end] != ']' || end - start <= IdPrefix.Length) continue;
            var id = text[start..end];
            if (seen.Add(id)) found.Add(id);
        }
        return found;
    }

    /// <summary>A fresh id. Random rather than sequential: two processes writing one group must not pick the same one.</summary>
    public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The quote as a block should spell it, so a model copying the rendered form gets it right.</summary>
    public static string Spell(string id) => $"[{id}]";

    /// <summary>Text normalised the way a line is stored, so "the same words" has one definition (see <see cref="MemoryText"/>).</summary>
    public static string NormalizeLine(string text) => MemoryText.Normalize(text);
}
