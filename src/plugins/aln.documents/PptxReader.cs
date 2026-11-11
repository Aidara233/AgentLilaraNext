using System.Text;
using ALN.Contracts;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;

namespace Aln.Documents;

/// <summary>
/// A .pptx read slide by slide, in the order the deck presents them.
///
/// <b>The order comes from the presentation, not from the package.</b> Slides are parts, and the order parts appear in
/// a package is the order somebody's zip writer produced them — a deck whose "agenda" slide showed up as page seven is
/// exactly the confusion this rule avoids. The slide id list is the order a person sees.
///
/// <b>A shape's own name is part of its text.</b> "Revenue chart" and "Q3 numbers" are what the author called them, and
/// in a deck they often carry the only words on the slide. Losing them turns a readable slide into a list of numbers.
/// </summary>
internal static class PptxReader
{
    public const int MaxSlides = 300;

    public static Result<ParsedDocument> Read(Stream stream, int maxChars)
    {
        PresentationDocument document;
        try
        {
            document = PresentationDocument.Open(stream, isEditable: false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException or System.Xml.XmlException)
        {
            return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                $"这不是一个能打开的 .pptx：{ex.Message}", "the zip opened but its presentation parts did not"));
        }

        using (document)
        {
            var presentationPart = document.PresentationPart;
            var ids = presentationPart?.Presentation?.SlideIdList?.Elements<SlideId>().ToList();
            if (presentationPart is null || ids is null)
                return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                    "这个 .pptx 里没有演示文稿部分（ppt/presentation.xml）。"));

            var sections = new List<DocumentSection>();
            var used = 0;
            var truncated = ids.Count > MaxSlides;

            for (var index = 0; index < ids.Count && index < MaxSlides; index++)
            {
                if (used >= maxChars) { truncated = true; break; }
                if (ids[index].RelationshipId?.Value is not { Length: > 0 } relationship
                    || presentationPart.GetPartById(relationship) is not SlidePart slide)
                    continue;

                var text = Slide(slide);
                var number = index + 1;
                var body = text.Length == 0 ? "这一页没有文字（只有图形或图片）。" : text;
                if (used + body.Length > maxChars) { truncated = true; break; }
                sections.Add(new DocumentSection(SectionKind.Slide, number, $"第 {number} 页", body));
                used += body.Length;
            }

            return Result<ParsedDocument>.Success(new ParsedDocument(DocumentFormats.PowerPoint, sections, truncated));
        }
    }

    private static string Slide(SlidePart part)
    {
        var builder = new StringBuilder();
        var slide = part.Slide;
        if (slide is null) return string.Empty;

        foreach (var shape in slide.Descendants<Shape>())
        {
            var name = shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value;
            var text = Blocks.Clean(shape.TextBody?.InnerText ?? string.Empty);
            if (text.Length == 0) continue;

            if (builder.Length > 0) builder.Append('\n');
            if (!string.IsNullOrEmpty(name)) builder.Append('【').Append(name).Append("】");
            builder.Append(text);
        }

        // A title placeholder is a shape like any other; if the slide has nothing else, that is the whole slide and it
        // still needs to say something. Tables and grouped text outside a shape's body are not read — a deck built
        // entirely from SmartArt reports "no text" rather than a plausible-looking fragment.
        return builder.ToString();
    }
}
