using ALN.Contracts.Models;

namespace ALN.Runtime;

/// <summary>
/// How big a request is, in the unit the window is measured in.
///
/// Exact token counts need the provider's own tokenizer, which is not in this process and is not the same between
/// providers. So this counts characters and converts, and the conversion is fitted to the one pairing that could be
/// checked end to end: a stored model call from the live deployment, whose 414 messages held 125,436 characters (17.2% of
/// them Chinese, the rest JSON, paths, tool output) and which the provider charged ~74k input tokens for — 0.59 tokens
/// per character between them.
///
/// Two classes, because the average is a trap: a Chinese character costs roughly one and a half tokens while Latin text
/// costs roughly a quarter of one, so a single ratio fitted here under-prices a conversation that is entirely in Chinese
/// by a factor of three. The classes are the smallest split that keeps that from happening.
///
/// ⚠️ The first version of this file was wrong by 2.6×, and the reason is worth keeping: it was calibrated against a
/// file measured in <b>bytes</b>, not characters. Chinese is three bytes per character in UTF-8 and six in an escaped
/// JSON string, so the denominator was inflated and the estimate came out comfortable and useless. A request estimated at
/// 51k was really being charged 81k.
///
/// Every constant here leans the same way — estimate high. Being wrong high means a Session folds history or compacts a
/// little earlier than it had to; being wrong low means the provider refuses the request outright, and since the history
/// that made it too big is still there, every request after it is refused too.
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// Tokens one CJK character costs. Chinese in modern BPE vocabularies sits between 0.7 and 1.7 tokens per character.
    /// </summary>
    private const double TokensPerCjkChar = 1.5;

    /// <summary>
    /// Tokens one other character costs, which is not the same as "text": these requests are full of JSON keys, paths,
    /// GUIDs and separators, and a run of punctuation costs about one token per character the way a word does not.
    /// 0.40 (2.5 chars/token) is fitted to the one pairing that could be checked — see the class comment — and prose
    /// alone would come out cheaper; over-pricing prose is the safe direction, under-pricing code is not.
    /// </summary>
    private const double TokensPerOtherChar = 0.40;

    /// <summary>
    /// What one message costs before its content: a role, two delimiters, and the field names around them. Not zero,
    /// and not worth counting precisely — but a hundred short messages are a hundred of these.
    /// </summary>
    private const int PerMessageOverhead = 4;

    /// <summary>What one tool definition costs before its schema: its name, its "type":"function" wrapper.</summary>
    private const int PerToolOverhead = 8;

    /// <summary>Roughly how many tokens this text will be charged as. Null and empty both cost nothing.</summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cjk = 0;
        foreach (var character in text)
            if (IsCjk(character)) cjk++;
        var other = text.Length - cjk;
        return (int)Math.Ceiling(cjk * TokensPerCjkChar + other * TokensPerOtherChar);
    }

    /// <summary>One message: its content, whatever it asked for, and the frame around it.</summary>
    public static int Estimate(ModelMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var tokens = PerMessageOverhead + Estimate(message.Content) + Estimate(message.Name);
        foreach (var call in message.ToolCalls ?? [])
            tokens += Estimate(call.ArgumentsJson) + Estimate(call.Name) + PerMessageOverhead;
        return tokens;
    }

    /// <summary>A whole request body, less the tool schemas.</summary>
    public static int Estimate(IEnumerable<ModelMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var tokens = 0;
        foreach (var message in messages) tokens += Estimate(message);
        return tokens;
    }

    /// <summary>
    /// One tool as the model is shown it. Schemas are sent on every request and never change within a Session, which
    /// is exactly why they are easy to forget when asking "is this request too big".
    /// </summary>
    public static int Estimate(ModelToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return PerToolOverhead + Estimate(definition.Name) + Estimate(definition.Description) +
               Estimate(definition.ParametersJson);
    }

    /// <summary>A whole toolbox as the model is shown it.</summary>
    public static int Estimate(IEnumerable<ModelToolDefinition>? definitions)
    {
        if (definitions is null) return 0;
        var tokens = 0;
        foreach (var definition in definitions) tokens += Estimate(definition);
        return tokens;
    }

    /// <summary>
    /// What the estimate missed by, as a multiplier: actual / estimated. 1.0 means it was exact. Reported rather than
    /// asserted on, because the point of it is to find out whether the constants above are still right after the
    /// models, the prompts or the conversations change.
    /// </summary>
    public static double Error(int estimated, int actual) => estimated <= 0 ? 0 : (double)actual / estimated;

    private static bool IsCjk(char character) => character switch
    {
        >= '\u4e00' and <= '\u9fff' => true,   // CJK unified ideographs
        >= '\u3400' and <= '\u4dbf' => true,   // extension A
        >= '\uf900' and <= '\ufaff' => true,   // compatibility ideographs
        >= '\u3000' and <= '\u303f' => true,   // CJK punctuation （。「」など）
        >= '\u3040' and <= '\u30ff' => true,   // hiragana and katakana
        >= '\uac00' and <= '\ud7af' => true,   // hangul syllables
        _ => false
    };
}
