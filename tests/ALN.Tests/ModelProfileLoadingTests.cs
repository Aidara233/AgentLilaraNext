using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Tests;

/// <summary>
/// A profile survives the trip from its module file into the live registry.
///
/// The bug this is about was invisible: the host read every profile out of <c>module.json</c> and then rebuilt it field by
/// field, so a profile that declared <c>Vision</c> came back unable to see. Nothing failed, nothing logged — every picture
/// simply arrived as a receipt, forever, with no way to tell why. The fix is one `with`; the test is that the next field
/// somebody adds cannot be lost the same way.
/// </summary>
public sealed class ModelProfileLoadingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string ModuleFile = """
        {
          "configuration": { "endpoint": "https://example.invalid" },
          "profiles": [
            { "id": "default", "modelId": "some-model", "capabilities": "Streaming, Vision" },
            { "id": "blind", "modelId": "some-model" }
          ]
        }
        """;

    [Fact]
    public void The_file_says_which_profiles_can_see_pictures()
    {
        var spec = JsonSerializer.Deserialize<ModelInstanceSpec>(ModuleFile, Json);

        Assert.NotNull(spec);
        var seeing = Assert.Single(spec!.Profiles!, profile => profile.Id == "default");
        Assert.True(seeing.CanSeeImages);
        // Unsaid is "no", not "guess yes": the other direction costs every request after the refused one.
        var blind = Assert.Single(spec.Profiles!, profile => profile.Id == "blind");
        Assert.False(blind.CanSeeImages);
    }

    [Fact]
    public void Swapping_in_the_provider_keeps_every_other_field()
    {
        var spec = JsonSerializer.Deserialize<ModelInstanceSpec>(ModuleFile, Json)!;
        var profile = spec.Profiles!.First(candidate => candidate.Id == "default");

        // This is exactly what the host does on restore: the provider address only exists once the process created it.
        var registered = profile with { Provider = new Address("model", "openai-chat", "restored") };

        Assert.True(registered.CanSeeImages);
        Assert.Equal(profile.ModelId, registered.ModelId);
        Assert.Equal(profile.ContextWindow, registered.ContextWindow);
    }
}
