namespace ALN.Contracts.Prompts;

/// <summary>
/// What a Session chose: one file per group, no file twice. A group is a directory under
/// <c>&lt;dataRoot&gt;/prompts/</c> and a file is one prompt in it, so "at most one per group" is not a
/// validation rule — a dictionary cannot hold the same key twice.
/// </summary>
/// <param name="Groups">Group name → file name (without the extension). Empty means "no prompt at all",
/// which is a legal choice: a script that only performs an action does not need a persona.</param>
public sealed record PromptSelection(IReadOnlyDictionary<string, string> Groups)
{
    public static PromptSelection None { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    public bool IsEmpty => Groups.Count == 0;

    /// <summary>Stable order for comparison: a selection is a set of pairs, not a sequence.</summary>
    public IEnumerable<KeyValuePair<string, string>> Ordered() =>
        Groups.OrderBy(pair => pair.Key, StringComparer.Ordinal);
}

/// <summary>One file as the catalog sees it: what it is called, what it hashes to, and where it can be edited.</summary>
public sealed record PromptEntry(string Name, long Bytes, string Fingerprint, string Path);

/// <summary>One directory as the catalog sees it.</summary>
public sealed record PromptGroup(string Group, IReadOnlyList<PromptEntry> Entries);

/// <summary>
/// What is on disk right now. <paramref name="Warnings"/> are the degradations that did not stop anything:
/// a file that could not be read, skipped so the rest of the group — and the host — keep working. They are
/// reported rather than swallowed: a silently missing prompt is a Session answering as somebody else.
/// </summary>
public sealed record PromptCatalog(IReadOnlyList<PromptGroup> Groups, IReadOnlyList<string> Warnings);

/// <summary>One file that actually took part in a request.</summary>
public sealed record PromptPart(string Group, string Name, long Bytes, string Fingerprint);

/// <summary>
/// The resolved prompt: the text that becomes the system message, plus exactly what produced it. The parts
/// are kept because a prompt that can be edited needs an answer to "which version answered this".
/// </summary>
public sealed record PromptSet(string Text, IReadOnlyList<PromptPart> Parts, string Fingerprint)
{
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>
/// Where prompts live. A prompt is a person's text, so it is a file — read on every use rather than cached,
/// because "edit it and the next turn is different" is the whole point of having it outside the script.
/// </summary>
public interface IPromptLibrary
{
    /// <summary>The roots actually searched, in precedence order — the answer to "where do I edit this?".</summary>
    IReadOnlyList<string> Roots { get; }

    /// <summary>Groups and their files, as they are on disk now. Unreadable files are skipped, not fatal.</summary>
    PromptCatalog List();

    /// <summary>
    /// Reads the selected files and joins them in group order. Refuses when a selected group or file does not
    /// exist, is empty, or is oversized — and says which ones are available, because continuing with a
    /// different persona than the one asked for is worse than failing this turn.
    /// </summary>
    ValueTask<Result<PromptSet>> ResolveAsync(PromptSelection selection, CancellationToken cancellationToken);

    /// <summary>
    /// Writes one prompt file and says where it went.
    ///
    /// It writes to the <b>last</b> root — the operator's data root — because that is the one that wins when two
    /// roots have the same group and file (see <see cref="Roots"/>). Writing anywhere else would produce an edit
    /// that is silently overridden by the file the loader actually reads, which is worse than refusing.
    /// </summary>
    /// <param name="group">The group directory, e.g. "1身份".</param>
    /// <param name="name">The file name without the .md extension, e.g. "标准".</param>
    ValueTask<Result<string>> SaveAsync(string group, string name, string text, CancellationToken cancellationToken);
}

/// <summary>Limits, in one place: a prompt is an instruction, not a knowledge base.</summary>
public static class PromptLimits
{
    public const long MaxFileBytes = 64 * 1024;
    public const long MaxTotalBytes = 256 * 1024;
    public const string Extension = ".md";
}
