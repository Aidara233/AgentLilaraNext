using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ALN.Adapter.Qq;

/// <summary>One inbound group message, after protocol-level cleaning and before Channel-level meaning.</summary>
public sealed record OneBotInbound(long GroupId, string? MessageId, string SenderId, string? SenderName,
    string Text, DateTimeOffset At);

/// <summary>The part of an action response that matters: did it work, and what id did the platform assign.</summary>
public sealed record OneBotResponse(bool Ok, string? MessageId, long RetCode, string? Message);

/// <summary>
/// OneBot v11 wire formats, as pure functions. Everything here is testable without a socket, which is
/// the point: the socket is thin, the mapping rules are where the bugs live (self echoes, CQ codes,
/// conversation ids).
///
/// Scope is deliberately narrow: group text in, group text out. Private chats, images, mentions on
/// the way out are not implemented; unsupported segments become visible placeholders rather than
/// silently disappearing, so a model reading the text knows something is there that it cannot see.
/// </summary>
public static partial class OneBotProtocol
{
    public const string PlatformId = "qq";

    /// <summary>
    /// Conversation ids must fit the Channel rule ([A-Za-z0-9_-], ≤128 chars), and QQ group numbers are
    /// pure digits that can equal a QQ number. The prefix keeps the two namespaces apart and keeps a
    /// future private-chat conversation from colliding with a group of the same number.
    /// </summary>
    public const string GroupPrefix = "g-";

    public static string GroupConversation(long groupId) => $"{GroupPrefix}{groupId.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryReadGroupConversation(string? conversationId, out long groupId)
    {
        groupId = 0;
        if (conversationId is null || !conversationId.StartsWith(GroupPrefix, StringComparison.Ordinal)) return false;
        var digits = conversationId.AsSpan(GroupPrefix.Length);
        return digits.Length > 0 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out groupId);
    }

    /// <summary>
    /// Reads one pushed event, or returns null when the event must be ignored: a meta event, a private
    /// message (not in scope), the bot's own message echoed back, or a message with nothing in it.
    /// Ignoring is normal here, so it is not an error path.
    /// </summary>
    public static OneBotInbound? ReadMessage(JsonElement root, long? selfId)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!TryString(root, "post_type", out var postType)) return null;

        // The bot's own sends come back as message_sent. Feeding them in would make every reply of ours
        // look like user input and start a self-feeding loop.
        if (postType == "message_sent") return null;
        if (postType != "message") return null;
        if (!TryString(root, "message_type", out var messageType) || messageType != "group") return null;
        if (!TryLong(root, "group_id", out var groupId)) return null;

        var senderId = ReadSenderId(root);
        if (senderId is null) return null;
        if (selfId is { } me && senderId == me) return null;

        var messageId = TryLong(root, "message_id", out var numericId)
            ? numericId.ToString(CultureInfo.InvariantCulture)
            : TryString(root, "message_id", out var textId) ? textId : null;

        var text = ReadText(root);
        if (text.Length == 0) return null;

        var at = TryLong(root, "time", out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : DateTimeOffset.UtcNow;

        return new OneBotInbound(groupId, messageId, senderId.Value.ToString(CultureInfo.InvariantCulture),
            ReadSenderName(root), text, at);
    }

    /// <summary>The account the listener is logged in as, from a heartbeat or lifecycle meta event.</summary>
    public static long? ReadSelfId(JsonElement root) =>
        TryLong(root, "self_id", out var selfId) ? selfId : null;

    /// <summary>
    /// <c>send_group_msg</c>. The text goes as a text segment rather than a CQ-code string so that a
    /// message containing "[CQ:at,qq=all]" is sent as literal text and not as an instruction.
    /// </summary>
    public static string BuildGroupSend(string echo, long groupId, string text, string? replyTo = null) =>
        JsonSerializer.Serialize(new
        {
            action = "send_group_msg",
            @params = new
            {
                group_id = groupId,
                message = BuildOutboundSegments(text, replyTo),
                auto_escape = false
            },
            echo
        }, Json);

    /// <summary>
    /// A reply segment needs a numeric id; anything else is dropped rather than sent as a broken
    /// segment, because a rejected send is worse than a send without quoting.
    /// </summary>
    public static object[] BuildOutboundSegments(string text, string? replyTo)
    {
        var segments = new List<object>(2);
        if (replyTo is { Length: > 0 } id && id.All(char.IsAsciiDigit))
            segments.Add(new { type = "reply", data = new { id } });
        segments.Add(new { type = "text", data = new { text } });
        return segments.ToArray();
    }

    public static OneBotResponse? ReadResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var retCode = TryLong(root, "retcode", out var code) ? code : 0;
        var status = TryString(root, "status", out var declared) ? declared : null;
        var message = TryString(root, "message", out var text) ? text
            : TryString(root, "wording", out var wording) ? wording : null;

        string? messageId = null;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("message_id", out var id))
        {
            messageId = id.ValueKind switch
            {
                JsonValueKind.Number => id.TryGetInt64(out var numeric) ? numeric.ToString(CultureInfo.InvariantCulture) : id.GetRawText(),
                JsonValueKind.String => id.GetString(),
                _ => null
            };
        }

        // "async" means the platform accepted the request and will report later; treat it as accepted.
        var ok = retCode == 0 && (status is null or "ok" or "async");
        return new OneBotResponse(ok, messageId, retCode, message);
    }

    // ---- internals ----

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static long? ReadSenderId(JsonElement root)
    {
        if (TryLong(root, "user_id", out var userId)) return userId;
        if (root.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object &&
            TryLong(sender, "user_id", out var senderId)) return senderId;
        return null;
    }

    private static string? ReadSenderName(JsonElement root)
    {
        if (!root.TryGetProperty("sender", out var sender) || sender.ValueKind != JsonValueKind.Object) return null;
        // Card is the per-group display name; nickname is the account-wide one. Group context wins.
        foreach (var key in new[] { "card", "nickname" })
            if (TryString(sender, key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    private static string ReadText(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message))
        {
            var text = message.ValueKind switch
            {
                JsonValueKind.Array => FlattenSegments(message),
                JsonValueKind.String => FlattenCqCodes(HtmlDecode(message.GetString() ?? string.Empty)),
                _ => string.Empty
            };
            if (text.Trim().Length > 0) return text.Trim();
        }
        // Some implementations send raw_message even when message_format is array; it is the same content.
        return TryString(root, "raw_message", out var raw) ? FlattenCqCodes(HtmlDecode(raw)).Trim() : string.Empty;
    }

    private static string FlattenSegments(JsonElement segments)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object) continue;
            var type = TryString(segment, "type", out var declared) ? declared : string.Empty;
            var data = segment.TryGetProperty("data", out var payload) && payload.ValueKind == JsonValueKind.Object
                ? payload : default;
            builder.Append(Narrate(type, data));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Segment → text. Kept as a table so "what does the model see for an unsupported type" is one
    /// readable answer instead of scattered defaults.
    /// </summary>
    private static string Narrate(string type, JsonElement data)
    {
        switch (type)
        {
            case "text":
                return TryString(data, "text", out var text) ? text : string.Empty;
            case "at":
                if (!TryString(data, "qq", out var qq)) return "@?";
                if (qq == "all") return "@全体成员";
                // Prefer the display name when the client sent one: the model cannot resolve a number.
                return TryString(data, "name", out var name) && name.Length > 0 ? $"@{name}" : $"@{qq}";
            case "image": return "[图片]";
            case "face":
            case "mface": return "[表情]";
            case "record": return "[语音]";
            case "video": return "[视频]";
            case "file": return "[文件]";
            case "json":
            case "xml":
            case "markdown": return "[卡片]";
            case "forward": return "[合并转发]";
            case "share":
            case "location":
            case "contact":
            case "music": return "[分享]";
            case "reply": return string.Empty; // quoting is metadata, not content; PlatformMessageId carries it
            case "dice":
            case "rps": return "[骰子]";
            case "poke": return "[戳一戳]";
            case "": return string.Empty;
            default: return $"[{type}]";
        }
    }

    /// <summary>
    /// CQ-code form: <c>[CQ:at,qq=123]</c>. Only reached when <c>message</c> arrives as a string, which is
    /// what a client with message_format=string sends.
    /// </summary>
    private static string FlattenCqCodes(string text)
    {
        if (text.Length == 0) return text;
        return CqCode().Replace(text, match =>
        {
            var type = match.Groups["type"].Value;
            var args = match.Groups["args"].Value;
            switch (type)
            {
                case "at":
                    var qq = CqArgument(args, "qq");
                    if (qq is null) return "@?";
                    if (qq == "all") return "@全体成员";
                    var name = CqArgument(args, "name");
                    return name is { Length: > 0 } ? $"@{name}" : $"@{qq}";
                case "image": return "[图片]";
                case "face":
                case "mface": return "[表情]";
                case "record": return "[语音]";
                case "video": return "[视频]";
                case "reply": return string.Empty;
                default: return type.Length > 0 ? $"[{type}]" : string.Empty;
            }
        });
    }

    private static string? CqArgument(string args, string key)
    {
        foreach (var part in args.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            if (string.Equals(part[..separator].Trim(), key, StringComparison.Ordinal))
                return part[(separator + 1)..].Trim();
        }
        return null;
    }

    private static string HtmlDecode(string value) => System.Net.WebUtility.HtmlDecode(value);

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var found)) return false;
        switch (found.ValueKind)
        {
            case JsonValueKind.String:
                value = found.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
                value = found.GetRawText();
                return true;
            default:
                return false;
        }
    }

    private static bool TryLong(JsonElement element, string property, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var found)) return false;
        return found.ValueKind switch
        {
            JsonValueKind.Number => found.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(found.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    [GeneratedRegex(@"\[CQ:(?<type>[A-Za-z0-9_]+)(?<args>(?:,[^\]]*)?)\]", RegexOptions.CultureInvariant)]
    private static partial Regex CqCode();
}
