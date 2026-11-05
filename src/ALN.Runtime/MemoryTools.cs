using System.Globalization;
using System.Text;
using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// Memory, offered as two tools and one line per round: write a fragment, read a group.
///
/// Two, not five. Everything else a memory needs — folding fragments into a body, archiving the active file, merging
/// two topics, deleting a group — is a file operation, and the file system module already offers those. A tool named
/// "consolidate" would be a second way to do what <c>fs_move</c> and <c>fs_write</c> already do, and it would have to
/// grow a variant for every new way of tidying. So the semantic layer here is only "what a fragment is" and "what
/// reading a group shows".
/// </summary>
public sealed class MemoryTools(IMemoryLibrary library, TimeProvider? timeProvider = null) : ISessionToolSource
{
    public const string SourceId = "system.memory-tools";
    public const string RememberToolName = "remember";
    public const string ReadToolName = "read_memory";

    /// <summary>Most characters one read returns. A group can hold megabytes of fragments; a read that pasted them all
    /// would cost the round its budget and hide the body it came to show.</summary>
    private const int MaxReadChars = 24_000;

    private readonly IMemoryLibrary _library = library;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Id => SourceId;

    public static readonly ModelToolDefinition RememberDefinition = new(RememberToolName,
        "Write down one thing that was said or seen, as a fragment in a memory group. This is cheap and append-only: "
        + "say it again and there is a second line, not an edit. The group is created if it does not exist; pick a "
        + "topic for the name, not a moment ('关系-小林', not 'today').",
        """
        {"type":"object","properties":{
          "group":{"type":"string","description":"Which memory group this belongs to. One name, no slashes."},
          "text":{"type":"string","description":"The fragment: one or two sentences, in your own words."},
          "who":{"type":"string","description":"Who said or did it, when that matters. Optional."}},
         "required":["group","text"]}
        """);

    public static readonly ModelToolDefinition ReadDefinition = new(ReadToolName,
        "Open one memory group: its whole tree (body, fragments, archive), its body in full, and the fragments the "
        + "body quotes. This is the only call that shows a group's contents — between calls you only know the group "
        + "names.",
        """
        {"type":"object","properties":{
          "group":{"type":"string","description":"Which memory group to open."}},
         "required":["group"]}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> offers =
        [
            // Neither speaks to anyone: a turn that only wrote something down has not replied (docs/08 §5.1).
            new(new RememberTool(this), RememberDefinition, ActsExternally: false),
            new(new ReadTool(this), ReadDefinition, ActsExternally: false)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private sealed class RememberTool(MemoryTools owner) : ISessionTool
    {
        public string Name => RememberToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var group = ToolArguments.Text(arguments, "group")?.Trim();
            if (string.IsNullOrEmpty(group))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    "remember needs a group: which topic does this belong to?"));
            var text = ToolArguments.Text(arguments, "text");
            if (string.IsNullOrWhiteSpace(text))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "remember needs text: what happened?"));

            var written = await owner._library.AppendLineAsync(group, text, ToolArguments.Text(arguments, "who"), null,
                cancellationToken).ConfigureAwait(false);
            if (!written.Ok || written.Value is null) return Result<SessionToolResult>.Failure(written.Error!);

            var builder = new StringBuilder();
            builder.Append(written.Value.CreatedGroup
                ? $"记下了 {written.Value.Line.Id}，也新建了记忆组「{group}」。"
                : $"记下了 {written.Value.Line.Id}，在「{group}」里。");
            if (written.Value.Note is { } note) builder.Append(' ').Append(note);
            return ToolArguments.Ok(builder.ToString());
        }
    }

    private sealed class ReadTool(MemoryTools owner) : ISessionTool
    {
        public string Name => ReadToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var group = ToolArguments.Text(arguments, "group")?.Trim();
            if (string.IsNullOrEmpty(group))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "read_memory needs a group name."));

            var block = await owner._library.ReadBlockAsync(group, cancellationToken).ConfigureAwait(false);
            if (block.Error is { } blockError) return Result<SessionToolResult>.Failure(blockError);
            if (block.Value is null)
            {
                var known = await owner._library.GroupsAsync(cancellationToken).ConfigureAwait(false);
                var names = known.Value is { Count: > 0 } groups
                    ? string.Join("、", groups.Select(item => item.Name))
                    : "（空的）";
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    $"没有「{group}」这个记忆组。现有：{names}"));
            }

            var tree = await owner._library.TreeAsync(group, cancellationToken).ConfigureAwait(false);
            var quotes = MemoryQuotes.Find(block.Value.Exists ? block.Value.Text : null);
            var cited = quotes.Count == 0
                ? Result<IReadOnlyList<MemoryLine>>.Success([])
                : await owner._library.ReadLinesAsync(group, quotes, cancellationToken).ConfigureAwait(false);

            var builder = new StringBuilder();
            builder.Append("记忆组「").Append(group).AppendLine("」的树：");
            builder.AppendLine(string.Join('\n', (tree.Value ?? []).Select(Node)));

            builder.AppendLine().AppendLine(block.Value.Exists
                ? $"—— {IMemoryLibrary.BlockFileName}（{block.Value.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}）——"
                : $"—— 还没有 {IMemoryLibrary.BlockFileName}：这一组只有碎片，还没有人把它折叠成结论 ——");
            if (block.Value.Exists && block.Value.Text.Trim().Length > 0) builder.AppendLine(block.Value.Text.TrimEnd());

            if (quotes.Count > 0)
            {
                builder.AppendLine().AppendLine("—— 正文引用的碎片 ——");
                var found = cited.Value ?? [];
                foreach (var line in found) builder.AppendLine(Render(line));
                // A quote whose line is gone is reported as gone: the alternative is a conclusion whose evidence looks
                // present because nobody said otherwise.
                foreach (var id in quotes.Where(id => found.All(line => line.Id != id)))
                    builder.AppendLine($"[{id}] …… 引用的这一行已经不在了（可能被删掉或搬出了这个组）");
            }

            builder.AppendLine().Append("要整理就用文件工具：")
                .Append($"{FileSystemTools.WriteToolName} 重写正文，")
                .Append($"{FileSystemTools.MoveToolName} 把 {IMemoryLibrary.LinesFileName} 搬进 {IMemoryLibrary.HistoryDirectory}/，")
                .Append($"{FileSystemTools.RemoveToolName} 删掉整组。");

            var text = builder.ToString();
            if (text.Length > MaxReadChars)
                text = string.Concat(text.AsSpan(0, MaxReadChars), "\n…（内容已截断：这一组的文件太大，用文件工具分块看）");
            return ToolArguments.Ok(text);
        }

        private static string Node(FileNode node) =>
            node.IsDirectory
                ? $"  {node.Path}/"
                : $"  {node.Path}  {node.Bytes} B";

        private static string Render(MemoryLine line)
        {
            var when = line.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var who = string.IsNullOrWhiteSpace(line.Who) ? "" : $"{line.Who} · ";
            return $"[{line.Id}] {when} · {who}{line.Text}";
        }
    }
}

/// <summary>
/// One line per round: the names of the memory groups. Never their contents.
///
/// Why names and nothing else: a group is a unit of "what this library knows about", and a round needs to know that
/// volume exists to decide whether to open it. Injecting the bodies would spend the context on the groups this turn
/// does not need; injecting nothing would make remembering silently useless, because a model that cannot see there is
/// anything to open will never open it.
///
/// Every group is listed, however many there are. A selection would be a judgement about which topics matter this
/// round, made with none of the context that decides it — and "there are 40 groups" is a fact somebody has to act on,
/// not one to hide behind a truncation.
/// </summary>
public sealed class MemoryGroupsSource(IMemoryLibrary library) : IFloatingContextSource
{
    public const string SourceId = "system.memory";
    public const string SectionId = "memory";

    private readonly IMemoryLibrary _library = library;

    public string Id => SourceId;

    public async ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(
        RoundContextRequest request, CancellationToken cancellationToken)
    {
        var groups = await _library.GroupsAsync(cancellationToken).ConfigureAwait(false);
        // A library that cannot be read says nothing rather than failing the round: memory is an advantage, and one
        // that is momentarily unavailable must not cost the conversation its answer.
        if (groups.Error is not null || groups.Value is null || groups.Value.Count == 0)
            return Result<IReadOnlyList<RoundContextContribution>>.Success([]);

        var names = groups.Value.Select(group => group.Name).ToList();
        var lines = new List<string>
        {
            $"记忆库（{names.Count} 组）：{string.Join("、", names)}。要打开一组用 {MemoryTools.ReadToolName}，要记一条用 {MemoryTools.RememberToolName}。"
        };

        var heavy = groups.Value.Where(group => group.NeedsConsolidation).Select(group => group.Name).ToList();
        if (heavy.Count > 0)
            lines.Add($"{heavy.Count} 组的碎片已经攒到该折叠了：{string.Join("、", heavy)}——" +
                      $"把要留的写进 {IMemoryLibrary.BlockFileName}（引用 [l-…]），再把 {IMemoryLibrary.LinesFileName} 搬进 {IMemoryLibrary.HistoryDirectory}/。");
        if (names.Count >= MemoryLimits.MaxGroups)
            lines.Add($"已经是 {names.Count} 组，到上限（{MemoryLimits.MaxGroups}）了：再加主题之前先合并或删掉一组。");

        IReadOnlyList<RoundContextContribution> sections =
        [
            new RoundContextContribution(SectionId, SectionId, string.Join('\n', lines), Order: -90)
        ];
        return Result<IReadOnlyList<RoundContextContribution>>.Success(sections);
    }
}
