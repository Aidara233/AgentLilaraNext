using ALN.Contracts;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Aln.Documents;

/// <summary>One block of a document being written. <see cref="Rows"/> is used only by tables.</summary>
public sealed record DocxBlock(string Kind, string Text, int Level = 1,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null)
{
    public const string Heading = "heading";
    public const string Paragraph = "paragraph";
    public const string Bullet = "bullet";
    public const string Table = "table";
}

/// <summary>
/// Writing a .docx that a person can open and a reader can read back.
///
/// <b>Headings carry both the style and the outline level.</b> A heading written with only a style id is a heading to
/// Word and a plain paragraph to anything that reads the outline; one written with only an outline level is the
/// reverse. Writing both costs one element and makes the document mean the same thing to both readers — which is
/// exactly what the reader on this side looks at.
///
/// <b>The styles part is not written.</b> It is optional in OOXML, and a heading that names <c>Heading1</c> without a
/// styles part still maps onto Word's built-in definition of that name. Shipping a styles part would mean shipping a
/// default font, and a document whose font nobody chose is a document that looks like a bug.
///
/// <b>A bullet is a paragraph with a marker, not a numbering definition.</b> A real numbered list needs a
/// <c>numbering.xml</c> part, a reference to it from the styles, and an abstract numbering per list — three parts for
/// something whose only job here is to be visible as a list when it is read back. The text says it is a list; that is
/// what the model reading this document will see, and it is the same thing Word shows for a manually typed bullet.
/// </summary>
internal static class DocxWriter
{
    public static Result Write(Stream output, IReadOnlyList<DocxBlock> blocks)
    {
        try
        {
            using var document = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document();
            var body = new W.Body();
            main.Document.Append(body);

            foreach (var block in blocks)
            {
                switch (block.Kind)
                {
                    case DocxBlock.Heading:
                        body.AppendChild(new W.Paragraph(
                            new W.ParagraphProperties(
                                new W.ParagraphStyleId { Val = $"Heading{Math.Clamp(block.Level, 1, 9)}" },
                                // 0-based in the format, 1-based in the label the model sees.
                                new W.OutlineLevel { Val = Math.Clamp(block.Level, 1, 9) - 1 }),
                            new W.Run(new W.Text(block.Text) { Space = SpaceProcessingModeValues.Preserve })));
                        break;

                    case DocxBlock.Bullet:
                        body.AppendChild(new W.Paragraph(
                            new W.ParagraphProperties(new W.Indentation { Left = "720" }),
                            new W.Run(new W.Text("• " + block.Text) { Space = SpaceProcessingModeValues.Preserve })));
                        break;

                    case DocxBlock.Table when block.Rows is { Count: > 0 } rows:
                        body.AppendChild(Table(rows));
                        break;

                    case DocxBlock.Paragraph:
                    default:
                        body.AppendChild(new W.Paragraph(
                            new W.Run(new W.Text(block.Text) { Space = SpaceProcessingModeValues.Preserve })));
                        break;
                }
            }

            main.Document.Save();
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or OpenXmlPackageException or InvalidOperationException)
        {
            return Result.Failure(new(ErrorCode.Internal, $"写 .docx 失败：{ex.Message}", "docx write"));
        }
    }

    private static W.Table Table(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columns = Math.Max(1, rows.Max(row => row.Count));
        var table = new W.Table();

        var borders = new W.TableBorders();
        borders.TopBorder = Edge<W.TopBorder>();
        borders.BottomBorder = Edge<W.BottomBorder>();
        borders.LeftBorder = Edge<W.LeftBorder>();
        borders.RightBorder = Edge<W.RightBorder>();
        borders.InsideHorizontalBorder = Edge<W.InsideHorizontalBorder>();
        borders.InsideVerticalBorder = Edge<W.InsideVerticalBorder>();
        table.AppendChild(new W.TableProperties(
            new W.TableWidth { Type = W.TableWidthUnitValues.Auto, Width = "0" },
            borders,
            new W.TableLook { Val = "04A0", FirstRow = true, LastRow = false, FirstColumn = true, LastColumn = false,
                NoHorizontalBand = false, NoVerticalBand = true }));

        var grid = new W.TableGrid();
        for (var column = 0; column < columns; column++) grid.AppendChild(new W.GridColumn());
        table.AppendChild(grid);

        foreach (var row in rows)
        {
            var tableRow = new W.TableRow();
            for (var column = 0; column < columns; column++)
            {
                var text = column < row.Count ? row[column] ?? string.Empty : string.Empty;
                tableRow.AppendChild(new W.TableCell(
                    new W.Paragraph(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }))));
            }
            table.AppendChild(tableRow);
        }
        return table;

        static T Edge<T>() where T : W.BorderType, new() => new()
        {
            Val = W.BorderValues.Single,
            Size = 4,
            Space = 0,
            Color = "auto"
        };
    }
}
