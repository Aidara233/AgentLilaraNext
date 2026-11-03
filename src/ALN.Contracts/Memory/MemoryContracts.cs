using System.Text;

namespace ALN.Contracts.Memory;

/// <summary>
/// The two things a memory row can be.
///
/// A <see cref="Fragment"/> is cheap, repeated, and allowed to be wrong: "he said the deploy is Friday". A
/// <see cref="Note"/> is a conclusion somebody paid for by reading several fragments: "the deadline moved to
/// Monday, because the client's freeze starts Friday". Fragments are what accumulates on its own; notes are what
/// stops the fragments from growing forever. Keeping them apart is the whole reason the table has a kind: mixing
/// them means either reciting raw chatter as if it were knowledge, or throwing away the details a conclusion
/// rests on.
/// </summary>
public enum MemoryKind { Fragment = 0, Note = 1 }

/// <summary>
/// What a caller wants written down.
/// </summary>
/// <param name="Scope">
/// Whose memory this is. A plain key rather than an address on purpose: a Session is the one thing in this
/// system that is <b>supposed</b> to be replaced, so memory must not be owned by one — it belongs to the
/// conversation the Session serves (today the connection key, <c>adapter/channel</c>), and outlives every
/// Session that reads or writes it.
/// </param>
/// <param name="Evidence">
/// The original text this entry rests on, quoted rather than summarized. Two reasons it is inline instead of a
/// pointer to a session file: a pointer breaks as soon as that Session is archived, and — worse — a conclusion
/// without its evidence cannot be checked. A model that mis-remembered "Friday" as "Thursday" leaves a trace
/// here, and the next reader can see the disagreement instead of inheriting the error.
/// </param>
/// <param name="Source">Who wrote it — a Session id, a tool name, a person. Null when nobody claims it.</param>
public sealed record MemoryDraft(
    string Scope,
    MemoryKind Kind,
    string Text,
    string? Evidence = null,
    string? Source = null,
    DateTimeOffset? At = null);

/// <summary>One row as stored. <see cref="Id"/> is assigned by the store, not by the caller.</summary>
public sealed record MemoryRecord(
    string Id,
    string Scope,
    MemoryKind Kind,
    string Text,
    string? Evidence,
    string? Source,
    DateTimeOffset At);

/// <summary>A stored row together with why it came back. The score is what lets a caller cut a list without
/// pretending the order was arbitrary.</summary>
public sealed record MemoryHit(MemoryRecord Record, double Score);

/// <summary>How much is in one scope, by kind. The number a reader needs to see a gap it should fill.</summary>
public sealed record MemoryCounts(int Fragments, int Notes)
{
    public int Total => Fragments + Notes;
}

/// <summary>
/// When fragments have piled up and nobody folded them, and the sentence that says so.
///
/// In the contract because two callers must say the same thing: the <c>recall</c> tool (which is where the model
/// can act on it) and the floating context line (which is where it finds out without asking). A threshold that
/// lives in two places is a threshold that drifts, and the failure it guards against is invisible — the store just
/// keeps growing while nothing ever says so.
/// </summary>
public static class MemoryGuidance
{
    /// <summary>Enough fragments that "recent" has stopped meaning anything.</summary>
    public const int FragmentThreshold = 10;

    /// <summary>...and enough fragments per note that the two are out of proportion.</summary>
    public const int FragmentsPerNote = 5;

    public static bool NeedsConsolidation(MemoryCounts counts) =>
        counts.Fragments >= FragmentThreshold && counts.Fragments >= counts.Notes * FragmentsPerNote;

    /// <summary>The nudge, or null when there is no gap worth naming.</summary>
    public static string? ConsolidationHint(MemoryCounts counts) => NeedsConsolidation(counts)
        ? $"碎片已经攒到 {counts.Fragments} 条、笔记还只有 {counts.Notes} 条。把反复出现的、互相印证的那些整理成一两条 note" +
          "（附上原文证据），再用 forget 删掉被吸收的碎片——不整理的话，下次 recall 只会捞回一堆重复的碎片。"
        : null;
}

/// <summary>
/// Persistent memory: fragments and notes, written by sessions and read back by them.
///
/// Deliberately a storage port and nothing more — no "consolidate the fragments" method, because that step needs
/// a model and a reader, and a store that could do it would be a store that decides what is true.
/// </summary>
public interface IMemoryStore
{
    /// <summary>The size a recall returns when the caller does not ask for one.</summary>
    const int DefaultLimit = 20;

    /// <summary>Longest text a row may hold. Rejects a caller trying to park a whole file in a fragment.</summary>
    const int MaxTextLength = 8000;

    /// <summary>Longest evidence a row may quote.</summary>
    const int MaxEvidenceLength = 20000;

    /// <summary>Writes one row and hands back the assigned id.</summary>
    ValueTask<Result<MemoryRecord>> AddAsync(MemoryDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// Best rows for <paramref name="query"/> in this scope, best first — at most <paramref name="limit"/> of
    /// them. An empty query is legal and means "whatever is worth remembering lately", so a session that has
    /// nothing specific to look for still gets the recent shape of things.
    /// </summary>
    ValueTask<Result<IReadOnlyList<MemoryHit>>> SearchAsync(string scope, string query, int limit,
        DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Newest first. What "state of this scope" means without a query.</summary>
    ValueTask<Result<IReadOnlyList<MemoryRecord>>> RecentAsync(string scope, int limit, CancellationToken cancellationToken);

    /// <summary>Null when the scope has no such row — absent is not an error, the same way a missing state key is not one.</summary>
    ValueTask<Result<MemoryRecord?>> ReadEntryAsync(string scope, string id, CancellationToken cancellationToken);

    /// <summary>Forgets one row. Deleting what is not there is a success: callers say "forget this" without asking.</summary>
    ValueTask<Result> ForgetAsync(string scope, string id, CancellationToken cancellationToken);

    ValueTask<Result<MemoryCounts>> CountEntriesAsync(string scope, CancellationToken cancellationToken);
}

/// <summary>
/// The ranking rule, in the contract rather than in one backend, because two stores answering the same query
/// with the same rows and a different order is a bug nobody will find.
///
/// What it does, and why it is this crude:
/// <list type="bullet">
///   <item><b>Term overlap, computed in code.</b> The text is mostly Chinese, where the word boundaries a
///   full-text index's unicode tokenizer does not know mean it matches whole runs of characters or nothing. So a
///   query becomes terms plus, for CJK, character bigrams — crude, portable, and identical in every backend.</item>
///   <item><b>Time decay, half-life 30 days.</b> A memory of last week should beat an equally matching one from
///   last spring. Never zero: old facts stay findable, they just stop crowding the top.</item>
///   <item><b>A small bonus for notes.</b> When both match, the conclusion is the more useful row; the fragments
///   it was drawn from are still one lookup away.</item>
/// </list>
/// </summary>
public static class MemoryRanking
{
    public const double HalfLifeDays = 30;
    public const double NoteBonus = 1.25;

    /// <summary>
    /// Splits a query into the things worth matching. Whitespace and punctuation separate; a run of CJK becomes
    /// its own term plus its overlapping character bigrams, so "部署时间" also matches text containing only
    /// "部署" or only "时间".
    /// </summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var terms = new List<string>();
        var word = new StringBuilder();
        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                Flush(word, terms);
                continue;
            }
            word.Append(char.ToLowerInvariant(c));
        }
        Flush(word, terms);
        return terms.Distinct(StringComparer.Ordinal).ToList();

        static void Flush(StringBuilder word, List<string> terms)
        {
            if (word.Length == 0) return;
            var term = word.ToString();
            word.Clear();
            if (term.Length == 1 && !IsCjk(term[0])) return; // one latin letter matches everything and means nothing
            terms.Add(term);
            if (term.Length <= 2 || !term.Any(IsCjk)) return;
            for (var i = 0; i + 1 < term.Length; i++) terms.Add(term.Substring(i, 2));
        }
    }

    public static bool IsCjk(char c) => c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';

    /// <summary>
    /// 0 when nothing in <paramref name="terms"/> appears in the text; otherwise the share of the query that
    /// matched. A whole term counts for more than its bigrams, so a query matched whole beats one matched by
    /// accident of two characters.
    /// </summary>
    public static double Coverage(string text, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return 0;
        var lower = text.ToLowerInvariant();
        double matched = 0, weight = 0;
        foreach (var term in terms)
        {
            var w = term.Length >= 2 ? 1.0 : 0.5;
            weight += w;
            if (lower.Contains(term, StringComparison.Ordinal)) matched += w;
        }
        return weight <= 0 ? 0 : matched / weight;
    }

    /// <summary>1 at age 0, 0.5 one half-life later, never 0 (old facts stay findable).</summary>
    public static double Decay(DateTimeOffset at, DateTimeOffset now)
    {
        var days = (now - at).TotalDays;
        if (days <= 0) return 1;
        return Math.Pow(0.5, days / HalfLifeDays);
    }

    /// <summary>
    /// The number a store sorts by. An empty query scores on recency alone, so "what is recent" and "what is
    /// relevant" are one call with different arguments rather than two tools.
    /// </summary>
    public static double Score(MemoryRecord record, IReadOnlyList<string> terms, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        var decay = Decay(record.At, now);
        var kind = record.Kind == MemoryKind.Note ? NoteBonus : 1.0;
        if (terms.Count == 0) return decay * kind;
        var text = record.Evidence is null ? record.Text : record.Text + "\n" + record.Evidence;
        return Coverage(text, terms) * (0.4 + 0.6 * decay) * kind;
    }
}
