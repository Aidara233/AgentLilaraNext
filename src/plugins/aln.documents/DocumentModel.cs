namespace Aln.Documents;

/// <summary>
/// What one piece of a document is. The four kinds are not decoration: they are what a range in <c>doc_read</c>
/// means, and they are the only thing a model can point at when it wants "the third sheet" instead of "somewhere in
/// the middle of the text".
/// </summary>
public enum SectionKind
{
    /// <summary>A paragraph of prose, a heading, or a list item — in a .docx.</summary>
    Paragraph,

    /// <summary>A table, already rendered as markdown, from a .docx.</summary>
    Table,

    /// <summary>One worksheet, rendered as a markdown table, from an .xlsx.</summary>
    Sheet,

    /// <summary>One slide's text, from a .pptx.</summary>
    Slide,

    /// <summary>One page of text, from a .pdf.</summary>
    Page
}

/// <summary>
/// One addressable piece: <b>what kind, which one, what it is called, and its text</b>.
///
/// <paramref name="Index"/> is 1-based within its kind and is the coordinate <c>doc_read</c> takes, because "paragraph
/// 40 to 60" is a request a model can make after seeing <c>doc_tree</c>, and "the fortieth thing in the file" is not —
/// a table costs one index, not forty.
///
/// <paramref name="Label"/> is for a human reading a listing: a heading's level, a sheet's used range, a slide number.
/// It is never parsed by this plugin; it exists so that a listing explains itself without the text.
/// </summary>
public sealed record DocumentSection(SectionKind Kind, int Index, string Label, string Text);

/// <summary>
/// A document read as far as the budget allowed.
///
/// <paramref name="Truncated"/> is the one field a caller must not ignore: it means the text here is a prefix of the
/// document, and the honest sentence to a model is "there is more, ask for a range" rather than a file that appears to
/// stop mid-sentence for no reason (<c>docs/24 §7</c> has the same rule for <c>fs_read</c>).
/// </summary>
public sealed record ParsedDocument(string Format, IReadOnlyList<DocumentSection> Sections, bool Truncated)
{
    public int Characters => Sections.Sum(section => section.Text.Length);

    public int Count(SectionKind kind) => Sections.Count(section => section.Kind == kind);
}

/// <summary>What the bytes are, decided by the header — never by the name, which is a claim by whoever wrote the file.</summary>
public static class DocumentFormats
{
    public const string Word = "docx";
    public const string Excel = "xlsx";
    public const string PowerPoint = "pptx";
    public const string Pdf = "pdf";

    /// <summary>The format this header claims, or null when it claims nothing this build can read.</summary>
    public static string? Detect(ReadOnlySpan<byte> head) =>
        head.Length >= 5 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46 && head[4] == 0x2D
            ? Pdf
            : head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && (head[2] == 0x03 || head[2] == 0x05)
                ? "ooxml"
                : null;

    /// <summary>Which of the three OOXML packages a zip is, by the one part each of them must have.</summary>
    public static string? OoxmlKind(IReadOnlyCollection<string> entries) =>
        entries.Any(entry => entry.StartsWith("word/", StringComparison.Ordinal)) ? Word
        : entries.Any(entry => entry.StartsWith("xl/", StringComparison.Ordinal)) ? Excel
        : entries.Any(entry => entry.StartsWith("ppt/", StringComparison.Ordinal)) ? PowerPoint
        : null;
}
