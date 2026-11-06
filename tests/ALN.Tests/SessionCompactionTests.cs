using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// Compaction: what gets replaced by a summary, what the summary is shown, and what it is allowed to be.
///
/// The order matters and is the thing being defended: folding is free and true, compaction costs a model call and loses
/// detail. So compaction is only reached when the outline itself no longer fits — and when it is reached, it must not
/// summarize the tail the model still needs, must not lose what the previous checkpoint concluded, and must not be
/// allowed to grow with every pass.
/// </summary>
public sealed class SessionCompactionTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_segment_is_what_is_old_and_the_tail_is_left_alone()
    {
        var history = Conversation(activations: 10);

        var plan = SessionCompaction.Plan(history, inputBudgetTokens: 100_000);

        Assert.NotNull(plan);
        // Ten activations, four of them kept verbatim: six turns of material, ending right before the tail begins.
        Assert.Equal(6 * 3, plan!.Entries);
        var tail = history.Where(entry => entry.ActivationId == "a5").Last();
        Assert.Equal(tail.Id, plan.LastCovered);
        Assert.Null(plan.PreviousCheckpoint);
        // The material is what a reader who was not there needs: what was said, what was called, what came back.
        Assert.Contains("看看这个", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("它调用了", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("工具返回", plan.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_result_is_quoted_as_a_length_rather_than_carried_again()
    {
        var history = new List<SessionHistoryEntry>
        {
            User("a1", 1),
            Assistant("a1", 2, new ModelToolCall("c1", "fs_read", """{"path":"x.md"}""")),
            Tool("a1", 2, "c1", new string('r', 9_000)),
            User("a2", 3), User("a3", 4), User("a4", 5), User("a5", 6)
        };

        var plan = SessionCompaction.Plan(history, inputBudgetTokens: 100_000);

        Assert.NotNull(plan);
        Assert.Contains("共 9000 字", plan!.Prompt, StringComparison.Ordinal);
        // Quoted to the clip length, not carried: 9,000 characters became 600.
        Assert.DoesNotContain(new string('r', SessionCompaction.MaterialCharsPerEntry + 1), plan.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_pass_carries_the_previous_conclusion_instead_of_restating_it()
    {
        var history = Conversation(activations: 10);
        var first = new SessionHistoryEntry("checkpoint:1", "a4:compaction", 3, SessionReplay.CheckpointRole,
            "## 已承诺\n把房子看一遍。\n## 已做过\n无\n## 还悬着\n还没看完。", At, CoversUpTo: "a4:user");
        history.Insert(3 * 3, first);

        var plan = SessionCompaction.Plan(history, inputBudgetTokens: 100_000);

        Assert.NotNull(plan);
        // The new segment starts after the old checkpoint: the six turns it already stands for are not summarized twice.
        Assert.DoesNotContain("a0:user", plan!.FirstCovered, StringComparison.Ordinal);
        Assert.Equal(first.Id, plan.PreviousCheckpoint);
        Assert.Contains("上一份简报的结论", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("把房子看一遍", plan.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_summarized_when_everything_is_still_in_the_tail()
    {
        var history = Conversation(activations: 3);

        Assert.Null(SessionCompaction.Plan(history, inputBudgetTokens: 100_000));
        Assert.Null(SessionCompaction.Plan([], inputBudgetTokens: 100_000));
    }

    [Fact]
    public void A_summary_is_refused_when_empty_and_cut_when_it_breaks_its_own_limit()
    {
        Assert.Null(SessionCompaction.Accept(null));
        Assert.Null(SessionCompaction.Accept("   \n  "));
        var long_ = "## 已承诺\n" + new string('s', SessionCompaction.MaxSummaryChars) + "\n## 还悬着\n等一个回复。";
        var accepted = SessionCompaction.Accept(long_);
        Assert.NotNull(accepted);
        Assert.Contains("中段已省略", accepted!, StringComparison.Ordinal);
        // Both ends survive: what was promised at the top, and what is still open at the bottom — the last section is
        // the one nothing else can reconstruct.
        Assert.StartsWith("## 已承诺", accepted, StringComparison.Ordinal);
        Assert.EndsWith("等一个回复。", accepted, StringComparison.Ordinal);
        Assert.True(accepted.Length <= SessionCompaction.MaxSummaryChars,
            $"a checkpoint may not cost {accepted.Length} characters");
        // A checkpoint that says nothing would silently erase the segment it claims to stand for.
        Assert.Equal("## 已承诺\n无", SessionCompaction.Accept("  ## 已承诺\n无  "));
    }

    [Fact]
    public void Material_that_does_not_fit_is_cut_from_the_oldest_end()
    {
        // The summarizing call is itself a request with a window. When the segment is too big to show, the part closest
        // to now is what gets summarized, and the omission is stated rather than silently dropped.
        var history = Conversation(activations: 60);

        var plan = SessionCompaction.Plan(history, inputBudgetTokens: 60);

        Assert.NotNull(plan);
        Assert.Contains("没有放进来", plan!.Prompt, StringComparison.Ordinal);
        Assert.Contains("看看这个", plan.Prompt, StringComparison.Ordinal);
    }

    private static List<SessionHistoryEntry> Conversation(int activations)
    {
        var history = new List<SessionHistoryEntry>();
        for (var turn = 0; turn < activations; turn++)
        {
            var activation = $"a{turn}";
            history.Add(User(activation, turn * 10));
            var call = new ModelToolCall($"c{turn}", "fs_read", $$"""{"path":"memory/{{turn}}.md"}""");
            history.Add(Assistant(activation, turn * 10 + 1, call));
            history.Add(Tool(activation, turn * 10 + 1, call.Id, $"第 {turn} 个文件的内容"));
        }
        return history;
    }

    private static SessionHistoryEntry User(string activation, int step) =>
        new($"{activation}:user", activation, step, "user", null, At,
            ChannelMessages: [new($"m-{activation}", "adapter.file", "owner", "看看这个", At)]);

    private static SessionHistoryEntry Assistant(string activation, int step, params ModelToolCall[] calls) =>
        new($"{activation}:assistant:{step}", activation, step, "assistant", null, At, ToolCalls: calls);

    private static SessionHistoryEntry Tool(string activation, int step, string callId, string content) =>
        new($"{activation}:tool:{step}:{callId}", activation, step, "tool", content, At, ToolCallId: callId);
}
