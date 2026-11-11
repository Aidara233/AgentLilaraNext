using System.IO.Compression;
using System.Text;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using Aln.Documents;

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

    private static MemoryStream MinimalDocx()
    {
        // A real OOXML container, not a marker: the two parts below are what makes a zip a document as far as any
        // reader is concerned, and building it the same way the plugin's parser will have to read it keeps this test
        // honest after the parser lands.
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open(), Encoding.UTF8))
                writer.Write("""<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>""");
            using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open(), Encoding.UTF8))
                writer.Write("""<?xml version="1.0"?><w:document xmlns:w="x"><w:body/></w:document>""");
        }
        zip.Position = 0;
        return zip;
    }

    [Fact]
    public async Task The_tool_is_offered_and_reading_a_file_is_not_speaking_to_anyone()
    {
        var (_, tools) = Open();
        var offers = (await tools.ToolsAsync(new RoundContextRequest("s-1", Guid.NewGuid(), 1), default)).Value!;

        var offer = Assert.Single(offers);
        Assert.Equal("doc_info", offer.Definition.Name);
        Assert.False(offer.ActsExternally);
        Assert.False(offer.Tool.EndsTurn);
        Assert.Equal(AccessLevel.Agent, offer.MinLevel);
    }

    [Fact]
    public async Task A_docx_is_recognised_as_the_container_it_is()
    {
        var (access, tools) = Open();
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "reports/q3.docx", MinimalDocx(), default)).Ok);

        var answer = await CallAsync(tools, "doc_info",
            """{"scope":"workspace","path":"reports/q3.docx"}""");

        Assert.True(answer.Ok);
        var text = answer.Value!.Content;
        Assert.Contains("ZIP", text, StringComparison.Ordinal);
        Assert.Contains("workspace/reports/q3.docx", text, StringComparison.Ordinal);
        // It says out loud that the parsing half is not there yet, instead of implying the content was read.
        Assert.Contains("解析还没接上", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bytes_decide_not_the_name()
    {
        var (access, tools) = Open();
        // A PDF named like a Word file. The name is what somebody typed; the header is what the file is.
        var pdf = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n"));
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "report.docx", pdf, default)).Ok);

        var answer = await CallAsync(tools, "doc_info", """{"scope":"workspace","path":"report.docx"}""");

        Assert.True(answer.Ok);
        Assert.Contains("PDF", answer.Value!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("ZIP", answer.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bytes_nobody_recognises_are_said_to_be_unrecognised()
    {
        var (access, tools) = Open();
        var noise = new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);
        Assert.True((await access.SaveAsync(FileScopes.Workspace, "blob.bin", noise, default)).Ok);

        var answer = await CallAsync(tools, "doc_info", """{"scope":"workspace","path":"blob.bin"}""");

        Assert.True(answer.Ok);
        Assert.Contains("没认出来", answer.Value!.Content, StringComparison.Ordinal);
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
