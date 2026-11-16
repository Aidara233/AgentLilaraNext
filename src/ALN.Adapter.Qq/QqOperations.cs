using System.Globalization;
using System.Text.Json;
using ALN.Contracts;

namespace ALN.Adapter.Qq;

/// <summary>
/// The QQ verbs worth naming, so that "禁言一个人" is one action with a schema instead of a bridge verb a model has to
/// spell from memory.
///
/// <b>Why these live in this package and not in the host.</b> <c>adapters.raw</c> exists because wrapping every bridge
/// verb would make the host grow a verb every time a bridge gained one (docs/16 §10). That reasoning still holds for the
/// long tail — it does not hold for the two dozen acts a conversation actually needs, and the cost of not having them is a
/// model that guesses <c>set_group_ban</c>'s argument names and its <c>duration</c> units. So the verbs are wrapped
/// <b>here</b>: a new bridge verb is a row in this table and a package update, never a host release.
///
/// <b>What wrapping buys, precisely.</b> Three things, and each is a question the raw door cannot answer:
/// <list type="number">
///   <item>a <b>schema</b> — the model sees <c>group</c>/<c>user</c>/<c>seconds</c>, not a platform's spelling of them;</item>
///   <item><b>normalisation</b> — <c>group</c> takes <c>123456</c>, <c>"123456"</c> or <c>group-123456</c> and goes out as
///   the number one of OneBot's own verbs insists on; the answer comes back with stable field names;</item>
///   <item>a <b>level per verb</b>, in the code table (<see cref="ActionDescriptor.MinLevel"/>), which is the only reason
///   "the model may read every group" and "a person approves who joins" can be two different sentences
///   (docs/07 §9.1).</item>
/// </list>
///
/// <b>Where the line sits.</b> Reading and speaking — the group list, the members, a notice, a card, a recall — is the
/// agent's own work, at <see cref="AccessLevel.Agent"/>. Acting <i>on other people or on the group itself</i> — mute,
/// kick, admin, rename, leave, and approving who may join — is <see cref="AccessLevel.Operator"/>: letting a stranger
/// into the account's reach is the same kind of decision as promoting somebody's level, and it is not the model's to take
/// (docs/07 §9.1, the <c>identity.control</c> ruling). <c>adapters.raw</c> stays as it was: the door for everything this
/// table does not name, still defaulting to <c>full</c> and still only ever lowerable.
/// </summary>
public static class QqOperations
{
    // ---- ids ----------------------------------------------------------------------------------
    public const string GroupList = "qq.group.list";
    public const string GroupGet = "qq.group.get";
    public const string GroupMembers = "qq.group.members";
    public const string GroupMemberGet = "qq.group.member.get";
    public const string GroupNotices = "qq.group.notices";
    public const string GroupNoticeSend = "qq.group.notice.send";
    public const string GroupCardSet = "qq.group.card.set";
    public const string GroupTitleSet = "qq.group.title.set";
    public const string GroupNameSet = "qq.group.name.set";
    public const string GroupMute = "qq.group.mute";
    public const string GroupMuteAll = "qq.group.mute.all";
    public const string GroupKick = "qq.group.kick";
    public const string GroupAdmin = "qq.group.admin";
    public const string GroupLeave = "qq.group.leave";
    public const string GroupRequestPending = "qq.group.request.pending";
    public const string GroupRequestDecide = "qq.group.request.decide";
    public const string FriendList = "qq.friend.list";
    public const string FriendLike = "qq.friend.like";
    public const string FriendRequestDecide = "qq.friend.request.decide";
    public const string StrangerGet = "qq.stranger.get";
    public const string MessageRecall = "qq.message.recall";
    public const string Status = "qq.status";

    private static readonly JsonSerializerOptions QqJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One named verb: what a caller sees, what it needs to be, and what it does.
    ///
    /// The schema fragments (<c>Properties</c>, <c>Required</c>) are fragments rather than a whole schema, because every
    /// verb takes the same <c>address</c> and a schema assembled once cannot disagree with itself about it.
    /// </summary>
    private sealed record QqVerb(string Id, string Summary, string Properties, string Required,
        string Capability, AccessLevel MinLevel, bool Mutating,
        Func<QqAdapter, JsonElement, CancellationToken, ValueTask<Result<JsonElement>>> Run);

    private const string AddressProperty =
        "\"address\":{\"type\":\"string\",\"description\":\"适配器实例，例如 adapter.qq#qq-1。\"}";

    private const string Empty = "";

    private const string Group =
        "\"group\":{\"type\":[\"integer\",\"string\"],\"description\":\"群号：123456、\\\"123456\\\" 或 group-123456。\"}";

    private const string User =
        "\"user\":{\"type\":[\"integer\",\"string\"],\"description\":\"QQ 号：20000、\\\"20000\\\" 或 user-20000。\"}";

    /// <summary>The table: read, speak, moderate — grouped by what a caller is trying to do, not by OneBot's own chapters.</summary>
    private static readonly QqVerb[] Verbs =
    [
        // ---- reading: the account's own view of the platform -----------------------------------
        new(GroupList,
            "这台账号在哪些群里：群号、群名、成员数、容量。回答 \"我在哪些群\" 从这一条开始。",
            "\"no_cache\":{\"type\":\"boolean\",\"description\":\"不用桥接的缓存，现问一次平台。可选，默认 false。\"}", Empty,
            Capabilities.AdaptersRead, AccessLevel.Agent, Mutating: false, GroupListAsync),
        new(GroupGet,
            "一个群是什么样：群名、成员数、容量。",
            Group, "\"group\"",
            Capabilities.AdaptersRead, AccessLevel.Agent, Mutating: false, GroupGetAsync),
        new(GroupMembers,
            "一个群的全部成员：QQ 号、昵称、群名片、角色（owner / admin / member）。群大时很长，按需再取单个成员。",
            Group, "\"group\"",
            Capabilities.AdaptersRead, AccessLevel.Agent, Mutating: false, GroupMembersAsync),
        new(GroupMemberGet,
            "一个群里的一个人：名片、昵称、角色、专属头衔。名片是**群内**的名字，同一个人在别的群叫别的。",
            Group + "," + User, "\"group\",\"user\"",
            Capabilities.AdaptersRead, AccessLevel.Agent, Mutating: false, MemberGetAsync),
        new(GroupNotices,
            "一个群现有的公告：谁发的、什么时候、正文。",
            Group, "\"group\"",
            Capabilities.AdaptersRead, AccessLevel.Agent, Mutating: false, GroupNoticesAsync),
        new(GroupRequestPending,
            "等着处理的入群邀请与加群申请，带 flag —— 给 qq.group.request.decide 用的那个。"
            + "不是每个桥接都实现（get_group_system_msg），不实现时它回的是平台自己的拒绝理由。",
            Empty, Empty,
            Capabilities.AdaptersRead, AccessLevel.Agent, Mutating: false, GroupRequestPendingAsync),
        new(FriendList,
            "好友列表：QQ 号、昵称、备注。备注是**这个账号**给人起的名字，与昵称分开。",
            Empty, Empty,
            Capabilities.AdaptersRead, AccessLevel.Agent, Mutating: false, FriendListAsync),
        new(StrangerGet,
            "陌生人（不是好友、也不在某个群的上下文里）的资料：昵称、性别、年龄。",
            User, "\"user\"",
            Capabilities.AdaptersRead, AccessLevel.Agent, Mutating: false, StrangerGetAsync),
        new(Status,
            "桥接自己好不好：在线、健康、应用名与版本。与 adapters.list 的 connected 互补——那个说的是这条链路，这个说的是对面的实现。",
            Empty, Empty,
            Capabilities.AdaptersRead, AccessLevel.Agent, Mutating: false, StatusAsync),

        // ---- speaking and grooming the account's own presence ----------------------------------
        new(GroupNoticeSend,
            "发一条群公告。公告不进聊天流，群里不会出现一条消息。",
            Group + ",\"text\":{\"type\":\"string\",\"description\":\"公告正文。\"}", "\"group\",\"text\"",
            Capabilities.AdaptersControl, AccessLevel.Agent, Mutating: true, GroupNoticeSendAsync),
        new(GroupCardSet,
            "改群名片。空字符串是删掉名片。改自己的名片是表达，改别人的名片也是同一个动词——所以它在 agent 级而不是 operator 级。",
            Group + "," + User + ",\"card\":{\"type\":\"string\",\"description\":\"新名片；空字符串表示删除。\"}",
            "\"group\",\"user\",\"card\"",
            Capabilities.AdaptersControl, AccessLevel.Agent, Mutating: true, GroupCardSetAsync),
        new(GroupTitleSet,
            "给群成员一个专属头衔。空字符串是取消头衔。时长固定为永久：OneBot 的 duration 各家实现都不认。",
            Group + "," + User + ",\"title\":{\"type\":\"string\",\"description\":\"头衔；空字符串表示取消。\"}",
            "\"group\",\"user\",\"title\"",
            Capabilities.AdaptersControl, AccessLevel.Agent, Mutating: true, GroupTitleSetAsync),
        new(MessageRecall,
            "撤回一条消息，按消息 id。消息 id 来自收到的消息（platformMessageId）或发送后的回执。",
            "\"message\":{\"type\":[\"integer\",\"string\"],\"description\":\"要撤回的消息 id。\"}", "\"message\"",
            Capabilities.AdaptersControl, AccessLevel.Agent, Mutating: true, MessageRecallAsync),
        new(FriendLike,
            "给一个人点名片赞（不是好友也能点，是平台允许的少数 \"对陌生人做的小事\"）。",
            User + ",\"times\":{\"type\":\"integer\",\"description\":\"点几次，1..10，默认 1。\"}", "\"user\"",
            Capabilities.AdaptersControl, AccessLevel.Agent, Mutating: true, FriendLikeAsync),

        // ---- acting on other people or on the group itself: a person's decision ----------------
        new(GroupMute,
            "禁言一个人若干秒；seconds 传 0 是解除禁言。要管理或群主权限，平台仍可能拒绝。",
            Group + "," + User + ",\"seconds\":{\"type\":\"integer\",\"description\":\"禁言秒数；0 表示解除。\"}",
            "\"group\",\"user\",\"seconds\"",
            Capabilities.AdaptersControl, AccessLevel.Operator, Mutating: true, GroupMuteAsync),
        new(GroupMuteAll,
            "全员禁言开或关。",
            Group + ",\"enabled\":{\"type\":\"boolean\",\"description\":\"true 开、false 关。\"}", "\"group\",\"enabled\"",
            Capabilities.AdaptersControl, AccessLevel.Operator, Mutating: true, GroupMuteAllAsync),
        new(GroupKick,
            "把一个人移出群；block 为 true 时同时拒绝他之后的加群申请。",
            Group + "," + User + ",\"block\":{\"type\":\"boolean\",\"description\":\"是否同时拒绝再加群。可选，默认 false。\"}",
            "\"group\",\"user\"",
            Capabilities.AdaptersControl, AccessLevel.Operator, Mutating: true, GroupKickAsync),
        new(GroupAdmin,
            "设或撤一个人的群管理员。",
            Group + "," + User + ",\"enabled\":{\"type\":\"boolean\",\"description\":\"true 设置、false 取消。\"}",
            "\"group\",\"user\",\"enabled\"",
            Capabilities.AdaptersControl, AccessLevel.Operator, Mutating: true, GroupAdminAsync),
        new(GroupNameSet,
            "改群名。改的是一个共享空间的招牌，所以它是人的决定而不是模型的。",
            Group + ",\"name\":{\"type\":\"string\",\"description\":\"新群名。\"}", "\"group\",\"name\"",
            Capabilities.AdaptersControl, AccessLevel.Operator, Mutating: true, GroupNameSetAsync),
        new(GroupLeave,
            "退出一个群；dissolve 只在 bot 是群主时有意义（解散群）。退出去之后收不到那个群的消息，也不再能发。",
            Group + ",\"dissolve\":{\"type\":\"boolean\",\"description\":\"解散该群（仅群主）。可选，默认 false。\"}",
            "\"group\"",
            Capabilities.AdaptersControl, AccessLevel.Operator, Mutating: true, GroupLeaveAsync),
        new(GroupRequestDecide,
            "处理一条加群申请或入群邀请：同意就是把一个陌生人放进这个账号的对话范围，所以它由人来决定。"
            + "flag 从 qq.group.request.pending 拿，kind 必须与那条请求自己的类型相符。",
            "\"flag\":{\"type\":\"string\",\"description\":\"请求的 flag，来自 qq.group.request.pending。\"},"
            + "\"kind\":{\"type\":\"string\",\"enum\":[\"add\",\"invite\"],\"description\":\"add = 别人申请进群，invite = 有人邀请我进群。\"},"
            + "\"approve\":{\"type\":\"boolean\",\"description\":\"true 同意、false 拒绝。\"},"
            + "\"reason\":{\"type\":\"string\",\"description\":\"拒绝理由，只在拒绝时有意义。可选。\"}",
            "\"flag\",\"kind\",\"approve\"",
            Capabilities.AdaptersControl, AccessLevel.Operator, Mutating: true, GroupRequestDecideAsync),
        new(FriendRequestDecide,
            "处理一条加好友请求。同意等于让一个陌生人能直接私聊这个账号。",
            "\"flag\":{\"type\":\"string\",\"description\":\"请求的 flag，来自桥接上报的加好友请求。\"},"
            + "\"approve\":{\"type\":\"boolean\",\"description\":\"true 同意、false 拒绝。\"},"
            + "\"remark\":{\"type\":\"string\",\"description\":\"加为好友后的备注，只在同意时有意义。可选。\"}",
            "\"flag\",\"approve\"",
            Capabilities.AdaptersControl, AccessLevel.Operator, Mutating: true, FriendRequestDecideAsync)
    ];

    /// <summary>
    /// Puts every verb in the control plane's table. Called once, when this package starts: the package owns the verbs,
    /// and the loader takes them back when it goes (docs/18).
    /// </summary>
    public static Result Register(IControlPlane plane, IAdapterManager adapters)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        if (adapters is null) return Result.Failure(new(ErrorCode.Invalid, "The adapter manager is required."));
        foreach (var verb in Verbs)
        {
            var registered = plane.Register(
                new(verb.Id, verb.Summary, verb.Capability, verb.Mutating, ParamsJson: SchemaOf(verb),
                    // The address is the one argument that is a string in every verb, and it is what the audit record can
                    // name without recording anything a caller typed into the rest of the object.
                    Subject: "address", MinLevel: verb.MinLevel),
                (arguments, cancellationToken) => RunAsync(adapters, verb, arguments, cancellationToken));
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    /// <summary>Every verb id this package registers — what a test (or a person) counts against the catalog.</summary>
    public static IReadOnlyList<string> Ids => Verbs.Select(verb => verb.Id).ToArray();

    private static string SchemaOf(QqVerb verb) =>
        "{\"type\":\"object\",\"properties\":{" + AddressProperty
        + (verb.Properties.Length == 0 ? string.Empty : "," + verb.Properties) + "}"
        + ",\"required\":[\"address\"" + (verb.Required.Length == 0 ? string.Empty : "," + verb.Required) + "]"
        + ",\"additionalProperties\":false}";

    /// <summary>
    /// Resolves the instance and hands the verb to it.
    ///
    /// The address is required for a reason that is not ceremony: one host can carry several QQ accounts, and "which
    /// account acts" is the difference between muting somebody in one group and muting them in another.
    /// </summary>
    private static async ValueTask<Result<JsonElement>> RunAsync(IAdapterManager adapters, QqVerb verb,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryReadText(arguments, "address", out var addressText))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"{verb.Id} 需要 'address'（例如 adapter.qq#qq-1）。"));
        if (!Address.TryParse(addressText, out var address))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"不是一个地址：{addressText}"));
        var found = adapters.Get(address);
        if (!found.Ok)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                $"没有适配器应这个地址：{address}。适配器列表见 adapters.list。"));
        if (found.Value is not QqAdapter adapter)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                $"{address} 不是 QQ 适配器实例（{adapters.TypeOf(address) ?? "未知类型"}）：qq.* 这组动词只对它自己的平台说话。"));
        return await verb.Run(adapter, arguments, cancellationToken).ConfigureAwait(false);
    }

    // ---- the verbs ------------------------------------------------------------------------------

    private static async ValueTask<Result<JsonElement>> GroupListAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var asked = await AskAsync(adapter, "get_group_list",
            new { no_cache = OptionalFlag(arguments, "no_cache") ?? false }, cancellationToken).ConfigureAwait(false);
        if (!asked.Ok) return asked;
        var groups = new List<object>();
        if (asked.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in asked.Value.EnumerateArray())
            {
                if (LongOf(entry, "group_id") is not { } id) continue;
                groups.Add(new
                {
                    id,
                    name = TextOf(entry, "group_name"),
                    members = LongOf(entry, "member_count"),
                    maxMembers = LongOf(entry, "max_member_count")
                });
            }
        }
        return Done(new { count = groups.Count, groups });
    }

    private static async ValueTask<Result<JsonElement>> GroupGetAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        var asked = await AskAsync(adapter, "get_group_info",
            new { group_id = group, no_cache = OptionalFlag(arguments, "no_cache") ?? false }, cancellationToken)
            .ConfigureAwait(false);
        if (!asked.Ok) return asked;
        var data = asked.Value;
        return Done(new
        {
            id = LongOf(data, "group_id") ?? group,
            name = TextOf(data, "group_name"),
            members = LongOf(data, "member_count"),
            maxMembers = LongOf(data, "max_member_count")
        });
    }

    private static async ValueTask<Result<JsonElement>> GroupMembersAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        var asked = await AskAsync(adapter, "get_group_member_list",
            new { group_id = group, no_cache = OptionalFlag(arguments, "no_cache") ?? false }, cancellationToken)
            .ConfigureAwait(false);
        if (!asked.Ok) return asked;
        var members = new List<object>();
        if (asked.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in asked.Value.EnumerateArray())
            {
                if (LongOf(entry, "user_id") is not { } id) continue;
                members.Add(new
                {
                    id,
                    name = TextOf(entry, "nickname"),
                    card = TextOf(entry, "card"),
                    role = TextOf(entry, "role")
                });
            }
        }
        return Done(new { count = members.Count, members });
    }

    private static async ValueTask<Result<JsonElement>> MemberGetAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        if (!TryReadUser(arguments, out var user, out missing)) return missing;
        var asked = await AskAsync(adapter, "get_group_member_info",
            new { group_id = group, user_id = user, no_cache = OptionalFlag(arguments, "no_cache") ?? false },
            cancellationToken).ConfigureAwait(false);
        if (!asked.Ok) return asked;
        var data = asked.Value;
        return Done(new
        {
            id = LongOf(data, "user_id") ?? user,
            name = TextOf(data, "nickname"),
            card = TextOf(data, "card"),
            role = TextOf(data, "role"),
            title = TextOf(data, "title")
        });
    }

    private static async ValueTask<Result<JsonElement>> GroupNoticesAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        var asked = await AskAsync(adapter, "_get_group_notice", new { group_id = group }, cancellationToken)
            .ConfigureAwait(false);
        if (!asked.Ok) return asked;
        var notices = new List<object>();
        if (asked.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in asked.Value.EnumerateArray())
            {
                var text = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("message", out var message)
                    ? TextOf(message, "text")
                    : null;
                notices.Add(new
                {
                    from = LongOf(entry, "sender_id"),
                    at = LongOf(entry, "publish_time") is { } seconds
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("O", CultureInfo.InvariantCulture)
                        : null,
                    text
                });
            }
        }
        return Done(new { count = notices.Count, notices });
    }

    private static async ValueTask<Result<JsonElement>> GroupRequestPendingAsync(QqAdapter adapter,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        var asked = await AskAsync(adapter, "get_group_system_msg", new { }, cancellationToken).ConfigureAwait(false);
        if (!asked.Ok) return asked;
        var invites = new List<object>();
        var joins = new List<object>();
        var data = asked.Value;
        // A bridge with nothing pending answers `data: null` (or omits both lists); that is "none waiting", not an error.
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("invited_requests", out var invited) && invited.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in invited.EnumerateArray())
                {
                    invites.Add(new
                    {
                        // The flag is the request id as text: it is what the decision verb needs, and a number that a model
                        // has to re-spell is a number that gets re-spelled wrong.
                        flag = LongOf(entry, "request_id")?.ToString(CultureInfo.InvariantCulture),
                        group = LongOf(entry, "group_id"),
                        groupName = TextOf(entry, "group_name"),
                        from = LongOf(entry, "invitor_uin"),
                        fromName = TextOf(entry, "invitor_nick"),
                        handled = BoolOf(entry, "checked")
                    });
                }
            }
            if (data.TryGetProperty("join_requests", out var joined) && joined.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in joined.EnumerateArray())
                {
                    joins.Add(new
                    {
                        flag = LongOf(entry, "request_id")?.ToString(CultureInfo.InvariantCulture),
                        group = LongOf(entry, "group_id"),
                        groupName = TextOf(entry, "group_name"),
                        user = LongOf(entry, "requester_uin"),
                        userName = TextOf(entry, "requester_nick"),
                        message = TextOf(entry, "message"),
                        handled = BoolOf(entry, "checked")
                    });
                }
            }
        }
        return Done(new { count = invites.Count + joins.Count, invites, joins });
    }

    private static async ValueTask<Result<JsonElement>> FriendListAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var asked = await AskAsync(adapter, "get_friend_list", new { }, cancellationToken).ConfigureAwait(false);
        if (!asked.Ok) return asked;
        var friends = new List<object>();
        if (asked.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in asked.Value.EnumerateArray())
            {
                if (LongOf(entry, "user_id") is not { } id) continue;
                friends.Add(new
                {
                    id,
                    name = TextOf(entry, "nickname"),
                    // A remark belongs to this account, not to the person: keeping it in its own field is what stops it
                    // from being read as "what he is called" (docs/16 §4).
                    remark = TextOf(entry, "remark")
                });
            }
        }
        return Done(new { count = friends.Count, friends });
    }

    private static async ValueTask<Result<JsonElement>> StrangerGetAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadUser(arguments, out var user, out var missing)) return missing;
        var asked = await AskAsync(adapter, "get_stranger_info",
            new { user_id = user, no_cache = OptionalFlag(arguments, "no_cache") ?? false }, cancellationToken)
            .ConfigureAwait(false);
        if (!asked.Ok) return asked;
        var data = asked.Value;
        return Done(new
        {
            id = LongOf(data, "user_id") ?? user,
            name = TextOf(data, "nickname"),
            sex = TextOf(data, "sex"),
            age = LongOf(data, "age")
        });
    }

    private static async ValueTask<Result<JsonElement>> StatusAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var asked = await AskAsync(adapter, "get_status", new { }, cancellationToken).ConfigureAwait(false);
        if (!asked.Ok) return asked;
        var data = asked.Value;
        // Two calls, one answer: "is it online" and "what is it running" are the same question to whoever is debugging,
        // and the second is best-effort — a bridge that does not implement it still answers the first.
        var version = await AskAsync(adapter, "get_version_info", new { }, cancellationToken).ConfigureAwait(false);
        return Done(new
        {
            online = BoolOf(data, "online"),
            good = BoolOf(data, "good"),
            app = version.Ok ? TextOf(version.Value, "app_name") ?? TextOf(version.Value, "app_full_name") : null,
            version = version.Ok ? TextOf(version.Value, "app_version") ?? TextOf(version.Value, "version") : null
        });
    }

    private static async ValueTask<Result<JsonElement>> GroupNoticeSendAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        if (!TryReadText(arguments, "text", out var text)) return MissingResult("text", "公告正文。");
        var asked = await AskAsync(adapter, "_send_group_notice", new { group_id = group, content = text },
            cancellationToken, retryable: false).ConfigureAwait(false);
        return asked.Ok ? Done(new { group, sent = true }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> GroupCardSetAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        if (!TryReadUser(arguments, out var user, out missing)) return missing;
        if (!TryReadEmptyable(arguments, "card", out var card)) return MissingResult("card", "新名片；空字符串表示删除。");
        var asked = await AskAsync(adapter, "set_group_card", new { group_id = group, user_id = user, card },
            cancellationToken, retryable: false).ConfigureAwait(false);
        return asked.Ok ? Done(new { group, user, card }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> GroupTitleSetAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        if (!TryReadUser(arguments, out var user, out missing)) return missing;
        if (!TryReadEmptyable(arguments, "title", out var title)) return MissingResult("title", "头衔；空字符串表示取消。");
        var asked = await AskAsync(adapter, "set_group_special_title",
            new { group_id = group, user_id = user, special_title = title, duration = -1 }, cancellationToken,
            retryable: false).ConfigureAwait(false);
        return asked.Ok ? Done(new { group, user, title }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> MessageRecallAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadText(arguments, "message", out var message)) return MissingResult("message", "要撤回的消息 id。");
        // OneBot's own verb takes a number, while some bridges hand out message ids as strings — so a numeric id goes as
        // the number it is, and anything else is passed through rather than guessed at.
        object id = long.TryParse(message, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
            ? numeric
            : message;
        var asked = await AskAsync(adapter, "delete_msg", new { message_id = id }, cancellationToken, retryable: false)
            .ConfigureAwait(false);
        return asked.Ok ? Done(new { message = id, recalled = true }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> FriendLikeAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadUser(arguments, out var user, out var missing)) return missing;
        var times = OptionalInteger(arguments, "times") ?? 1;
        if (times is < 1 or > 10) return InvalidResult("times", "点几次是 1..10 之间的整数。");
        var asked = await AskAsync(adapter, "send_like", new { user_id = user, times }, cancellationToken,
            retryable: false).ConfigureAwait(false);
        return asked.Ok ? Done(new { user, times }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> GroupMuteAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        if (!TryReadUser(arguments, out var user, out missing)) return missing;
        if (OptionalInteger(arguments, "seconds") is not { } seconds)
            return MissingResult("seconds", "禁言秒数；0 表示解除。");
        // One month is QQ's own ceiling for a mute; a larger number is a typo far more often than a request.
        if (seconds is < 0 or > 30 * 24 * 60 * 60)
            return InvalidResult("seconds", "禁言秒数在 0..2592000 之间（0 表示解除）。");
        var asked = await AskAsync(adapter, "set_group_ban",
            new { group_id = group, user_id = user, duration = seconds }, cancellationToken, retryable: false)
            .ConfigureAwait(false);
        return asked.Ok ? Done(new { group, user, seconds }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> GroupMuteAllAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        if (OptionalFlag(arguments, "enabled") is not { } enabled) return MissingResult("enabled", "true 开、false 关。");
        var asked = await AskAsync(adapter, "set_group_whole_ban", new { group_id = group, enable = enabled },
            cancellationToken, retryable: false).ConfigureAwait(false);
        return asked.Ok ? Done(new { group, enabled }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> GroupKickAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        if (!TryReadUser(arguments, out var user, out missing)) return missing;
        var block = OptionalFlag(arguments, "block") ?? false;
        var asked = await AskAsync(adapter, "set_group_kick",
            new { group_id = group, user_id = user, reject_add_request = block }, cancellationToken, retryable: false)
            .ConfigureAwait(false);
        return asked.Ok ? Done(new { group, user, block }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> GroupAdminAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        if (!TryReadUser(arguments, out var user, out missing)) return missing;
        if (OptionalFlag(arguments, "enabled") is not { } enabled)
            return MissingResult("enabled", "true 设置管理员、false 取消。");
        var asked = await AskAsync(adapter, "set_group_admin", new { group_id = group, user_id = user, enable = enabled },
            cancellationToken, retryable: false).ConfigureAwait(false);
        return asked.Ok ? Done(new { group, user, enabled }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> GroupNameSetAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        if (!TryReadText(arguments, "name", out var name)) return MissingResult("name", "新群名。");
        var asked = await AskAsync(adapter, "set_group_name", new { group_id = group, group_name = name },
            cancellationToken, retryable: false).ConfigureAwait(false);
        return asked.Ok ? Done(new { group, name }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> GroupLeaveAsync(QqAdapter adapter, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadGroup(arguments, out var group, out var missing)) return missing;
        var dissolve = OptionalFlag(arguments, "dissolve") ?? false;
        var asked = await AskAsync(adapter, "set_group_leave", new { group_id = group, is_dismiss = dissolve },
            cancellationToken, retryable: false).ConfigureAwait(false);
        return asked.Ok ? Done(new { group, left = true, dissolve }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> GroupRequestDecideAsync(QqAdapter adapter,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryReadText(arguments, "flag", out var flag))
            return MissingResult("flag", "请求的 flag，来自 qq.group.request.pending。");
        if (!TryReadText(arguments, "kind", out var kind))
            return MissingResult("kind", "add（别人申请进群）或 invite（有人邀请我进群）。");
        kind = kind.ToLowerInvariant();
        // A kind that does not match the request is refused by the platform, and the mismatch is the kind of thing that
        // gets answered with "no such request" — so it is refused here, where the reason can be spelled out.
        if (kind is not ("add" or "invite")) return InvalidResult("kind", "kind 只能是 add 或 invite。");
        if (OptionalFlag(arguments, "approve") is not { } approve) return MissingResult("approve", "true 同意、false 拒绝。");
        var reason = OptionalText(arguments, "reason");
        var asked = await AskAsync(adapter, "set_group_add_request",
            new { flag, sub_type = kind, approve, reason = approve ? null : reason }, cancellationToken,
            retryable: false).ConfigureAwait(false);
        return asked.Ok ? Done(new { flag, kind, approve }) : asked;
    }

    private static async ValueTask<Result<JsonElement>> FriendRequestDecideAsync(QqAdapter adapter,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryReadText(arguments, "flag", out var flag))
            return MissingResult("flag", "请求的 flag，来自桥接上报的加好友请求。");
        if (OptionalFlag(arguments, "approve") is not { } approve) return MissingResult("approve", "true 同意、false 拒绝。");
        var remark = OptionalText(arguments, "remark");
        var asked = await AskAsync(adapter, "set_friend_add_request",
            new { flag, approve, remark = approve ? remark : null }, cancellationToken, retryable: false)
            .ConfigureAwait(false);
        return asked.Ok ? Done(new { flag, approve }) : asked;
    }

    // ---- talking to the bridge ------------------------------------------------------------------

    /// <summary>
    /// Sends one platform verb and hands back its <c>data</c>, or the platform's own refusal as a failure.
    ///
    /// <b>A refusal by the platform is a failure here, not a successful call with a refusal inside it</b> — the opposite
    /// of the raw door, and on purpose. The raw door's caller asked for a verb, so it gets the platform's verdict
    /// verbatim; a caller of <c>qq.group.mute</c> asked <i>to mute somebody</i>, and "it did not happen, here is why" is
    /// the answer to that question. The <c>retcode</c> and the platform's message travel inside the error, so nothing a
    /// bridge said is lost.
    /// </summary>
    /// <param name="retryable">
    /// False for anything that changes something: a call that failed may still have half happened, and telling a caller
    /// "retry" is how the same mute gets applied twice (docs/08 §5, the same rule as a timed-out tool call).
    /// </param>
    private static async ValueTask<Result<JsonElement>> AskAsync(QqAdapter adapter, string action, object parameters,
        CancellationToken cancellationToken, bool retryable = true)
    {
        var called = await adapter.CallBridgeAsync(echo => OneBotProtocol.BuildAction(echo, action, parameters),
            cancellationToken).ConfigureAwait(false);
        if (!called.Ok)
            return Result<JsonElement>.Failure(called.Error ?? new(ErrorCode.Unavailable, $"{action} 没有回答。"));
        var reply = called.Value;
        if (reply.Response.Ok && reply.Response.RetCode == 0) return Result<JsonElement>.Success(reply.Data);
        return Result<JsonElement>.Failure(new(ErrorCode.Unavailable,
            $"桥接拒绝了 {action}（retcode {reply.Response.RetCode}）：{reply.Response.Message ?? "没有说明"}",
            action, retryable));
    }

    private static Result<JsonElement> Done(object receipt) =>
        Result<JsonElement>.Success(JsonSerializer.SerializeToElement(receipt, QqJson));

    // ---- reading arguments ----------------------------------------------------------------------

    /// <summary>
    /// A group id, in any of the three spellings a caller may have in hand: the number, the number as text, or the
    /// conversation id this system uses for it (<c>group-123456</c>). The platform gets a number either way — which is
    /// the other half of what wrapping is for.
    /// </summary>
    private static bool TryReadGroup(JsonElement arguments, out long group, out Result<JsonElement> missing)
    {
        missing = default;
        if (TryReadId(arguments, "group", out group)) return true;
        missing = MissingResult("group", "群号：123456、\"123456\" 或 group-123456。");
        return false;
    }

    private static bool TryReadUser(JsonElement arguments, out long user, out Result<JsonElement> missing)
    {
        missing = default;
        if (TryReadId(arguments, "user", out user)) return true;
        missing = MissingResult("user", "QQ 号：20000、\"20000\" 或 user-20000。");
        return false;
    }

    private static bool TryReadId(JsonElement arguments, string name, out long id)
    {
        id = 0;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value)) return false;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt64(out id) && id > 0;
            case JsonValueKind.String:
            {
                var text = (value.GetString() ?? string.Empty).Trim();
                if (OneBotProtocol.TryReadConversation(text, out var fromConversation, out _))
                {
                    id = fromConversation;
                    return true;
                }
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
            }
            default:
                return false;
        }
    }

    private static bool TryReadText(JsonElement arguments, string name, out string value)
    {
        value = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var found)) return false;
        switch (found.ValueKind)
        {
            case JsonValueKind.String:
                value = (found.GetString() ?? string.Empty).Trim();
                return value.Length > 0;
            case JsonValueKind.Number:
                value = found.GetRawText();
                return true;
            default:
                return false;
        }
    }

    private static string? OptionalText(JsonElement arguments, string name) =>
        TryReadText(arguments, name, out var text) ? text : null;

    /// <summary>
    /// A field that may legitimately be empty — a card, a title. Empty is the platform's "remove it", so the only thing
    /// that counts as missing here is the field itself; <see cref="TryReadText"/> would have turned "" into a typo.
    /// </summary>
    private static bool TryReadEmptyable(JsonElement arguments, string name, out string value)
    {
        value = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var found) ||
            found.ValueKind != JsonValueKind.String)
            return false;
        value = (found.GetString() ?? string.Empty).Trim();
        return true;
    }

    private static bool? OptionalFlag(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var found)) return null;
        return found.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when found.TryGetInt64(out var number) => number != 0,
            JsonValueKind.String => (found.GetString() ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on" => true,
                "false" or "0" or "no" or "off" => false,
                _ => null
            },
            _ => null
        };
    }

    private static long? OptionalInteger(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var found)) return null;
        return found.ValueKind switch
        {
            JsonValueKind.Number when found.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse((found.GetString() ?? string.Empty).Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    // ---- reading the platform's `data` -----------------------------------------------------------

    /// <summary>
    /// A number out of the platform's answer, including the bridges that spell ids as strings (NapCat does this in
    /// places). Reading both is the difference between "the list is empty" and "I did not recognise the field".
    /// </summary>
    private static long? LongOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var found)) return null;
        return found.ValueKind switch
        {
            JsonValueKind.Number when found.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse((found.GetString() ?? string.Empty).Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? TextOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var found) &&
        found.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(found.GetString()) ? null : found.GetString()!.Trim())
            : null;

    private static bool? BoolOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var found)
            ? found.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static Result<JsonElement> MissingResult(string name, string what) =>
        Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"需要 '{name}'：{what}"));

    private static Result<JsonElement> InvalidResult(string name, string what) =>
        Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"'{name}' 不合法：{what}"));
}
