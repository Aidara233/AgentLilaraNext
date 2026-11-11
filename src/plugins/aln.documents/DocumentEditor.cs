using ALN.Contracts;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Aln.Documents;

/// <summary>A cell to rewrite: which sheet, which reference, what text.</summary>
public sealed record CellEdit(string Sheet, string Reference, string Text);

/// <summary>A paragraph to rewrite: its 1-based position among the document's non-empty paragraphs.</summary>
public sealed record ParagraphEdit(int Index, string Text);

/// <summary>A shape's text to rewrite: which slide, which shape by the name its author gave it.</summary>
public sealed record SlideEdit(int Slide, string Shape, string Text);

/// <summary>
/// What an edit did, and the sentences a count cannot carry — "this list became one line", "the formula's cached value
/// is now stale". A number alone would be a report that is true and incomplete, which is the kind this project keeps
/// finding at the bottom of a bug.
/// </summary>
public sealed record EditOutcome(int Applied, IReadOnlyList<string> Notes);

/// <summary>
/// Changing a document that already exists, which is a different job from writing a new one.
///
/// <b>Every edit here is narrow on purpose, and narrow means "refuses more than it can do".</b> A paragraph holding a
/// field, a cross-reference, a comment anchor or a hyperlink is refused rather than rewritten: those are places where
/// the text and something else have to agree, and a rewrite that changes one without the other produces a document that
/// opens fine and is wrong. A refusal costs one round trip; a broken cross-reference in a contract costs whatever the
/// contract was for.
///
/// <b>Formulas are never recalculated.</b> Writing over a cell drops its formula and its cached value together; writing
/// a formula's result ourselves would mean shipping a formula engine that disagrees with Excel. Either way the outcome
/// says so, every time, because the stale number and the missing one fail differently and the caller has to know which
/// it got.
///
/// <b>Everything is read-modify-write.</b> The caller keeps the original bytes as a <c>.bak</c>, the edit happens on a
/// copy, and the copy replaces the file in one write — so a failure in the middle leaves the original exactly as it was.
/// </summary>
public static class DocumentEditor
{
    public static Result<EditOutcome> EditDocx(Stream input, Stream output, IReadOnlyList<ParagraphEdit> edits)
    {
        try
        {
            using var document = WordprocessingDocument.Open(input, isEditable: true);
            if (document.MainDocumentPart?.Document is not { Body: { } body } mainDocument)
                return Result<EditOutcome>.Failure(new(ErrorCode.Invalid, "这个 .docx 里没有正文部分。"));

            // The same filtering the reader uses, or "paragraph 3" would mean two different things to the two halves.
            var paragraphs = body.ChildElements.OfType<W.Paragraph>()
                .Where(paragraph => Blocks.Clean(paragraph.InnerText).Length > 0).ToList();

            var notes = new List<string>();
            var applied = 0;
            foreach (var edit in edits)
            {
                if (edit.Index < 1 || edit.Index > paragraphs.Count)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid,
                        $"段落 {edit.Index} 不存在：这个文档有 {paragraphs.Count} 个非空段落（从 1 数）。"));

                var paragraph = paragraphs[edit.Index - 1];
                if (FieldLike(paragraph) is { } why)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Denied,
                        $"第 {edit.Index} 段里有{why}，改它的文字会和它对应的东西对不上。本框架不替你改这种段落——"
                        + "要改，请在 Word 里改，或者先把这一段拆成普通文字。"));

                var texts = paragraph.Descendants<W.Text>().ToList();
                if (texts.Count == 0)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid,
                        $"第 {edit.Index} 段没有可以直接改的文字节点。"));

                // One text node keeps the paragraph's own formatting; the rest are dropped so that the new text is the
                // whole paragraph rather than its first fragment followed by the old remainder.
                if (texts.Count > 1)
                    notes.Add($"第 {edit.Index} 段原本有 {texts.Count} 个文字片段，已合成一段（第一个片段的格式留下）。");
                texts[0].Text = edit.Text;
                texts[0].Space = SpaceProcessingModeValues.Preserve;
                for (var index = 1; index < texts.Count; index++) texts[index].Remove();
                applied++;
            }

            mainDocument.Save();
            if (document.Clone(output) is not { } clone)
                return Result<EditOutcome>.Failure(new(ErrorCode.Internal, "写回失败：文档包没有复制出来。"));
            // Disposed here on purpose: the clone writes into the stream as it is disposed, and the caller is about to
            // hand those bytes to the port. Returning before that would save half a file.
            using (clone) { }
            return Result<EditOutcome>.Success(new EditOutcome(applied, notes));
        }
        catch (Exception ex) when (ex is IOException or OpenXmlPackageException or InvalidOperationException)
        {
            return Result<EditOutcome>.Failure(new(ErrorCode.Internal, $"改 .docx 失败：{ex.Message}", "docx edit"));
        }
    }

    /// <summary>Why this paragraph is not safe to rewrite, or null when it is.</summary>
    private static string? FieldLike(W.Paragraph paragraph)
    {
        if (paragraph.Descendants<W.FieldCode>().Any() || paragraph.Descendants<W.SimpleField>().Any())
            return "域（field）";
        if (paragraph.Descendants<W.CommentReference>().Any() || paragraph.Descendants<W.CommentRangeStart>().Any())
            return "批注";
        if (paragraph.Descendants<W.BookmarkStart>().Any()) return "书签";
        if (paragraph.Descendants<W.Hyperlink>().Any()) return "超链接";
        return null;
    }

    public static Result<EditOutcome> EditXlsx(Stream input, Stream output, IReadOnlyList<CellEdit> edits)
    {
        try
        {
            using var document = SpreadsheetDocument.Open(input, isEditable: true);
            var workbookPart = document.WorkbookPart;
            var sheets = workbookPart?.Workbook?.Sheets?.Elements<S.Sheet>().ToList();
            if (workbookPart is null || sheets is null)
                return Result<EditOutcome>.Failure(new(ErrorCode.Invalid, "这个 .xlsx 里没有工作簿部分。"));

            var notes = new List<string>();
            var applied = 0;
            foreach (var edit in edits)
            {
                var sheet = sheets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name?.Value, edit.Sheet, StringComparison.Ordinal));
                if (sheet is null)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid,
                        $"没有叫「{edit.Sheet}」的工作表。有：{string.Join("、", sheets.Select(s => s.Name?.Value))}。"));
                if (sheet.Id?.Value is not { Length: > 0 } id || workbookPart.GetPartById(id) is not WorksheetPart part)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid, $"工作表「{edit.Sheet}」的部件不在包里。"));

                if (Parse(edit.Reference, out var rowIndex, out var columnIndex) is { } badReference)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid, badReference));

                var worksheet = part.Worksheet;
                if (worksheet is null)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid, $"工作表「{edit.Sheet}」里没有表格部分。"));

                var data = worksheet.GetFirstChild<S.SheetData>();
                if (data is null)
                {
                    data = new S.SheetData();
                    worksheet.AppendChild(data);
                }

                var cell = Locate(data, rowIndex, columnIndex);
                var hadFormula = cell.CellFormula is not null;
                // The formula and its cached value go together: leaving the old cache under a new formula would be a
                // worse lie than leaving no value at all.
                cell.RemoveAllChildren<S.CellFormula>();
                cell.RemoveAllChildren<S.CellValue>();
                cell.RemoveAllChildren<S.InlineString>();
                if (LooksNumeric(edit.Text))
                {
                    cell.DataType = null;
                    cell.CellValue = new S.CellValue(edit.Text.Trim());
                }
                else
                {
                    cell.DataType = S.CellValues.InlineString;
                    cell.InlineString = new S.InlineString(new S.Text(edit.Text) { Space = SpaceProcessingModeValues.Preserve });
                }
                if (hadFormula)
                    notes.Add($"{edit.Sheet}!{edit.Reference} 原来是个公式，公式和它的缓存值都已去掉。");
                applied++;
            }

            foreach (var candidate in sheets)
                if (candidate.Id?.Value is { Length: > 0 } sheetId &&
                    workbookPart.GetPartById(sheetId) is WorksheetPart { Worksheet: { } sheetXml })
                    sheetXml.Save();

            workbookPart.Workbook?.Save();
            if (document.Clone(output) is not { } clone)
                return Result<EditOutcome>.Failure(new(ErrorCode.Internal, "写回失败：文档包没有复制出来。"));
            // Disposed here on purpose: the clone writes into the stream as it is disposed, and the caller is about to
            // hand those bytes to the port. Returning before that would save half a file.
            using (clone) { }
            return Result<EditOutcome>.Success(new EditOutcome(applied, notes));
        }
        catch (Exception ex) when (ex is IOException or OpenXmlPackageException or InvalidOperationException)
        {
            return Result<EditOutcome>.Failure(new(ErrorCode.Internal, $"改 .xlsx 失败：{ex.Message}", "xlsx edit"));
        }
    }

    /// <summary>"B2" to (2, 2). A reference that is not one is a refusal that says what was wrong with it.</summary>
    private static string? Parse(string reference, out uint row, out int column)
    {
        row = 0;
        column = 0;
        var text = reference.Trim().Replace("$", string.Empty, StringComparison.Ordinal);
        var split = 0;
        while (split < text.Length && char.IsLetter(text[split])) split++;
        if (split == 0 || split == text.Length)
            return $"「{reference}」不是单元格引用：要像 B2 或 AA17 这样。";

        for (var index = 0; index < split; index++)
            column = column * 26 + (char.ToUpperInvariant(text[index]) - 'A' + 1);
        if (!uint.TryParse(text[split..], out row) || row == 0)
            return $"「{reference}」没有有效的行号：要像 B2 或 AA17 这样。";
        return null;
    }

    private static S.Cell Locate(S.SheetData data, uint rowIndex, int columnIndex)
    {
        var row = data.Elements<S.Row>().FirstOrDefault(candidate => candidate.RowIndex?.Value == rowIndex);
        if (row is null)
        {
            row = new S.Row { RowIndex = rowIndex };
            // Rows have to stay in sheet order: Excel reads them positionally, and a row numbered 2 that sits after row
            // 40 in the file is a file it reports as damaged.
            var after = data.Elements<S.Row>().LastOrDefault(candidate => (candidate.RowIndex?.Value ?? 0) < rowIndex);
            if (after is not null) data.InsertAfter(row, after);
            else data.PrependChild(row);
        }

        var reference = ColumnName(columnIndex) + rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var cell = row.Elements<S.Cell>().FirstOrDefault(candidate =>
            string.Equals(candidate.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));
        if (cell is null)
        {
            cell = new S.Cell { CellReference = reference };
            var after = row.Elements<S.Cell>().LastOrDefault(candidate => Column(candidate) < columnIndex);
            if (after is not null) row.InsertAfter(cell, after);
            else row.PrependChild(cell);
        }
        return cell;
    }

    private static int Column(S.Cell candidate)
    {
        var column = 0;
        foreach (var character in candidate.CellReference?.Value ?? string.Empty)
        {
            if (!char.IsLetter(character)) break;
            column = column * 26 + (char.ToUpperInvariant(character) - 'A' + 1);
        }
        return column;
    }

    private static string ColumnName(int column)
    {
        var builder = new System.Text.StringBuilder(3);
        while (column > 0)
        {
            var remainder = (column - 1) % 26;
            builder.Insert(0, (char)('A' + remainder));
            column = (column - 1) / 26;
        }
        return builder.ToString();
    }

    private static bool LooksNumeric(string text)
    {
        var span = text.Trim();
        if (span.Length == 0 || span.Length > 20) return false;
        if (span.Length > 1 && (span[0] == '0' || span[0] == '+')) return false;
        return decimal.TryParse(span, System.Globalization.NumberStyles.AllowLeadingSign |
                                       System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    public static Result<EditOutcome> EditPptx(Stream input, Stream output, IReadOnlyList<SlideEdit> edits)
    {
        try
        {
            using var document = PresentationDocument.Open(input, isEditable: true);
            var presentationPart = document.PresentationPart;
            var ids = presentationPart?.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList();
            if (presentationPart is null || ids is null)
                return Result<EditOutcome>.Failure(new(ErrorCode.Invalid, "这个 .pptx 里没有演示文稿部分。"));

            var notes = new List<string>();
            var applied = 0;
            foreach (var edit in edits)
            {
                if (edit.Slide < 1 || edit.Slide > ids.Count)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid,
                        $"第 {edit.Slide} 页不存在：这个文档有 {ids.Count} 页。"));

                if (ids[edit.Slide - 1].RelationshipId?.Value is not { Length: > 0 } relationship ||
                    presentationPart.GetPartById(relationship) is not SlidePart slidePart)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid, $"第 {edit.Slide} 页的部件不在包里。"));

                var slide = slidePart.Slide;
                if (slide is null)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid, $"第 {edit.Slide} 页的内容不在包里。"));

                var shapes = slide.Descendants<P.Shape>().ToList();
                var shape = shapes.FirstOrDefault(candidate => string.Equals(
                    candidate.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value,
                    edit.Shape, StringComparison.Ordinal));
                if (shape is null)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid,
                        $"第 {edit.Slide} 页没有叫「{edit.Shape}」的形状。这一页有："
                        + $"{string.Join("、", shapes.Select(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value))}。"));
                if (shape.TextBody is null)
                    return Result<EditOutcome>.Failure(new(ErrorCode.Invalid,
                        $"第 {edit.Slide} 页的「{edit.Shape}」没有文本框：它是图片或图形，改不了文字。"));

                // The paragraphs' own properties go with them; the body's survive, so the new text lands in the same
                // place with the same alignment. Keeping a bullet list's paragraphs would mean deciding which of them
                // the new text replaces, and there is no way to ask — so a list becomes one line, and it says so.
                var paragraphs = shape.TextBody.Elements<A.Paragraph>().Count();
                shape.TextBody.RemoveAllChildren<A.Paragraph>();
                shape.TextBody.AppendChild(new A.Paragraph(new A.Run(new A.Text(edit.Text))));
                if (paragraphs > 1)
                    notes.Add($"第 {edit.Slide} 页「{edit.Shape}」原本有 {paragraphs} 段，现在合成了一段。");
                applied++;
            }

            presentationPart.Presentation?.Save();
            if (document.Clone(output) is not { } clone)
                return Result<EditOutcome>.Failure(new(ErrorCode.Internal, "写回失败：文档包没有复制出来。"));
            // Disposed here on purpose: the clone writes into the stream as it is disposed, and the caller is about to
            // hand those bytes to the port. Returning before that would save half a file.
            using (clone) { }
            return Result<EditOutcome>.Success(new EditOutcome(applied, notes));
        }
        catch (Exception ex) when (ex is IOException or OpenXmlPackageException or InvalidOperationException)
        {
            return Result<EditOutcome>.Failure(new(ErrorCode.Internal, $"改 .pptx 失败：{ex.Message}", "pptx edit"));
        }
    }
}
