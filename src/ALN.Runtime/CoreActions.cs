using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Runtime;

/// <summary>
/// The actions the core exposes. Handlers return purpose-built DTOs, never internal objects: a
/// provider spec carries an api key, a session history is unbounded, and an Address is transport
/// detail. Redaction lives here, next to the data, not in each transport.
/// </summary>
public static class CoreActions
{
    public const string SystemInfo = "system.info";
    public const string AdaptersList = "adapters.list";
    public const string AdaptersStart = "adapters.start";
    public const string AdaptersStop = "adapters.stop";
    public const string ChannelsList = "channels.list";
    public const string ChannelsStart = "channels.start";
    public const string ChannelsStop = "channels.stop";
    public const string ModelsList = "models.list";
    public const string SignalsRecent = "signals.recent";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    public static Result RegisterAll(ControlPlane plane, IAdapterManager adapters, IChannelManager channels,
        IModelManager models, ISignalLog signals)
    {
        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(SystemInfo, "Core version, uptime and counts.", Capabilities.SystemRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                {
                    core = "ALN",
                    running = true,
                    adapters = adapters.List().Count,
                    channels = channels.List().Count,
                    profiles = models.ListProfiles().Count
                })))),

            (new(AdaptersList, "List adapters without credentials.", Capabilities.AdaptersRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(
                    adapters.List().Select(a => new
                    {
                        address = a.Address.ToString(),
                        platform = a.Descriptor.PlatformId,
                        account = a.Descriptor.AccountId,
                        direction = a.Descriptor.Direction.ToString(),
                        state = a.State.ToString()
                    }))))),

            (new(AdaptersStart, "Start one adapter.", Capabilities.AdaptersControl, true), StartAdapter),
            (new(AdaptersStop, "Stop one adapter.", Capabilities.AdaptersControl, true), StopAdapter),

            (new(ChannelsList, "List channels and their bindings.", Capabilities.ChannelsRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(
                    channels.List().Select(c => new
                    {
                        address = c.Address.ToString(),
                        name = c.Name,
                        running = c.Running,
                        bindings = c.Bindings.Select(b => new
                        {
                            adapter = b.Adapter.ToString(),
                            conversation = b.PlatformConversationId
                        })
                    }))))),

            (new(ChannelsStart, "Start one channel.", Capabilities.ChannelsControl, true), StartChannel),
            (new(ChannelsStop, "Stop one channel.", Capabilities.ChannelsControl, true), StopChannel),

            (new(ModelsList, "List provider instances and profiles without configuration or keys.", Capabilities.ModelsRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                {
                    // Only addresses and type ids: ModelProviderInfo carries configuration, which can
                    // hold an api key, so it is never serialized here.
                    providers = models.List().Select(p => new { address = p.Address.ToString() }),
                    profiles = models.ListProfiles().Select(p => new
                    {
                        id = p.Id,
                        provider = p.Provider.ToString(),
                        model = p.ModelId
                    })
                })))),

            (new(SignalsRecent, "Recent signal records, newest last.", Capabilities.SignalsRead, false), RecentSignals)
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();

        async ValueTask<Result<JsonElement>> StartAdapter(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            var started = await adapters.StartAsync(target.Value, ct).ConfigureAwait(false);
            return started.Ok
                ? Result<JsonElement>.Success(ToJson(new { address = target.Value.ToString(), state = "running" }))
                : Result<JsonElement>.Failure(started.Error!);
        }

        async ValueTask<Result<JsonElement>> StopAdapter(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            var stopped = await adapters.StopAsync(target.Value, ct).ConfigureAwait(false);
            return stopped.Ok
                ? Result<JsonElement>.Success(ToJson(new { address = target.Value.ToString(), state = "stopped" }))
                : Result<JsonElement>.Failure(stopped.Error!);
        }

        async ValueTask<Result<JsonElement>> StartChannel(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            var started = await channels.StartAsync(target.Value, ct).ConfigureAwait(false);
            return started.Ok
                ? Result<JsonElement>.Success(ToJson(new { address = target.Value.ToString(), state = "running" }))
                : Result<JsonElement>.Failure(started.Error!);
        }

        async ValueTask<Result<JsonElement>> StopChannel(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            var stopped = await channels.StopAsync(target.Value, ct).ConfigureAwait(false);
            return stopped.Ok
                ? Result<JsonElement>.Success(ToJson(new { address = target.Value.ToString(), state = "stopped" }))
                : Result<JsonElement>.Failure(stopped.Error!);
        }

        ValueTask<Result<JsonElement>> RecentSignals(JsonElement arguments, CancellationToken ct)
        {
            var limit = 200;
            if (arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var requested))
                limit = Math.Clamp(requested, 1, 1_000);
            var group = arguments.ValueKind == JsonValueKind.Object &&
                        arguments.TryGetProperty("group", out var groupElement) && groupElement.ValueKind == JsonValueKind.String
                ? groupElement.GetString()
                : null;
            var records = signals.Snapshot()
                .Where(r => group is null || string.Equals(r.Group, group, StringComparison.Ordinal))
                .TakeLast(limit)
                .Select(r => new
                {
                    at = r.At,
                    level = r.Level.ToString(),
                    group = r.Group,
                    name = r.Name,
                    step = r.StepId,
                    outcome = r.Outcome,
                    durationMs = Math.Round(r.DurationMs, 1),
                    detail = r.Detail
                });
            return ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new { records })));
        }
    }

    private static Result<Address> ReadAddress(JsonElement arguments, string property)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String || !Address.TryParse(value.GetString(), out var address) ||
            !address.IsInstance)
            return Result<Address>.Failure(new(ErrorCode.Invalid, $"Argument '{property}' must be an instance address."));
        return Result<Address>.Success(address);
    }
}
