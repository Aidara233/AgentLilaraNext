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
}
