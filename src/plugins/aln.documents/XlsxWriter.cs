using System.Globalization;
using System.Text;
using ALN.Contracts;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Aln.Documents;

/// <summary>One worksheet being written: a name and rows of cell text.</summary>
public sealed record XlsxSheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Writing a .xlsx, with two decisions that matter more than the plumbing.
///
/// <b>A number is written as a number, everything else as text.</b> A spreadsheet where every cell is text is a
/// spreadsheet whose columns do not sort, do not sum, and show the green triangle of a number stored as text — an
/// artefact of generation that a person then has to clean up by hand. A cell whose text parses as a plain decimal is
/// therefore written as a number. Leading zeros ("007", a phone number) keep it text, because that is what the zeros
/// were for.
///
/// <b>No shared string table.</b> Shared strings exist to shrink a file with the same text repeated thousands of times;
/// writing them means building an index before any cell can be emitted, and getting that wrong turns a small mistake
/// into a file Excel refuses to open. Inline strings are legal, larger, and impossible to get wrong — the right
/// trade for a document generated once and then read back.
/// </summary>
internal static class XlsxWriter
{
    private const int MaxColumnName = 16_384; // XFD, the last column Excel has

    public static Result Write(Stream output, IReadOnlyList<XlsxSheet> sheets)
    {
        if (sheets.Count == 0)
            return Result.Failure(new(ErrorCode.Invalid, "至少要有一个工作表。"));

        try
        {
            using var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook);
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook();
            var sheetCollection = new S.Sheets();
            workbookPart.Workbook.AppendChild(sheetCollection);

            for (var index = 0; index < sheets.Count; index++)
            {
                var sheet = sheets[index];
                var part = workbookPart.AddNewPart<WorksheetPart>();
                var data = new S.SheetData();
                for (var row = 0; row < sheet.Rows.Count; row++)
                {
                    var line = new S.Row { RowIndex = (uint)(row + 1) };
                    var values = sheet.Rows[row];
                    for (var column = 0; column < values.Count; column++)
                    {
                        var text = values[column];
                        if (string.IsNullOrEmpty(text)) continue;
                        line.AppendChild(Cell(ColumnName(column + 1) + (row + 1).ToString(CultureInfo.InvariantCulture), text));
                    }
                    if (line.ChildElements.Count > 0) data.AppendChild(line);
                }
                part.Worksheet = new S.Worksheet(data);
                part.Worksheet.Save();

                sheetCollection.AppendChild(new S.Sheet
                {
                    Id = workbookPart.GetIdOfPart(part),
                    SheetId = (uint)(index + 1),
                    Name = LegalName(sheet.Name, index + 1)
                });
            }

            workbookPart.Workbook.Save();
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or OpenXmlPackageException or InvalidOperationException)
        {
            return Result.Failure(new(ErrorCode.Internal, $"写 .xlsx 失败：{ex.Message}", "xlsx write"));
        }
    }

    private static S.Cell Cell(string reference, string text)
    {
        if (LooksNumeric(text))
            return new S.Cell { CellReference = reference, CellValue = new S.CellValue(text) };

        return new S.Cell
        {
            CellReference = reference,
            DataType = S.CellValues.InlineString,
            InlineString = new S.InlineString(new S.Text(text) { Space = SpaceProcessingModeValues.Preserve })
        };
    }

    /// <summary>
    /// Whether this text is a number a person would want summed.
    ///
    /// Deliberately narrow: no currency symbols, no thousands separators, no leading zeros, no bare "+". "1,234" and
    /// "007" are text as far as this is concerned, because turning them into 1234 and 7 is a decision about somebody's
    /// data that a document writer is not entitled to make.
    /// </summary>
    private static bool LooksNumeric(string text)
    {
        var span = text.Trim();
        if (span.Length == 0 || span.Length > 20) return false;
        if (span.Length > 1 && (span[0] == '0' || span[0] == '+')) return false;
        return decimal.TryParse(span, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Excel's own limits, applied rather than crashed into: 31 characters, and no : \ / ? * [ ].</summary>
    private static string LegalName(string? name, int position)
    {
        var cleaned = new StringBuilder();
        foreach (var character in name ?? string.Empty)
        {
            if (character is '\\' or '/' or '?' or '*' or '[' or ']' or ':') cleaned.Append('_');
            else cleaned.Append(character);
            if (cleaned.Length >= 31) break;
        }
        var result = cleaned.ToString().Trim();
        return result.Length == 0 ? $"Sheet{position}" : result;
    }

    /// <summary>"A", "Z", "AA" — the same base-26-without-zero that the reader parses back.</summary>
    private static string ColumnName(int column)
    {
        if (column > MaxColumnName) column = MaxColumnName;
        var builder = new StringBuilder(3);
        while (column > 0)
        {
            var remainder = (column - 1) % 26;
            builder.Insert(0, (char)('A' + remainder));
            column = (column - 1) / 26;
        }
        return builder.ToString();
    }
}
