using System.Text.Json;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// What a long conversation looks like as a request.
///
/// The measurement this is built around: one real Session had 14 KB of user text against 465 KB of tool arguments and
/// tool results, and was replayed in full into every request — 81k input tokens after a day of use, almost all of it
/// served from the prefix cache, which is a cache holding the design together rather than a budget.
///
/// The rules being defended: the records are never touched, the pairing of a call with its result is never broken (a
/// provider refuses that), and the same history builds the same bytes every time.
/// </summary>
public sealed class SessionReplayTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Old_work_becomes_an_outline_and_every_call_still_has_its_result()
    {
        var history = Conversation(activations: 6, resultsPerTurn: 2, resultChars: 4_000);

        var plan = SessionReplay.Build(history, toolsActExternally: true, budgetTokens: 1_000_000, keepActivations: 2);

        var toolMessages = plan.Messages.Where(message => message.Role == "tool").ToArray();
        var callIds = plan.Messages.SelectMany(message => message.ToolCalls ?? []).Select(call => call.Id).ToArray();
        // Every call has exactly one result, in the same order, with the same id: dropping either half makes the whole
        // request refusable, and "it worked for a week and then stopped" is the failure that would follow.
        Assert.Equal(callIds, toolMessages.Select(message => message.ToolCallId).ToArray());
        Assert.Equal(history.Count(entry => entry.Role == "tool"), toolMessages.Length);

        // The old ones say so, and say whose they were; the newest two turns still have their full text.
        Assert.Contains("已折叠", toolMessages[0].Content, StringComparison.Ordinal);
        Assert.Contains("fs_read", toolMessages[0].Content, StringComparison.Ordinal);
        Assert.Equal(new string('x', 4_000), toolMessages[^1].Content);
        Assert.Equal(2, plan.KeptActivations);
        Assert.True(plan.FoldedEntries > 0);
    }

    [Fact]
    public void Folding_takes_most_of_the_weight_out_of_a_working_conversation()
    {
        var history = Conversation(activations: 20, resultsPerTurn: 3, resultChars: 6_000);
        var verbatim = SessionReplay.Build(history, true, budgetTokens: int.MaxValue, keepActivations: 20);
        var folded = SessionReplay.Build(history, true, budgetTokens: 20_000);

        // The point of folding at all: the same timeline, a fraction of the request.
        Assert.True(folded.EstimatedTokens < verbatim.EstimatedTokens / 4,
            $"folded {folded.EstimatedTokens} vs verbatim {verbatim.EstimatedTokens}");
        Assert.True(folded.EstimatedTokens <= 20_000);
        Assert.True(folded.KeptActivations < 20);
    }

    [Fact]
    public void The_same_history_builds_the_same_bytes_twice()
    {
        // Nothing here may read the clock, a random id, or the order a dictionary happens to enumerate in: the prefix
        // is what the provider caches, so an unstable rendering costs every turn after it.
        var history = Conversation(activations: 8, resultsPerTurn: 2, resultChars: 3_000);

        var first = SessionReplay.Build(history, true, budgetTokens: 30_000, keepActivations: 3);
        var second = SessionReplay.Build(history, true, budgetTokens: 30_000, keepActivations: 3);

        Assert.Equal(Render(first), Render(second));
    }

    [Fact]
    public void A_payload_of_a_write_is_folded_but_the_arguments_stay_json()
    {
        var body = new string('y', 5_000);
        var call = new ModelToolCall("call_write", "fs_write",
            $$"""{"path":"memory/自我/block.md","content":"{{body}}"}""");
        var history = new List<SessionHistoryEntry>
        {
            User("a1", step: 1),
            Assistant("a1", 2, call),
            Tool("a1", 2, call.Id, "written"),
            User("a2", step: 3),
            Assistant("a2", 4, new ModelToolCall("call_2", "fs_read", """{"path":"x.md"}""")),
            Tool("a2", 4, "call_2", "second turn")
        };

        var plan = SessionReplay.Build(history, true, budgetTokens: 1_000, keepActivations: 1);

        var folded = plan.Messages.First(message => message.ToolCalls is { Count: > 0 }).ToolCalls![0];
        Assert.True(folded.ArgumentsJson.Length < 200, folded.ArgumentsJson);
        using var document = JsonDocument.Parse(folded.ArgumentsJson);
        Assert.Equal("fs_write", document.RootElement.GetProperty("_folded").GetProperty("tool").GetString());
        Assert.Equal(call.ArgumentsJson.Length, document.RootElement.GetProperty("_folded").GetProperty("characters").GetInt32());
        // Short arguments are not touched: a path is the plan, not the payload.
        Assert.Equal("""{"path":"x.md"}""", plan.Messages.Last(message => message.ToolCalls is { Count: > 0 }).ToolCalls![0].ArgumentsJson);
    }

    [Fact]
    public void A_budget_that_cannot_be_met_still_leaves_the_outline_and_says_so()
    {
        // Folding has a floor: the timeline is still a timeline. Saying "the request is 12k against a 1k budget" is the
        // caller's problem to solve with a summary, not something to hide by emptying the history.
        var history = Conversation(activations: 10, resultsPerTurn: 2, resultChars: 2_000);

        var plan = SessionReplay.Build(history, true, budgetTokens: 1);

        Assert.NotEmpty(plan.Messages);
        Assert.Equal(0, plan.KeptActivations);
        Assert.True(plan.EstimatedTokens > 1);
        Assert.All(plan.Messages.Where(message => message.Role == "tool"),
            message => Assert.Contains("已折叠", message.Content, StringComparison.Ordinal));
    }

    [Fact]
    public void Scratch_the_model_never_said_out_loud_is_not_replayed_when_tools_act_externally()
    {
        // A step that produced text and called no tool said nothing anyone saw. Replaying it makes the model re-read its
        // own thinking every turn — and, without tools, the same text *was* the reply, so it has to stay.
        var history = new List<SessionHistoryEntry>
        {
            User("a1", 1),
            new("a1:scratch", "a1", 2, "assistant", "我在想该怎么回答", At),
            User("a2", 3)
        };

        var withTools = SessionReplay.Build(history, toolsActExternally: true, budgetTokens: 1_000_000);
        var withoutTools = SessionReplay.Build(history, toolsActExternally: false, budgetTokens: 1_000_000);

        Assert.DoesNotContain(withTools.Messages, message => message.Content == "我在想该怎么回答");
        Assert.Contains(withoutTools.Messages, message => message.Content == "我在想该怎么回答");
    }

    private static string Render(ReplayPlan plan) =>
        string.Join("\n", plan.Messages.Select(message =>
            $"{message.Role}|{message.ToolCallId}|{message.Content}|" +
            string.Join(",", (message.ToolCalls ?? []).Select(call => $"{call.Id}:{call.Name}:{call.ArgumentsJson}"))));

    /// <summary>
    /// Turns of the shape the live deployment produces: a channel message nobody replays, a decision to call tools, and
    /// results far bigger than the conversation they serve.
    /// </summary>
    private static List<SessionHistoryEntry> Conversation(int activations, int resultsPerTurn, int resultChars)
    {
        var history = new List<SessionHistoryEntry>();
        for (var turn = 1; turn <= activations; turn++)
        {
            var activation = $"activation-{turn:D3}";
            history.Add(User(activation, turn * 10));
            var calls = new ModelToolCall[resultsPerTurn];
            for (var index = 0; index < resultsPerTurn; index++)
                calls[index] = new ModelToolCall($"call_{turn}_{index}", "fs_read", $$"""{"path":"memory/x/{{index}}.md"}""");
            history.Add(Assistant(activation, turn * 10 + 1, calls));
            foreach (var call in calls)
                history.Add(Tool(activation, turn * 10 + 1, call.Id, new string('x', resultChars)));
        }
        return history;
    }

    private static SessionHistoryEntry User(string activation, int step) =>
        new($"{activation}:user", activation, step, "user", null, At,
            ChannelMessages: [new("m1", "adapter.file", "owner", "看看这个", At)]);

    private static SessionHistoryEntry Assistant(string activation, int step, params ModelToolCall[] calls) =>
        new($"{activation}:assistant:{step}", activation, step, "assistant", null, At, ToolCalls: calls);

    private static SessionHistoryEntry Tool(string activation, int step, string callId, string content) =>
        new($"{activation}:tool:{step}:{callId}", activation, step, "tool", content, At, ToolCallId: callId);
}
