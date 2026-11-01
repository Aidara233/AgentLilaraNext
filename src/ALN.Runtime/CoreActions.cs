using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Graph.Contracts;

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
    public const string ChannelsSend = "channels.send";
    public const string GraphList = "graph.list";
    public const string GraphMount = "graph.mount";
    public const string ParamsList = "params.list";
    public const string ParamsSet = "params.set";
    public const string GraphUnmount = "graph.unmount";
    public const string ModelsList = "models.list";
    public const string SignalsRecent = "signals.recent";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    public static Result RegisterAll(ControlPlane plane, IAdapterManager adapters, IChannelManager channels,
        IModelManager models, ISignalLog signals, IGraphRuntime graph, IParameterRegistry parameters)
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
                        // The declared label, and — when the adapter can learn it — who it actually is.
                        account = Identity(a.Address)?.AccountId ?? a.Descriptor.AccountId,
                        declaredAccount = a.Descriptor.AccountId,
                        displayName = Identity(a.Address)?.DisplayName,
                        direction = a.Descriptor.Direction.ToString(),
                        state = a.State.ToString(),
                        // Whether the connection behind the state is actually up. Null when the adapter
                        // type has no opinion, so "false" never means "unknown".
                        connected = Health(a.Address)?.Connected,
                        detail = Health(a.Address)?.HealthDetail
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
                        maxMessages = c.MaxMessages,
                        bindings = c.Bindings.Select(b => new
                        {
                            adapter = b.Adapter.ToString(),
                            conversation = b.PlatformConversationId
                        })
                    }))))),

            (new(ChannelsStart, "Start one channel.", Capabilities.ChannelsControl, true), StartChannel),
            (new(ChannelsStop, "Stop one channel.", Capabilities.ChannelsControl, true), StopChannel),
            (new(ChannelsSend, "Send one text message through a channel's binding.", Capabilities.MessagesWrite, true), SendChannel),

            (new(GraphList, "List mounted scripts and their targets.", Capabilities.GraphRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new
                {
                    mounted = graph.Mounted.Select(m => new
                    {
                        script = m.Package.Manifest.Id,
                        version = m.Package.Manifest.Version,
                        targetType = m.Package.Manifest.TargetType,
                        events = m.Package.Manifest.Events,
                        trustDomain = m.Package.Manifest.TrustDomain.ToString(),
                        target = m.Target.ToString()
                    })
                })))),

            // Manual scripts are the per-instance policy switch: a script declared "mount": "manual" is
            // not attached to anything by its lifecycle, so this is the only way it ever runs.
            (new(GraphMount, "Mount a manual script onto one instance.", Capabilities.GraphControl, true), MountScript),
            (new(GraphUnmount, "Unmount a script from one instance.", Capabilities.GraphControl, true), UnmountScript),

            (new(ParamsList, "List every tunable the running components expose, with its current value.", Capabilities.ParamsRead, false),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ToJson(new { parameters = parameters.List() })))),

            (new(ParamsSet, "Change a tunable: validated by its owner, applied now, persisted for restarts.", Capabilities.ParamsWrite, true),
                async (arguments, ct) =>
                {
                    if (!TryReadText(arguments, "scope", out var scope) || scope.Trim().Length == 0)
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "params.set requires 'scope'."));
                    if (!TryReadText(arguments, "name", out var name) || name.Trim().Length == 0)
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "params.set requires 'name'."));
                    if (!TryReadText(arguments, "value", out var value))
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "params.set requires 'value' (send \"\" to clear)."));

                    var changed = await parameters.SetAsync(scope.Trim(), name.Trim(), value, ct).ConfigureAwait(false);
                    if (!changed.Ok) return Result<JsonElement>.Failure(changed.Error!);
                    var current = parameters.List()
                        .FirstOrDefault(p => p.Scope == scope.Trim() && p.Name == name.Trim());
                    return Result<JsonElement>.Success(ToJson(new
                    {
                        scope = scope.Trim(),
                        name = name.Trim(),
                        value = current?.Value,
                        apply = current?.Apply.ToString(),
                        secret = current?.Secret ?? false
                    }));
                }),

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

        // Optional per-adapter health: the state says whether somebody asked it to run, this says
        // whether it is actually connected right now.
        IAdapterHealth? Health(Address address)
        {
            var found = adapters.Get(address);
            return found.Ok ? found.Value as IAdapterHealth : null;
        }

        async ValueTask<Result<JsonElement>> MountScript(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "target");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            if (!TryReadText(arguments, "script", out var scriptId) || scriptId.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "graph.mount requires 'script' (the script id)."));

            var mounted = await graph.MountAsync(scriptId.Trim(), target.Value, ct).ConfigureAwait(false);
            return mounted.Ok
                ? Result<JsonElement>.Success(ToJson(new { script = scriptId.Trim(), target = target.Value.ToString(), mounted = true }))
                : Result<JsonElement>.Failure(mounted.Error!);
        }

        async ValueTask<Result<JsonElement>> UnmountScript(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "target");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            if (!TryReadText(arguments, "script", out var scriptId) || scriptId.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "graph.unmount requires 'script' (the script id)."));

            var unmounted = await graph.UnmountAsync(scriptId.Trim(), target.Value, ct).ConfigureAwait(false);
            return unmounted.Ok
                ? Result<JsonElement>.Success(ToJson(new { script = scriptId.Trim(), target = target.Value.ToString(), mounted = false }))
                : Result<JsonElement>.Failure(unmounted.Error!);
        }

        IAdapterIdentity? Identity(Address address)
        {
            var found = adapters.Get(address);
            return found.Ok ? found.Value as IAdapterIdentity : null;
        }

        /// <summary>
        /// One text, one binding, no policy: this is the action a human or a test uses to prove that the
        /// Channel → Adapter → platform path works without waiting for somebody to talk to the bot.
        ///
        /// It refuses a channel with zero or several bindings rather than picking one: guessing which
        /// conversation a message was meant for is exactly the kind of guess that ends up in the wrong
        /// group.
        /// </summary>
        async ValueTask<Result<JsonElement>> SendChannel(JsonElement arguments, CancellationToken ct)
        {
            var target = ReadAddress(arguments, "address");
            if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
            if (!TryReadText(arguments, "text", out var text) || text.Trim().Length == 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "channels.send requires a non-empty 'text'."));
            var replyTo = TryReadText(arguments, "replyTo", out var reply) ? reply : null;

            var channel = channels.Get(target.Value);
            if (!channel.Ok) return Result<JsonElement>.Failure(channel.Error!);
            if (!channel.Value.Running)
                return Result<JsonElement>.Failure(new(ErrorCode.Conflict, $"Channel is not running: {target.Value}"));
            if (channel.Value.Bindings.Count != 1)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                    $"Channel has {channel.Value.Bindings.Count} bindings; channels.send needs exactly one. " +
                    "Sending to a chosen binding among several is not implemented."));

            var binding = channel.Value.Bindings[0];
            var adapter = adapters.Get(binding.Adapter);
            if (!adapter.Ok) return Result<JsonElement>.Failure(adapter.Error!);

            var sent = await adapter.Value.SendAsync(new OutboundMessage(binding.PlatformConversationId, text, replyTo), ct)
                .ConfigureAwait(false);
            if (!sent.Ok) return Result<JsonElement>.Failure(sent.Error!);
            return Result<JsonElement>.Success(ToJson(new
            {
                channel = target.Value.ToString(),
                adapter = binding.Adapter.ToString(),
                conversation = binding.PlatformConversationId,
                sent = sent.Value.Sent,
                messageId = sent.Value.AttemptId,
                detail = sent.Value.Detail
            }));
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

    private static bool TryReadText(JsonElement arguments, string property, out string value)
    {
        value = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(property, out var found) ||
            found.ValueKind != JsonValueKind.String) return false;
        value = found.GetString() ?? string.Empty;
        return true;
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
