using System.Globalization;
using ALN.Contracts;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Aln.Documents;

/// <summary>
/// An .xlsx read as what it is: a list of worksheets, each one a grid.
///
/// <b>Formulas are shown, never evaluated.</b> A spreadsheet's value for a cell is whatever the last program that
/// opened it computed and cached there; that number is in the file, and recalculating it here would mean shipping a
/// formula engine and disagreeing with Excel about the answer. So a cell that holds a formula is rendered as
/// <c>42 [=SUM(A1:A3)]</c> — the cached value, and how it got there — and the sheet says out loud that the cache may be
/// stale. A model that needs a fresh number can compute it; a model that is handed a wrong number cannot tell.
///
/// <b>A grid with no rows is still a sheet.</b> Empty sheets carry meaning in a workbook (a placeholder, a deleted
/// tab), and dropping them would make "the third sheet" wrong in a way the reader cannot see.
/// </summary>
internal static class XlsxReader
{
    public const int MaxRows = 200;
    public const int MaxColumns = 16;

    public static Result<ParsedDocument> Read(Stream stream, int maxChars)
    {
        SpreadsheetDocument document;
        try
        {
            document = SpreadsheetDocument.Open(stream, isEditable: false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException or System.Xml.XmlException)
        {
            return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                $"这不是一个能打开的 .xlsx：{ex.Message}", "the zip opened but its spreadsheet parts did not"));
        }

        using (document)
        {
            var workbookPart = document.WorkbookPart;
            var sheets = workbookPart?.Workbook?.Sheets?.Elements<Sheet>().ToList();
            if (workbookPart is null || sheets is null)
                return Result<ParsedDocument>.Failure(new(ErrorCode.Invalid,
                    "这个 .xlsx 里没有工作簿部分（xl/workbook.xml）。"));

            var shared = workbookPart.SharedStringTablePart?.SharedStringTable?
                .Elements<SharedStringItem>().Select(item => item.InnerText).ToArray() ?? [];
            var styles = workbookPart.WorkbookStylesPart?.Stylesheet;

            var sections = new List<DocumentSection>();
            var used = 0;
            var truncated = false;
            var index = 0;

            foreach (var sheet in sheets)
            {
                if (used >= maxChars) { truncated = true; break; }
                index++;

                var name = sheet.Name?.Value ?? $"#{index}";
                if (sheet.Id?.Value is not { Length: > 0 } id || workbookPart.GetPartById(id) is not WorksheetPart part)
                {
                    sections.Add(new DocumentSection(SectionKind.Sheet, index, $"{name}（读不到）",
                        "这个工作表指向的部件不在包里，内容读不出来。"));
                    continue;
                }

                var (rows, rowCount, columnCount, cut) = Grid(part, shared, styles);
                var label = $"{name}（{rowCount} 行 × {columnCount} 列）";
                var text = rows.Count == 0
                    ? "这个表是空的。"
                    : Blocks.RenderGrid(rows, MaxColumns) + (cut ? $"\n（只渲染了前 {MaxRows} 行 / {MaxColumns} 列。）" : "")
                      + (HasFormula(part) ? "\n（含公式的单元格显示为「缓存值 [=公式]」；缓存值可能陈旧，本框架不重算。）" : "");

                if (used + text.Length > maxChars) { truncated = true; break; }
                sections.Add(new DocumentSection(SectionKind.Sheet, index, label, text));
                used += text.Length;
            }

            return Result<ParsedDocument>.Success(new ParsedDocument(DocumentFormats.Excel, sections, truncated));
        }
    }

    private static (List<IReadOnlyList<string>> Rows, int RowCount, int ColumnCount, bool Cut) Grid(
        WorksheetPart part, string[] shared, Stylesheet? styles)
    {
        var cells = part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>().Take(MaxRows + 1).ToList();
        if (cells is null || cells.Count == 0) return ([], 0, 0, false);

        var cut = cells.Count > MaxRows;
        var rows = new List<IReadOnlyList<string>>();
        var columns = 0;
        var position = 0;
        foreach (var row in cells.Take(MaxRows))
        {
            position++;
            var rowIndex = row.RowIndex?.Value is { } declared ? (int)declared : position;
            _ = rowIndex; // the grid is rendered in file order; a gap in the sheet is a gap in the text
            var values = new SortedDictionary<int, string>();
            var cursor = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var column = cell.CellReference?.Value is { Length: > 0 } reference
                    ? ColumnIndex(reference)
                    : cursor + 1;
                cursor = column;
                if (column > MaxColumns) continue;
                values[column] = Value(cell, shared, styles);
            }
            if (values.Count == 0) continue;
            var width = Math.Min(MaxColumns, values.Keys.Max());
            columns = Math.Max(columns, width);
            var line = new string[width];
            for (var column = 1; column <= width; column++)
                line[column - 1] = values.TryGetValue(column, out var value) ? value : string.Empty;
            rows.Add(line);
        }
        return (rows, position, columns, cut);
    }

    private static bool HasFormula(WorksheetPart part) =>
        part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>()
            .Take(MaxRows).Any(row => row.Elements<Cell>().Any(cell => cell.CellFormula is not null)) ?? false;

    /// <summary>"C12" to 3. The letters are A=1, Z=26, AA=27 — base 26 with no zero digit.</summary>
    private static int ColumnIndex(string reference)
    {
        var column = 0;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character)) break;
            column = column * 26 + (char.ToUpperInvariant(character) - 'A' + 1);
        }
        return column;
    }

    private static string Value(Cell cell, string[] shared, Stylesheet? styles)
    {
        var raw = cell.CellValue?.InnerText;
        var formula = cell.CellFormula?.Text;
        var type = cell.DataType?.Value;
        string text;
        if (type == CellValues.SharedString)
            text = int.TryParse(raw, out var index) && index >= 0 && index < shared.Length
                ? shared[index]
                : raw ?? string.Empty;
        else if (type == CellValues.InlineString)
            text = cell.InlineString?.InnerText ?? string.Empty;
        else if (type == CellValues.Boolean)
            text = raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw ?? string.Empty;
        else if (type == CellValues.Error)
            text = raw ?? string.Empty;
        else
            text = NumericOrDate(cell, raw, styles);

        text = Blocks.Clean(text).Replace("|", "\\|", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(formula)) return text;
        return text.Length == 0 ? $"[={formula}]" : $"{text} [={formula}]";
    }

    /// <summary>
    /// A number, or the date that number stands for. 45000 is a number; in a date-formatted cell it is 2023-03-15, and
    /// handing a model the raw serial is how "the invoice date" becomes a five-digit integer.
    /// </summary>
    private static string NumericOrDate(Cell cell, string? raw, Stylesheet? styles)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        if (!IsDate(cell, styles)) return raw;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) ||
            serial is <= 0 or >= 2958466) return raw;

        var moment = DateTime.FromOADate(serial);
        // Time is shown only when the cell holds one: a date column reads as dates, and appending 00:00 to every one
        // of them is noise a model then has to learn to ignore.
        return moment.TimeOfDay == TimeSpan.Zero
            ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : moment.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static bool IsDate(Cell cell, Stylesheet? styles)
    {
        if (cell.StyleIndex?.Value is not { } styleIndex) return false;
        var formats = styles?.CellFormats?.Elements<CellFormat>().ToList();
        if (formats is null || styleIndex >= formats.Count) return false;
        var formatId = formats[(int)styleIndex].NumberFormatId?.Value ?? 0;

        // The built-in date and time formats, by the ids ECMA-376 fixes for them.
        if (formatId is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58) return true;
        if (formatId < 164) return false;

        // A custom format: the code is the only place the answer lives, and a date code always has a year, month, day
        // or time token in it. Quoted literals and colours are skipped so that `[Red]"day"0` is not read as a date.
        var custom = styles?.NumberingFormats?.Elements<NumberingFormat>()
            .FirstOrDefault(format => format.NumberFormatId?.Value == formatId)?.FormatCode?.Value;
        return custom is not null && LooksLikeDate(custom);
    }

    private static bool LooksLikeDate(string code)
    {
        var inQuotes = false;
        var inBrackets = false;
        foreach (var character in code)
        {
            if (character == '"') { inQuotes = !inQuotes; continue; }
            if (inQuotes) continue;
            if (character == '[') { inBrackets = true; continue; }
            if (character == ']') { inBrackets = false; continue; }
            if (inBrackets) continue;
            if (character is 'y' or 'Y' or 'd' or 'D' or 'h' or 'H' or 's' or 'S') return true;
            if (character is 'm' or 'M')
            {
                // "m" alone is minutes when it follows an hour token, but in a format code the distinction only exists
                // between h and s; either way it is a time format, which is still a date for this purpose.
                return true;
            }
        }
        return false;
    }
}
