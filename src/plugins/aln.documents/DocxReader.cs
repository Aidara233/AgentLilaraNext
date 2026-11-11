using System.Text;
using ALN.Contracts;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Aln.Documents;

/// <summary>
/// A .docx read as the blocks a person would point at: headings, paragraphs, list items, tables.
///
/// <b>Order is the document's order, not the schema's.</b> The body is walked as a sequence of block-level children
/// and each one becomes its own section, because "paragraph 12" in a listing has to mean the twelfth thing on the
/// page — a reader that emitted all paragraphs and then all tables would put every table at the end.
///
/// <b>Headers, footers and footnotes are not read.</b> They are not part of the flow of the document, they repeat on
/// every page, and pasting a letterhead into the middle of a chapter is how a summary ends up about the wrong thing.
/// A model that wants them can be given them later; a model that gets them unasked cannot tell they were furniture.
/// </summary>
internal static class DocxReader
{
    public const int MaxSections = 4000;

    public static Result<ParsedDocument> Read(Stream stream, int maxChars)
    {
        WordprocessingDocument document;
        try
        {
            document = WordprocessingDocument.Open(stream, isEditable: false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException or System.Xml.XmlException)
        {
            return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                $"这不是一个能打开的 .docx：{ex.Message}", "the zip opened but its wordprocessing parts did not"));
        }

        using (document)
        {
            var body = document.MainDocumentPart?.Document?.Body;
            if (body is null)
                return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                    "这个 .docx 里没有正文部分（word/document.xml）。"));

            var sections = new List<DocumentSection>();
            var used = 0;
            var truncated = false;
            var paragraphIndex = 0;
            var tableIndex = 0;

            foreach (var block in body.ChildElements)
            {
                if (used >= maxChars || sections.Count >= MaxSections) { truncated = true; break; }

                switch (block)
                {
                    case Paragraph paragraph:
                    {
                        var text = Blocks.Clean(paragraph.InnerText);
                        if (text.Length == 0) break; // an empty paragraph is spacing, not content
                        paragraphIndex++;
                        var level = HeadingLevel(paragraph);
                        var bullet = paragraph.ParagraphProperties?.NumberingProperties is not null;
                        var label = level > 0 ? $"标题 {level}" : bullet ? "列表项" : "段落";
                        var rendered = level > 0 ? new string('#', level) + " " + text
                            : bullet ? "- " + text
                            : text;
                        used = Append(sections, ref truncated, used, maxChars,
                            new DocumentSection(SectionKind.Paragraph, paragraphIndex, label, rendered));
                        break;
                    }
                    case Table table:
                    {
                        var rendered = Blocks.RenderTable(table);
                        if (rendered.Length == 0) break;
                        tableIndex++;
                        used = Append(sections, ref truncated, used, maxChars,
                            new DocumentSection(SectionKind.Table, tableIndex, "表格", rendered));
                        break;
                    }
                }
            }

            return Result<ParsedDocument>.Success(
                new ParsedDocument(DocumentFormats.Word, sections, truncated));
        }
    }

    private static int Append(List<DocumentSection> sections, ref bool truncated, int used, int maxChars,
        DocumentSection section)
    {
        if (used + section.Text.Length > maxChars)
        {
            truncated = true;
            return used;
        }
        sections.Add(section);
        return used + section.Text.Length;
    }

    /// <summary>
    /// How deep a heading sits, or 0 for ordinary prose. Two sources, because a real document uses one or the other:
    /// the outline level (<c>w:outlineLvl</c>, which is what the navigation pane reads) and the style id
    /// (<c>HeadingN</c>, which is what a person sees applied). Neither is trusted alone — a document produced by a
    /// tool often has one and not the other.
    /// </summary>
    private static int HeadingLevel(Paragraph paragraph)
    {
        var properties = paragraph.ParagraphProperties;
        if (properties?.OutlineLevel?.Val?.Value is { } outline && outline is >= 0 and <= 8)
            return outline + 1;

        var styleId = properties?.ParagraphStyleId?.Val?.Value;
        if (string.IsNullOrEmpty(styleId)) return 0;
        if (!styleId.Contains("heading", StringComparison.OrdinalIgnoreCase) &&
            !styleId.Contains("标题", StringComparison.Ordinal)) return 0;

        var digits = new string(styleId.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var level) && level is >= 1 and <= 9 ? level : 1;
    }
}
