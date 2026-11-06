using System.Text;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// What replaces the beginning of a long conversation when it stops fitting.
///
/// Folding (see <see cref="SessionReplay"/>) is tried first, because it costs nothing and loses nothing: the outline of
/// old work is still true. Compaction is what happens when even the outline is too big — it costs a model call and it
/// loses detail permanently, so it is the second resort, not the first.
///
/// The shape is a checkpoint rather than a sliding window, and that is an economic decision as much as a corrective one:
/// a request whose prefix changes every turn cannot be cached by the provider, and the measured deployment was paying
/// for 80.7k of its 81k input tokens out of the prefix cache. A checkpoint walks a long way, then jumps once — so the
/// full price is paid once per jump instead of on every turn.
///
/// What a summary must contain is short enough to state: what was promised, what was done, what is still open. Not a
/// narrative — a narrative of a long session is exactly as long as the session.
/// </summary>
public static class SessionCompaction
{
    /// <summary>
    /// How many of the newest activations are never summarized. Compaction and folding keep the same kind of window for
    /// the same reason: the current turn is always the newest one, and a model that cannot see what it just did will
    /// redo it.
    /// </summary>
    public const int KeepRecentActivations = 4;

    /// <summary>
    /// The most a checkpoint may be. A summary that grows with each compaction is not a summary: it has to be cheap
    /// enough that replacing a segment with it is always a win.
    /// </summary>
    public const int MaxSummaryChars = 2_000;

    /// <summary>One tool result is quoted this far into the material before it becomes a length.</summary>
    public const int MaterialCharsPerEntry = 600;

    /// <summary>
    /// What the summarizing model is asked to do. Written as instructions to a reader who was not there: it has the
    /// timeline, not the conversation, and the difference is what the demands are for.
    /// </summary>
    public static string Instruction(int maxChars) =>
        $"""
        你在替一个长期运行的会话做压缩。下面【材料】是按时间顺序摘出来的记录：谁说了什么、它做了什么、工具返回了什么
        （过长的返回已经标成折叠，只说明长度）。你的输出会被放进"接下来的它"的上下文最前面，替掉这段材料。

        写三节，标题照抄，缺的一节写"无"：
        ## 已承诺
        ## 已做过
        ## 还悬着

        要求：
        - 只写材料里有的。不要推测动机，不要补写没发生的事，不要写"它似乎想…"。
        - 写具体的：谁、哪句话、哪个路径、哪个数字。宁可写原话和路径，也不要写"一些改动""相关文件"。
        - 做完且不再需要回看的事，用一句话带过；还悬着的事，一条一行写清楚差什么。
        - 材料开头若有"上一份简报的结论"，最后加一行"承上：<一句话>"，不要重述上一份简报。
        - 全部不超过 {maxChars} 字。超了就砍细节，先砍"已做过"。
        """;

    /// <summary>
    /// What to summarize, or null when there is nothing worth doing — an empty segment, or a segment that is already
    /// inside the tail nobody summarizes.
    /// </summary>
    /// <param name="inputBudgetTokens">
    /// How big the material may be. The summarizing call is itself a request with a window: a segment too big to show
    /// is summarized from its newest part, with one line saying the rest is only in the carried-forward conclusion.
    /// </param>
    public static CompactionPlan? Plan(IReadOnlyList<SessionHistoryEntry> history, int inputBudgetTokens,
        int keepActivations = KeepRecentActivations)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0) return null;
        var checkpoint = SessionReplay.LastCheckpoint(history);
        var previous = history[checkpoint].Role == SessionReplay.CheckpointRole ? history[checkpoint] : null;
        var first = previous is null ? 0 : checkpoint + 1;

        // Everything from after the last checkpoint, minus the tail that stays verbatim.
        var segmentEnd = TailStart(history, first, keepActivations);
        if (segmentEnd <= first) return null;
        var segment = history.Skip(first).Take(segmentEnd - first).ToArray();
        if (segment.Length == 0) return null;
        var material = Material(segment, inputBudgetTokens);
        if (material.Length == 0) return null;
        var prompt = new StringBuilder();
        if (previous is not null)
            prompt.AppendLine("【上一份简报的结论】").AppendLine(previous.Content).AppendLine();
        prompt.AppendLine("【材料】").Append(material);
        return new CompactionPlan(segment[0].Id, segment[^1].Id, segment.Length, prompt.ToString(), previous?.Id);
    }

    /// <summary>
    /// The material as text: what was said to it, what it said, what it called, what came back. Channel messages are
    /// where a conversation actually lives — the entries that carry them have no content of their own.
    /// </summary>
    private static string Material(IReadOnlyList<SessionHistoryEntry> segment, int budgetTokens)
    {
        var lines = new List<string>(segment.Count);
        foreach (var entry in segment)
        {
            foreach (var message in entry.ChannelMessages ?? [])
                lines.Add($"[{message.Sender ?? message.Source}] {Clip(message.Content)}");
            if (entry.ToolCalls is { Count: > 0 } calls)
                lines.Add($"[它调用了] {string.Join(", ", calls.Select(call => call.Name))}");
            if (entry.Role == "tool")
                lines.Add($"[工具返回] {Clip(entry.Content)}");
            else if (entry.Content is { Length: > 0 } content && entry.ChannelMessages is null)
                lines.Add($"[记录] {Clip(content)}");
        }
        // Newest first while trimming: the end of a segment is what the next turn is closest to.
        var kept = new List<string>();
        var tokens = 0;
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var cost = TokenEstimator.Estimate(lines[index]) + 4;
            if (tokens + cost > budgetTokens) break;
            kept.Add(lines[index]);
            tokens += cost;
        }
        kept.Reverse();
        if (kept.Count == 0) return string.Empty;
        var omitted = lines.Count - kept.Count;
        return omitted == 0
            ? string.Join('\n', kept)
            : $"（更早的 {omitted} 行没有放进来，只有上面那句承上代表它们）\n" + string.Join('\n', kept);
    }

    private static string Clip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "（空）";
        return text.Length <= MaterialCharsPerEntry
            ? text
            : string.Concat(text.AsSpan(0, MaterialCharsPerEntry), $"…（共 {text.Length} 字）");
    }

    /// <summary>
    /// Where the verbatim tail begins: the first entry of the newest <paramref name="keepActivations"/> activations at
    /// or after <paramref name="first"/>.
    /// </summary>
    private static int TailStart(IReadOnlyList<SessionHistoryEntry> history, int first, int keep)
    {
        if (keep <= 0) return history.Count;
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = first; index < history.Count; index++)
            if (seen.Add(history[index].ActivationId)) order.Add(history[index].ActivationId);
        if (keep >= order.Count) return first;
        var kept = order.Skip(order.Count - keep).ToHashSet(StringComparer.Ordinal);
        for (var index = first; index < history.Count; index++)
            if (kept.Contains(history[index].ActivationId)) return index;
        return history.Count;
    }

    /// <summary>
    /// The summary as it should be stored: trimmed to what a checkpoint may cost, and refused when it is empty. A
    /// checkpoint with no content would silently erase the segment it claims to stand for.
    /// </summary>
    public static string? Accept(string? summary)
    {
        var trimmed = summary?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= MaxSummaryChars
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, MaxSummaryChars), "\n（简报被截断：它超过了上限）");
    }
}

/// <summary>
/// One compaction, decided but not performed.
/// </summary>
/// <param name="FirstCovered">The id of the first record the summary would stand for.</param>
/// <param name="LastCovered">The id of the last one — what the checkpoint records as <c>CoversUpTo</c>.</param>
/// <param name="PreviousCheckpoint">The checkpoint this one supersedes, if there is one. Kept as an id so the chain
/// stays readable without keeping the text in every request.</param>
public sealed record CompactionPlan(
    string FirstCovered,
    string LastCovered,
    int Entries,
    string Prompt,
    string? PreviousCheckpoint);
