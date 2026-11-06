using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Runtime;

/// <summary>
/// The model-provider verbs, declared by the manager that owns the registry.
///
/// The configuration files are a host concern (where they live), so the store is handed in when the actions are
/// registered — the same way the host hands out the control plane. What the manager owns, and what these actions
/// therefore live next to: which provider kinds exist, which instances are running, and which profiles point where.
/// </summary>
public sealed partial class ModelManager
{
    /// <summary>Registers this module's actions. The store is optional: without it, only the live registry is reachable.</summary>
    private static readonly JsonSerializerOptions ActionJson = new(JsonSerializerDefaults.Web);

    public Result RegisterActions(IControlPlane plane, IModelInstanceStore? instances = null)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        var store = instances;
        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(ModelActions.List, "List provider instances and profiles without configuration or keys, plus the "
                    + "provider kinds this process can build (a form has to know before it can offer one).",
                Capabilities.ModelsRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    // Only addresses and type ids: ModelProviderInfo carries configuration, which can hold an api key,
                    // so it is never serialized here.
                    providers = List().Select(p => new { address = p.Address.ToString(), typeId = p.Descriptor.TypeId, inFlight = p.InFlight }),
                    profiles = ListProfiles().Select(p => new { id = p.Id, provider = p.Provider.ToString(), model = p.ModelId }),
                    types = ListTypes().Select(type => new
                    {
                        typeId = type.TypeId,
                        category = type.Category,
                        display = type.DisplayName
                    })
                })))),

            (new(ModelActions.ProviderGet, "One provider instance as configured on disk, with credentials masked as ***.",
                Capabilities.ModelsRead, false, ParamsJson: ProviderArgs(), Subject: "address"),
                (arguments, ct) => ReadProviderAsync(arguments, store, ct)),

            (new(ModelActions.ProviderSave, "Write a provider instance's configuration. A *** value means \"keep the "
                    + "stored one\"; applies now when the provider is not running, else on restart.", Capabilities.ModelsControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "address":{"type":"string","description":"provider instance, e.g. model.openai-chat#main"},
                  "category":{"type":"string","description":"Alternative to address: the provider's category."},
                  "instance":{"type":"string","description":"Alternative to address: the instance name."},
                  "configuration":{"type":"object","description":"Provider configuration; *** keeps the stored value."},
                  "profiles":{"type":"array","items":{"type":"object","properties":{
                    "id":{"type":"string"},"model":{"type":"string"},"defaultSampling":{"type":"object"}},
                    "required":["id","model"]}},
                  "enabled":{"type":"boolean","description":"Default true."}},
                 "required":["configuration"],"additionalProperties":false}
                """, Subject: "address"), (arguments, ct) => SaveProviderAsync(arguments, store, ct)),

            (new(ModelActions.ProviderDestroy, "Destroy a provider instance and delete its configuration file. Refused "
                    + "while a profile still points at it: a profile with no provider fails at the next turn instead.",
                Capabilities.ModelsControl, true, ParamsJson: ProviderArgs(), Subject: "address"),
                (arguments, ct) => DestroyProviderAsync(arguments, store, ct)),

            (new(ModelActions.ProfileCreate, "Register one model profile and write it into its provider's file.",
                Capabilities.ModelsControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string"},
                  "model":{"type":"string","description":"The provider's own model id."},
                  "provider":{"type":"string","description":"The provider instance address."},
                  "defaultSampling":{"type":"object","description":"Optional sampling settings."}},
                 "required":["id","model","provider"],"additionalProperties":false}
                """, Subject: "id"), (arguments, ct) => CreateProfileAsync(arguments, store, ct)),

            (new(ModelActions.ProfileRemove, "Remove one model profile from the registry and from its provider's file.",
                Capabilities.ModelsControl, true,
                ParamsJson: """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""",
                Subject: "id"), (arguments, ct) => RemoveProfileAsync(arguments, store, ct))
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    private static string ProviderArgs() =>
        """
        {"type":"object","properties":{
          "address":{"type":"string","description":"provider instance, e.g. model.openai-chat#main"},
          "category":{"type":"string","description":"Alternative: the provider's category."},
          "instance":{"type":"string","description":"Alternative: the instance name."}},
         "additionalProperties":false}
        """;

    /// <summary>The category a kind belongs to: read from the type tree, not taken from the address.</summary>
    private string CategoryOfKind(string kind)
    {
        var typeId = "model." + kind;
        return ListTypes().FirstOrDefault(type => string.Equals(type.TypeId, typeId, StringComparison.Ordinal))?.Category ?? kind;
    }

    /// <summary>
    /// Accepts either an address ("model.openai-chat#deepseek_official") or category + instance. Two spellings because
    /// two callers exist: the UI edits a row it found, a person adds one they are typing.
    /// </summary>
    private (string Category, string Instance, string Kind, string Address)? ReadName(JsonElement arguments, out Error? error)
    {
        error = null;
        var category = arguments.TryReadText("category", out var categoryText) && categoryText.Trim().Length > 0 ? categoryText.Trim() : null;
        var instance = arguments.TryReadText("instance", out var instanceText) && instanceText.Trim().Length > 0 ? instanceText.Trim() : null;
        string? kind = null;
        if (arguments.TryReadText("address", out var addressText) && addressText.Trim().Length > 0)
        {
            if (!Address.TryParse(addressText.Trim(), out var address) || !address.IsInstance)
            {
                error = new(ErrorCode.Invalid, $"Not a model provider address: {addressText}");
                return null;
            }
            kind = address.Kind;
            instance = address.Instance;
        }
        if (instance is null)
        {
            error = new(ErrorCode.Invalid, "A model provider needs 'instance' (or an 'address').");
            return null;
        }
        if (category is null)
        {
            if (kind is null)
            {
                error = new(ErrorCode.Invalid, "A model provider needs 'category' (or an 'address' with its kind).");
                return null;
            }
            category = CategoryOfKind(kind);
        }
        if (kind is null)
        {
            var typeId = ListTypes().FirstOrDefault(type => string.Equals(type.Category, category, StringComparison.Ordinal))?.TypeId;
            const string prefix = "model.";
            if (typeId is null || !typeId.StartsWith(prefix, StringComparison.Ordinal))
            {
                error = new(ErrorCode.Invalid, $"No model provider type serves category '{category}'.");
                return null;
            }
            kind = typeId[prefix.Length..];
        }
        return (category, instance, kind, $"model.{kind}#{instance}");
    }

    private async ValueTask<Result<JsonElement>> ReadProviderAsync(JsonElement arguments, IModelInstanceStore? store,
        CancellationToken ct)
    {
        if (store is null) return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "No model instance store is configured."));
        var named = ReadName(arguments, out var error);
        if (named is null) return Result<JsonElement>.Failure(error!);
        var stored = await store.ReadAsync(named.Value.Address, ct).ConfigureAwait(false);
        // "No such file" is Success(null), so the test is Error, not Ok: a nullable Value makes Ok false by itself.
        if (stored.Error is { } storedError) return Result<JsonElement>.Failure(storedError);
        var running = List().Any(provider => provider.Address.ToString() == named.Value.Address);
        if (stored.Value is null)
            return Result<JsonElement>.Success(ActionArgs.JsonOf(new { address = named.Value.Address, found = false, running }));
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            address = named.Value.Address,
            found = true,
            running,
            category = named.Value.Category,
            instance = named.Value.Instance,
            enabled = stored.Value.Enabled,
            // The credentials go back as ***, so a screenshot of this page is not a key leak.
            configuration = ModelSecrets.Mask(stored.Value.Configuration),
            profiles = (stored.Value.Profiles ?? []).Select(profile => new
            {
                id = profile.Id,
                model = profile.ModelId,
                defaultSampling = profile.DefaultSampling
            })
        }));
    }

    private async ValueTask<Result<JsonElement>> SaveProviderAsync(JsonElement arguments, IModelInstanceStore? store,
        CancellationToken ct)
    {
        if (store is null) return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "No model instance store is configured."));
        var named = ReadName(arguments, out var error);
        if (named is null) return Result<JsonElement>.Failure(error!);
        if (!arguments.TryGetProperty("configuration", out var configuration) || configuration.ValueKind != JsonValueKind.Object)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "models.provider.save requires 'configuration' as a JSON object."));

        var existing = await store.ReadAsync(named.Value.Address, ct).ConfigureAwait(false);
        if (existing.Error is { } existingError) return Result<JsonElement>.Failure(existingError);
        // A *** the caller could not read back means "leave it alone" — otherwise every save erases the key.
        var merged = existing.Value is null
            ? configuration
            : ModelSecrets.Restore(configuration, existing.Value.Configuration);

        var profiles = existing.Value?.Profiles;
        if (arguments.TryGetProperty("profiles", out var profileItems))
        {
            if (profileItems.ValueKind != JsonValueKind.Array)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "models.provider.save 'profiles' must be an array."));
            var parsed = new List<ModelProfile>();
            foreach (var item in profileItems.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var idText) || idText.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idText.GetString()) ||
                    !item.TryGetProperty("model", out var modelText) || modelText.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(modelText.GetString()))
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "Each profile needs a non-empty 'id' and 'model'."));
                SamplingSettings? sampling = null;
                if (item.TryGetProperty("defaultSampling", out var samplingItem) && samplingItem.ValueKind == JsonValueKind.Object)
                {
                    try { sampling = samplingItem.Deserialize<SamplingSettings>(ActionJson); }
                    catch (JsonException ex) { return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "Profile sampling is malformed.", ex.Message)); }
                }
                parsed.Add(new ModelProfile(idText.GetString()!.Trim(), default, modelText.GetString()!.Trim(), sampling));
            }
            profiles = parsed;
        }

        var enabled = !arguments.TryGetProperty("enabled", out var enabledItem) || enabledItem.ValueKind != JsonValueKind.False;
        var spec = new ModelInstanceSpec(merged, profiles, enabled);
        var saved = await store.SaveAsync(named.Value.Address, spec, ct).ConfigureAwait(false);
        if (!saved.Ok) return Result<JsonElement>.Failure(saved.Error!);

        // A provider that is not running yet can be built right now, so a freshly added key is usable without a restart.
        // One that is running holds its configuration; saying "restart" is honest, applying silently is not possible
        // without pulling it out from under calls that are in flight.
        var running = List().Any(provider => provider.Address.ToString() == named.Value.Address);
        if (!running && enabled)
        {
            var created = await CreateAsync(named.Value.Category, named.Value.Instance, merged.GetRawText(), ct).ConfigureAwait(false);
            if (!created.Ok || created.Value is null)
                return Result<JsonElement>.Failure(created.Error ?? new(ErrorCode.Unavailable, "Provider could not be created."));
            // A profile read out of the provider's own file carries no provider: the file it lives in *is* the provider,
            // so repeating the address there would be a second place for it to be wrong. Registering it needs the
            // address all the same — this is the one moment the two spellings meet.
            var provider = new Address("model", named.Value.Kind, named.Value.Instance);
            foreach (var profile in profiles ?? [])
                RegisterProfile(profile.Provider.IsInstance ? profile : profile with { Provider = provider });
            return Result<JsonElement>.Success(ActionArgs.JsonOf(new { address = named.Value.Address, saved = true, applied = "now" }));
        }
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            address = named.Value.Address, saved = true, applied = running ? "restart" : "disabled"
        }));
    }

    private async ValueTask<Result<JsonElement>> DestroyProviderAsync(JsonElement arguments, IModelInstanceStore? store,
        CancellationToken ct)
    {
        if (store is null) return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "No model instance store is configured."));
        var named = ReadName(arguments, out var error);
        if (named is null) return Result<JsonElement>.Failure(error!);
        var running = List().Any(provider => provider.Address.ToString() == named.Value.Address);
        if (running)
        {
            // Destroy refuses while a profile still points at it, and that refusal is the useful answer: a profile with
            // no provider is a session that fails at its next turn with a stranger error.
            var destroyed = await DestroyAsync(new Address("model", named.Value.Kind, named.Value.Instance), ct).ConfigureAwait(false);
            if (!destroyed.Ok) return Result<JsonElement>.Failure(destroyed.Error!);
        }
        var deleted = await store.DeleteAsync(named.Value.Address, ct).ConfigureAwait(false);
        return deleted.Ok
            ? Result<JsonElement>.Success(ActionArgs.JsonOf(new
            {
                address = named.Value.Address, destroyed = true, wasRunning = running
            }))
            : Result<JsonElement>.Failure(deleted.Error!);
    }

    private async ValueTask<Result<JsonElement>> CreateProfileAsync(JsonElement arguments, IModelInstanceStore? store,
        CancellationToken ct)
    {
        if (!arguments.TryReadText("id", out var id) || id.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "models.profile.create requires 'id'."));
        if (!arguments.TryReadText("model", out var model) || model.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "models.profile.create requires 'model'."));
        var provider = arguments.ReadAddress("provider");
        if (!provider.Ok) return Result<JsonElement>.Failure(provider.Error!);
        SamplingSettings? sampling = null;
        if (arguments.TryGetProperty("defaultSampling", out var samplingItem) && samplingItem.ValueKind == JsonValueKind.Object)
        {
            try { sampling = samplingItem.Deserialize<SamplingSettings>(ActionJson); }
            catch (JsonException ex) { return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "defaultSampling is malformed.", ex.Message)); }
        }
        var registered = RegisterProfile(new ModelProfile(id.Trim(), provider.Value, model.Trim(), sampling));
        if (!registered.Ok) return Result<JsonElement>.Failure(registered.Error!);

        // The live registry is not the file: a profile that is not written down dies at the next restart, and "it worked
        // until we restarted" is the worst kind of configuration bug.
        if (store is not null && provider.Value.IsInstance)
        {
            var stored = await store.ReadAsync(provider.Value.ToString(), ct).ConfigureAwait(false);
            if (stored.Error is null && stored.Value is { } instanceSpec)
            {
                // The provider is implied by the file it lives in, so the stored profile leaves it empty: a file that
                // repeats its own address is a file that can disagree with its own directory.
                var profiles = (instanceSpec.Profiles ?? []).Where(profile => profile.Id != id.Trim())
                    .Append(new ModelProfile(id.Trim(), default, model.Trim(), sampling)).ToArray();
                var saved = await store.SaveAsync(provider.Value.ToString(), instanceSpec with { Profiles = profiles }, ct)
                    .ConfigureAwait(false);
                if (!saved.Ok) return Result<JsonElement>.Failure(saved.Error!);
            }
        }
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            id = id.Trim(), provider = provider.Value.ToString(), model = model.Trim()
        }));
    }

    private async ValueTask<Result<JsonElement>> RemoveProfileAsync(JsonElement arguments, IModelInstanceStore? store,
        CancellationToken ct)
    {
        if (!arguments.TryReadText("id", out var id) || id.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "models.profile.remove requires 'id'."));
        var removed = RemoveProfile(id.Trim());
        if (!removed.Ok) return Result<JsonElement>.Failure(removed.Error!);
        if (store is not null)
        {
            var all = await store.LoadAllAsync(ct).ConfigureAwait(false);
            if (all.Error is null && all.Value is { } entries)
            {
                foreach (var entry in entries.Where(entry => (entry.Spec.Profiles ?? []).Any(profile => profile.Id == id.Trim())))
                {
                    var profiles = (entry.Spec.Profiles ?? []).Where(profile => profile.Id != id.Trim()).ToArray();
                    var saved = await store.SaveAsync(entry.Provider.ToString(), entry.Spec with { Profiles = profiles }, ct)
                        .ConfigureAwait(false);
                    if (!saved.Ok) return Result<JsonElement>.Failure(saved.Error!);
                }
            }
        }
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new { id = id.Trim(), removed = true }));
    }
}
