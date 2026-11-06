using System.Text.Json;

namespace ALN.Contracts;

/// <summary>
/// Reading an action's arguments, once.
///
/// Every action takes untrusted JSON and has to pick a name, an address or a word out of it. That reading used to be
/// copied into each module that grew a verb, and a copy is where "must be an instance address" quietly becomes "must
/// be a non-empty string" in one place and stays strict in another. Nothing here knows what any argument means: it only
/// refuses what is not the shape the caller asked for.
/// </summary>
public static class ActionArgs
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>An action's answer: one JSON object, in the spelling every client already reads.</summary>
    public static JsonElement JsonOf(object value) => JsonSerializer.SerializeToElement(value, Json);

    public static bool TryReadText(this JsonElement arguments, string property, out string value)
    {
        value = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(property, out var found) ||
            found.ValueKind != JsonValueKind.String) return false;
        value = found.GetString() ?? string.Empty;
        return true;
    }

    public static Result<Address> ReadAddress(this JsonElement arguments, string property)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String || !Address.TryParse(value.GetString(), out var address) ||
            !address.IsInstance)
            return Result<Address>.Failure(new(ErrorCode.Invalid, $"Argument '{property}' must be an instance address."));
        return Result<Address>.Success(address);
    }

    /// <summary>
    /// The address an instance is created at: the type plus the name the caller gave, never a string the caller spelled.
    /// A caller that can say "adapter.file#x" while naming type "adapter.qq" is a caller that can make the two disagree
    /// before the spec is even stored.
    /// </summary>
    public static Result<Address> BuildAddress(string typeId, string instance)
    {
        if (typeId.Contains('#') || instance.Contains('#') || instance.Any(char.IsWhiteSpace))
            return Result<Address>.Failure(new(ErrorCode.Invalid, $"Not a usable instance name: {instance}"));
        if (!Address.TryParse($"{typeId}#{instance}", out var address) || !address.IsInstance)
            return Result<Address>.Failure(new(ErrorCode.Invalid, $"Not an instance address: {typeId}#{instance}"));
        return Result<Address>.Success(address);
    }

    /// <summary>
    /// Settings as a flat string map. Strict on purpose: an object value here would be stored as "[object Object]" by
    /// whoever reads it next, and a number that silently became "1.0" is a config bug nobody can see in the file.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ReadTextMap(this JsonElement arguments, string property,
        out Error? error)
    {
        error = null;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(property, out var settings) ||
            settings.ValueKind == JsonValueKind.Null) return null;
        if (settings.ValueKind != JsonValueKind.Object)
        {
            error = new(ErrorCode.Invalid, $"'{property}' must be an object of string values.");
            return null;
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in settings.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.String)
            {
                error = new(ErrorCode.Invalid, $"'{property}.{item.Name}' must be a string.");
                return null;
            }
            map[item.Name] = item.Value.GetString() ?? "";
        }
        return map;
    }
}
