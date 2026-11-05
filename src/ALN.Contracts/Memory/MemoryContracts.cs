using System.Text;

namespace ALN.Contracts.Memory;

/// <summary>
/// How two pieces of text are decided to be the same thing.
///
/// In the contract because it decides what "already written down" means for every backend, and a backend that answered
/// that question differently would either duplicate lines or swallow distinct ones. Deliberately blunt: whitespace
/// collapsed, trimmed. Not fuzzy — "部署改到周五" and "部署改到周一" must stay two lines, and a rule clever enough to
/// merge them is a rule that deletes facts.
///
/// Deliberately not case-folded: "Deploy" and "deploy" are two lines, which is visible and cheap to fix, while folding
/// them would silently merge two lines somebody wrote on purpose.
/// </summary>
public static class MemoryText
{
    /// <summary>Trims and collapses every run of whitespace to one space, so formatting is not a difference.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }
}

/// <summary>
/// The ranking primitives, in the contract rather than in one backend, because two readers answering the same query
/// with the same lines and a different order is a bug nobody will find.
///
/// What it does, and why it is this crude:
/// <list type="bullet">
///   <item><b>Term overlap, computed in code.</b> The text is mostly Chinese, where the word boundaries a full-text
///   index's tokenizer does not know mean it matches whole runs of characters or nothing. So a query becomes terms
///   plus, for CJK, character bigrams — crude, portable, and identical everywhere.</item>
///   <item><b>Time decay, half-life 30 days.</b> A fragment from last week should beat an equally matching one from
///   last spring. Never zero: old facts stay findable, they just stop crowding the top.</item>
/// </list>
///
/// What a fragment adds on top of this (a bonus for one the group's block quotes) lives in <c>MemoryLineRanking</c>:
/// the primitives are shared, the shape of each kind is not.
/// </summary>
public static class MemoryRanking
{
    public const double HalfLifeDays = 30;

    /// <summary>
    /// Splits a query into the things worth matching. Whitespace and punctuation separate; a run of CJK becomes its own
    /// term plus its overlapping character bigrams, so "部署时间" also matches text containing only "部署" or "时间".
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
    /// 0 when nothing in <paramref name="terms"/> appears in the text; otherwise the share of the query that matched. A
    /// whole term counts for more than its bigrams, so a query matched whole beats one matched by two characters.
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

    /// <summary>1 at age 0, 0.5 one half-life later, never 0: old facts stay findable.</summary>
    public static double Decay(DateTimeOffset at, DateTimeOffset now)
    {
        var days = (now - at).TotalDays;
        if (days <= 0) return 1;
        return Math.Pow(0.5, days / HalfLifeDays);
    }
}
