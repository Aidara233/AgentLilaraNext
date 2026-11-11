using System.Text;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using Aln.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
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

        Assert.Equal(["doc_info", "doc_tree", "doc_read"], offers.Select(offer => offer.Definition.Name));
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
}
