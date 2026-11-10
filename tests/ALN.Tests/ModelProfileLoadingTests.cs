using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Runtime;

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

    [Fact]
    public async Task The_file_loader_keeps_the_capabilities_it_reads()
    {
        // The test above starts from an already-deserialized spec, which is exactly why it did not catch the bug: the
        // hand-rolled parser was one layer down, in the store. This one goes through the real loader.
        var root = Directory.CreateTempSubdirectory("model-store-").FullName;
        try
        {
            var directory = Path.Combine(root, "modules", "model.openai-chat#one");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "module.json"), """
                {
                  "id": "model.openai-chat#one",
                  "type": "model.openai-chat",
                  "config": {
                    "enabled": true,
                    "configuration": { "endpoint": "https://example.invalid" },
                    "profiles": [
                      { "id": "default", "modelId": "some-model", "capabilities": "Streaming, Vision" },
                      { "id": "blind", "modelId": "some-model" }
                    ]
                  }
                }
                """);

            var loaded = await new FileModelInstanceStore(root, new AnythingUnderModel()).LoadAllAsync(default);

            Assert.True(loaded.Ok, loaded.Error?.Message);
            var entry = Assert.Single(loaded.Value!);
            Assert.True(Assert.Single(entry.Spec.Profiles!, candidate => candidate.Id == "default").CanSeeImages);
            Assert.False(Assert.Single(entry.Spec.Profiles!, candidate => candidate.Id == "blind").CanSeeImages);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_capability_nobody_defined_is_refused_with_the_profile_that_said_it()
    {
        using var document = JsonDocument.Parse("""{ "id": "p", "modelId": "m", "capabilities": "Vision, Telepathy" }""");

        var parsed = ModelProfileJson.Parse(document.RootElement, Json);

        Assert.False(parsed.Ok);
        Assert.Contains("Telepathy", parsed.Error!.Detail ?? parsed.Error.Message);
    }

    /// <summary>Enough of a type catalog to say "everything named model.* is a model" — the store asks nothing else.</summary>
    private sealed class AnythingUnderModel : IModuleTypeCatalog
    {
        public bool IsKnownAbstract(string typeId) => typeId == "model";
        public bool IsConcrete(string typeId) => typeId.StartsWith("model.", StringComparison.Ordinal);
        public bool IsAssignableTo(string concreteTypeId, string abstractTypeId) =>
            abstractTypeId == "model" && concreteTypeId.StartsWith("model.", StringComparison.Ordinal);
    }
}
