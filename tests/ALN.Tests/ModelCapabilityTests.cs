using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Tests;

/// <summary>
/// What a <b>model</b> can do, as opposed to what its provider's protocol can say.
///
/// The distinction is the whole point of these tests. <c>ModelCapabilities.Vision</c> existed from the start and nothing
/// read it, which is the same as not having it: a provider descriptor cannot tell a model that reads pictures from one
/// that answers 400 to them, because one endpoint serves both. So the declaration moved to the profile, where somebody
/// can name the model they actually run — and where the absence of a declaration is read as "no", not as "probably".
/// </summary>
public sealed class ModelCapabilityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_profile_that_says_nothing_cannot_see_pictures()
    {
        var profile = new ModelProfile("default", new Address("model.openai-chat#one"), "deepseek-chat");

        Assert.Null(profile.Capabilities);
        // The safe direction: an undeclared model gets a receipt instead of an image. Being wrong here costs a sentence;
        // being wrong the other way costs a failed turn, and then every turn after it, because the history still has the
        // picture in it.
        Assert.False(profile.CanSeeImages);
    }

    [Fact]
    public void Capabilities_are_written_as_names_and_read_back_the_same_way()
    {
        var profile = new ModelProfile("vision", new Address("model.openai-chat#one"), "gpt-4o",
            Capabilities: ModelCapabilities.Streaming | ModelCapabilities.ToolCalls | ModelCapabilities.Vision);

        var json = JsonSerializer.Serialize(profile, Json);
        Assert.Contains("\"capabilities\":\"Streaming, ToolCalls, Vision\"", json, StringComparison.Ordinal);

        var restored = JsonSerializer.Deserialize<ModelProfile>(json, Json)!;
        Assert.Equal(profile.Capabilities, restored.Capabilities);
        Assert.True(restored.CanSeeImages);
    }

    [Fact]
    public void A_number_is_still_accepted_because_an_older_file_may_carry_one()
    {
        var restored = JsonSerializer.Deserialize<ModelProfile>(
            """{"id":"p","provider":{"module":"model","kind":"openai-chat","instance":"one"},"modelId":"m","capabilities":6}""", Json)!;

        Assert.Equal(ModelCapabilities.ToolCalls | ModelCapabilities.Vision, restored.Capabilities);
        Assert.True(restored.CanSeeImages);
    }

    [Fact]
    public void A_capability_nobody_defined_is_refused_rather_than_ignored()
    {
        // A typo here would otherwise mean "this model cannot see pictures" on a model that can — the failure would show
        // up as the agent describing an image it was never shown.
        var bad = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModelProfile>(
            """{"id":"p","provider":{"module":"model","kind":"openai-chat","instance":"one"},"modelId":"m","capabilities":"Streaming, Visoin"}""", Json));
        // The failure names the word it did not understand, one level down: STJ wraps whatever a converter throws.
        Assert.Contains("Visoin", (bad.InnerException?.Message ?? bad.Message), StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_round_trips_through_the_module_file_that_holds_it()
    {
        // The profile lives in modules/model.openai-chat#<instance>/module.json under "profiles"; the loader hands the
        // JSON to the same serializer, so this is the shape an operator edits.
        var spec = JsonSerializer.Deserialize<ModelInstanceSpec>(
            """
            {"configuration":{"apiKey":"***"},"profiles":[
              {"id":"default","provider":{"module":"model","kind":"openai-chat","instance":"one"},"modelId":"deepseek-chat"},
              {"id":"eyes","provider":{"module":"model","kind":"openai-chat","instance":"one"},"modelId":"gpt-4o","capabilities":"Streaming, Vision"}]}
            """, Json)!;

        Assert.False(spec.Profiles![0].CanSeeImages);
        Assert.True(spec.Profiles[1].CanSeeImages);
        Assert.Equal("gpt-4o", spec.Profiles[1].ModelId);
    }
}
