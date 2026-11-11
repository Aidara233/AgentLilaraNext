using System.IO.Compression;
using System.Text;
using ALN.Contracts;

namespace Aln.Documents;

/// <summary>
/// The one place that decides which reader sees a file, and the one place that says no.
///
/// <b>The decision is made by the bytes, in two steps, and neither step guesses.</b> The first few bytes say whether
/// this is a PDF or a zip; if it is a zip, the part names inside say which of the three office formats it is. A file
/// called <c>report.docx</c> that is really a spreadsheet is read as a spreadsheet, because the name is what somebody
/// typed and the parts are what the file is.
///
/// <b>Three refusals that must stay apart.</b> "Not a format this build reads" is not "this file is broken", and
/// neither is "this file is locked". A model that hears the same sentence for all three will try the same thing three
/// times; a model told which one it is can act — ask for another file, ask for the password, or stop.
/// </summary>
public static class DocumentReader
{
    /// <summary>
    /// How much extracted text one read may produce. Not a file size: a 2 MB .docx can hold a million characters of
    /// prose. This is the point where "read the document" becomes "read part of the document and say so".
    /// </summary>
    public const int MaxTextChars = 200_000;

    public static Result<ParsedDocument> Read(Stream stream, int maxChars = MaxTextChars)
    {
        if (!stream.CanSeek)
            return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                "这个流不能回溯，读文档需要能回到开头（OpenReadAsync 给的是文件流，应该可以）。"));

        var head = new byte[8];
        stream.Position = 0;
        var read = stream.Read(head, 0, head.Length);
        stream.Position = 0;
        var span = head.AsSpan(0, read);

        // An OLE container is not a fourth format: it is what a .doc, a .xls, and *an encrypted OOXML file* all look
        // like from outside. Saying which of those it is would be a guess, so the sentence names the ambiguity.
        if (span.Length >= 4 && span[0] == 0xD0 && span[1] == 0xCF && span[2] == 0x11 && span[3] == 0xE0)
            return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                "这是 OLE 复合文档：要么是 97-2003 的老格式（.doc/.xls/.ppt），要么是加了密码的 OOXML。"
                + "两种情况本框架都读不了——加了密码的要先解密。", "ole container"));

        var detected = DocumentFormats.Detect(span);
        if (detected is null)
            return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                "这些字节不是本框架认识的办公文档（认识 .docx / .xlsx / .pptx / .pdf）。"
                + "如果它本该是其中之一，文件可能没下载完或者是别的格式。" + Describe(span), Describe(span)));

        if (detected == DocumentFormats.Pdf)
            return Result<ParsedDocument>.Failure(new(ErrorCode.Unavailable,
                "PDF 的解析还没接上。"));

        string? kind;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            kind = DocumentFormats.OoxmlKind(zip.Entries.Select(entry => entry.FullName).ToList());
        }
        catch (InvalidDataException ex)
        {
            return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                $"这是个 zip 容器，但打不开：{ex.Message}", "zip directory unreadable"));
        }
        finally
        {
            stream.Position = 0;
        }

        if (kind is null)
            return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                "这是个 zip，但不是 Word / Excel / PowerPoint 三者之一：里面没有 word/、xl/ 或 ppt/ 开头的部件。"));

        stream.Position = 0;
        return kind switch
        {
            DocumentFormats.Word => DocxReader.Read(stream, maxChars),
            DocumentFormats.Excel => XlsxReader.Read(stream, maxChars),
            DocumentFormats.PowerPoint => PptxReader.Read(stream, maxChars),
            _ => Result<ParsedDocument>.Failure(new(ErrorCode.Internal, $"没有为 {kind} 准备解析器。"))
        };
    }

    private static string Describe(ReadOnlySpan<byte> head)
    {
        var builder = new StringBuilder("开头几个字节：");
        for (var index = 0; index < head.Length; index++)
        {
            if (index > 0) builder.Append(' ');
            builder.Append(head[index].ToString("X2"));
        }
        return builder.ToString();
    }
}
