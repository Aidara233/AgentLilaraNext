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
///
/// Deliberately <b>not</b> a record: a record struct allows <c>id with { Value = … }</c>, which is exactly the
/// rename this type exists to make impossible. An instance's id is fixed at creation; there is no member here
/// that produces the same id with a different value.
/// </remarks>
public readonly struct ModuleId : IEquatable<ModuleId>, IComparable<ModuleId>
{
    private static readonly string[] Reserved =
    [
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    ];

    public ModuleId(string value) => Value = value;

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    /// <summary>
    /// The default id of a new instance: the type's own prefix, a separator, and a fresh suffix. Readable for a
    /// human, meaningless to the code — nothing here may be parsed back out.
    /// </summary>
    public static ModuleId NewFor(string typeId) => Create(typeId, NewSuffix());

    /// <summary>
    /// Builds the readable form once, at creation, from the owner's own type and a suffix the caller chose. An
    /// instance may be named by hand instead (a person typing "qq" in a config file is not a parsing problem), so
    /// the suffix is whatever the caller asked for — it is validated as a name, never interpreted.
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
    /// Validation only: "is this a usable name", never "what does this name mean". There is deliberately
    /// nothing here that reads a type, a parent or an instance back out of the value.
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

    public bool Equals(ModuleId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ModuleId other && Equals(other);

    public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(ModuleId other) => string.CompareOrdinal(Value, other.Value);

    public static bool operator ==(ModuleId left, ModuleId right) => left.Equals(right);

    public static bool operator !=(ModuleId left, ModuleId right) => !left.Equals(right);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// A module id is a name on the wire too: one string, never an object with a <c>value</c> inside it. Whoever edits a
/// <c>module.json</c> by hand writes <c>"id": "adapters"</c>, so that is what gets written.
/// </summary>
public sealed class ModuleIdJsonConverter : System.Text.Json.Serialization.JsonConverter<ModuleId>
{
    public override ModuleId Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType == System.Text.Json.JsonTokenType.String && ModuleId.TryParse(reader.GetString(), out var id)
            ? id
            : throw new System.Text.Json.JsonException("A module id must be a legal string.");

    public override void Write(System.Text.Json.Utf8JsonWriter writer, ModuleId value,
        System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}
