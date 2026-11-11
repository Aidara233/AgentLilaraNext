using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace Aln.Documents;

/// <summary>
/// The tools this plugin offers a round. One source, one name, so that <c>Unregister</c> takes all of it back at once.
///
/// Every tool here answers a question about <b>a local file the model named itself</b> — a scope plus a relative
/// path, the same pair <c>fs_read</c> takes. Nothing here reaches a platform, downloads anything, or remembers what it
/// read last round: a document is opened when asked and forgotten when the call returns.
///
/// <b>Three tools, and the order they are meant to be used in is the order they are described.</b> A model that opens a
/// 200-page report with <c>doc_read</c> spends its window on page one and never finds out what is on page 40, so the
/// shape offered is: <c>doc_info</c> says what the file is, <c>doc_tree</c> says what is in it, and only then does
/// <c>doc_read</c> take a range. Each tool says what the next one is for, because a list of names is not a procedure.
/// </summary>
public sealed partial class DocumentTools(IBinaryFileAccess files) : ISessionToolSource
{
    public const string SourceId = "aln.documents";
    public const string InfoToolName = "doc_info";
    public const string TreeToolName = "doc_tree";
    public const string ReadToolName = "doc_read";

    /// <summary>How long an outline may get before it stops being an outline.</summary>
    public const int MaxTreeLines = 200;

    /// <summary>Characters one <c>doc_read</c> returns when the caller names no budget.</summary>
    public const int DefaultReadChars = 8_000;

    /// <summary>Most a single call may return, whatever it asks for. A tool result is not a place to put a book.</summary>
    public const int MaxReadChars = 60_000;

    private readonly IBinaryFileAccess _files = files;

    public string Id => SourceId;

    public static readonly ModelToolDefinition InfoDefinition = new(InfoToolName,
        "先说清一个办公文件是什么。给 scope（通常是 workspace）和相对路径。\n"
        + "回答：格式（按字节判断，不看文件名）、大小、里面有多少段落/表格/工作表/幻灯片/页。\n"
        + "这是三步里的第一步：知道了是什么，再看 doc_tree 有什么，最后才用 doc_read 取内容。\n"
        + "读的是本机文件。别人在聊天里发来的文件不在盘上，要先用 files_get 把它取到 workspace。",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"宿主登记过的区名，例如 workspace。"},
          "path":{"type":"string","description":"相对那个区的路径，用 / 分段，例如 reports/q3.docx。"}
        },"required":["scope","path"]}
        """);

    public static readonly ModelToolDefinition TreeDefinition = new(TreeToolName,
        "列出文件的骨架：每一段、每一张表、每个工作表、每页是什么，各占多少字。给 scope 和 path。\n"
        + "先看这个再读：大文件里你要的那一段，通常不在开头。每条前面方括号里的数字就是 doc_read 的 from/to。",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"宿主登记过的区名，例如 workspace。"},
          "path":{"type":"string","description":"相对那个区的路径。"},
          "limit":{"type":"integer","description":"最多列多少条，默认 200。"}
        },"required":["scope","path"]}
        """);

    public static readonly ModelToolDefinition ReadDefinition = new(ReadToolName,
        "读文件里的一段内容。给 scope、path，用 from/to 选中 doc_tree 里那些方括号编号（1 起，含两端）。\n"
        + "不写 from/to 就从第一段开始，按 max_chars 尽量多读。读不完会明说断在哪、还剩多少段——"
        + "继续用更大的 from 读下去，不要假装读完了。\n"
        + "docx 的段落和表格、xlsx 的每个工作表、pptx 的每页、pdf 的每页，在这里都只是一种可以按编号取的段。",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"宿主登记过的区名，例如 workspace。"},
          "path":{"type":"string","description":"相对那个区的路径。"},
          "from":{"type":"integer","description":"从第几条开始（1 起）。省略 = 1。"},
          "to":{"type":"integer","description":"到第几条为止（含）。省略 = 一直读到预算用完。"},
          "max_chars":{"type":"integer","description":"这次最多返回多少字符，默认 8000，上限 60000。"}
        },"required":["scope","path"]}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> tools =
        [
            // ActsExternally = false: reading or writing a file is not saying something to anybody, and the loop uses
            // that to decide whether this round's text was its reply (docs/08 §5.1).
            new SessionToolOffer(new InfoTool(this), InfoDefinition, ActsExternally: false),
            new SessionToolOffer(new TreeTool(this), TreeDefinition, ActsExternally: false),
            new SessionToolOffer(new ReadTool(this), ReadDefinition, ActsExternally: false),
            // Writing is the same kind of act as reading — it changes this disk, not anybody's inbox — so these are
            // not "acts externally" either: a round that only wrote a file has not spoken to anyone (docs/08 §5.1).
            new SessionToolOffer(new CreateTool(this, DocxToolName), DocxDefinition, ActsExternally: false),
            new SessionToolOffer(new CreateTool(this, XlsxToolName), XlsxDefinition, ActsExternally: false),
            new SessionToolOffer(new EditTool(this, EditDocxToolName), EditDocxDefinition, ActsExternally: false),
            new SessionToolOffer(new EditTool(this, EditXlsxToolName), EditXlsxDefinition, ActsExternally: false),
            new SessionToolOffer(new EditTool(this, EditPptxToolName), EditPptxDefinition, ActsExternally: false)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(tools));
    }

    private static string Sizes(long bytes) => bytes < 1024 ? $"{bytes} 字节" : $"{bytes / 1024.0:0.#} KB";

    /// <summary>One line of something, short enough to sit in a listing.</summary>
    private static string Preview(string text, int width = 60)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= width ? line : string.Concat(line.AsSpan(0, width), "…");
    }

    /// <summary>
    /// Opens the file and parses it, with the path glued onto whatever went wrong.
    ///
    /// The prefix is not decoration: a scope with four files in it produces four <c>doc_read</c> calls in a row, and an
    /// error that does not say which one it was about is an error the model will retry against the wrong file.
    /// </summary>
    private async ValueTask<Result<ParsedDocument>> LoadAsync(string scope, string path, CancellationToken cancellationToken)
    {
        var opened = await _files.OpenReadAsync(scope, path, cancellationToken).ConfigureAwait(false);
        if (!opened.Ok || opened.Value is not { } stream)
        {
            // The port's own words, not a paraphrase: it is the only thing that knows whether the scope was refused,
            // the path was bad, or the file is simply not there.
            var error = opened.Error ?? new Error(ErrorCode.Unavailable, "这个文件打不开，端口没有说原因。");
            return Result<ParsedDocument>.Failure(error);
        }

        await using (stream)
        {
            var parsed = DocumentReader.Read(stream);
            if (parsed.Ok && parsed.Value is not null) return parsed;
            var failure = parsed.Error ?? new Error(ErrorCode.Unavailable, "这个文件读不出来。");
            return Result<ParsedDocument>.Failure(
                new Error(failure.Code, $"{scope}/{path}：{failure.Message}", failure.Detail));
        }
    }

    private sealed class InfoTool(DocumentTools owner) : ISessionTool
    {
        public string Name => InfoToolName;

        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (Arguments.Parse(call.ArgumentsJson) is not { } arguments)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "doc_info 需要一个 JSON 对象作为参数。"));

            if (owner.Locate(arguments, out var scope, out var path) is { } refusal) return refusal;

            var parsed = await owner.LoadAsync(scope!, path!, cancellationToken).ConfigureAwait(false);
            if (!parsed.Ok || parsed.Value is not { } document)
                return Result<SessionToolResult>.Failure(parsed.Error!);

            var text = new StringBuilder();
            text.Append($"{scope}/{path}：").Append(document.Format).Append("。\n");

            var parts = new List<string>();
            if (document.Count(SectionKind.Paragraph) is var paragraphs and > 0) parts.Add($"段落 {paragraphs}");
            if (document.Count(SectionKind.Table) is var tables and > 0) parts.Add($"表格 {tables}");
            if (document.Count(SectionKind.Sheet) is var sheets and > 0) parts.Add($"工作表 {sheets}");
            if (document.Count(SectionKind.Slide) is var slides and > 0) parts.Add($"幻灯片 {slides}");
            if (document.Count(SectionKind.Page) is var pages and > 0) parts.Add($"页 {pages}");
            text.Append(parts.Count == 0 ? "没有读出任何内容。" : string.Join("，", parts));
            text.Append($"；共 {document.Characters} 字符。");
            if (document.Truncated) text.Append("\n（读到预算上限就停了，后面的内容还没读。）");
            text.Append("\n下一步：doc_tree 看骨架，doc_read 取内容。");

            return Result<SessionToolResult>.Success(new SessionToolResult(text.ToString()));
        }
    }

    private sealed class TreeTool(DocumentTools owner) : ISessionTool
    {
        public string Name => TreeToolName;

        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (Arguments.Parse(call.ArgumentsJson) is not { } arguments)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "doc_tree 需要一个 JSON 对象作为参数。"));

            if (owner.Locate(arguments, out var scope, out var path) is { } refusal) return refusal;
            var limit = (int)Math.Clamp(Arguments.Number(arguments, "limit") ?? MaxTreeLines, 1, MaxTreeLines);

            var parsed = await owner.LoadAsync(scope!, path!, cancellationToken).ConfigureAwait(false);
            if (!parsed.Ok || parsed.Value is not { } document)
                return Result<SessionToolResult>.Failure(parsed.Error!);

            var sections = document.Sections;
            var text = new StringBuilder();
            text.Append($"{scope}/{path}：{document.Format}，共 {sections.Count} 段");
            if (document.Truncated) text.Append("（读到预算上限，这是一份不完整的骨架）");
            text.Append("。\n");

            foreach (var (section, position) in sections.Take(limit).Select((section, index) => (section, index + 1)))
            {
                text.Append('[').Append(position).Append(']').Append(' ')
                    .Append(section.Label).Append(" · ")
                    .Append(Preview(section.Text)).Append(" · ")
                    .Append(section.Text.Length).Append(" 字\n");
            }

            if (sections.Count > limit)
                text.Append($"（后面还有 {sections.Count - limit} 段没列出来：doc_read from={limit + 1}，或者把 limit 调大。）");

            return Result<SessionToolResult>.Success(new SessionToolResult(text.ToString().TrimEnd()));
        }
    }

    private sealed class ReadTool(DocumentTools owner) : ISessionTool
    {
        public string Name => ReadToolName;

        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (Arguments.Parse(call.ArgumentsJson) is not { } arguments)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "doc_read 需要一个 JSON 对象作为参数。"));

            if (owner.Locate(arguments, out var scope, out var path) is { } refusal) return refusal;

            var from = Math.Max(1, (int)(Arguments.Number(arguments, "from") ?? 1));
            var to = (int)(Arguments.Number(arguments, "to") ?? int.MaxValue);
            var budget = (int)Math.Clamp(Arguments.Number(arguments, "max_chars") ?? DefaultReadChars,
                200, MaxReadChars);
            if (to < from)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    $"to 是 {to}，小于 from 的 {from}：这段是空的。要读就往大里给。"));

            var parsed = await owner.LoadAsync(scope!, path!, cancellationToken).ConfigureAwait(false);
            if (!parsed.Ok || parsed.Value is not { } document)
                return Result<SessionToolResult>.Failure(parsed.Error!);

            var sections = document.Sections;
            if (from > sections.Count)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    $"{scope}/{path} 只有 {sections.Count} 段，from={from} 越界了（段号从 1 起）。"));

            var until = Math.Min(to, sections.Count);
            var text = new StringBuilder();
            var used = 0;
            var reached = from;
            for (var position = from; position <= until; position++)
            {
                var section = sections[position - 1];
                var block = $"[{position}] {section.Label}\n{section.Text}\n\n";
                if (used + block.Length > budget)
                {
                    // The cut is announced with what is left, so the next call is a decision rather than a guess.
                    if (used == 0)
                    {
                        // Not even one section fits: give a truncated view of it rather than nothing at all, and say
                        // the cut is inside this section.
                        var head = section.Text.Length > budget ? section.Text[..budget] : section.Text;
                        text.Append($"[{position}] {section.Label}\n{head}\n…（这一段就被截断了：它自身 {section.Text.Length} 字，"
                            + $"这次只给了 {budget} 字。调大 max_chars 再取这一段。）");
                    }
                    break;
                }
                text.Append(block);
                used += block.Length;
                reached = position;
            }

            var header = $"{scope}/{path} 第 {from}–{until} 段（共 {sections.Count} 段）";
            header += reached < until
                ? $"：读到第 {reached} 段就用完了 {budget} 字的预算，剩下的用 doc_read from={reached + 1} 继续。\n\n"
                : "：\n\n";
            return Result<SessionToolResult>.Success(new SessionToolResult(header + text.ToString().TrimEnd()));
        }
    }

    /// <summary>
    /// The scope and path both tools need, with the one refusal that <b>cannot</b> come from the port.
    ///
    /// A missing argument is this tool's own mistake, not the file system's, so it is refused here and lists the rooms
    /// that do accept bytes — a model that typed a scope wrong should learn the right ones without a second round trip.
    /// </summary>
    private Result<SessionToolResult>? Locate(JsonElement arguments, out string? scope, out string? path)
    {
        scope = Arguments.Text(arguments, "scope");
        path = Arguments.Text(arguments, "path");
        if (string.IsNullOrWhiteSpace(scope))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                $"需要一个 scope。这个宿主接受字节的区：{string.Join("、", _files.BinaryScopes)}。"));
        if (string.IsNullOrWhiteSpace(path))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "需要一个 path（相对那个区的路径，用 / 分段）。"));
        return null;
    }

    /// <summary>
    /// The little bit of JSON reading a tool needs. A plugin cannot use the host's argument helpers — they live in the
    /// implementation assembly, which a plugin is not allowed to reference — so the rules it does share are spelled the
    /// same way: a missing or mistyped argument is a failure with a sentence, never a default.
    /// </summary>
    private static class Arguments
    {
        public static JsonElement? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.Clone()
                    : null;
            }
            catch (JsonException) { return null; }
        }

        public static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        public static double? Number(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number)
                ? number
                : null;

        /// <summary>An array of objects, or nothing when the argument is missing or is not an array.</summary>
        public static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                : [];

        /// <summary>
        /// A two-dimensional block of cell text. Numbers and booleans are taken as they are written in the JSON rather
        /// than re-formatted: "1.50" in the request becomes "1.50" in the cell, because a model that wrote two decimals
        /// meant two decimals.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<string>>? Grid(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return null;
            var rows = new List<IReadOnlyList<string>>();
            foreach (var row in value.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array) continue;
                var cells = new List<string>();
                foreach (var cell in row.EnumerateArray())
                    cells.Add(cell.ValueKind switch
                    {
                        JsonValueKind.String => cell.GetString() ?? string.Empty,
                        JsonValueKind.Number => cell.GetRawText(),
                        JsonValueKind.True => "TRUE",
                        JsonValueKind.False => "FALSE",
                        _ => string.Empty
                    });
                rows.Add(cells);
            }
            return rows;
        }
    }
}
