using System.Text.Json;
using ALN.Adapter.Qq;

namespace ALN.Tests;

/// <summary>
/// The OneBot mapping rules, tested without a socket. These are the rules that decide whether the bot
/// answers the right person in the right group, or answers itself forever — worth pinning down before
/// anything connects to a real account.
/// </summary>
public sealed class OneBotProtocolTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string GroupMessage = """
        {
          "post_type": "message",
          "message_type": "group",
          "sub_type": "normal",
          "self_id": 10000,
          "group_id": 123456,
          "user_id": 20000,
          "message_id": 4242,
          "time": 1730000000,
          "sender": { "user_id": 20000, "nickname": "阿蓝", "card": "蓝" },
          "message": [
            { "type": "text", "data": { "text": "在吗 " } },
            { "type": "at", "data": { "qq": "10000" } },
            { "type": "image", "data": { "file": "a.png" } },
            { "type": "face", "data": { "id": "1" } }
          ],
          "raw_message": "[CQ:at,qq=10000]"
        }
        """;

    [Fact]
    public void A_group_message_becomes_an_envelope_with_visible_placeholders()
    {
        var inbound = OneBotProtocol.ReadMessage(Parse(GroupMessage), selfId: 10000);

        Assert.NotNull(inbound);
        Assert.Equal("group", inbound!.Platform);
        Assert.Equal("group-123456", inbound.ConversationId);
        Assert.Equal("4242", inbound.MessageId);
        Assert.Equal("20000", inbound.SenderId);
        Assert.Equal("蓝", inbound.SenderName);
        // Image and face do not disappear: the model should know something it cannot read is there.
        Assert.Equal("在吗 @10000[图片][表情]", inbound.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1730000000), inbound.At);
    }

    [Fact]
    public void A_private_message_becomes_a_p_conversation_named_after_the_sender()
    {
        var privateMessage = """
            {
              "post_type": "message", "message_type": "private", "sub_type": "friend",
              "self_id": 10000, "user_id": 1664093638, "message_id": 11, "time": 1730000000,
              "sender": { "user_id": 1664093638, "nickname": "对方" },
              "message": [{ "type": "text", "data": { "text": "私聊测试" } }]
            }
            """;

        var inbound = OneBotProtocol.ReadMessage(Parse(privateMessage), selfId: 10000);

        Assert.NotNull(inbound);
        Assert.Equal("private", inbound!.Platform);
        Assert.Equal("user-1664093638", inbound.ConversationId);
        Assert.Equal("1664093638", inbound.SenderId);
    }

    [Fact]
    public void The_bots_own_messages_never_enter_as_user_input()
    {
        // message_sent is how an echo arrives; the sender check covers the same message arriving as
        // "message" when a client does that instead.
        var sent = GroupMessage.Replace("\"post_type\": \"message\"", "\"post_type\": \"message_sent\"");
        Assert.Null(OneBotProtocol.ReadMessage(Parse(sent), selfId: 10000));

        var asSelf = GroupMessage.Replace("\"user_id\": 20000", "\"user_id\": 10000");
        Assert.Null(OneBotProtocol.ReadMessage(Parse(asSelf), selfId: 10000));
    }

    [Theory]
    [InlineData("\"post_type\": \"message\"", "\"post_type\": \"notice\"")]
    [InlineData("\"post_type\": \"message\"", "\"post_type\": \"meta_event\"")]
    [InlineData("\"post_type\": \"message\"", "\"post_type\": \"request\"")]
    public void Events_outside_scope_are_ignored_rather_than_failing(string original, string replacement)
    {
        Assert.Null(OneBotProtocol.ReadMessage(Parse(GroupMessage.Replace(original, replacement)), selfId: 10000));
    }

    [Fact]
    public void A_message_with_nothing_but_a_quote_is_not_a_message()
    {
        var quoted = """
            {
              "post_type": "message", "message_type": "group", "group_id": 1, "user_id": 2, "message_id": 3,
              "message": [{ "type": "reply", "data": { "id": "99" } }]
            }
            """;
        Assert.Null(OneBotProtocol.ReadMessage(Parse(quoted), selfId: 10000));
    }

    [Fact]
    public void Cq_code_strings_are_flattened_the_same_way_as_segments()
    {
        var text = """
            {
              "post_type": "message", "message_type": "group", "group_id": 1, "user_id": 2, "message_id": 3,
              "message": "&lt;hi&gt; [CQ:at,qq=all] [CQ:image,file=x.jpg]"
            }
            """;
        var inbound = OneBotProtocol.ReadMessage(Parse(text), selfId: 10000);
        Assert.Equal("<hi> @全体成员 [图片]", inbound!.Text);
    }

    [Fact]
    public void An_at_segment_prefers_the_display_name()
    {
        var withName = """
            {
              "post_type": "message", "message_type": "group", "group_id": 1, "user_id": 2, "message_id": 3,
              "message": [{ "type": "at", "data": { "qq": "777", "name": "小明" } }]
            }
            """;
        Assert.Equal("@小明", OneBotProtocol.ReadMessage(Parse(withName), selfId: 10000)!.Text);
    }

    [Theory]
    [InlineData("group-123456", true, 123456L, "group")]
    [InlineData("group-1", true, 1L, "group")]
    [InlineData("user-1664093638", true, 1664093638L, "private")]
    [InlineData("123456", false, 0L, "")]
    [InlineData("x-123456", false, 0L, "")]
    [InlineData("group-", false, 0L, "")]
    [InlineData("group-abc", false, 0L, "")]
    [InlineData(null, false, 0L, "")]
    public void Conversation_ids_round_trip_and_reject_anything_else(string? conversationId, bool ok, long id,
        string platform)
    {
        Assert.Equal(ok, OneBotProtocol.TryReadConversation(conversationId, out var parsed, out var parsedPlatform));
        if (!ok) return;
        Assert.Equal(id, parsed);
        Assert.Equal(platform, parsedPlatform);
    }

    [Fact]
    public void A_send_is_a_text_segment_and_quotes_only_a_numeric_id()
    {
        var plain = Parse(OneBotProtocol.BuildSend("echo-1", 123456, "group", "你好"));
        Assert.Equal("send_group_msg", plain.GetProperty("action").GetString());
        Assert.Equal("echo-1", plain.GetProperty("echo").GetString());
        var parameters = plain.GetProperty("params");
        Assert.Equal(123456, parameters.GetProperty("group_id").GetInt64());
        var segments = parameters.GetProperty("message").EnumerateArray().ToArray();
        Assert.Single(segments);
        Assert.Equal("text", segments[0].GetProperty("type").GetString());
        Assert.Equal("你好", segments[0].GetProperty("data").GetProperty("text").GetString());

        var quoted = Parse(OneBotProtocol.BuildSend("echo-2", 1, "group", "回你", "4242"));
        var quotedSegments = quoted.GetProperty("params").GetProperty("message").EnumerateArray().ToArray();
        Assert.Equal(2, quotedSegments.Length);
        Assert.Equal("reply", quotedSegments[0].GetProperty("type").GetString());
        Assert.Equal("4242", quotedSegments[0].GetProperty("data").GetProperty("id").GetString());

        // A non-numeric id would be rejected by the platform; dropping the quote is better than failing the send.
        var notQuoted = Parse(OneBotProtocol.BuildSend("echo-3", 1, "group", "回你", "abc"));
        Assert.Single(notQuoted.GetProperty("params").GetProperty("message").EnumerateArray());
    }

    [Fact]
    public void A_private_send_targets_the_user_not_a_group()
    {
        var privateSend = Parse(OneBotProtocol.BuildSend("echo-p", 1664093638, "private", "私聊回你"));

        Assert.Equal("send_private_msg", privateSend.GetProperty("action").GetString());
        var parameters = privateSend.GetProperty("params");
        Assert.Equal(1664093638, parameters.GetProperty("user_id").GetInt64());
        Assert.False(parameters.TryGetProperty("group_id", out _));
    }

    [Fact]
    public void Mentions_self_is_decided_by_the_adapter_not_by_reading_the_text()
    {
        var atSelf = """
            {
              "post_type": "message", "message_type": "group", "self_id": 10000, "group_id": 1,
              "user_id": 2, "message_id": 3,
              "message": [
                { "type": "at", "data": { "qq": "10000" } },
                { "type": "text", "data": { "text": " 在吗" } }
              ]
            }
            """;
        var atAll = atSelf.Replace("\"qq\": \"10000\"", "\"qq\": \"all\"");
        var atSomebodyElse = atSelf.Replace("\"qq\": \"10000\"", "\"qq\": \"7\"");
        var plain = """
            {
              "post_type": "message", "message_type": "group", "self_id": 10000, "group_id": 1,
              "user_id": 2, "message_id": 3,
              "message": [{ "type": "text", "data": { "text": "今天下雨" } }]
            }
            """;

        Assert.True(OneBotProtocol.ReadMessage(Parse(atSelf), selfId: 10000)!.MentionsSelf);
        // Addressing the whole group is not addressing us: a valve that treats it as ours would answer
        // every announcement in the group.
        Assert.False(OneBotProtocol.ReadMessage(Parse(atAll), selfId: 10000)!.MentionsSelf);
        Assert.False(OneBotProtocol.ReadMessage(Parse(atSomebodyElse), selfId: 10000)!.MentionsSelf);
        Assert.False(OneBotProtocol.ReadMessage(Parse(plain), selfId: 10000)!.MentionsSelf);
        // Without knowing our own id there is nothing honest to answer.
        Assert.False(OneBotProtocol.ReadMessage(Parse(atSelf), selfId: null)!.MentionsSelf);

        // CQ-string form has to agree with the segment form.
        var cq = """
            {
              "post_type": "message", "message_type": "group", "self_id": 10000, "group_id": 1,
              "user_id": 2, "message_id": 3, "message": "[CQ:at,qq=10000] 在吗"
            }
            """;
        Assert.True(OneBotProtocol.ReadMessage(Parse(cq), selfId: 10000)!.MentionsSelf);
    }

    [Fact]
    public void Login_info_gives_back_the_account_the_platform_says_we_are()
    {
        var login = OneBotProtocol.ReadLoginInfo(Parse("""{ "user_id": 1664093638, "nickname": "Lilara" }"""));

        Assert.NotNull(login);
        Assert.Equal(1664093638, login!.UserId);
        Assert.Equal("Lilara", login.Nickname);

        // Nothing usable: no id, so nothing to learn. The caller keeps whatever it had.
        Assert.Null(OneBotProtocol.ReadLoginInfo(Parse("""{ "nickname": "x" }""")));
        Assert.Null(OneBotProtocol.ReadLoginInfo(Parse("""{ "user_id": 0 }""")));
        Assert.Null(OneBotProtocol.ReadLoginInfo(Parse("""[]""")));
    }

    [Theory]
    [InlineData("""{ "status": "ok", "retcode": 0, "data": { "message_id": 7 } }""", true, "7")]
    [InlineData("""{ "status": "async", "retcode": 1 }""", false, null)]
    [InlineData("""{ "status": "ok", "retcode": 0 }""", true, null)]
    [InlineData("""{ "status": "failed", "retcode": 100, "message": "被禁言" }""", false, null)]
    public void Action_responses_are_read_as_ok_or_not(string json, bool ok, string? messageId)
    {
        var response = OneBotProtocol.ReadResponse(Parse(json));
        Assert.NotNull(response);
        Assert.Equal(ok, response!.Ok);
        Assert.Equal(messageId, response.MessageId);
    }

    [Fact]
    public void The_self_id_is_picked_up_from_a_heartbeat()
    {
        var heartbeat = """
            { "post_type": "meta_event", "meta_event_type": "heartbeat", "self_id": 10000, "status": { "online": true } }
            """;
        Assert.Equal(10000, OneBotProtocol.ReadSelfId(Parse(heartbeat)));
        Assert.Null(OneBotProtocol.ReadMessage(Parse(heartbeat), selfId: null));
    }

    [Fact]
    public void A_senders_name_is_the_group_card_first_and_the_nickname_second()
    {
        // Two wrappers, one rule: the same question is asked about a pushed event's sender object and about a
        // get_group_member_info answer, so both go through the same reading.
        Assert.Equal("蓝", OneBotProtocol.ReadMemberName(Parse("""{ "user_id": 20000, "nickname": "阿蓝", "card": "蓝" }""")));
        Assert.Equal("阿蓝", OneBotProtocol.ReadMemberName(Parse("""{ "user_id": 20000, "nickname": "阿蓝", "card": "" }""")));
        Assert.Equal("阿蓝", OneBotProtocol.ReadMemberName(Parse("""{ "user_id": 20000, "nickname": "阿蓝", "card": "  " }""")));
        Assert.Null(OneBotProtocol.ReadMemberName(Parse("""{ "user_id": 20000 }""")));
        Assert.Null(OneBotProtocol.ReadMemberName(Parse("""[]""")));

        Assert.Equal("阿蓝", OneBotProtocol.ReadStrangerName(Parse("""{ "user_id": 20000, "nickname": "阿蓝" }""")));
        Assert.Null(OneBotProtocol.ReadStrangerName(Parse("""{ "user_id": 20000, "nickname": " " }""")));
        Assert.Null(OneBotProtocol.ReadStrangerName(Parse("""{ "status": "failed" }""")));
    }

    [Fact]
    public void A_friend_list_is_read_for_the_remark_this_account_gave_each_person()
    {
        // The remark is what makes a transcript readable: "小明" says who they are to themselves, "老王" says who they
        // are to us. Rows that cannot be tied to an account are dropped rather than kept as nameless.
        var friends = OneBotProtocol.ReadFriendList(Parse("""
            [
              { "user_id": 20000, "nickname": "小明", "remark": "老王" },
              { "user_id": 20001, "nickname": "小红", "remark": "" },
              { "user_id": 20002, "nickname": "  ", "remark": " 旧友 " },
              { "nickname": "没有 id", "remark": "无名" },
              { "user_id": 0, "nickname": "零" },
              "不是一个对象"
            ]
            """));

        Assert.Equal(3, friends.Count);
        Assert.Equal(new OneBotFriend(20000, "小明", "老王"), friends[0]);
        Assert.Equal(new OneBotFriend(20001, "小红", null), friends[1]);      // an empty remark is not a remark
        Assert.Equal(new OneBotFriend(20002, null, "旧友"), friends[2]);      // trimmed, and a blank nickname is null
        Assert.Empty(OneBotProtocol.ReadFriendList(Parse("""{ "status": "ok" }""")));
    }

    [Theory]
    [InlineData("get_friend_list", null)]
    [InlineData("get_group_member_info", 20000L)]
    [InlineData("get_stranger_info", 20000L)]
    public void The_name_actions_carry_the_ids_they_have_to(string action, long? id)
    {
        const string echo = "e1";
        var json = action switch
        {
            "get_friend_list" => OneBotProtocol.BuildFriendList(echo),
            "get_group_member_info" => OneBotProtocol.BuildGroupMemberInfo(echo, 123456, 20000),
            _ => OneBotProtocol.BuildStrangerInfo(echo, 20000)
        };
        var root = Parse(json);

        Assert.Equal(action, root.GetProperty("action").GetString());
        Assert.Equal(echo, root.GetProperty("echo").GetString());
        var parameters = root.GetProperty("params");
        if (id is { } expected) Assert.Equal(expected, parameters.GetProperty("user_id").GetInt64());
        if (action == "get_group_member_info") Assert.Equal(123456, parameters.GetProperty("group_id").GetInt64());
        if (action != "get_friend_list") Assert.False(parameters.GetProperty("no_cache").GetBoolean());
    }

    [Fact]
    public void A_raw_action_carries_the_verb_and_the_parameters_untouched()
    {
        var parameters = Parse("""{"group_id": 123456, "user_id": 654321}""");
        var built = Parse(OneBotProtocol.BuildRaw("echo-1", "set_group_ban", parameters));

        Assert.Equal("set_group_ban", built.GetProperty("action").GetString());
        Assert.Equal("echo-1", built.GetProperty("echo").GetString());
        Assert.Equal(123456, built.GetProperty("params").GetProperty("group_id").GetInt64());
        // Untouched means untouched: no renaming, no filtering, no defaults invented on the way.
        Assert.Equal(2, built.GetProperty("params").EnumerateObject().Count());
    }

    [Fact]
    public void A_reply_with_no_data_is_an_answer_with_a_null_data_not_an_exception()
    {
        // Half of OneBot's verbs answer with a status and nothing else — a poke, a set_*. The reply's `data` is then a
        // default JsonElement, and writing one of those throws "Operation is not valid due to the current state of the
        // object": a successful poke arrived at the model as a failure, which is exactly how a poke gets retried four
        // times with four different parameter shapes.
        var answered = OneBotProtocol.BuildRawAnswer(new OneBotResponse(true, null, 0, null), default);

        Assert.True(answered.GetProperty("ok").GetBoolean());
        Assert.Equal(0, answered.GetProperty("retcode").GetInt32());
        Assert.Equal(JsonValueKind.Null, answered.GetProperty("data").ValueKind);

        // And when there is data, it goes through whole — retcode and message included, because those are the platform's
        // own words about what happened.
        var withData = OneBotProtocol.BuildRawAnswer(OneBotResponseOf("""{"retcode":1200,"message":"消息体无法解析","data":{"x":1}}"""),
            Parse("""{"x":1}"""));
        Assert.Equal(1200, withData.GetProperty("retcode").GetInt32());
        Assert.Equal("消息体无法解析", withData.GetProperty("message").GetString());
        Assert.Equal(1, withData.GetProperty("data").GetProperty("x").GetInt32());
    }

    private static OneBotResponse OneBotResponseOf(string json)
    {
        var root = Parse(json);
        return new OneBotResponse(root.GetProperty("retcode").GetInt32() == 0,
            root.TryGetProperty("message_id", out var id) ? id.ToString() : null,
            root.GetProperty("retcode").GetInt32(),
            root.TryGetProperty("message", out var message) ? message.GetString() : null);
    }
}
