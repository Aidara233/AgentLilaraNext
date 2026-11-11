using System.Text;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using Aln.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ALN.Tests;

/// <summary>
/// The document plugin as a model meets it: one tool that says what a file on this disk is, by reading it.
///
/// Two things are being pinned here, and neither is about parsing. <b>The plugin reads through the host's byte port
/// and nothing else</b> — so the scope rule, the containment rule and "absent is an error" are the host's answers, not
/// a second copy of them. And <b>format is decided by the bytes</b>, never by the file name: an extension is a claim
/// made by whoever wrote the file, which for an arriving message is a stranger.
/// </summary>
public sealed class DocumentToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-doc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (LocalFileSystemAccess Access, DocumentTools Tools) Open(params string[] scopes)
    {
        var access = new LocalFileSystemAccess(_root, scopes.Length == 0 ? [FileScopes.Workspace] : scopes,
            binaryScopes: scopes.Length == 0 ? [FileScopes.Workspace] : scopes);
        return (access, new DocumentTools(access));
    }

    private static async Task<Result<SessionToolResult>> CallAsync(DocumentTools tools, string tool, string arguments)
    {
        var offers = (await tools.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default)).Value!;
        var offer = offers.Single(candidate => candidate.Definition.Name == tool);
        return await offer.Tool.ExecuteAsync(new ModelToolCall("call-1", tool, arguments), default);
    }

    /// <summary>A real .docx, not a marker: the plugin's answer has to come from the parts inside the container.</summary>
    private static MemoryStream Docx()
    {
        var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(
                    new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
                    new W.Run(new W.Text("季度报告"))),
                new W.Paragraph(new W.Run(new W.Text("第一段：收入比上季度高了。"))),
                new W.Paragraph(new W.Run(new W.Text("第二段：成本没有变化。"))),
                new W.Table(
                    new W.TableRow(
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("项目")))),
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("金额"))))),
                    new W.TableRow(
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("苹果")))),
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("42"))))))));
            main.Document.Save();
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task The_tools_are_offered_and_reading_a_file_is_not_speaking_to_anyone()
    {
        var (_, tools) = Open();
        var offers = (await tools.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default)).Value!;

        Assert.Equal([
                "doc_info", "doc_tree", "doc_read",
                "doc_create_docx", "doc_create_xlsx",
                "doc_edit_docx", "doc_edit_xlsx", "doc_edit_pptx"
            ],
            offers.Select(offer => offer.Definition.Name));
        Assert.All(offers, offer => Assert.False(offer.ActsExternally));
        Assert.All(offers, offer => Assert.False(offer.Tool.EndsTurn));
        Assert.All(offers, offer => Assert.Equal(AccessLevel.Agent, offer.MinLevel));
    }

    [Fact]
    public async Task The_outline_numbers_every_piece_so_that_a_range_can_name_one()
    {
        var (access, tools) = Open();
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "q3.docx", Docx(), default)).Ok);

        var answer = await CallAsync(tools, "doc_tree", """{"scope":"workspace","path":"q3.docx"}""");

        Assert.True(answer.Ok, answer.Error?.Message);
        var text = answer.Value!.Content;
        Assert.Contains("[1] 标题 1 · # 季度报告", text, StringComparison.Ordinal);
        Assert.Contains("[2] 段落 · 第一段", text, StringComparison.Ordinal);
        Assert.Contains("[4] 表格", text, StringComparison.Ordinal);
        Assert.Contains("共 4 段", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_range_reads_exactly_the_pieces_it_names()
    {
        var (access, tools) = Open();
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "q3.docx", Docx(), default)).Ok);

        var answer = await CallAsync(tools, "doc_read", """{"scope":"workspace","path":"q3.docx","from":2,"to":3}""");

        Assert.True(answer.Ok, answer.Error?.Message);
        var text = answer.Value!.Content;
        Assert.Contains("第一段", text, StringComparison.Ordinal);
        Assert.Contains("第二段", text, StringComparison.Ordinal);
        // A range means a range: what was outside it must not leak in wearing a heading.
        Assert.DoesNotContain("季度报告", text, StringComparison.Ordinal);
        Assert.DoesNotContain("| 项目 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_range_past_the_end_says_how_many_pieces_there_are()
    {
        var (access, tools) = Open();
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "q3.docx", Docx(), default)).Ok);

        var answer = await CallAsync(tools, "doc_read", """{"scope":"workspace","path":"q3.docx","from":99}""");

        Assert.False(answer.Ok);
        Assert.Contains("4", answer.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_docx_is_reported_as_a_docx_with_what_it_contains()
    {
        var (access, tools) = Open();
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "reports/q3.docx", Docx(), default)).Ok);

        var answer = await CallAsync(tools, "doc_info",
            """{"scope":"workspace","path":"reports/q3.docx"}""");

        Assert.True(answer.Ok, answer.Error?.Message);
        var text = answer.Value!.Content;
        Assert.Contains("workspace/reports/q3.docx", text, StringComparison.Ordinal);
        Assert.Contains("docx", text, StringComparison.Ordinal);
        Assert.Contains("段落 3", text, StringComparison.Ordinal);
        Assert.Contains("表格 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bytes_decide_not_the_name()
    {
        var (access, tools) = Open();
        // A PDF named like a Word file. The name is what somebody typed; the header is what the file is.
        var pdf = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n"));
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "report.docx", pdf, default)).Ok);

        var answer = await CallAsync(tools, "doc_info", """{"scope":"workspace","path":"report.docx"}""");

        // It got as far as "this is a PDF", which is the answer the name would have got wrong. The PDF reader is the
        // next thing to land; until then the refusal says which half is missing rather than blaming the file.
        Assert.False(answer.Ok);
        Assert.Contains("PDF", answer.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bytes_nobody_recognises_are_said_to_be_unrecognised()
    {
        var (access, tools) = Open();
        var noise = new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "blob.bin", noise, default)).Ok);

        var answer = await CallAsync(tools, "doc_info", """{"scope":"workspace","path":"blob.bin"}""");

        Assert.False(answer.Ok);
        Assert.Contains("不是本框架认识的办公文档", answer.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_is_the_ports_answer_rather_than_a_guess()
    {
        var (_, tools) = Open();

        var answer = await CallAsync(tools, "doc_info", """{"scope":"workspace","path":"nope.docx"}""");

        Assert.False(answer.Ok);
        // Not rewritten into "there is no such document": what happened is whatever the port said, and it is the only
        // thing that knows whether the path was wrong or the file simply is not there.
        Assert.Contains("nope.docx", answer.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scope_this_host_does_not_have_is_refused_and_the_ones_it_has_are_listed()
    {
        var (_, tools) = Open();

        var answer = await CallAsync(tools, "doc_info", """{"scope":"nowhere","path":"a.docx"}""");

        Assert.False(answer.Ok);
        Assert.Contains("nowhere", answer.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arguments_that_are_not_a_json_object_are_refused_with_a_sentence()
    {
        var (_, tools) = Open();

        var answer = await CallAsync(tools, "doc_info", "not json");

        Assert.False(answer.Ok);
        Assert.Equal(ErrorCode.Invalid, answer.Error!.Code);
    }

    [Fact]
    public async Task A_created_docx_reads_back_as_what_was_asked_for()
    {
        var (_, tools) = Open();

        var created = await CallAsync(tools, "doc_create_docx",
            """
            {"scope":"workspace","path":"out/summary.docx","blocks":[
              {"kind":"heading","text":"季度总结","level":2},
              {"kind":"paragraph","text":"收入上升。"},
              {"kind":"bullet","text":"成本持平"},
              {"kind":"table","rows":[["项目","金额"],["苹果","42"]]}]}
            """);
        Assert.True(created.Ok, created.Error?.Message);

        // The round trip is the test: what was written is only right if it reads back as the same four pieces.
        var tree = await CallAsync(tools, "doc_tree", """{"scope":"workspace","path":"out/summary.docx"}""");
        Assert.True(tree.Ok, tree.Error?.Message);
        Assert.Contains("共 4 段", tree.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("[1] 标题 2", tree.Value.Content, StringComparison.Ordinal);

        var read = await CallAsync(tools, "doc_read",
            """{"scope":"workspace","path":"out/summary.docx","from":1,"to":4}""");
        Assert.True(read.Ok, read.Error?.Message);
        var text = read.Value!.Content;
        Assert.Contains("## 季度总结", text, StringComparison.Ordinal);
        Assert.Contains("收入上升。", text, StringComparison.Ordinal);
        Assert.Contains("成本持平", text, StringComparison.Ordinal);
        Assert.Contains("| 苹果 | 42 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_created_sheet_keeps_numbers_as_numbers()
    {
        var (_, tools) = Open();

        var created = await CallAsync(tools, "doc_create_xlsx",
            """
            {"scope":"workspace","path":"out/stock.xlsx","sheets":[
              {"name":"库存","rows":[["名称","数量"],["苹果","42"],["编号","007"]]}]}
            """);
        Assert.True(created.Ok, created.Error?.Message);

        var read = await CallAsync(tools, "doc_read", """{"scope":"workspace","path":"out/stock.xlsx"}""");
        Assert.True(read.Ok, read.Error?.Message);
        var text = read.Value!.Content;
        Assert.Contains("库存", text, StringComparison.Ordinal);
        Assert.Contains("| 苹果 | 42 |", text, StringComparison.Ordinal);
        // Leading zeros are data, not a number to be normalised away.
        Assert.Contains("007", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_over_an_existing_file_is_refused_rather_than_replacing_it()
    {
        var (access, tools) = Open();
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "taken.docx", Docx(), default)).Ok);

        var answer = await CallAsync(tools, "doc_create_docx",
            """{"scope":"workspace","path":"taken.docx","blocks":[{"kind":"paragraph","text":"会覆盖吗"}]}""");

        Assert.False(answer.Ok);
        Assert.Equal(ErrorCode.Conflict, answer.Error!.Code);
        Assert.Contains("已经存在", answer.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_that_does_not_match_the_format_is_refused()
    {
        var (_, tools) = Open();

        var answer = await CallAsync(tools, "doc_create_docx",
            """{"scope":"workspace","path":"report.txt","blocks":[{"kind":"paragraph","text":"x"}]}""");

        Assert.False(answer.Ok);
        Assert.Contains(".docx", answer.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_a_paragraph_replaces_its_text_and_leaves_a_backup()
    {
        var (access, tools) = Open();
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "q3.docx", Docx(), default)).Ok);

        var edited = await CallAsync(tools, "doc_edit_docx",
            """{"scope":"workspace","path":"q3.docx","edits":[{"index":2,"text":"改过的正文。"}]}""");
        Assert.True(edited.Ok, edited.Error?.Message);
        Assert.Contains(".bak", edited.Value!.Content, StringComparison.Ordinal);

        var read = await CallAsync(tools, "doc_read", """{"scope":"workspace","path":"q3.docx","from":2,"to":2}""");
        Assert.True(read.Ok, read.Error?.Message);
        Assert.Contains("改过的正文。", read.Value!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("第一段", read.Value.Content, StringComparison.Ordinal);

        // The copy is the only thing between an edit and a lost document, so its existence is part of the contract.
        Assert.True(File.Exists(Path.Combine(_root, "workspace", "q3.docx.bak")));
    }

    [Fact]
    public async Task Editing_a_cell_writes_the_new_value()
    {
        var (_, tools) = Open();
        Assert.True((await CallAsync(tools, "doc_create_xlsx",
            """
            {"scope":"workspace","path":"stock.xlsx","sheets":[
              {"name":"库存","rows":[["名称","数量"],["苹果","42"]]}]}
            """)).Ok);

        var edited = await CallAsync(tools, "doc_edit_xlsx",
            """{"scope":"workspace","path":"stock.xlsx","edits":[{"sheet":"库存","ref":"B2","text":"45"}]}""");
        Assert.True(edited.Ok, edited.Error?.Message);

        var read = await CallAsync(tools, "doc_read", """{"scope":"workspace","path":"stock.xlsx"}""");
        Assert.True(read.Ok, read.Error?.Message);
        Assert.Contains("| 苹果 | 45 |", read.Value!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("| 苹果 | 42 |", read.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cell_that_held_a_formula_says_the_formula_is_gone()
    {
        var (_, tools) = Open();
        Assert.True((await CallAsync(tools, "doc_create_xlsx",
            """
            {"scope":"workspace","path":"sum.xlsx","sheets":[{"name":"表","rows":[["a","1"],["b","2"]]}]}
            """)).Ok);

        // Put a formula into the sheet by hand: the creator does not write them, and this test is about editing one.
        var path = Path.Combine(_root, "workspace", "sum.xlsx");
        using (var document = SpreadsheetDocument.Open(path, isEditable: true))
        {
            var workbookPart = document.WorkbookPart!;
            var sheet = workbookPart.Workbook!.Sheets!.Elements<S.Sheet>().First();
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            var row = worksheetPart.Worksheet!.GetFirstChild<S.SheetData>()!.Elements<S.Row>().Last();
            row.AppendChild(new S.Cell
            {
                CellReference = "C2",
                CellFormula = new S.CellFormula("SUM(B1:B2)"),
                CellValue = new S.CellValue("3")
            });
            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.Save();
        }

        var edited = await CallAsync(tools, "doc_edit_xlsx",
            """{"scope":"workspace","path":"sum.xlsx","edits":[{"sheet":"表","ref":"C2","text":"5"}]}""");

        Assert.True(edited.Ok, edited.Error?.Message);
        // The stale cache under a new formula would be a worse lie than no cache, so the formula goes and it is said.
        Assert.Contains("公式", edited.Value!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_paragraph_holding_a_field_is_refused_rather_than_rewritten()
    {
        var (access, tools) = Open();

        var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("普通段落"))),
                // A field is text that means something else as well: page number, cross-reference, date. Rewriting the
                // text leaves the field instruction pointing at whatever it pointed at before, which is how a document
                // opens fine and is wrong.
                new W.Paragraph(new W.Run(new W.FieldCode { Text = " PAGE " }))));
            main.Document.Save();
        }
        stream.Position = 0;
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "fields.docx", stream, default)).Ok);

        var answer = await CallAsync(tools, "doc_edit_docx",
            """{"scope":"workspace","path":"fields.docx","edits":[{"index":2,"text":"改这里"}]}""");

        Assert.False(answer.Ok);
        Assert.Equal(ErrorCode.Denied, answer.Error!.Code);
        Assert.Contains("域", answer.Error.Message, StringComparison.Ordinal);
    }
}
