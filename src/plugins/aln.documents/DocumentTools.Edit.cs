using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace Aln.Documents;

/// <summary>
/// The editing half: change something inside a document that already exists.
///
/// <b>Three tools, like the creators, and for the same reason.</b> A <c>cell</c> is addressed by sheet and reference, a
/// paragraph by its position, a shape by slide and the name its author gave it — one schema holding all three would be
/// mostly empty keys and a <c>kind</c> that decides which of the others to believe.
///
/// <b>The original is copied before anything is written.</b> Every edit here replaces the file in one write, so the only
/// thing between a mistake and a lost document is the copy taken first — and the answer says where it is, because a
/// backup nobody is told about is not a backup.
/// </summary>
public sealed partial class DocumentTools
{
    public const string EditDocxToolName = "doc_edit_docx";
    public const string EditXlsxToolName = "doc_edit_xlsx";
    public const string EditPptxToolName = "doc_edit_pptx";

    public static readonly ModelToolDefinition EditDocxDefinition = new(EditDocxToolName,
        "改一个已有 .docx 里某几段的文字。给 scope、path，和 edits：每项要 index（doc_tree 里段落那条的编号）和 text。\n"
        + "只改文字：段里的域、批注、书签、超链接会被拒绝——那些地方文字和别的东西要互相印证，改一个会变成一份能打开但错了的文件。\n"
        + "改之前会把原文件备份成同目录的 .bak。改完会回读一遍确认。",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"通常是 workspace。"},
          "path":{"type":"string","description":"已有的 .docx。"},
          "edits":{"type":"array","items":{
            "type":"object",
            "properties":{
              "index":{"type":"integer","description":"第几段（从 1 数，按 doc_tree 的编号）。"},
              "text":{"type":"string","description":"这段的新文字（整段替换）。"}
            },"required":["index","text"]}}
        },"required":["scope","path","edits"]}
        """);

    public static readonly ModelToolDefinition EditXlsxDefinition = new(EditXlsxToolName,
        "改一个已有 .xlsx 里某几个单元格。给 scope、path，和 edits：每项要 sheet、ref（像 B2）、text。\n"
        + "原来的公式连同它的缓存值一起去掉——本框架不重算，结果里会说明。\n"
        + "改之前会把原文件备份成同目录的 .bak。改完会回读一遍确认。",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"通常是 workspace。"},
          "path":{"type":"string","description":"已有的 .xlsx。"},
          "edits":{"type":"array","items":{
            "type":"object",
            "properties":{
              "sheet":{"type":"string","description":"工作表名，和 doc_tree 里看到的一致。"},
              "ref":{"type":"string","description":"单元格引用，例如 B2 或 AA17。"},
              "text":{"type":"string","description":"新内容。看起来像数字的会写成数字。"}
            },"required":["sheet","ref","text"]}}
        },"required":["scope","path","edits"]}
        """);

    public static readonly ModelToolDefinition EditPptxDefinition = new(EditPptxToolName,
        "改一个已有 .pptx 里某几个文本框。给 scope、path，和 edits：每项要 slide（第几页，1 起）、shape（形状名，"
        + "doc_tree 里【】之间那个）、text。\n"
        + "不动的部分不改：版式、位置、图片都保留，只换文字。原本有多段的文本框会合成一段，结果里会说明。\n"
        + "改之前会把原文件备份成同目录的 .bak。",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"通常是 workspace。"},
          "path":{"type":"string","description":"已有的 .pptx。"},
          "edits":{"type":"array","items":{
            "type":"object",
            "properties":{
              "slide":{"type":"integer","description":"第几页，1 起。"},
              "shape":{"type":"string","description":"形状名，要和文件里的一致。"},
              "text":{"type":"string","description":"新的文字。"}
            },"required":["slide","shape","text"]}}
        },"required":["scope","path","edits"]}
        """);

    private async ValueTask<Result<SessionToolResult>> EditDocxAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var edits = new List<ParagraphEdit>();
        foreach (var element in Arguments.Items(arguments, "edits"))
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var index = (int)(Arguments.Number(element, "index") ?? 0);
            var text = Arguments.Text(element, "text");
            if (index < 1)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    "edits 里每项都要 index（从 1 数的段落号，doc_tree 会给出）。"));
            if (text is null)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, $"第 {index} 段缺 text。"));
            edits.Add(new ParagraphEdit(index, text));
        }
        if (edits.Count == 0)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "edits 是空的：没有要改的东西。"));

        return await EditAsync(arguments, ".docx", (input, output) => DocumentEditor.EditDocx(input, output, edits),
            $"段落 {string.Join("、", edits.Select(edit => edit.Index))}", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<SessionToolResult>> EditXlsxAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var edits = new List<CellEdit>();
        foreach (var element in Arguments.Items(arguments, "edits"))
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var sheet = Arguments.Text(element, "sheet");
            var reference = Arguments.Text(element, "ref");
            var text = Arguments.Text(element, "text");
            if (string.IsNullOrWhiteSpace(sheet) || string.IsNullOrWhiteSpace(reference) || text is null)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    "edits 里每项都要 sheet、ref 和 text。"));
            edits.Add(new CellEdit(sheet!, reference!, text));
        }
        if (edits.Count == 0)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "edits 是空的：没有要改的单元格。"));

        return await EditAsync(arguments, ".xlsx", (input, output) => DocumentEditor.EditXlsx(input, output, edits),
            $"{edits.Count} 个单元格", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<SessionToolResult>> EditPptxAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var edits = new List<SlideEdit>();
        foreach (var element in Arguments.Items(arguments, "edits"))
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var slide = (int)(Arguments.Number(element, "slide") ?? 0);
            var shape = Arguments.Text(element, "shape");
            var text = Arguments.Text(element, "text");
            if (slide < 1 || string.IsNullOrWhiteSpace(shape) || text is null)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    "edits 里每项都要 slide（1 起）、shape 和 text。"));
            edits.Add(new SlideEdit(slide, shape!, text));
        }
        if (edits.Count == 0)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "edits 是空的：没有要改的文本框。"));

        return await EditAsync(arguments, ".pptx", (input, output) => DocumentEditor.EditPptx(input, output, edits),
            $"{edits.Count} 个文本框", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The read-copy-edit-write both editors share.
    ///
    /// The order is the point: read the bytes, copy them aside as a <c>.bak</c>, edit the copy, write the result over the
    /// original, then read it back. A failure anywhere before the final write leaves the file as it was; a failure in the
    /// final write leaves the backup. The read-back is not decoration — an edit that produced a file this plugin can no
    /// longer parse has to say so now, while the backup is still the obvious answer, not in the next round.
    /// </summary>
    private async ValueTask<Result<SessionToolResult>> EditAsync(JsonElement arguments, string extension,
        Func<Stream, Stream, Result<EditOutcome>> apply, string what, CancellationToken cancellationToken)
    {
        if (Locate(arguments, out var scope, out var path) is { } refusal) return refusal;
        if (!path!.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                $"{path} 不是 {extension}：doc_edit 只能改和它同一格式的文件。"));

        var opened = await _files.OpenReadAsync(scope!, path, cancellationToken).ConfigureAwait(false);
        if (!opened.Ok || opened.Value is not { } stream)
            return Result<SessionToolResult>.Failure(opened.Error ??
                new Error(ErrorCode.Unavailable, "打不开这个文件。"));

        using var original = new MemoryStream();
        await using (stream)
            await stream.CopyToAsync(original, cancellationToken).ConfigureAwait(false);

        // The copy is taken before anything is touched, and its name is said in the answer: a backup nobody knows about
        // is not a backup.
        original.Position = 0;
        using (var copy = new MemoryStream(original.ToArray()))
        {
            var backedUp = await _files.SaveAsync(scope!, path + ".bak", copy, cancellationToken).ConfigureAwait(false);
            if (!backedUp.Ok)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Internal,
                    $"备份没写成，所以一个字也没改：{backedUp.Error?.Message}"));
        }

        using var edited = new MemoryStream();
        original.Position = 0;
        var outcome = apply(original, edited);
        if (!outcome.Ok || outcome.Value is not { } result)
            return Result<SessionToolResult>.Failure(outcome.Error!);

        edited.Position = 0;
        var saved = await _files.SaveAsync(scope!, path, edited, cancellationToken).ConfigureAwait(false);
        if (!saved.Ok)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Internal,
                $"改好的内容没写回去，原文件还在：{saved.Error?.Message}"));

        var text = new StringBuilder();
        text.Append($"改了 {result.Applied} 处（{what}）：{scope}/{path}。\n");
        foreach (var note in result.Notes) text.Append("· ").Append(note).Append('\n');
        text.Append($"原文件备份在 {scope}/{path}.bak。\n");

        edited.Position = 0;
        var check = DocumentReader.Read(edited);
        text.Append(check.Ok && check.Value is { } document
            ? $"回读确认：现在是 {document.Sections.Count} 段、{document.Characters} 字符。"
            : $"回读失败：{check.Error?.Message}——原文件在 .bak 里，可以拿回去。");
        return Result<SessionToolResult>.Success(new SessionToolResult(text.ToString().TrimEnd()));
    }

    /// <summary>
    /// One callable shape for the three editors: they differ only in which apply function they carry.
    /// </summary>
    private sealed class EditTool(DocumentTools owner, string name) : ISessionTool
    {
        public string Name => name;

        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (Arguments.Parse(call.ArgumentsJson) is not { } arguments)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    $"{name} 需要一个 JSON 对象作为参数。"));

            return name switch
            {
                EditDocxToolName => await owner.EditDocxAsync(arguments, cancellationToken).ConfigureAwait(false),
                EditXlsxToolName => await owner.EditXlsxAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => await owner.EditPptxAsync(arguments, cancellationToken).ConfigureAwait(false)
            };
        }
    }
}
