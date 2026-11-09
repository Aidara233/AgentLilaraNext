using System.Text.Json;
using ALN.Contracts.Models;
using ALN.Model.OpenAIChat;

namespace ALN.Tests;

/// <summary>
/// The wire shape of one chat message.
///
/// This exists because a real round against DeepSeek failed with "messages[14]: missing field `content`": the
/// OpenAI shape allows an assistant message that only called tools to omit the field, DeepSeek does not. That
/// message is not an edge case — a session that asks to wait says nothing and calls a tool, so the very first
/// replay of such a turn broke. The unit test is here so the next person sees the rule without a 422.
/// </summary>
public sealed class ChatPayloadTests
{
    [Fact]
    public void A_tool_call_with_no_text_still_sends_content()
    {
        var message = new ModelMessage("assistant", null, ToolCalls: [new ModelToolCall("call-1", "wait", "{}")]);
        var payload = ChatPayload.Message(message);

        Assert.Equal("assistant", payload["role"]);
        Assert.True(payload.ContainsKey("content"), "content must be present even when the turn said nothing");
        Assert.Equal("", payload["content"]);
        Assert.NotNull(payload["tool_calls"]);
    }

    [Fact]
    public void Every_message_in_a_request_carries_the_field()
    {
        ModelMessage[] messages =
        [
            new("system", "你是助手"),
            new("user", "记一下：部署改到周五了"),
            new("assistant", null, ToolCalls: [new ModelToolCall("call-1", "remember", "{\"text\":\"周五部署\",\"kind\":\"fragment\"}")]),
            new("tool", "记下了 mem-1。", ToolCallId: "call-1", Name: "remember"),
            new("assistant", "", ToolCalls: [new ModelToolCall("call-2", "wait", "{}")])
        ];

        var json = JsonSerializer.Serialize(messages.Select(ChatPayload.Message), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(messages.Length, items.Count);
        foreach (var item in items)
            Assert.True(item.TryGetProperty("content", out _), $"a {item.GetProperty("role").GetString()} message lost its content field");

        // The tool message keeps the id the call it answers had, or the provider cannot match them up.
        Assert.Equal("call-1", items[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("remember", items[3].GetProperty("name").GetString());
    }

    [Fact]
    public void A_message_without_parts_still_sends_a_plain_string()
    {
        // The regression that matters: pictures must not change the shape of an ordinary request. A proxy in front of a
        // provider may understand only the string form, and "it worked before" is the cheapest compatibility there is.
        var payload = ChatPayload.Message(new ModelMessage("user", "看图", Parts: null));
        Assert.IsType<string>(payload["content"]);
        Assert.Equal("看图", payload["content"]);

        // An empty Parts list is the same fact as no parts — nothing to say beyond the text.
        var empty = ChatPayload.Message(new ModelMessage("user", "看图", Parts: []));
        Assert.Equal("看图", empty["content"]);
    }

    [Fact]
    public void A_message_with_parts_sends_a_content_array_in_order()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var message = new ModelMessage("user", null, Parts:
        [
            new TextPart("这张图里写了什么？"),
            new ImagePart("image/png", png, 1920, 1080, "截图")
        ]);

        var payload = ChatPayload.Message(message);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var parts = document.RootElement.GetProperty("content").EnumerateArray().ToList();

        Assert.Equal(2, parts.Count);
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("这张图里写了什么？", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        // A data URL, so the bytes are in the request and the provider needs nothing from this machine.
        var url = parts[1].GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url, StringComparison.Ordinal);
        Assert.Equal(png, Convert.FromBase64String(url["data:image/png;base64,".Length..]));
    }

    [Fact]
    public void A_part_survives_a_round_trip_through_json_because_scripts_hand_requests_over_that_way()
    {
        // Graph scripts can build a request as JSON; if the polymorphic shape did not round-trip, pictures would be a
        // feature only the host could use.
        var original = new ModelMessage("user", null, Parts:
        [
            new TextPart("看"),
            new ImagePart("image/jpeg", new byte[] { 0xFF, 0xD8 }, 10, 20)
        ]);

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<ModelMessage>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var parts = restored.Parts!;
        Assert.Equal("看", Assert.IsType<TextPart>(parts[0]).Text);
        var image = Assert.IsType<ImagePart>(parts[1]);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, image.Bytes.ToArray());
        Assert.Equal((10, 20), (image.Width, image.Height));
    }
}
