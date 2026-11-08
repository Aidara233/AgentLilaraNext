using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ALN.Adapter.Qq;

/// <summary>One inbound message, after protocol-level cleaning and before Channel-level meaning.</summary>
public sealed record OneBotInbound(string ConversationId, string Platform, string? MessageId, string SenderId,
    string? SenderName, string Text, DateTimeOffset At, bool MentionsSelf = false);

/// <summary>The part of an action response that matters: did it work, and what id did the platform assign.</summary>
public sealed record OneBotResponse(bool Ok, string? MessageId, long RetCode, string? Message);

/// <summary>Who the listener is logged in as, learned from the platform rather than configured.</summary>
public sealed record OneBotLogin(long UserId, string? Nickname);

/// <summary>One row of the account's friend list: who they are, and what this account calls them.</summary>
/// <param name="Nickname">The name the person chose for themselves.</param>
/// <param name="Remark">The name this account gave them. Belongs to us, not to them, and is often the only name
/// that means anything to whoever reads the transcript.</param>
public sealed record OneBotFriend(long UserId, string? Nickname, string? Remark);

/// <summary>
/// OneBot v11 wire formats, as pure functions. Everything here is testable without a socket, which is
/// the point: the socket is thin, the mapping rules are where the bugs live (self echoes, CQ codes,
/// conversation ids).
///
/// Scope: group and private text. Images, mentions on the way out and other rich segments are not
/// implemented; unsupported segments become visible placeholders rather than silently disappearing,
/// so a model reading the text knows something is there that it cannot see.
/// </summary>
public static partial class OneBotProtocol
{
    public const string PlatformId = "qq";
    public const string GroupPlatform = "group";
    public const string PrivatePlatform = "private";

    /// <summary>
    /// Conversation ids must fit the Channel rule ([A-Za-z0-9_-], ≤128 chars), and QQ group numbers are
    /// pure digits that can equal a QQ number. The prefix keeps the two namespaces apart — a group and
    /// a person with the same number are different conversations.
    /// </summary>
    public const string GroupPrefix = "group-";
    public const string PrivatePrefix = "user-";

    public static string GroupConversation(long groupId) => $"{GroupPrefix}{Number(groupId)}";
    public static string PrivateConversation(long userId) => $"{PrivatePrefix}{Number(userId)}";

    /// <summary>Parses a conversation id back into (platform, id). Anything else is not ours to send to.</summary>
    public static bool TryReadConversation(string? conversationId, out long id, out string platform)
    {
        id = 0;
        platform = string.Empty;
        if (conversationId is null) return false;
        if (conversationId.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            platform = GroupPlatform;
            return TryParseNumber(conversationId.AsSpan(GroupPrefix.Length), out id);
        }
        if (conversationId.StartsWith(PrivatePrefix, StringComparison.Ordinal))
        {
            platform = PrivatePlatform;
            return TryParseNumber(conversationId.AsSpan(PrivatePrefix.Length), out id);
        }
        return false;
    }

    /// <summary>
    /// Reads one pushed message, or returns null when the event must be ignored: a meta event, a notice,
    /// a message from a group the bot is not in the scope of, the bot's own message echoed back, or a
    /// message with nothing in it. Ignoring is normal here, so it is not an error path.
    /// </summary>
    public static OneBotInbound? ReadMessage(JsonElement root, long? selfId)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!TryString(root, "post_type", out var postType)) return null;

        // The bot's own sends come back as message_sent. Feeding them in would make every reply of ours
        // look like user input and start a self-feeding loop.
        if (postType == "message_sent") return null;
        if (postType != "message") return null;
        if (!TryString(root, "message_type", out var messageType)) return null;

        // Conversation id encodes the platform object, so a group and a person with the same number are
        // different conversations rather than the same one.
        var conversationId = messageType switch
        {
            "group" when TryLong(root, "group_id", out var groupId) => GroupConversation(groupId),
            "private" when ReadSenderId(root) is { } privateUser => PrivateConversation(privateUser),
            // "guild" and anything newer: not in scope, and guessing would be worse than ignoring.
            _ => null
        };
        if (conversationId is null) return null;

        var senderId = ReadSenderId(root);
        if (senderId is null) return null;
        if (selfId is { } me && senderId == me) return null;

        var messageId = TryLong(root, "message_id", out var numericId)
            ? Number(numericId)
            : TryString(root, "message_id", out var textId) ? textId : null;

        var text = ReadText(root);
        if (text.Length == 0) return null;

        var at = TryLong(root, "time", out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : DateTimeOffset.UtcNow;

        return new OneBotInbound(conversationId, messageType, messageId, Number(senderId.Value),
            ReadSenderName(root), text, at, Mentions(root, selfId));
    }

    /// <summary>
    /// Whether this message addressed the bot by name. The adapter is the only layer that knows our own
    /// account id, so it answers the question once and puts the answer on the envelope: a script that
    /// compared "@nickname" against a stored nickname would break the first time somebody renamed the
    /// bot, and "@全体成员" is not a mention of us.
    /// </summary>
    private static bool Mentions(JsonElement root, long? selfId)
    {
        if (selfId is not { } me) return false;
        var meText = Number(me);
        if (root.TryGetProperty("message", out var message))
        {
            if (message.ValueKind == JsonValueKind.Array) return MentionsSegments(message, meText);
            if (message.ValueKind == JsonValueKind.String) return MentionsCq(HtmlDecode(message.GetString() ?? string.Empty), meText);
        }
        return TryString(root, "raw_message", out var raw) && MentionsCq(HtmlDecode(raw), meText);
    }

    private static bool MentionsSegments(JsonElement segments, string selfId)
    {
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object) continue;
            if (!TryString(segment, "type", out var type) || type != "at") continue;
            if (!segment.TryGetProperty("data", out var data)) continue;
            if (TryString(data, "qq", out var qq) && qq == selfId) return true;
        }
        return false;
    }

    private static bool MentionsCq(string text, string selfId) =>
        text.Length > 0 && CqCode().Matches(text).Any(match =>
            match.Groups["type"].Value == "at" && CqArgument(match.Groups["args"].Value, "qq") == selfId);

    /// <summary>The account the listener is logged in as, from a heartbeat or lifecycle meta event.</summary>
    public static long? ReadSelfId(JsonElement root) =>
        TryLong(root, "self_id", out var selfId) ? selfId : null;

    /// <summary>
    /// <c>send_group_msg</c> or <c>send_private_msg</c>, chosen by the conversation id. The text goes as a
    /// text segment rather than a CQ-code string so that a message containing "[CQ:at,qq=all]" is sent as
    /// literal text and not as an instruction.
    /// </summary>
    public static string BuildSend(string echo, long targetId, string platform, string text, string? replyTo = null)
    {
        var segments = BuildOutboundSegments(text, replyTo);
        return platform == PrivatePlatform
            ? JsonSerializer.Serialize(new { action = "send_private_msg", @params = new { user_id = targetId, message = segments, auto_escape = false }, echo }, Json)
            : JsonSerializer.Serialize(new { action = "send_group_msg", @params = new { group_id = targetId, message = segments, auto_escape = false }, echo }, Json);
    }

    /// <summary>
    /// Any action at all, passed through: the verbs this host never wrapped. Nothing here decides what is allowed — that
    /// is the caller's instance configuration (<c>rawActions</c>) — and nothing translates, which is why the answer is
    /// handed back as the platform wrote it.
    /// </summary>
    public static string BuildRaw(string echo, string action, JsonElement parameters) =>
        JsonSerializer.Serialize(new { action, @params = parameters, echo }, Json);

    /// <summary><c>get_login_info</c>: how the account id is learned instead of typed in by hand.</summary>
    public static string BuildLoginInfo(string echo) =>
        JsonSerializer.Serialize(new { action = "get_login_info", @params = new { }, echo }, Json);

    /// <summary>
    /// <c>get_friend_list</c>: the one call that carries remarks. Remarks are a property of the account, not of a
    /// place, so one answer covers every conversation — which is why it is worth fetching once and keeping.
    /// </summary>
    public static string BuildFriendList(string echo) =>
        JsonSerializer.Serialize(new { action = "get_friend_list", @params = new { }, echo }, Json);

    /// <summary>Parses the <c>data</c> array of a <c>get_friend_list</c> answer. Rows without a usable id are dropped.</summary>
    public static IReadOnlyList<OneBotFriend> ReadFriendList(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) return [];
        var friends = new List<OneBotFriend>(data.GetArrayLength());
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!TryLong(entry, "user_id", out var userId) || userId <= 0) continue;
            friends.Add(new OneBotFriend(userId,
                Blank(TryString(entry, "nickname", out var nickname) ? nickname : null),
                Blank(TryString(entry, "remark", out var remark) ? remark : null)));
        }
        return friends;
    }

    /// <summary>
    /// <c>get_group_member_info</c>: the display name inside one group. It has to be asked per group because the card
    /// is per group — a person can be "组长" in one and their own nickname in another, and both are correct.
    /// </summary>
    public static string BuildGroupMemberInfo(string echo, long groupId, long userId) =>
        JsonSerializer.Serialize(new
        {
            action = "get_group_member_info",
            @params = new { group_id = groupId, user_id = userId, no_cache = false },
            echo
        }, Json);

    /// <summary><c>get_stranger_info</c>: the name to use where there is no group context to ask in.</summary>
    public static string BuildStrangerInfo(string echo, long userId) =>
        JsonSerializer.Serialize(new
        {
            action = "get_stranger_info",
            @params = new { user_id = userId, no_cache = false },
            echo
        }, Json);

    /// <summary>Parses the <c>data</c> object of a <c>get_group_member_info</c> answer: the card, then the nickname.</summary>
    public static string? ReadMemberName(JsonElement data) => MemberName(data);

    /// <summary>Parses the <c>data</c> object of a <c>get_stranger_info</c> answer.</summary>
    public static string? ReadStrangerName(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && TryString(data, "nickname", out var nickname) ? Blank(nickname) : null;

    /// <summary>
    /// Parses the <c>data</c> object of a <c>get_login_info</c> answer (the caller already unwrapped the
    /// envelope, so this is the account object itself).
    /// </summary>
    public static OneBotLogin? ReadLoginInfo(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!TryLong(data, "user_id", out var userId) || userId <= 0) return null;
        return new OneBotLogin(userId, TryString(data, "nickname", out var nickname) ? nickname : null);
    }

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
                JsonValueKind.Number => id.TryGetInt64(out var numeric) ? Number(numeric) : id.GetRawText(),
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

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryParseNumber(ReadOnlySpan<char> digits, out long value)
    {
        value = 0;
        return digits.Length > 0 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

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
        return MemberName(sender);
    }

    /// <summary>
    /// How a person is displayed in one place: the group card first, the account nickname second. One rule, in one
    /// place, because it is asked about a pushed event's <c>sender</c> object and about a
    /// <c>get_group_member_info</c> answer, which are the same question with different wrappers.
    /// </summary>
    private static string? MemberName(JsonElement person)
    {
        if (person.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "card", "nickname" })
            if (TryString(person, key, out var value) && Blank(value) is { } name) return name;
        return null;
    }

    /// <summary>Empty and whitespace are "the platform did not say", not a name made of spaces.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
