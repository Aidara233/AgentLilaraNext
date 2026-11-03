namespace ALN.Contracts;

/// <summary>
/// The name of one module instance.
///
/// It is a name, not a description: nothing parses it, splits it, or reads a type, a parent or a category out
/// of it. Renaming an instance to any other legal value changes nothing except the references that pointed at
/// it. Everything a caller might want to know about the thing — what type it is, which category it belongs to,
/// who mounted what on it — is answered by the catalog, keyed by this value.
///
/// The shape it usually has ("Adapter.QQ#ABCDEF12") exists so that a person reading a log can tell roughly what
/// they are looking at. That is the whole of its job.
/// </summary>
/// <remarks>
/// Still validated, because it doubles as a directory name: <c>[A-Za-z0-9._#-]</c>, 1..96 characters, not
/// <c>.</c> or <c>..</c>, not a Windows reserved device name. Invalid input is <b>refused</b>, never rewritten —
/// silently turning "a/b" into "a_b" would let two different names collide on disk.
/// Uniqueness is case-insensitive because the file systems are; equality of the value itself stays ordinal.
/// </remarks>
public readonly record struct ModuleId(string Value) : IComparable<ModuleId>
{
    private static readonly string[] Reserved =
    [
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    ];

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    /// <summary>
    /// Builds the readable form once, at creation, from the owner's own type and a fresh suffix. Generation is
    /// allowed; reading the pieces back out is not.
    /// </summary>
    public static ModuleId Create(string typeId, string? suffix = null) =>
        new(string.IsNullOrWhiteSpace(suffix) ? typeId : $"{typeId}#{suffix}");

    /// <summary>A short fresh suffix. Uniqueness is still the catalog's job — this only has to be unlikely.</summary>
    public static string NewSuffix() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public static bool IsLegal(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 96 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '#' or '-' or '_') &&
        value is not ("." or "..") &&
        !Reserved.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Validation only: "is this a usable name", never "what does this name mean". Parsing is deliberately
    /// absent — the old three-part address type is gone precisely so that nobody can write it again.
    /// </summary>
    public static bool TryParse(string? text, out ModuleId id)
    {
        id = default;
        if (!IsLegal(text)) return false;
        id = new ModuleId(text!);
        return true;
    }

    public static ModuleId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new ArgumentException($"Not a legal module id: {text}", nameof(text));

    public int CompareTo(ModuleId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;
}
