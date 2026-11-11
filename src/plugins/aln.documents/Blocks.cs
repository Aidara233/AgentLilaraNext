using System.Text;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Aln.Documents;

/// <summary>
/// Turning document blocks into text a model can read without guessing.
///
/// Two transformations happen here and nowhere else, because doing them in three readers is how they drift: whitespace
/// is normalised (a document's line breaks are layout, not meaning, and a model paying for "\r\n\r\n\r\n" is paying
/// for nothing), and a table becomes a markdown table (the only shape that survives being read back by the same model
/// that has to reason about columns).
/// </summary>
internal static class Blocks
{
    /// <summary>Widest table rendered. Beyond this a row stops being a row and becomes a wall.</summary>
    public const int MaxColumns = 12;

    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var character in text)
        {
            // Every kind of document whitespace collapses to one space: a tab in a .docx is not data, and a run of
            // them is indentation somebody's layout engine produced.
            if (character is ' ' or '\t' or '\r' or '\n' or '\u00A0')
            {
                if (!lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            builder.Append(character);
            lastWasSpace = false;
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The same tidying, except that line breaks survive.
    ///
    /// A PDF page is one string in which the line breaks <b>are</b> the layout: a heading, an address block and a table
    /// row all arrive as lines, and flattening them turns a page into one very long sentence. Three or more breaks
    /// become one, because a page whose leading is generous should not cost the model a dozen newlines.
    /// </summary>
    public static string NormalizeLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        var breaks = 0;
        var trailingSpace = true;
        foreach (var character in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (character == '\n')
            {
                breaks++;
                trailingSpace = true;
                continue;
            }
            if (character is ' ' or '\t' or '\u00A0')
            {
                if (!trailingSpace) builder.Append(' ');
                trailingSpace = true;
                continue;
            }
            if (breaks > 0)
            {
                builder.Append('\n');
                if (breaks > 1) builder.Append('\n');
                breaks = 0;
            }
            builder.Append(character);
            trailingSpace = false;
        }
        return builder.ToString().Trim();
    }

    public static string RenderTable(Table table)
    {
        var rows = new List<List<string>>();
        foreach (var row in table.Elements<TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<TableCell>())
                // A pipe inside a cell would end the column early, which turns a two-column table into nonsense.
                cells.Add(Clean(cell.InnerText).Replace("|", "\\|", StringComparison.Ordinal));
            if (cells.Count > 0) rows.Add(cells);
        }
        return RenderGrid(rows);
    }

    /// <summary>
    /// Rows in, a markdown table out. Shared by the readers so that a spreadsheet and a Word table look the same to
    /// whoever reads them — one shape to learn, one shape to test.
    /// </summary>
    public static string RenderGrid(IReadOnlyList<IReadOnlyList<string>> rows, int maxColumns = MaxColumns)
    {
        if (rows.Count == 0) return string.Empty;

        var width = Math.Min(maxColumns, rows.Max(row => row.Count));
        if (width == 0) return string.Empty;
        var narrowed = rows.Any(row => row.Count > width);

        var builder = new StringBuilder();
        for (var index = 0; index < rows.Count; index++)
        {
            var cells = rows[index];
            builder.Append('|');
            for (var column = 0; column < width; column++)
                builder.Append(' ').Append(column < cells.Count ? cells[column] : string.Empty).Append(" |");
            builder.Append('\n');
            if (index == 0)
            {
                // The header rule: markdown needs it, and its absence is why a pasted table reads as prose.
                builder.Append('|');
                for (var column = 0; column < width; column++) builder.Append(" --- |");
                builder.Append('\n');
            }
        }
        if (narrowed)
            builder.Append($"（原表有超过 {width} 列，这里只渲染了前 {width} 列。）\n");
        return builder.ToString().TrimEnd('\n');
    }
}
