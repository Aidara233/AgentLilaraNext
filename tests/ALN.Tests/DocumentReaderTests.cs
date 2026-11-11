using System.Text;
using ALN.Contracts;
using Aln.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ALN.Tests;

/// <summary>
/// The parsers, fed documents built in the test rather than checked-in binaries.
///
/// <b>Why build them here.</b> A .docx in a repository is a file nobody can review: its content is a zip of XML that
/// nobody diffs, and the day it stops being interesting nobody can tell whether it was ever right. Constructing the
/// smallest document that exercises a rule keeps the rule visible in the test that pins it — and there is no binary in
/// the tree that has to be trusted.
/// </summary>
public sealed class DocumentReaderTests
{
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
                new W.Paragraph(new W.Run(new W.Text("这是第一段正文。"))),
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

    private static MemoryStream Xlsx()
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(
                    Inline("A1", "名称"),
                    Inline("B1", "数量")),
                new S.Row(
                    Inline("A2", "苹果"),
                    new S.Cell { CellReference = "B2", CellValue = new S.CellValue("42") })));
            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.AppendChild(new S.Sheets(new S.Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "库存"
            }));
            workbookPart.Workbook.Save();
        }
        stream.Position = 0;
        return stream;

        static S.Cell Inline(string reference, string text) => new()
        {
            CellReference = reference,
            DataType = S.CellValues.InlineString,
            InlineString = new S.InlineString(new S.Text(text))
        };
    }

    private static MemoryStream Pptx()
    {
        var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            var ids = new P.SlideIdList();
            for (var index = 1; index <= 2; index++)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                    new P.Shape(
                        new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Name = "标题" }),
                        new P.TextBody(new A.BodyProperties(),
                            new A.Paragraph(new A.Run(new A.Text($"第 {index} 页的文字"))))))));
                slidePart.Slide.Save();
                // The slide id list is what decides presentation order — which is the whole point of reading it
                // instead of trusting the order the parts happen to sit in the package.
                ids.Append(new P.SlideId
                {
                    Id = (uint)(255 + index),
                    RelationshipId = presentationPart.GetIdOfPart(slidePart)
                });
            }
            presentationPart.Presentation.Append(ids);
            presentationPart.Presentation.Save();
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void A_docx_keeps_the_document_order()
    {
        using var stream = Docx();
        var parsed = DocumentReader.Read(stream);

        Assert.True(parsed.Ok, parsed.Error?.Message);
        var sections = parsed.Value!.Sections;
        Assert.Equal(DocumentFormats.Word, parsed.Value.Format);
        Assert.Equal(3, sections.Count);
        Assert.Equal(SectionKind.Paragraph, sections[0].Kind);
        Assert.Contains("季度报告", sections[0].Text, StringComparison.Ordinal);
        // A heading is marked so that a model reading the text back can see the structure it was given.
        Assert.StartsWith("# ", sections[0].Text, StringComparison.Ordinal);
        Assert.Equal("这是第一段正文。", sections[1].Text);
        Assert.Equal(SectionKind.Table, sections[2].Kind);
        Assert.Contains("| 项目 | 金额 |", sections[2].Text, StringComparison.Ordinal);
        Assert.Contains("| 苹果 | 42 |", sections[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sheet_arrives_with_its_name_and_its_cells()
    {
        using var stream = Xlsx();
        var parsed = DocumentReader.Read(stream);

        Assert.True(parsed.Ok, parsed.Error?.Message);
        var sheet = Assert.Single(parsed.Value!.Sections);
        Assert.Equal(SectionKind.Sheet, sheet.Kind);
        Assert.Contains("库存", sheet.Label, StringComparison.Ordinal);
        Assert.Contains("名称", sheet.Text, StringComparison.Ordinal);
        Assert.Contains("苹果", sheet.Text, StringComparison.Ordinal);
        Assert.Contains("| 42 |", sheet.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Slides_are_in_presentation_order()
    {
        using var stream = Pptx();
        var parsed = DocumentReader.Read(stream);

        Assert.True(parsed.Ok, parsed.Error?.Message);
        var slides = parsed.Value!.Sections;
        Assert.Equal(2, slides.Count);
        Assert.All(slides, slide => Assert.Equal(SectionKind.Slide, slide.Kind));
        Assert.Contains("第 1 页的文字", slides[0].Text, StringComparison.Ordinal);
        Assert.Contains("第 2 页的文字", slides[1].Text, StringComparison.Ordinal);
        // The shape's own name travels with its text: in a deck it is often the only word on the slide.
        Assert.Contains("【标题】", slides[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_budget_stops_the_read_and_says_so()
    {
        using var stream = Docx();
        var parsed = DocumentReader.Read(stream, maxChars: 12);

        Assert.True(parsed.Ok, parsed.Error?.Message);
        Assert.True(parsed.Value!.Truncated);
        Assert.True(parsed.Value.Characters <= 12);
    }

    [Fact]
    public void Bytes_that_are_not_a_document_are_refused_with_what_they_were()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("hello, I am a text file"));

        var parsed = DocumentReader.Read(stream);

        Assert.False(parsed.Ok);
        Assert.Contains("开头几个字节", parsed.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zip_that_is_not_a_document_is_named_as_that_rather_than_as_corruption()
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open(), Encoding.UTF8))
            writer.Write("just a zip");
        stream.Position = 0;

        var parsed = DocumentReader.Read(stream);

        Assert.False(parsed.Ok);
        Assert.Contains("zip", parsed.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_encrypted_or_ancient_office_file_says_which_ambiguity_it_is()
    {
        // An OLE container: the same four bytes for a .doc, a .xls, and a password-protected .docx.
        using var stream = new MemoryStream([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);

        var parsed = DocumentReader.Read(stream);

        Assert.False(parsed.Ok);
        Assert.Contains("OLE", parsed.Error!.Message, StringComparison.Ordinal);
    }
}
