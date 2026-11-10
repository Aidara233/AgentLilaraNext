using System.Text.Json;
using ALN.Contracts;
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
        int keepActivations = KeepRecentActivations, double correction = 1.0)
    {
        ArgumentNullException.ThrowIfNull(history);
        // A request starts where the last checkpoint left off. Everything before it is still in the timeline and still
        // readable — it is simply not what the next answer needs, and carrying it is what made requests grow.
        var checkpoint = LastCheckpoint(history);
        var replayable = history.Skip(checkpoint).Where(entry => ShouldReplay(entry, toolsActExternally)).ToArray();
        var calls = CallNames(replayable);
        // The budget is in the provider's units; the estimate comes out of a character model. The correction turns one
        // into the other, and it is applied to each candidate estimate rather than to the budget so that "how much does
        // keeping one more round cost" stays comparable with the budget itself.
        for (var keep = Math.Clamp(keepActivations, 0, CountActivations(replayable)); ; keep--)
        {
            var messages = Messages(replayable, OldestKeptIndex(replayable, keep), calls, out var folded, out var pictures);
            var tokens = TokenEstimator.Corrected(TokenEstimator.Estimate(messages), correction);
            // At zero kept activations there is nowhere left to go: what remains is the outline itself, and cutting
            // into that is compaction's job (a summary), not folding's.
            if (tokens <= budgetTokens || keep == 0)
                return new ReplayPlan(messages, tokens, keep, folded, pictures);
        }
    }

    /// <summary>
    /// Whether a timeline entry belongs in a later request.
    ///
    /// A checkpoint always does — it is the only thing standing for everything before it. A step that carried tool calls
    /// also always does: its results are in the timeline, and a request that leaves the call out answers a call nobody
    /// made, which every provider rejects outright. So those go in even if an older build happened to attach a channel
    /// view to them.
    ///
    /// Everything else with a channel view is carried by the <c>&lt;channel_messages&gt;</c> block instead, so
    /// replaying it would say the same thing twice. Pure-text assistant entries are scratch: nobody saw them, and in a
    /// Session with tools the model already has its own words through that block. Sessions without tools keep the old
    /// meaning — the text was the reply, so it must be replayed.
    /// </summary>
    public static bool ShouldReplay(SessionHistoryEntry entry, bool scratchIsNotConversation) =>
        entry.Role == CheckpointRole ||
        entry.ToolCalls is { Count: > 0 } ||
        (entry.ChannelMessages is null && !(scratchIsNotConversation && entry.Role == "assistant"));

    /// <summary>
    /// The role a checkpoint is written with. System, because that is what it is: not something anybody said, and not
    /// something the model said — a statement about the conversation, made by the loop that kept it.
    /// </summary>
    public const string CheckpointRole = "system";

    /// <summary>Where the request may start: the last checkpoint, or the first record when there is none.</summary>
    public static int LastCheckpoint(IReadOnlyList<SessionHistoryEntry> history)
    {
        for (var index = history.Count - 1; index >= 0; index--)
            if (history[index].Role == CheckpointRole) return index;
        return 0;
    }

    private static IReadOnlyList<ModelMessage> Messages(IReadOnlyList<SessionHistoryEntry> replayable, int oldestKept,
        IReadOnlyDictionary<string, string> calls, out int folded, out IReadOnlyList<ReplayedPicture> pictures)
    {
        folded = 0;
        var carried = new List<ReplayedPicture>();
        pictures = carried;
        var messages = new List<ModelMessage>(replayable.Count);
        for (var index = 0; index < replayable.Count; index++)
        {
            var entry = replayable[index];
            if (index >= oldestKept)
            {
                messages.Add(new ModelMessage(entry.Role, entry.Content, entry.ToolCalls, entry.ToolCallId));
                // A picture the record brought is a pointer, and this is exactly where it would stop travelling: the
                // message above is its text alone. Remembered instead of read — this builder is synchronous and owns no
                // file port. SessionManager fills these in before the request goes out.
                if (entry.Attachments is { Count: > 0 } brought)
                    carried.Add(new ReplayedPicture(messages.Count - 1, brought));
                continue;
            }
            // Old: the structure stays, the payload goes. A tool call without its matching result is refused by every
            // provider, so both halves are kept — only their sizes are cut, never their pairing. A checkpoint is never
            // folded: it is already the smallest thing that can stand for what came before it.
            if (entry.Role == CheckpointRole)
            {
                messages.Add(new ModelMessage(entry.Role, entry.Content));
                continue;
            }
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
/// <param name="Pictures">
/// Messages that brought a picture and were rendered as their text only, because reading a file is not something this
/// builder can do. The timeline already holds the pointer (<see cref="SessionHistoryEntry.Attachments"/>); whoever owns
/// the file port fills these in. Empty when nothing carried one, which is the ordinary case and costs nothing.
/// </param>
public sealed record ReplayPlan(
    IReadOnlyList<ModelMessage> Messages,
    int EstimatedTokens,
    int KeptActivations,
    int FoldedEntries,
    IReadOnlyList<ReplayedPicture>? Pictures = null)
{
    /// <summary>Nothing to replay: what a Session with no history is handed, and equal to an empty build.</summary>
    public static ReplayPlan Empty { get; } = new([], 0, 0, 0);
}

/// <summary>
/// A replayed message that carried a picture, and where it sits in <see cref="ReplayPlan.Messages"/>.
///
/// A record of "there is something to read here", not a read: the builder decides what a request says, and a file port
/// is not part of that. Kept as a position rather than as the message itself so that filling it in is a replacement in
/// one list rather than a rebuild of the request.
/// </summary>
public sealed record ReplayedPicture(int Index, IReadOnlyList<MediaRef> Media);
