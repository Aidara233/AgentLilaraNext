using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace Aln.Documents;

/// <summary>
/// The writing half: make a new document, in a shape the caller can actually express.
///
/// <b>Three tools and not one.</b> A single "create a document" tool with a <c>format</c> field would need a schema that
/// is the union of three: blocks for Word, sheets for Excel, slides for PowerPoint, of which two thirds are always
/// wrong. A model filling in a union schema produces the right document by luck; a model choosing between three tools
/// produces it by intent, and the wrong choice is visible in the arguments instead of buried in them.
///
/// <b>Nothing here overwrites.</b> The path exists or it does not; if it does, the answer names the file and asks for
/// another name. A tool that quietly replaces what was at that path is a tool that can destroy the only copy of
/// something, and the model has the directory listing it needs to pick a different name.
/// </summary>
public sealed partial class DocumentTools
{
    public const string DocxToolName = "doc_create_docx";
    public const string XlsxToolName = "doc_create_xlsx";

    public static readonly ModelToolDefinition DocxDefinition = new(DocxToolName,
        "在 workspace 里造一个新的 .docx。给 scope、path，以及按顺序排列的 blocks。\n"
        + "block 的 kind：heading（要 level，1-9）、paragraph、bullet、table（要 rows，二维字符串数组，第一行当表头）。\n"
        + "写到已经存在的路径会被拒绝——换个名字，或者先用 doc_read 看看那里是什么。",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"通常是 workspace。"},
          "path":{"type":"string","description":"要写的相对路径，例如 reports/q3.docx。"},
          "blocks":{"type":"array","description":"按顺序写入的块。","items":{
            "type":"object","properties":{
              "kind":{"type":"string","description":"heading / paragraph / bullet / table"},
              "text":{"type":"string","description":"heading、paragraph、bullet 的正文。"},
              "level":{"type":"integer","description":"heading 的层级，1-9，默认 1。"},
              "rows":{"type":"array","description":"table 的内容，二维字符串数组。","items":{
                "type":"array","items":{"type":"string"}}}
            },"required":["kind"]}}
        },"required":["scope","path","blocks"]}
        """);

    public static readonly ModelToolDefinition XlsxDefinition = new(XlsxToolName,
        "在 workspace 里造一个新的 .xlsx。给 scope、path，以及一到多个 sheets，每个 sheet 是一张二维表。\n"
        + "看起来是数字的单元格会写成数字（能排序、能求和），带前导零或带符号的保持文本。"
        + "公式不会生成——要公式，写完用口径说明让人去加，或者用精确编辑工具改单元格。\n"
        + "写到已经存在的路径会被拒绝。",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"通常是 workspace。"},
          "path":{"type":"string","description":"要写的相对路径，例如 reports/q3.xlsx。"},
          "sheets":{"type":"array","items":{
            "type":"object","properties":{
              "name":{"type":"string","description":"工作表名，最多 31 字。"},
              "rows":{"type":"array","items":{"type":"array","items":{"type":"string"}}}
            },"required":["rows"]}}
        },"required":["scope","path","sheets"]}
        """);

    private async ValueTask<Result<SessionToolResult>> CreateDocxAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (Locate(arguments, out var scope, out var path) is { } refusal) return refusal;

        if (!arguments.TryGetProperty("blocks", out var blocksElement) || blocksElement.ValueKind != JsonValueKind.Array)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "需要 blocks：一个数组，每项是一个 kind 加它的内容。空的文档没有意义。"));

        var blocks = new List<DocxBlock>();
        foreach (var element in blocksElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "blocks 里每一项都要是一个对象。"));

            var kind = Arguments.Text(element, "kind")?.Trim().ToLowerInvariant();
            var text = Arguments.Text(element, "text") ?? string.Empty;
            switch (kind)
            {
                case DocxBlock.Heading when text.Length == 0:
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "heading 需要 text。"));
                case DocxBlock.Heading:
                    blocks.Add(new DocxBlock(DocxBlock.Heading, text,
                        (int)Math.Clamp(Arguments.Number(element, "level") ?? 1, 1, 9)));
                    break;
                case DocxBlock.Table:
                {
                    var rows = Arguments.Grid(element, "rows");
                    if (rows is null || rows.Count == 0)
                        return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                            "table 需要 rows：二维字符串数组，第一行当表头。"));
                    blocks.Add(new DocxBlock(DocxBlock.Table, string.Empty, 1, rows));
                    break;
                }
                case DocxBlock.Bullet when text.Length == 0:
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "bullet 需要 text。"));
                case DocxBlock.Bullet:
                    blocks.Add(new DocxBlock(DocxBlock.Bullet, text));
                    break;
                case DocxBlock.Paragraph when text.Length == 0:
                    // An empty paragraph is spacing, which the writer can add on its own; asking for one is a mistake.
                    break;
                case DocxBlock.Paragraph:
                    blocks.Add(new DocxBlock(DocxBlock.Paragraph, text));
                    break;
                default:
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                        $"不认识的 kind「{kind}」。可用：heading、paragraph、bullet、table。"));
            }
        }

        if (blocks.Count == 0)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "blocks 里没有任何能写的内容。"));

        return await WriteAsync(scope!, path!, ".docx", stream => DocxWriter.Write(stream, blocks),
            $"docx，{blocks.Count} 个块", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<SessionToolResult>> CreateXlsxAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (Locate(arguments, out var scope, out var path) is { } refusal) return refusal;

        if (!arguments.TryGetProperty("sheets", out var sheetsElement) || sheetsElement.ValueKind != JsonValueKind.Array)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "需要 sheets：一个数组，每项是一张表（name 可选，rows 必填）。"));

        var sheets = new List<XlsxSheet>();
        var position = 0;
        foreach (var element in sheetsElement.EnumerateArray())
        {
            position++;
            if (element.ValueKind != JsonValueKind.Object)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "sheets 里每一项都要是一个对象。"));

            var rows = Arguments.Grid(element, "rows");
            if (rows is null)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    $"第 {position} 张表缺 rows（二维字符串数组）。"));
            sheets.Add(new XlsxSheet(Arguments.Text(element, "name") ?? $"Sheet{position}", rows));
        }

        if (sheets.Count == 0)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "sheets 是空的：至少要有一张表。"));

        return await WriteAsync(scope!, path!, ".xlsx", stream => XlsxWriter.Write(stream, sheets),
            $"xlsx，{sheets.Count} 张表", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The write both creators share: refuse to clobber, build in memory, hand the bytes to the port.
    ///
    /// <b>Built in memory, written once.</b> OOXML needs a seekable stream to lay out its parts and its directory, so a
    /// document cannot be streamed as it is generated; the whole thing is assembled here and handed over in one call so
    /// the port's temporary-file-and-rename rule still holds. That is why the ceiling is the transfer constant and not
    /// something larger: a generated document that does not fit in it is a mistake in the arguments.
    /// </summary>
    private async ValueTask<Result<SessionToolResult>> WriteAsync(string scope, string path, string expectedExtension,
        Func<Stream, Result> build, string described, CancellationToken cancellationToken)
    {
        if (!path.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                $"路径要以 {expectedExtension} 结尾：写的是 {described}，而文件名决定了别人用什么打开它。"));

        // Existence is decided by whether the port can open it, not by a second opinion kept here.
        var existing = await _files.OpenReadAsync(scope, path, cancellationToken).ConfigureAwait(false);
        if (existing.Ok && existing.Value is { } opened)
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            return Result<SessionToolResult>.Failure(new(ErrorCode.Conflict,
                $"{scope}/{path} 已经存在。doc_create 不覆盖别人的文件——换个名字（目录里有什么可以用 fs_list 看）。"));
        }

        using var buffer = new MemoryStream();
        var built = build(buffer);
        if (!built.Ok)
            return Result<SessionToolResult>.Failure(built.Error!);

        buffer.Position = 0;
        var saved = await _files.SaveAsync(scope, path, buffer, cancellationToken).ConfigureAwait(false);
        if (!saved.Ok)
            return Result<SessionToolResult>.Failure(saved.Error!);

        var text = new StringBuilder();
        text.Append($"已写入 {scope}/{path}（{described}，{buffer.Length} 字节）。\n");
        text.Append("写完先 doc_read 读一遍再告诉别人：一份没人读过的新文档，和它声称的内容是两件事。");
        return Result<SessionToolResult>.Success(new SessionToolResult(text.ToString()));
    }

    /// <summary>
    /// One callable shape for both creators: they differ only in which builder they hand the bytes to, and giving each
    /// its own <c>ISessionTool</c> would be two classes that differ by one line.
    /// </summary>
    private sealed class CreateTool(DocumentTools owner, string name) : ISessionTool
    {
        public string Name => name;

        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (Arguments.Parse(call.ArgumentsJson) is not { } arguments)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    $"{name} 需要一个 JSON 对象作为参数。"));

            return name == DocxToolName
                ? await owner.CreateDocxAsync(arguments, cancellationToken).ConfigureAwait(false)
                : await owner.CreateXlsxAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
    }
}
