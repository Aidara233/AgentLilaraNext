using ALN.Contracts.Models;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The estimate a Session plans against, checked against the one measurement that matters: a real request from the
/// live deployment, 341,890 characters of messages answered as 81,355 input tokens (4.20 chars/token, 5.5% Chinese).
///
/// The rules being defended: never under-estimate that corpus by much (under-estimating is what makes a provider
/// refuse a request, and the history that caused it stays), and do not let a different script change the answer by a
/// factor of two — a conversation entirely in Chinese costs about four times what its character count suggests.
/// </summary>
public sealed class TokenEstimatorTests
{
    [Fact]
    public void A_settled_fact_is_estimated_high_but_not_absurdly_high()
    {
        // The measured mix: 5.5% Chinese, the rest Latin, JSON keys, paths and punctuation.
        var (sample, actualTokens) = Measured(341_890, cjkShare: 0.055);

        var estimated = TokenEstimator.Estimate(sample);

        Assert.True(estimated > actualTokens, $"estimate {estimated} should not under-shoot {actualTokens}");
        // 30% is the ceiling: a Session that compacts a third earlier than needed is annoying, one that does not
        // compact at all is dead.
        Assert.True(estimated < actualTokens * 1.30, $"estimate {estimated} is more than 30% over {actualTokens}");
    }

    [Fact]
    public void Text_entirely_in_chinese_is_not_counted_like_text_in_english()
    {
        // The mistake a single flat ratio makes. 1000 Chinese characters are ~700-1400 tokens; 1000 Latin characters
        // are ~150-250. Anyone measuring only the mixed corpus would price both at ~240.
        var chinese = string.Concat(Enumerable.Repeat("这是一段相当普通的中文文本，用来估计长度。", 40)); // 800 chars
        var english = new string('a', 800);

        var chineseTokens = TokenEstimator.Estimate(chinese);
        var englishTokens = TokenEstimator.Estimate(english);

        Assert.True(chineseTokens > englishTokens * 2,
            $"Chinese {chineseTokens} vs Latin {englishTokens}: the split is what makes this honest");
        Assert.True(chineseTokens >= 600, $"800 Chinese characters cannot cost {chineseTokens} tokens");
    }

    [Fact]
    public void Message_and_tool_frames_cost_something_even_when_they_carry_nothing()
    {
        // A hundred empty messages are not free, and neither are tool schemas: they ride along on every single
        // request and are the easiest part of the body to forget when asking whether the body fits.
        var empty = new ModelMessage("user", "");
        Assert.Equal(4, TokenEstimator.Estimate(empty));

        var withCall = new ModelMessage("assistant", null,
            [new ModelToolCall("call_1", "fs_read", """{"path":"memory/自我/block.md"}""")]);
        Assert.True(TokenEstimator.Estimate(withCall) > TokenEstimator.Estimate(empty) + 4);

        var tool = new ModelToolDefinition("fs_read", "Read one file.", """{"type":"object","properties":{"path":{"type":"string"}}}""");
        var noTools = TokenEstimator.Estimate([empty]);
        var tools = TokenEstimator.Estimate([tool]);
        Assert.True(tools > 20, $"a tool definition cannot cost {tools} tokens");
        Assert.True(noTools < tools);

        Assert.Equal(0, TokenEstimator.Estimate((IEnumerable<ModelToolDefinition>?)[]));
        Assert.Equal(0, TokenEstimator.Estimate((string?)null));
    }

    [Fact]
    public void The_error_multiplier_says_what_the_estimate_missed_by()
    {
        Assert.Equal(1, TokenEstimator.Error(100, 100), 3);
        Assert.Equal(0.5, TokenEstimator.Error(200, 100), 3);
        Assert.Equal(0, TokenEstimator.Error(0, 100), 3);
    }

    /// <summary>
    /// A body with the composition of the measured one, and what the provider actually charged for it. The characters
    /// are synthetic — the shape is what the constants were calibrated on.
    /// </summary>
    private static (string Sample, int Tokens) Measured(int characters, double cjkShare)
    {
        const string chinese = "记忆组里写着这个人说过的话，还有几件没有做完的事情。";
        const string latin = """{"path":"modules/s-c534/state.json","step":176,"ok":true} """;
        var builder = new System.Text.StringBuilder(characters);
        var cjkWanted = (int)(characters * cjkShare);
        var cjkWritten = 0;
        while (builder.Length < characters)
        {
            if (cjkWritten < cjkWanted)
            {
                builder.Append(chinese);
                cjkWritten += chinese.Length;
            }
            else builder.Append(latin);
        }
        // 4.20 characters per token is the measurement; the sample is trimmed to the same length afterwards.
        var sample = builder.ToString(0, characters);
        return (sample, (int)Math.Round(characters / 4.20));
    }
}
