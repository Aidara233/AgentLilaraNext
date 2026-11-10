using System.Text.Json;
using ALN.Contracts;
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

    [Fact]
    public void A_layout_becomes_segments_in_order_with_the_quote_first()
    {
        var picture = new MediaRef(FileScopes.Media, "image/ab/x.png", MediaKind.Image, "image/png", 33, 4, 3, "ab");
        var segments = OneBotProtocol.BuildOutboundSegments(
            [new TextBlock("看 "), new MentionBlock("20000"), new TextBlock("："), new FaceBlock("1"), new ImageBlock(picture)],
            replyTo: "4242", imageUri: _ => "base64://QUJD");

        var root = JsonDocument.Parse(JsonSerializer.Serialize(segments)).RootElement;
        var kinds = root.EnumerateArray().Select(segment => segment.GetProperty("type").GetString()).ToArray();
        // The quote is outermost, then the layout exactly as it was written.
        Assert.Equal(new[] { "reply", "text", "at", "text", "face", "image" }, kinds);
        Assert.Equal("4242", root[0].GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("看 ", root[1].GetProperty("data").GetProperty("text").GetString());
        Assert.Equal("20000", root[2].GetProperty("data").GetProperty("qq").GetString());
        Assert.Equal("：", root[3].GetProperty("data").GetProperty("text").GetString());
        Assert.Equal("1", root[4].GetProperty("data").GetProperty("id").GetString());
        // A picture is bytes on the wire, not a path a bridge on another machine could not open.
        Assert.Equal("base64://QUJD", root[5].GetProperty("data").GetProperty("file").GetString());
    }

    [Fact]
    public void A_picture_that_could_not_be_read_still_takes_its_place_as_a_sentence()
    {
        // The caller resolves the bytes (it holds the file port). Null means "I could not read it", and the message must
        // still arrive with the part visible — a silently missing picture is one the model believes it sent.
        var picture = new MediaRef(FileScopes.Media, "image/ab/x.png", MediaKind.Image, "image/png", 33, 4, 3, "ab");
        var segments = OneBotProtocol.BuildOutboundSegments([new TextBlock("看"), new ImageBlock(picture)], null,
            imageUri: _ => null);

        var root = JsonDocument.Parse(JsonSerializer.Serialize(segments)).RootElement;
        Assert.Equal(2, root.GetArrayLength());
        Assert.All(root.EnumerateArray(), segment => Assert.Equal("text", segment.GetProperty("type").GetString()));
        Assert.Equal("[图片：media/image/ab/x.png]", root[1].GetProperty("data").GetProperty("text").GetString());
    }

    [Fact]
    public void A_message_that_is_only_a_reply_still_sends_the_text_view()
    {
        // OneBot rejects an empty message array, and an empty bubble is not what anybody meant: the flattening goes
        // rather than a refusal nobody can act on.
        var segments = OneBotProtocol.BuildOutboundSegments([new TextBlock("")], replyTo: "4242");

        var root = JsonDocument.Parse(JsonSerializer.Serialize(segments)).RootElement;
        Assert.Equal(2, root.GetArrayLength());
        Assert.Equal("text", root[1].GetProperty("type").GetString());
    }

    // ---- pictures ----------------------------------------------------------------------------------------------

    [Fact]
    public void An_image_segment_says_where_the_picture_is_without_fetching_it()
    {
        // The protocol layer's whole job here: read where it is. Fetching is the Adapter's, storing is the Channel's.
        var images = OneBotProtocol.ReadImages(Parse("""
            {"post_type":"message","message":[
              {"type":"image","data":{"file":"abc.image","url":"https://gchat.qpic.cn/x?term=2","sub_type":1}},
              {"type":"text","data":{"text":"看"}},
              {"type":"image","data":{"file":"no-url.png"}}]}
            """));

        Assert.Equal(2, images.Count);
        Assert.Equal("https://gchat.qpic.cn/x?term=2", images[0].Url);
        Assert.Equal("abc.image", images[0].File);
        // A segment with no url is still reported: the Adapter says it could not be fetched rather than pretending the
        // message had one picture when it had two.
        Assert.Null(images[1].Url);
        Assert.Equal("no-url.png", images[1].File);
    }

    [Fact]
    public void The_cq_code_form_is_read_too_because_the_format_is_configuration()
    {
        // message_format=string is a deployment setting; the same bridge can switch it. The url arrives HTML-escaped
        // ("&amp;" for "&"), and left escaped it is not a url at all.
        var images = OneBotProtocol.ReadImages(Parse("""
            {"post_type":"message","message":"[CQ:image,file=b.jpg,url=https://gchat.qpic.cn/y?a=1&amp;b=2] 看图"}
            """));

        var image = Assert.Single(images);
        Assert.Equal("https://gchat.qpic.cn/y?a=1&b=2", image.Url);
        Assert.Equal("b.jpg", image.File);
    }

    [Fact]
    public void A_message_without_pictures_reports_none()
    {
        Assert.Empty(OneBotProtocol.ReadImages(Parse(GroupMessage.Replace(
            """{ "type": "image", "data": { "file": "a.png" } },""", ""))));
        // And the text still carries "[图片]" when there is one, so a model that gets no bytes still learns something
        // was there (see ReadMessage → Narrate).
        Assert.Equal("[图片]", OneBotProtocol.ReadMessage(Parse("""
            {"post_type":"message","message_type":"group","self_id":10000,"group_id":1,"user_id":20000,
             "message_id":1,"time":1730000000,"message":[{"type":"image","data":{"file":"a.png"}}]}
            """), selfId: 10000)!.Text);
    }

    [Fact]
    public void A_flattened_array_does_not_hide_the_raw_CQ_code()
    {
        // The shape that made this silent: the bridge degrades the segment to the text "[图片]" in the array, while
        // raw_message still holds the picture. ReadText falls through to raw_message and prints "[图片]"; ReadImages
        // used to return at the array and find nothing — so the text said a picture arrived and no bytes ever did.
        var images = OneBotProtocol.ReadImages(Parse("""
            {"post_type":"message","message":[{"type":"text","data":{"text":"[图片]"}}],
             "raw_message":"[CQ:image,file=c.jpg,url=https://gchat.qpic.cn/z]"}
            """));

        var image = Assert.Single(images);
        Assert.Equal("c.jpg", image.File);
        Assert.Equal("https://gchat.qpic.cn/z", image.Url);
    }

    [Fact]
    public void One_picture_described_twice_is_fetched_once()
    {
        // Both spellings naming the same picture must not become two downloads: the file name is what the platform
        // calls it, so that is what decides, and a CQ code that omits the url still matches its segment.
        var images = OneBotProtocol.ReadImages(Parse("""
            {"post_type":"message","message":[{"type":"image","data":{"file":"d.png","url":"https://gchat.qpic.cn/d"}}],
             "raw_message":"[CQ:image,file=d.png]"}
            """));

        Assert.Single(images);
    }

    [Fact]
    public void The_shape_of_an_event_names_the_segment_types_and_nothing_else()
    {
        // The diagnostic that turns "the model cannot see pictures" into "the bridge sent the word". Only type names:
        // a url or a file name in a log line is a copy of something that did not need copying.
        var shape = OneBotProtocol.ShapeOf(Parse("""
            {"post_type":"message","message":[{"type":"text","data":{"text":"[图片]"}}],
             "raw_message":"[CQ:image,file=secret-name.jpg,url=https://gchat.qpic.cn/secret]"}
            """));

        Assert.Contains("message: [text]", shape, StringComparison.Ordinal);
        Assert.Contains("raw_message: image", shape, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", shape, StringComparison.Ordinal);
        // "No message at all" and "a message with no CQ in it" are different answers, and both are worth telling apart.
        Assert.Equal("message: absent; raw_message: absent", OneBotProtocol.ShapeOf(Parse("{}")));
        Assert.Equal("message: no CQ; raw_message: absent", OneBotProtocol.ShapeOf(Parse("""{"message":"纯文字"}""")));
    }

    // ---- files: a pointer, not bytes ---------------------------------------------------------------

    [Fact]
    public void A_file_segment_carries_the_handle_that_fetches_it()
    {
        // The bytes are not in the message and are not fetched here: what a model needs is the name, the size, and the
        // handle `files_get` takes — a bare "[文件]" would leave it with something it cannot name (docs/28 §6.8).
        var inbound = OneBotProtocol.ReadMessage(Parse("""
            {"post_type":"message","message_type":"group","self_id":10000,"group_id":616565017,"user_id":20000,
             "message_id":7,"time":1730000000,
             "message":[{"type":"text","data":{"text":"看看这个 "}},
                        {"type":"file","data":{"file":"周报.zip","file_id":"abc123","file_size":2411724,"busid":0}}]}
            """), selfId: 10000);

        Assert.NotNull(inbound);
        Assert.Contains("看看这个", inbound!.Text, StringComparison.Ordinal);
        Assert.Contains("周报.zip", inbound.Text, StringComparison.Ordinal);
        Assert.Contains("2.3 MB", inbound.Text, StringComparison.Ordinal);
        Assert.Contains("f:0|abc123", inbound.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_file_upload_arrives_as_a_notice_with_no_message_id()
    {
        // On this platform a file in the group's file area is a notice, not a message: nothing appears in the chat, and
        // there is no message_id to dedupe on. The envelope still has to say what arrived and how to get it.
        var inbound = OneBotProtocol.ReadMessage(Parse("""
            {"post_type":"notice","notice_type":"group_upload","self_id":10000,"group_id":616565017,
             "user_id":20000,"time":1730000000,
             "file":{"id":"xyz789","name":"成片.mp4","size":104857600,"busid":0}}
            """), selfId: 10000);

        Assert.NotNull(inbound);
        Assert.Null(inbound!.MessageId);
        Assert.Equal("group-616565017", inbound.ConversationId);
        Assert.Equal("20000", inbound.SenderId);
        Assert.Contains("上传了文件", inbound.Text, StringComparison.Ordinal);
        Assert.Contains("成片.mp4", inbound.Text, StringComparison.Ordinal);
        Assert.Contains("100 MB", inbound.Text, StringComparison.Ordinal);
        Assert.Contains("f:0|xyz789", inbound.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_the_bridge_only_named_says_so_instead_of_inventing_a_handle()
    {
        var inbound = OneBotProtocol.ReadMessage(Parse("""
            {"post_type":"message","message_type":"group","self_id":10000,"group_id":1,"user_id":20000,
             "message_id":8,"time":1730000000,
             "message":[{"type":"file","data":{"name":"旧.zip"}}]}
            """), selfId: 10000);

        Assert.Contains("旧.zip", inbound!.Text, StringComparison.Ordinal);
        Assert.Contains("取不到", inbound.Text, StringComparison.Ordinal);
        // No handle at all: a made-up one would be a call that can only fail.
        Assert.DoesNotContain("f:", inbound.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_notices_are_still_ignored()
    {
        // A poke, a recall, somebody joining: none of them is something to say, and turning them into messages would make
        // the conversation noisier than the chat it mirrors.
        Assert.Null(OneBotProtocol.ReadMessage(Parse("""
            {"post_type":"notice","notice_type":"group_increase","self_id":10000,"group_id":1,"user_id":20000}
            """), selfId: 10000));
        Assert.Null(OneBotProtocol.ReadMessage(Parse("""
            {"post_type":"notice","notice_type":"group_upload","self_id":10000,"group_id":1,"user_id":20000}
            """), selfId: 10000));
    }
}
