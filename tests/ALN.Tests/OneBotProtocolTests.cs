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
        Assert.Equal(123456, inbound!.GroupId);
        Assert.Equal("g-123456", OneBotProtocol.GroupConversation(inbound.GroupId));
        Assert.Equal("4242", inbound.MessageId);
        Assert.Equal("20000", inbound.SenderId);
        Assert.Equal("蓝", inbound.SenderName);
        // Image and face do not disappear: the model should know something it cannot read is there.
        Assert.Equal("在吗 @10000[图片][表情]", inbound.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1730000000), inbound.At);
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
    [InlineData("\"message_type\": \"group\"", "\"message_type\": \"private\"")] // private: not in v1 scope
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
    [InlineData("g-123456", true, 123456)]
    [InlineData("g-1", true, 1)]
    [InlineData("123456", false, 0)]
    [InlineData("p-123456", false, 0)]
    [InlineData("g-", false, 0)]
    [InlineData("g-abc", false, 0)]
    [InlineData(null, false, 0)]
    public void Conversation_ids_round_trip_and_reject_anything_else(string? conversationId, bool ok, long groupId)
    {
        Assert.Equal(ok, OneBotProtocol.TryReadGroupConversation(conversationId, out var parsed));
        if (ok) Assert.Equal(groupId, parsed);
    }

    [Fact]
    public void A_send_is_a_text_segment_and_quotes_only_a_numeric_id()
    {
        var plain = Parse(OneBotProtocol.BuildGroupSend("echo-1", 123456, "你好"));
        Assert.Equal("send_group_msg", plain.GetProperty("action").GetString());
        Assert.Equal("echo-1", plain.GetProperty("echo").GetString());
        var parameters = plain.GetProperty("params");
        Assert.Equal(123456, parameters.GetProperty("group_id").GetInt64());
        var segments = parameters.GetProperty("message").EnumerateArray().ToArray();
        Assert.Single(segments);
        Assert.Equal("text", segments[0].GetProperty("type").GetString());
        Assert.Equal("你好", segments[0].GetProperty("data").GetProperty("text").GetString());

        var quoted = Parse(OneBotProtocol.BuildGroupSend("echo-2", 1, "回你", "4242"));
        var quotedSegments = quoted.GetProperty("params").GetProperty("message").EnumerateArray().ToArray();
        Assert.Equal(2, quotedSegments.Length);
        Assert.Equal("reply", quotedSegments[0].GetProperty("type").GetString());
        Assert.Equal("4242", quotedSegments[0].GetProperty("data").GetProperty("id").GetString());

        // A non-numeric id would be rejected by the platform; dropping the quote is better than failing the send.
        var notQuoted = Parse(OneBotProtocol.BuildGroupSend("echo-3", 1, "回你", "abc"));
        Assert.Single(notQuoted.GetProperty("params").GetProperty("message").EnumerateArray());
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
}
