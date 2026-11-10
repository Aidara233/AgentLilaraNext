using System.Text.Json;
using ALN.Contracts.Models;
using ALN.Model.OpenAIChat;

namespace ALN.Tests;

/// <summary>
/// The wire shape of one chat request, at the one place it used to throw.
///
/// A tool whose source did not give a parameter schema took the whole turn down: the schema was deserialized inside the
/// request builder, a bad one threw, and the session reported "Model request could not be mapped to OpenAI Chat" with
/// the parser's message and no mention of which tool did it. Every turn failed, including the ones that would never
/// have called that tool. What follows is the reason that cannot happen again.
/// </summary>
public sealed class ChatPayloadTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    public void A_schema_that_is_missing_or_not_an_object_becomes_the_empty_one(string? schema)
    {
        var parameters = ChatPayload.Parameters(schema);

        Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        // The empty object schema: a tool that takes nothing. Which is what a tool with no description of its
        // arguments takes.
        Assert.Equal(JsonValueKind.Object, parameters.GetProperty("properties").ValueKind);
        Assert.Empty(parameters.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public void A_schema_that_is_an_object_is_passed_through_untouched()
    {
        const string schema = """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}""";

        var parameters = ChatPayload.Parameters(schema);

        Assert.Equal(["url"], parameters.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["url"], parameters.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void A_tool_with_no_schema_is_still_offered_with_a_usable_one()
    {
        var tool = new ModelToolDefinition("script.without.schema", "A script that forgot to say what it takes.", "");

        var shaped = JsonSerializer.SerializeToElement(ChatPayload.Tool(tool), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("function", shaped.GetProperty("type").GetString());
        var function = shaped.GetProperty("function");
        Assert.Equal("script.without.schema", function.GetProperty("name").GetString());
        Assert.Equal("A script that forgot to say what it takes.", function.GetProperty("description").GetString());
        // Offered rather than dropped: the model can still call it, and a script that then says "I need a URL" is the
        // script saying so — which is where that belongs.
        Assert.Equal("object", function.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public void A_tool_that_does_have_a_schema_keeps_it()
    {
        var tool = new ModelToolDefinition("web_search", "Search.", """{"type":"object","properties":{"query":{"type":"string"}}}""");

        var shaped = JsonSerializer.SerializeToElement(ChatPayload.Tool(tool), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(shaped.GetProperty("function").GetProperty("parameters").GetProperty("properties")
            .TryGetProperty("query", out _));
    }
}
