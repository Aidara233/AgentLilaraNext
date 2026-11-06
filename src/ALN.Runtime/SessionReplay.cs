using System.Text.Json;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// A Session's history as a request: which records go in, and how much of each.
///
/// The distinction this rests on: a timeline is what happened, and a request is a view of it. Nothing here writes
/// anything — folding is a rendering decision, taken fresh on every turn, and the records stay as complete as they
/// were written. That is what makes folding safe: the detail is still on disk, and a later turn may show more of it.
///
/// What gets folded, and why exactly that: in a working Session the conversation is small and the work traces are
/// enormous — one measured Session had 14 KB of user text against 465 KB of tool arguments and tool results. So the
/// things worth folding are the ones that have a copy elsewhere: a tool result can be produced again, and the body of
/// a write is on the file it wrote. The model keeps the outline (which tool, which path, how big) and loses the payload.
///
/// Recent activations go in untouched, because folding only pays where nothing is going to look again. The window is
/// counted in activations rather than in messages so that it does not shrink when a turn happens to be chatty.
/// </summary>
public static class SessionReplay
{
    /// <summary>
    /// How many of the newest activations go in as they were written. Counted in activations because a turn is the unit
    /// a model reasons about — "what did I just do" — and the current turn is always the newest activation, so a turn
    /// in the middle of twenty tool calls still sees all twenty.
    /// </summary>
    public const int KeepRecentActivations = 5;

    /// <summary>
    /// Arguments longer than this are payload rather than plan. A path, an id, a query are all far below it; the body
    /// of a file being written, a patch, a batch of records are all far above it.
    /// </summary>
    public const int FoldArgumentsOver = 300;

    /// <summary>
    /// The request body for this history: folded as little as possible, and as much as <paramref name="budgetTokens"/>
    /// requires.
    /// </summary>
    /// <param name="toolsActExternally">
    /// Whether this Session's tools can affect anything outside it. Decides what a step's text meant: with tools, text
    /// nobody saw is scratch and is not replayed; without them, the text <b>was</b> the reply.
    /// </param>
    public static ReplayPlan Build(IReadOnlyList<SessionHistoryEntry> history, bool toolsActExternally, int budgetTokens,
        int keepActivations = KeepRecentActivations)
    {
        ArgumentNullException.ThrowIfNull(history);
        var replayable = history.Where(entry => ShouldReplay(entry, toolsActExternally)).ToArray();
        var calls = CallNames(replayable);
        for (var keep = Math.Clamp(keepActivations, 0, CountActivations(replayable)); ; keep--)
        {
            var messages = Messages(replayable, OldestKeptIndex(replayable, keep), calls, out var folded);
            var tokens = TokenEstimator.Estimate(messages);
            // At zero kept activations there is nowhere left to go: what remains is the outline itself, and cutting
            // into that is compaction's job (a summary), not folding's.
            if (tokens <= budgetTokens || keep == 0)
                return new ReplayPlan(messages, tokens, keep, folded);
        }
    }

    /// <summary>
    /// Whether a timeline entry belongs in a later request.
    ///
    /// A step that carried tool calls is structural, not chat: its results are in the timeline, and a request that
    /// leaves the call out answers a call nobody made — which every provider rejects outright. So those entries always
    /// go in, even if an older build happened to attach a channel view to them.
    ///
    /// Everything else with a channel view is carried by the <c>&lt;channel_messages&gt;</c> block instead, so
    /// replaying it would say the same thing twice. Pure-text assistant entries are scratch: nobody saw them, and in a
    /// Session with tools the model already has its own words through that block. Sessions without tools keep the old
    /// meaning — the text was the reply, so it must be replayed.
    /// </summary>
    public static bool ShouldReplay(SessionHistoryEntry entry, bool scratchIsNotConversation) =>
        entry.ToolCalls is { Count: > 0 } ||
        (entry.ChannelMessages is null && !(scratchIsNotConversation && entry.Role == "assistant"));

    private static IReadOnlyList<ModelMessage> Messages(IReadOnlyList<SessionHistoryEntry> replayable, int oldestKept,
        IReadOnlyDictionary<string, string> calls, out int folded)
    {
        folded = 0;
        var messages = new List<ModelMessage>(replayable.Count);
        for (var index = 0; index < replayable.Count; index++)
        {
            var entry = replayable[index];
            if (index >= oldestKept)
            {
                messages.Add(new ModelMessage(entry.Role, entry.Content, entry.ToolCalls, entry.ToolCallId));
                continue;
            }
            // Old: the structure stays, the payload goes. A tool call without its matching result is refused by every
            // provider, so both halves are kept — only their sizes are cut, never their pairing.
            var toolCalls = entry.ToolCalls is { Count: > 0 } ? entry.ToolCalls.Select(FoldArguments).ToArray() : null;
            var content = entry.Role == "tool" ? FoldedResult(entry, calls) : entry.Content;
            if (toolCalls is not null || entry.Role == "tool") folded++;
            messages.Add(new ModelMessage(entry.Role, content, toolCalls, entry.ToolCallId));
        }
        return messages;
    }

    private static string FoldedResult(SessionHistoryEntry entry, IReadOnlyDictionary<string, string> calls)
    {
        var name = entry.ToolCallId is not null && calls.TryGetValue(entry.ToolCallId, out var found) ? found : "工具";
        var characters = entry.Content?.Length ?? 0;
        return $"（已折叠：{name} 返回的 {characters} 字。原文仍在这台机器上，需要时重新调用同一个工具即可。）";
    }

    /// <summary>
    /// Old arguments, kept as a shape instead of a payload. Still valid JSON, because a request full of broken JSON is
    /// a request that teaches the model to write broken JSON — and the outline is all this is for: which tool, how big.
    /// </summary>
    private static ModelToolCall FoldArguments(ModelToolCall call)
    {
        if (call.ArgumentsJson.Length <= FoldArgumentsOver) return call;
        var folded = JsonSerializer.Serialize(new
        {
            _folded = new { tool = call.Name, characters = call.ArgumentsJson.Length }
        });
        return new ModelToolCall(call.Id, call.Name, folded);
    }

    /// <summary>Which tool each call id belonged to, so a folded result can still say whose it was.</summary>
    private static IReadOnlyDictionary<string, string> CallNames(IReadOnlyList<SessionHistoryEntry> entries)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in entries.SelectMany(entry => entry.ToolCalls ?? []))
            names.TryAdd(call.Id, call.Name);
        return names;
    }

    private static int CountActivations(IReadOnlyList<SessionHistoryEntry> entries)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries) ids.Add(entry.ActivationId);
        return ids.Count;
    }

    /// <summary>The first index belonging to the newest <paramref name="keep"/> activations; everything before it is old.</summary>
    private static int OldestKeptIndex(IReadOnlyList<SessionHistoryEntry> entries, int keep)
    {
        if (keep <= 0) return entries.Count;
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (seen.Add(entry.ActivationId)) order.Add(entry.ActivationId);
        if (keep >= order.Count) return 0;
        var kept = order.Skip(order.Count - keep).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
            if (kept.Contains(entries[index].ActivationId)) return index;
        return entries.Count;
    }
}

/// <summary>
/// One request's body, and what building it cost. Reported rather than kept private: "the request was this big" and
/// "the request was this big because it kept no verbs" are the two facts a signal needs after a provider refuses one.
/// </summary>
public sealed record ReplayPlan(
    IReadOnlyList<ModelMessage> Messages,
    int EstimatedTokens,
    int KeptActivations,
    int FoldedEntries);
