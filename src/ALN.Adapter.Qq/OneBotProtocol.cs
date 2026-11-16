using System.Globalization;
using ALN.Contracts;
using ALN.Contracts.Adapters;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ALN.Adapter.Qq;

/// <summary>One inbound message, after protocol-level cleaning and before Channel-level meaning.</summary>
public sealed record OneBotInbound(string ConversationId, string Platform, string? MessageId, string SenderId,
    string? SenderName, string Text, DateTimeOffset At, bool MentionsSelf = false);

/// <summary>The part of an action response that matters: did it work, and what id did the platform assign.</summary>
public sealed record OneBotResponse(bool Ok, string? MessageId, long RetCode, string? Message);

/// <summary>
/// One picture an inbound message carried: where to get it, and what the platform called the file.
///
/// The bytes are not here. A bridge sends a url (or a bare file name that only <c>get_image</c> can resolve), and
/// fetching it is the Adapter's next step — which is why a message whose picture cannot be fetched still arrives, as
/// the words around it plus <c>[图片]</c> (docs/28 §1).
/// </summary>
public sealed record OneBotImage(string? Url, string? File);

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
        // A file somebody put in the group's file area arrives as a notice, not as a message (docs/16 §12.6).
        if (postType == "notice") return ReadNotice(root);
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
    /// A notice worth a turn: today only <c>group_upload</c>, somebody putting a file in the group's file area.
    ///
    /// It is a notice and not a message on this platform — no <c>message_id</c>, nothing in the chat stream, the chat
    /// shows no trace of it. So the envelope it becomes carries <b>no platform message id</b> (which is why
    /// <see cref="OneBotInbound.MessageId"/> is nullable), and its text is ours: what arrived, how big, and the handle
    /// that fetches it. A model that only ever sees "a file appeared" without a name to point at would go looking for it
    /// with the one tool that cannot find it (docs/28 §6.8).
    /// </summary>
    public static OneBotInbound? ReadNotice(JsonElement root)
    {
        if (!TryString(root, "notice_type", out var notice) || notice != "group_upload") return null;
        if (!TryLong(root, "group_id", out var groupId)) return null;
        if (!TryLong(root, "user_id", out var userId)) return null;
        if (!root.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object) return null;
        var note = FileNote(file);
        if (note is null) return null;
        var at = TryLong(root, "time", out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : DateTimeOffset.UtcNow;
        return new OneBotInbound(GroupConversation(groupId), GroupPlatform, null, Number(userId), null,
            $"（群里有人上传了文件）{note}", at);
    }

    /// <summary>
    /// A file, as the text a model reads: the name, the size, and the handle that fetches it.
    ///
    /// <b>No bytes and no fetch here.</b> On this platform a file in a message is a pointer, and pulling it is a decision
    /// with a destination attached — which belongs to whoever wants the file, not to the adapter (docs/28 §6.7). What
    /// the adapter owes is the handle, spelled the way <c>files_get</c> takes it.
    ///
    /// Null only when there is nothing to point at or to call it: a segment with neither id nor name is `[文件]` and
    /// stays that way.
    /// </summary>
    public static string? FileNote(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        TryString(data, "file_id", out var id);
        // Older shapes put the id in "id", and some put the whole thing in "file" (a path or a name).
        if (string.IsNullOrWhiteSpace(id)) TryString(data, "id", out id);
        TryString(data, "name", out var name);
        if (string.IsNullOrWhiteSpace(name)) TryString(data, "file", out name);

        // Two spellings for the same thing: a message segment says `file_size`, a group_upload notice says `size`.
        long? bytes = TryLong(data, "file_size", out var fileSize) && fileSize > 0 ? fileSize
            : TryLong(data, "size", out var declaredSize) && declaredSize > 0 ? declaredSize
            : null;
        var size = bytes is { } known ? $"（{Measure(known)}）" : string.Empty;
        var label = string.IsNullOrWhiteSpace(name) ? "文件" : name;
        if (string.IsNullOrWhiteSpace(id))
            return name is null ? "[文件]" : $"[文件：{label}{size}；桥接只给了名字，取不到它]";

        var busid = TryLong(data, "busid", out var bus) ? bus.ToString(CultureInfo.InvariantCulture) : "0";
        return $"[文件：{label}{size}，files_get 取它：{FileHandles.Wrap($"{busid}|{id}")}]";

        static string Measure(long value) => value switch
        {
            >= 1024L * 1024 * 1024 => $"{value / (1024.0 * 1024 * 1024):0.#} GB",
            >= 1024 * 1024 => $"{value / (1024.0 * 1024):0.#} MB",
            >= 1024 => $"{value / 1024.0:0.#} KB",
            _ => $"{value} B"
        };
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

    /// <summary>
    /// The pictures in an inbound message, in the order they appeared.
    ///
    /// Both message formats are read, because which one a bridge sends is configuration and the same deployment can
    /// switch: the array form has a segment per picture with <c>data.url</c>/<c>data.file</c>, and the string form carries
    /// <c>[CQ:image,file=…,url=…]</c> with the url percent- or HTML-escaped. Nothing here fetches anything: a text block
    /// is what a model reads when the picture cannot be had, and <c>[图片]</c> is already in the text by the time this
    /// is asked (see <see cref="Narrate"/>).
    /// </summary>
    public static IReadOnlyList<OneBotImage> ReadImages(JsonElement root)
    {
        var images = new List<OneBotImage>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var message))
        {
            if (message.ValueKind == JsonValueKind.Array) ReadImageSegments(message, images);
            else if (message.ValueKind == JsonValueKind.String)
                AppendCqImages(HtmlDecode(message.GetString() ?? string.Empty), images);
            // Anything else — null, an object, a number — says nothing here and falls through to raw_message.
        }
        // The fallback is not only for a bridge that sends no "message" at all. Some send a *flattened* array — the
        // picture already turned into the text "[图片]" — while raw_message still holds the original CQ code. Reading
        // only one of the two is how the text says a picture arrived and the bytes never do (the asymmetry that made
        // this silent: ReadText falls through, ReadImages used to return instead). De-duplicated by what names a
        // picture on this platform, because both forms describing one picture must not fetch it twice.
        if (root.ValueKind == JsonValueKind.Object && TryString(root, "raw_message", out var raw))
            AppendCqImages(HtmlDecode(raw), images);
        return images;
    }

    /// <summary>
    /// What a pushed event carried, as a shape rather than as content: which segment types, in which order, and whether
    /// raw_message agrees.
    ///
    /// This exists because of a failure that had no voice. A bridge may hand a picture over as the <b>text</b>
    /// "[图片]" — a segment of type <c>text</c> whose body is a placeholder — and then every layer below is correct and
    /// silent while nobody ever sees a picture: no image segment, so no attachment, so nothing is ingested, so nothing
    /// is shown. The text still says a picture arrived, which is the whole trap. Naming the shape turns "the model says
    /// it cannot see pictures" into "the bridge sent the word instead of the picture".
    ///
    /// Only type names are reported — never a url, a file name, or any content. This is a diagnostic, not a copy.
    /// </summary>
    public static string ShapeOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "not an object";
        var shape = new List<string>(3);
        if (!root.TryGetProperty("message", out var message)) shape.Add("message: absent");
        else
        {
            switch (message.ValueKind)
            {
                case JsonValueKind.Array:
                    var types = new List<string>(message.GetArrayLength());
                    foreach (var segment in message.EnumerateArray())
                        types.Add(segment.ValueKind == JsonValueKind.Object && TryString(segment, "type", out var type)
                            ? type : "?");
                    shape.Add($"message: [{string.Join(",", types)}]");
                    break;
                case JsonValueKind.String:
                    shape.Add($"message: {CqTypesOf(HtmlDecode(message.GetString() ?? string.Empty))}");
                    break;
                default:
                    shape.Add($"message: {message.ValueKind.ToString().ToLowerInvariant()}");
                    break;
            }
        }
        shape.Add(TryString(root, "raw_message", out var raw)
            ? $"raw_message: {CqTypesOf(HtmlDecode(raw))}"
            : "raw_message: absent");
        return string.Join("; ", shape);
    }

    /// <summary>
    /// The CQ segment types inside a string message, as names — the same answer <see cref="FlattenCqCodes"/> reads,
    /// without the words between them. "no CQ" is an answer too: it says the text was handed over pre-flattened, which
    /// is exactly the case worth telling apart from "the picture was never sent".
    /// </summary>
    private static string CqTypesOf(string text)
    {
        var types = new List<string>(4);
        if (text.Length > 0)
            foreach (Match match in CqCode().Matches(text)) types.Add(match.Groups["type"].Value);
        return types.Count == 0 ? "no CQ" : string.Join(",", types);
    }

    private static void ReadImageSegments(JsonElement message, List<OneBotImage> images)
    {
        foreach (var segment in message.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object) continue;
            if (!TryString(segment, "type", out var type) || type != "image") continue;
            if (!segment.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            Remember(images, new OneBotImage(
                TryString(data, "url", out var url) ? Blank(url) : null,
                TryString(data, "file", out var file) ? Blank(file) : null));
        }
    }

    private static void AppendCqImages(string text, List<OneBotImage> images)
    {
        if (text.Length == 0) return;
        foreach (Match match in CqCode().Matches(text))
        {
            if (match.Groups["type"].Value != "image") continue;
            var args = match.Groups["args"].Value;
            // CQ arguments escape "&#44;" for a comma and "&#91;" for brackets: left as-is, the url is not a url.
            Remember(images, new OneBotImage(Blank(HtmlDecode(CqArgument(args, "url") ?? "")),
                Blank(HtmlDecode(CqArgument(args, "file") ?? ""))));
        }
    }

    /// <summary>
    /// One picture, once. The two spellings of a message describe the same picture differently — a segment almost
    /// always carries both a url and a file name, a CQ code often carries only the name — so the comparison prefers
    /// the more stable of the two and only falls back to the other when one side is missing it.
    /// </summary>
    private static void Remember(List<OneBotImage> images, OneBotImage image)
    {
        foreach (var existing in images)
            if (SamePicture(existing, image)) return;
        images.Add(image);
    }

    private static bool SamePicture(OneBotImage one, OneBotImage other)
    {
        if (!string.IsNullOrWhiteSpace(one.File) && !string.IsNullOrWhiteSpace(other.File))
            return string.Equals(one.File, other.File, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(one.Url) && !string.IsNullOrWhiteSpace(other.Url))
            return string.Equals(one.Url, other.Url, StringComparison.Ordinal);
        return false;
    }

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
        return BuildSend(echo, targetId, platform, segments);
    }

    /// <summary>
    /// The layout form: one message made of segments, in the order the blocks were. The reply quote is added by
    /// <see cref="BuildOutboundSegments(IReadOnlyList{MessageBlock}, string?, Func{ImageBlock, string?})"/>, and pictures
    /// are looked up in <paramref name="pictures"/> by what they point at — null there means "could not be read", which
    /// travels as its own one-line text.
    /// </summary>
    public static string BuildSend(string echo, long targetId, string platform, OutboundMessage message,
        IReadOnlyDictionary<string, string?> pictures)
    {
        var segments = BuildOutboundSegments(message.Blocks!, message.ReplyTo,
            picture => pictures.GetValueOrDefault(picture.Picture.Locator));
        return BuildSend(echo, targetId, platform, segments);
    }

    private static string BuildSend(string echo, long targetId, string platform, object[] segments)
    {
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

    /// <summary>
    /// One action of ours — the ones this package wraps by name — with a payload built from typed values.
    ///
    /// <see cref="BuildRaw"/> carries whatever the caller wrote, which is the point of a pass-through door and the reason a
    /// wrapped verb must not use it: the whole value of wrapping is that <c>group_id</c> is a number here because this code
    /// said it is, not because a model remembered which of OneBot's verbs take a string (docs/16 §10.1).
    /// </summary>
    public static string BuildAction(string echo, string action, object parameters) =>
        JsonSerializer.Serialize(new { action, @params = parameters, echo }, Json);

    /// <summary>
    /// What a raw call answers: the platform's own verdict, with its <c>retcode</c> intact.
    ///
    /// The one thing to get right is that a reply may carry no <c>data</c> at all — a poke, a set_*, half of OneBot's
    /// verbs answer with nothing but a status. That arrives as a default <see cref="JsonElement"/>, and serialising one
    /// of those throws "Operation is not valid due to the current state of the object" — which is how a successful poke
    /// reaches the model as a failure and gets retried until it looks like something is wrong.
    /// </summary>
    public static JsonElement BuildRawAnswer(OneBotResponse response, JsonElement data) =>
        JsonSerializer.SerializeToElement(new
        {
            ok = response.Ok,
            retcode = response.RetCode,
            message = response.Message,
            data = data.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : data
        }, Json);

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
    public static object[] BuildOutboundSegments(string text, string? replyTo) =>
        BuildOutboundSegments([new TextBlock(text)], replyTo);

    /// <summary>
    /// A laid-out message as OneBot segments, in order. The reply quote goes first when there is one, because it is the
    /// outermost thing the message says ("this answers that"), and a quote in the middle would be a quote of nothing.
    ///
    /// Pictures go as <c>base64://</c> rather than as a file path: a locale on the same machine is not something a
    /// deployment may assume, and a base64 segment needs no shared filesystem at all. A face is sent as the platform's own
    /// id, untouched — it is already dialect.
    /// </summary>
    public static object[] BuildOutboundSegments(IReadOnlyList<MessageBlock> blocks, string? replyTo,
        Func<ImageBlock, string?>? imageUri = null)
    {
        var segments = new List<object>(blocks.Count + 2);
        if (replyTo is { Length: > 0 } id && id.All(char.IsAsciiDigit))
            segments.Add(new { type = "reply", data = new { id } });
        var body = 0;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextBlock text when text.Text.Length > 0:
                    segments.Add(new { type = "text", data = new { text = text.Text } });
                    break;
                case MentionBlock mention:
                    segments.Add(new { type = "at", data = new { qq = mention.Id } });
                    break;
                case FaceBlock face:
                    segments.Add(new { type = "face", data = new { id = face.Id } });
                    break;
                case ImageBlock picture:
                {
                    // The bytes are resolved by the caller (it is the one holding the file port). A picture that could not
                    // be read still becomes a segment — as the sentence the text view already had — so a message never
                    // silently loses a part.
                    var uri = imageUri?.Invoke(picture);
                    segments.Add(uri is null
                        ? new { type = "text", data = new { text = picture.AsText } }
                        : new { type = "image", data = new { file = uri, sub_type = 0 } });
                    break;
                }
                case FileBlock:
                    // Deliberately not a segment. OneBot has no "here is a file" message part: `upload_group_file` is an
                    // action of its own and the chat never mentions what it delivered (docs/28 §6.6). The adapter delivers
                    // file blocks itself, one action each — one arriving here was put into a segment list by mistake, and
                    // skipping it is better than inventing a part the bridge does not have.
                    continue;
                default:
                    continue;
            }
            body++;
        }
        // Only a quote (or nothing at all): OneBot rejects an empty message array, and an empty bubble is not what any
        // caller meant, so the flattening is sent rather than a refusal nobody can act on.
        if (body == 0) segments.Add(new { type = "text", data = new { text = MessageBlock.Flatten(blocks) } });
        return segments.ToArray();
    }

    /// <summary>
    /// <c>upload_group_file</c> or <c>upload_private_file</c>: how a file gets there when it is not part of the message.
    ///
    /// <paramref name="file"/> is whatever the bridge accepts as a resource — a URL it can fetch, a path on its own disk,
    /// or <c>base64://</c> — and which one it accepts is the one real difference between OneBot implementations: NapCat
    /// documents http(s) URLs and recommends an HTTP port for exactly this (docs/16 §12.5), while the go-cqhttp API says
    /// local path only. That is why the choice is configuration, not a guess: <c>fileDelivery</c>.
    ///
    /// <c>folder</c> is omitted rather than sent as null when there is none: a group's root directory is the default, and
    /// "folder: null" is a value somebody has to interpret.
    /// </summary>
    public static string BuildUploadFile(string echo, long targetId, string platform, string file, string name,
        string? folder = null)
    {
        var parameters = new Dictionary<string, object?> { ["file"] = file, ["name"] = name };
        if (platform == PrivatePlatform) parameters["user_id"] = targetId;
        else
        {
            parameters["group_id"] = targetId;
            if (string.IsNullOrWhiteSpace(folder) is false) parameters["folder"] = folder;
        }
        return JsonSerializer.Serialize(new
        {
            action = platform == PrivatePlatform ? "upload_private_file" : "upload_group_file",
            @params = parameters,
            echo
        }, Json);
    }

    /// <summary>
    /// <c>download_file</c>: the bridge fetches a URL onto its own disk and answers with the path. The second half of the
    /// two-step upload, and the only way to use a URL on an implementation whose <c>upload_*</c> takes paths only.
    ///
    /// <paramref name="threads"/> is a request, not a promise: it is what makes the bridge ask for byte ranges instead of
    /// one long body (docs/28 §6.6), which is why the door we hand it has to answer ranges.
    /// </summary>
    public static string BuildDownloadFile(string echo, string url, int threads) =>
        JsonSerializer.Serialize(new
        {
            action = "download_file",
            @params = new Dictionary<string, object?> { ["url"] = url, ["thread_count"] = threads },
            echo
        }, Json);

    /// <summary>Where <c>download_file</c> put it. Implementations differ on the key, so both spellings are read.</summary>
    public static string? ReadDownloadedPath(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "file", "file_path", "path" })
            if (TryString(data, key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    /// <summary>
    /// <c>get_group_root_files</c>: the group's own file area, which is a place of its own rather than the chat.
    ///
    /// Only the root is listed because only the root is one call. A group with folders answers with them as well, and
    /// saying "there are three folders here, this build does not open them" is the honest version of a partial listing
    /// (docs/28 §6.7) — <see cref="ReadGroupFiles"/> keeps both lists.
    /// </summary>
    public static string BuildListGroupFiles(string echo, long groupId) =>
        JsonSerializer.Serialize(new
        {
            action = "get_group_root_files",
            @params = new Dictionary<string, object?> { ["group_id"] = groupId },
            echo
        }, Json);

    /// <summary>
    /// <c>get_file</c>: the bytes behind one id, or where they are. The handle carries what the call needs —
    /// <c>{busid}|{file_id}</c> for a group file, <c>{file_id}</c> alone for a private one — because a group file's id
    /// is not enough on its own: <c>busid</c> says which of QQ's file systems it lives in, and the bridge wants both.
    /// </summary>
    public static string BuildGetFile(string echo, string busid, string fileId)
    {
        var parameters = new Dictionary<string, object?> { ["file_id"] = fileId };
        // A number when it is one: a bridge on either side of the v11 dialect reads `busid` as an integer, and sending it
        // as a string is the kind of thing that works until it does not.
        if (long.TryParse(busid, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var numeric))
            parameters["busid"] = numeric;
        else if (!string.IsNullOrWhiteSpace(busid)) parameters["busid"] = busid;
        return JsonSerializer.Serialize(new { action = "get_file", @params = parameters, echo }, Json);
    }

    /// <summary>
    /// <c>get_group_file_url</c>: QQ's own download link for one group file, resolved by the bridge over its packet
    /// connection. The caller fetches that link itself — which is the whole point, because a link works from any machine
    /// and a path only from the one the bridge runs on (docs/28 §6.8).
    ///
    /// <c>group_id</c> goes as a string because that is what this action declares; the list action next door takes a
    /// number. Two spellings of one thing in one dialect, which is not something a caller can fix from here.
    /// </summary>
    public static string BuildGetGroupFileUrl(string echo, long groupId, string fileId) =>
        JsonSerializer.Serialize(new
        {
            action = "get_group_file_url",
            @params = new Dictionary<string, object?> { ["group_id"] = Number(groupId), ["file_id"] = fileId },
            echo
        }, Json);

    /// <summary>
    /// <c>get_private_file_url</c>: the same idea for a file in a private conversation. It takes the id alone, and asked
    /// about a group's file it answers with a link that reads <c>http://undefined:undefined…</c> while still saying
    /// <c>retcode 0</c> — so the answer is checked for being a link, not for being an answer (docs/28 §6.8).
    /// </summary>
    public static string BuildGetPrivateFileUrl(string echo, string fileId) =>
        JsonSerializer.Serialize(new
        {
            action = "get_private_file_url",
            @params = new Dictionary<string, object?> { ["file_id"] = fileId },
            echo
        }, Json);

    /// <summary>
    /// One entry in a group's file area, as the bridge described it. Kept as its own record rather than parsed straight
    /// into a <c>PlatformFile</c> because the handle is the adapter's business: the protocol knows a file id and a busid,
    /// and how those two become a name nobody else parses is decided one layer up.
    /// </summary>
    public sealed record OneBotFile(string Id, string Name, long? Bytes, DateTimeOffset? At, string? From, string BusId);

    /// <summary>
    /// The two lists <c>get_group_root_files</c> answers with. Either may be missing, and a file without an id is not a
    /// file we can point at later — dropping it is better than handing out a handle nobody can use.
    /// </summary>

    public static (IReadOnlyList<OneBotFile> Files, IReadOnlyList<string> Folders) ReadGroupFiles(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return ([], []);

        var files = new List<OneBotFile>();
        if (data.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!TryString(entry, "file_id", out var id) || string.IsNullOrWhiteSpace(id)) continue;
                TryString(entry, "file_name", out var name);
                var bytes = TryLong(entry, "file_size", out var size) ? size : (long?)null;
                // upload_time is a unix second on every implementation that sends it; modify_time is the fallback, and
                // either is more useful than "no date" when the model is asked "is this today's".
                var at = TryLong(entry, "upload_time", out var uploaded) && uploaded > 0 ? Seconds(uploaded)
                    : TryLong(entry, "modify_time", out var modified) && modified > 0 ? Seconds(modified)
                    : (DateTimeOffset?)null;
                TryString(entry, "uploader_name", out var from);
                var busid = TryLong(entry, "busid", out var bus) ? bus.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
                files.Add(new OneBotFile(id, string.IsNullOrWhiteSpace(name) ? id : name, bytes, at,
                    string.IsNullOrWhiteSpace(from) ? null : from, busid));
            }
        }

        var folders = new List<string>();
        if (data.TryGetProperty("folders", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var entry in children.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object && TryString(entry, "folder_name", out var folder) &&
                    !string.IsNullOrWhiteSpace(folder))
                    folders.Add(folder);

        return (files, folders);

        static DateTimeOffset Seconds(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);
    }

    /// <summary>
    /// Every place <c>get_file</c> said the bytes are, with the field each one came from.
    ///
    /// <b>Every one of them, because "the first non-empty field" answers a different question than "which of these can
    /// this host reach".</b> Implementations really do answer differently: NapCat hands back a path inside its own
    /// container <i>and</i> — when <c>enableLocalFile2Url</c> is on — the same bytes as a bare <c>base64</c> string, and
    /// the path is the field a dictionary-ordered read finds first. Taking it would keep the one answer the caller cannot
    /// use and throw away the one it can (docs/28 §6.8).
    ///
    /// Order is the order the fields were read, and duplicates are dropped: several keys routinely carry one value.
    /// </summary>
    public static IReadOnlyList<(string Field, string Value)> ReadFileBodies(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return [];
        var found = new List<(string Field, string Value)>();
        foreach (var key in new[] { "file", "url", "base64", "file_path", "path" })
        {
            if (!TryString(data, key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            if (found.Exists(entry => string.Equals(entry.Value, value, StringComparison.Ordinal))) continue;
            found.Add((key, value));
        }
        return found;
    }

    /// <summary>
    /// The link a url-shaped action answered with, when it answered with one.
    ///
    /// <b>A link is not a claim to be trusted on the strength of the status.</b> <c>get_private_file_url</c> asked about a
    /// group's file answers <c>retcode 0</c> with <c>http://undefined:undefined…</c> — a url assembled out of fields the
    /// bridge did not have — so whoever fetches it has to look at the value (docs/28 §6.8).
    /// </summary>
    public static string? ReadFileUrl(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && TryString(data, "url", out var url) &&
        !string.IsNullOrWhiteSpace(url) ? url : null;

    /// <summary>The name <c>get_file</c> felt like including, when it did. Often it does not — the caller has a fallback.</summary>
    public static string? ReadFileName(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && TryString(data, "file_name", out var name) &&
        !string.IsNullOrWhiteSpace(name) ? name : null;

    /// <summary>What the bridge claims the bytes are. A claim: whoever stores them probes them (docs/28 §5).</summary>
    public static string? ReadFileMediaType(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && TryString(data, "content_type", out var type) &&
        !string.IsNullOrWhiteSpace(type) ? type : null;

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
            case "file": return FileNote(data) ?? "[文件]";
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
