using ALN.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace Aln.Documents;

/// <summary>
/// A PDF read page by page, and three answers that must not be confused with each other.
///
/// <b>A scanned page has no text layer, and that is not an empty page.</b> A file produced by a scanner is a picture of
/// a page: the PDF opens, the page count is right, and every page's text is the empty string. Reporting that as "the
/// document is empty" would be a lie about a document that is sitting right there, so the reader says which it is —
/// "pages 1–3 have no text layer" — and stops. Reading them is a job for OCR, which this build does not have and will
/// not pretend to.
///
/// <b>An encrypted file is not a corrupt one.</b> A PDF can be encrypted with an empty user password and still be
/// locked; guessing is not a thing this reader does, and a password prompt is not a thing a tool can have. So the
/// sentence names the lock.
///
/// <b>Tables are approximations here and are labelled as such.</b> A PDF has no notion of a table — it has glyphs at
/// coordinates, and a "column" is a coincidence of x positions. This reader gives lines, which is what a text layer can
/// honestly be turned into; anything that looked like a table is the reader's inference and is described that way.
/// </summary>
internal static class PdfReader
{
    public const int MaxPages = 500;

    public static Result<ParsedDocument> Read(Stream stream, int maxChars)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(stream);
        }
        catch (PdfDocumentEncryptedException)
        {
            return Result<ParsedDocument>.Failure(new(ErrorCode.Denied,
                "这个 PDF 加密了。本框架不猜密码，也不问——需要的人先解密，再把这个文件放回来。"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // Everything else a malformed or non-PDF file can produce: a broken xref, a missing trailer, a stream that
            // lies about its length. The library's exception zoo is not part of any contract, so the guard is written
            // against what must not be swallowed instead of against the names of what must.
            return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                $"这不是一个能打开的 PDF：{ex.Message}", "pdf structure unreadable"));
        }

        using (document)
        {
            var sections = new List<DocumentSection>();
            var used = 0;
            var truncated = false;
            var withoutText = new List<int>();
            var total = document.NumberOfPages;

            foreach (var page in document.GetPages())
            {
                if (page.Number > MaxPages) { truncated = true; break; }
                if (used >= maxChars) { truncated = true; break; }

                var text = Blocks.NormalizeLines(page.Text);
                if (text.Length == 0) { withoutText.Add(page.Number); continue; }
                if (used + text.Length > maxChars) { truncated = true; break; }

                sections.Add(new DocumentSection(SectionKind.Page, page.Number, $"第 {page.Number} 页", text));
                used += text.Length;
            }

            if (sections.Count == 0)
            {
                // Two different empty documents, said differently: one has no text layer at all (a scan), the other has
                // pages that happen to be blank. Both are answers; neither is "there is nothing here".
                return Result<ParsedDocument>.Failure(new(withoutText.Count > 0 ? ErrorCode.Unavailable : ErrorCode.Invalid,
                    withoutText.Count > 0
                        ? $"这个 PDF 有 {total} 页，但没有文字层：{Describe(withoutText)}。"
                          + "它多半是扫描件或导出时被栅格化了。本框架不做 OCR——要看内容，得先有人把文字认出来。"
                        : $"这个 PDF 有 {total} 页，但一页可读的文字都没有（页是空白的）。"));
            }

            if (withoutText.Count > 0)
            {
                // Said on the successful path too: a document with nine readable pages and one scan has a gap, and a
                // model that does not know about it will answer questions about page 10 as if it had read it.
                sections.Add(new DocumentSection(SectionKind.Page, 0,
                    "没有文字层的页",
                    $"{Describe(withoutText)} 没有文字层（扫描件或图片页），内容没有读出来。"));
                truncated = true;
            }

            return Result<ParsedDocument>.Success(new ParsedDocument(DocumentFormats.Pdf, sections, truncated));
        }
    }

    /// <summary>Page numbers as a person would say them: 3, or 3–5, or 3, 7, 9–11.</summary>
    private static string Describe(List<int> pages)
    {
        var builder = new System.Text.StringBuilder();
        var start = pages[0];
        var previous = start;
        for (var index = 1; index <= pages.Count; index++)
        {
            var current = index < pages.Count ? pages[index] : int.MinValue;
            if (current == previous + 1) { previous = current; continue; }
            if (builder.Length > 0) builder.Append('、');
            builder.Append(start == previous ? $"第 {start} 页" : $"第 {start}–{previous} 页");
            if (index < pages.Count) { start = current; previous = current; }
        }
        return builder.ToString();
    }
}
