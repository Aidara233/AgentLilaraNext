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
                    profiles = ListProfiles().Select(p => new
                    {
                        id = p.Id, provider = p.Provider.ToString(), model = p.ModelId,
                        contextWindow = p.EffectiveContextWindow, contextWindowAssumed = p.ContextWindowAssumed
                    }),
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
                    "id":{"type":"string"},"model":{"type":"string"},"defaultSampling":{"type":"object"},
                    "contextWindow":{"type":"integer","minimum":1,"description":"Tokens one request may carry for this profile. Omit and a Session plans against 128k."}},
                    "required":["id","model"]}},
                  "enabled":{"type":"boolean","description":"Default true."}},
                 "required":["configuration"],"additionalProperties":false}
                """, Subject: "address"), (arguments, ct) => SaveProviderAsync(arguments, store, ct)),

            (new(ModelActions.ProviderDestroy, "Destroy a provider instance and delete its configuration file. Refused "
                    + "while a profile still points at it: a profile with no provider fails at the next turn instead. "
                    + "drainSeconds waits for in-flight calls before giving up, instead of refusing immediately.",
                Capabilities.ModelsControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "address":{"type":"string","description":"provider instance, e.g. model.openai-chat#main"},
                  "category":{"type":"string","description":"Alternative: the provider's category."},
                  "instance":{"type":"string","description":"Alternative: the instance name."},
                  "drainSeconds":{"type":"integer","minimum":1,"maximum":600,"description":"Wait this long for calls in flight to finish."}},
                 "additionalProperties":false}
                """, Subject: "address"),
                (arguments, ct) => DestroyProviderAsync(arguments, store, ct)),

            (new(ModelActions.ProfileCreate, "Register one model profile and write it into its provider's file.",
                Capabilities.ModelsControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string"},
                  "model":{"type":"string","description":"The provider's own model id."},
                  "provider":{"type":"string","description":"The provider instance address."},
                  "defaultSampling":{"type":"object","description":"Optional sampling settings."},
                  "contextWindow":{"type":"integer","minimum":1,"description":"Tokens one request may carry. Omit and a Session plans against 128k."}},
                 "required":["id","model","provider"],"additionalProperties":false}
                """, Subject: "id"), (arguments, ct) => CreateProfileAsync(arguments, store, ct)),

            (new(ModelActions.ProfileRemove, "Remove one model profile from the registry and from its provider's file.",
                Capabilities.ModelsControl, true,
                ParamsJson: """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""",
                Subject: "id"), (arguments, ct) => RemoveProfileAsync(arguments, store, ct)),

            (new(ModelActions.ProfileSet, "Change a profile in place: model, provider, sampling, context window. Anything "
                    + "not named keeps what it had, and the change is what the very next model step uses — a Session in "
                    + "the middle of a turn reads its profile again every step, so this is how a conversation moves to a "
                    + "different model without being restarted or rebuilt.",
                Capabilities.ModelsControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string"},
                  "model":{"type":"string","description":"The provider's own model id."},
                  "provider":{"type":"string","description":"Another provider instance, already running."},
                  "defaultSampling":{"type":"object"},
                  "contextWindow":{"type":"integer","minimum":1}},
                 "required":["id"],"additionalProperties":false}
                """, Subject: "id"), (arguments, ct) => SetProfileAsync(arguments, store, ct))
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

    /// <summary>
    /// A profile's declared window, or null when nobody declared one. A value that is present but unusable is refused
    /// rather than read as "absent": a window typed as "128k" would otherwise become the default, and the Session would
    /// plan against a number the caller never chose while every answer still looked right.
    /// </summary>
    private static int? ReadContextWindow(JsonElement arguments, out Error? error)
    {
        error = null;
        if (!arguments.TryGetProperty("contextWindow", out var window) || window.ValueKind == JsonValueKind.Null) return null;
        if (window.ValueKind != JsonValueKind.Number || !window.TryGetInt32(out var declared) || declared <= 0)
        {
            error = new(ErrorCode.Invalid, "contextWindow must be a positive whole number of tokens.");
            return null;
        }
        return declared;
    }

    /// <summary>
    /// The capabilities a caller named, as names (<c>"Streaming, Vision"</c>). Absent means "not declared", which reads
    /// as the least: a profile that says nothing gets receipts instead of pictures, not a refused request.
    /// </summary>
    private static ModelCapabilities? ReadCapabilities(JsonElement arguments, out Error? error)
    {
        error = null;
        if (!arguments.TryGetProperty("capabilities", out var item) || item.ValueKind is JsonValueKind.Null)
            return null;
        if (item.ValueKind != JsonValueKind.String)
        {
            error = new(ErrorCode.Invalid, "capabilities must be a string such as \"Streaming, Vision\".");
            return null;
        }
        try { return ModelCapabilitiesJson.Parse(item.GetString()); }
        catch (JsonException ex)
        {
            error = new(ErrorCode.Invalid, "capabilities names something this host does not know.", ex.Message);
            return null;
        }
    }

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
                defaultSampling = profile.DefaultSampling,
                // Read back as written: a save that drops a field nobody touched is how a settings page quietly
                // changes what the Session plans against.
                contextWindow = profile.ContextWindow
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
                var window = ReadContextWindow(item, out var windowFailure);
                if (windowFailure is { } failure) return Result<JsonElement>.Failure(failure);
                // Same parser as the file loader. A profile that survives one path and loses a field in the other is how
                // "I set it and it kept working until I restarted" happened once already.
                var parsedProfile = ModelProfileJson.Parse(item, ActionJson);
                if (!parsedProfile.Ok) return Result<JsonElement>.Failure(parsedProfile.Error!);
                parsed.Add(parsedProfile.Value!);
            }
            profiles = parsed;
        }

        var enabled = !arguments.TryGetProperty("enabled", out var enabledItem) || enabledItem.ValueKind != JsonValueKind.False;
        var spec = new ModelInstanceSpec(merged, profiles, enabled);
        var saved = await store.SaveAsync(named.Value.Address, spec, ct).ConfigureAwait(false);
        if (!saved.Ok) return Result<JsonElement>.Failure(saved.Error!);

        var address = new Address("model", named.Value.Kind, named.Value.Instance);
        var running = List().Any(provider => provider.Address.ToString() == named.Value.Address);
        if (running)
        {
            if (!enabled)
                // Disabling a provider that is already built would mean taking it out from under whatever is using it;
                // the file says disabled, the process keeps serving until a restart. Saying so is the difference between
                // a setting and a silent no-op.
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    address = named.Value.Address, saved = true, applied = "restart",
                    reason = "the instance is still running; 'enabled: false' takes effect when the host restarts"
                }));

            // A provider that is already built takes the new settings where it can, and says "restart" where it cannot.
            // This is the path a session in the middle of a turn cares about: the next step goes to the new endpoint,
            // and the turn that was already in flight finishes where it started.
            var applied = ReconfigureProvider(address, merged.GetRawText());
            if (!applied.Ok)
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    address = named.Value.Address, saved = true, applied = "restart", reason = applied.Error?.Message
                }));
            var profilesApplied = ApplyProfiles(address, profiles ?? []);
            return Result<JsonElement>.Success(ActionArgs.JsonOf(new
            {
                address = named.Value.Address,
                saved = true,
                applied = profilesApplied.Ok ? "now" : "profiles-not-applied",
                reason = profilesApplied.Error?.Message
            }));
        }

        if (enabled)
        {
            var created = await CreateAsync(named.Value.Category, named.Value.Instance, merged.GetRawText(), ct).ConfigureAwait(false);
            if (!created.Ok || created.Value is null)
                return Result<JsonElement>.Failure(created.Error ?? new(ErrorCode.Unavailable, "Provider could not be created."));
            // A profile read out of the provider's own file carries no provider: the file it lives in *is* the provider,
            // so repeating the address there would be a second place for it to be wrong. Registering it needs the
            // address all the same — this is the one moment the two spellings meet.
            var registered = ApplyProfiles(address, profiles ?? []);
            if (!registered.Ok) return Result<JsonElement>.Failure(registered.Error!);
            return Result<JsonElement>.Success(ActionArgs.JsonOf(new { address = named.Value.Address, saved = true, applied = "now" }));
        }
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            address = named.Value.Address, saved = true, applied = "disabled"
        }));
    }

    /// <summary>
    /// Registers or updates each profile the caller wrote. In place when the id already exists: a profile is read by id
    /// on every step, so remove-then-create would turn a save into a window where a live session cannot find its model.
    /// </summary>
    private Result ApplyProfiles(Address address, IReadOnlyList<ModelProfile> profiles)
    {
        var known = ListProfiles().Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            var resolved = profile.Provider.IsInstance ? profile : profile with { Provider = address };
            var applied = known.Contains(resolved.Id) ? UpdateProfile(resolved) : RegisterProfile(resolved);
            if (!applied.Ok) return applied;
        }
        return Result.Success();
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
            // A profile pointing here is a refusal, and that refusal is the useful answer: a profile with no provider is
            // a session that fails at its next turn with a stranger error. Calls still in flight are the other refusal —
            // and the one a caller can wait out, which is what drainSeconds is for.
            var drain = arguments.TryGetProperty("drainSeconds", out var drainItem) && drainItem.TryGetInt32(out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
            var destroyed = await DestroyAsync(new Address("model", named.Value.Kind, named.Value.Instance), drain, ct)
                .ConfigureAwait(false);
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
        var window = ReadContextWindow(arguments, out var windowError);
        if (windowError is { } error) return Result<JsonElement>.Failure(error);
        var capabilities = ReadCapabilities(arguments, out var capabilityError);
        if (capabilityError is { } capabilitiesError) return Result<JsonElement>.Failure(capabilitiesError);
        var profile = new ModelProfile(id.Trim(), provider.Value, model.Trim(), sampling, window, capabilities);
        var registered = RegisterProfile(profile);
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
                var profiles = (instanceSpec.Profiles ?? []).Where(candidate => candidate.Id != id.Trim())
                    // The same profile that was just registered, with the provider blanked: the file must not repeat the
                    // address it lives under, and rebuilding it here is how its capabilities would be lost on save.
                    .Append(profile with { Provider = default }).ToArray();
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

    private async ValueTask<Result<JsonElement>> SetProfileAsync(JsonElement arguments, IModelInstanceStore? store,
        CancellationToken ct)
    {
        if (!arguments.TryReadText("id", out var id) || id.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "models.profile.set requires 'id'."));
        var existing = ListProfiles().FirstOrDefault(candidate => candidate.Id == id.Trim());
        if (existing is null)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                $"Model profile not found: {id.Trim()}. models.profile.create is how one comes into being."));

        // Anything not named keeps what it had. A verb that reset a field the caller did not mention would turn "point
        // this at another model" into "and also forget the context window".
        var provider = existing.Provider;
        if (arguments.TryGetProperty("provider", out _))
        {
            var read = arguments.ReadAddress("provider");
            if (!read.Ok) return Result<JsonElement>.Failure(read.Error!);
            provider = read.Value;
        }
        var model = arguments.TryReadText("model", out var named) && named.Trim().Length > 0 ? named.Trim() : existing.ModelId;

        var sampling = existing.DefaultSampling;
        if (arguments.TryGetProperty("defaultSampling", out var samplingItem))
        {
            if (samplingItem.ValueKind != JsonValueKind.Object)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "'defaultSampling' must be an object."));
            try { sampling = samplingItem.Deserialize<SamplingSettings>(ActionJson); }
            catch (JsonException ex) { return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "defaultSampling is malformed.", ex.Message)); }
        }

        var window = existing.ContextWindow;
        if (arguments.TryGetProperty("contextWindow", out _))
        {
            window = ReadContextWindow(arguments, out var windowError);
            if (windowError is { } error) return Result<JsonElement>.Failure(error);
        }

        var updated = existing with { Provider = provider, ModelId = model, DefaultSampling = sampling, ContextWindow = window };
        var applied = UpdateProfile(updated);
        if (!applied.Ok) return Result<JsonElement>.Failure(applied.Error!);

        if (store is not null && provider.IsInstance)
        {
            var stored = await store.ReadAsync(provider.ToString(), ct).ConfigureAwait(false);
            if (stored.Error is null && stored.Value is { } instanceSpec)
            {
                var profiles = (instanceSpec.Profiles ?? []).Where(candidate => candidate.Id != id.Trim())
                    // `updated` carries every field the profile had; only the provider is blanked for the file.
                    .Append(updated with { Provider = default }).ToArray();
                var saved = await store.SaveAsync(provider.ToString(), instanceSpec with { Profiles = profiles }, ct)
                    .ConfigureAwait(false);
                if (!saved.Ok) return Result<JsonElement>.Failure(saved.Error!);
            }
        }
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            id = id.Trim(),
            provider = provider.ToString(),
            model,
            contextWindow = window,
            applies = "next step"
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
