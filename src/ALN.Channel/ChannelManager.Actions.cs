using System.Text.Json;
using ALN.Contracts;

namespace ALN.Channel;

/// <summary>
/// The actions the Channel module answers to, declared next to the thing that carries them out.
///
/// They used to live in the host, in one class that implemented every verb in the system. That put the host in the
/// business of knowing how a channel is created, which is knowledge the manager already has, and it made the host the
/// only place an action id could come from. A verb belongs to whoever implements it: this file is the Channel's half
/// of the table, and what is left here is unpacking arguments — every rule it applies is a rule the manager already
/// enforces when it is called directly.
/// </summary>
public sealed partial class ChannelManager
{
    private static readonly JsonSerializerOptions ActionJson = new(JsonSerializerDefaults.Web);

    public const string ToolHint = "Create one instance with channels.create (not modules.load: that one loads an "
        + "existing directory into the graph).";

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, ActionJson);

    /// <summary>
    /// Registers this module's actions. Called once, by whoever built the manager: the host used to do the same from
    /// the outside, with the manager passed in as one of eighteen arguments.
    /// </summary>
    public Result RegisterActions(IControlPlane plane)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(ChannelActions.List, "List channels and what each one is bound to.", Capabilities.ChannelsRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(Json(List().Select(c => new
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

            (new(ChannelActions.Start, "Start one channel.", Capabilities.ChannelsControl, true,
                ParamsJson: AddressArgs("The channel to start."), Subject: "address"), ChangeRunningAsync(running: true)),
            (new(ChannelActions.Stop, "Stop one channel.", Capabilities.ChannelsControl, true,
                ParamsJson: AddressArgs("The channel to stop."), Subject: "address"), ChangeRunningAsync(running: false)),

            (new(ChannelActions.Send, "Send one text message through the channel's binding. The channel must be running "
                    + "and bound to exactly one conversation.", Capabilities.MessagesWrite, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "address":{"type":"string","description":"The channel to send through."},
                  "text":{"type":"string","description":"What to send."},
                  "replyTo":{"type":"string","description":"Platform message id to answer. Optional."}},
                 "required":["address","text"],"additionalProperties":false}
                """, Subject: "address"), SendThroughChannelAsync),

            (new(ChannelActions.Create, "Create one channel instance from its own arguments. " + ToolHint
                    + " It needs exactly one binding: a channel serves one conversation.", Capabilities.ChannelsControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "type":{"type":"string","description":"A concrete channel type, e.g. channel.conversation."},
                  "instance":{"type":"string","description":"The instance name. Default: a fresh suffix."},
                  "name":{"type":"string","description":"A display name. Default: the instance name."},
                  "bindings":{"type":"array","minItems":1,"maxItems":1,"items":{"type":"object",
                    "properties":{"adapter":{"type":"string"},"conversation":{"type":"string"}},
                    "required":["adapter","conversation"]}},
                  "maxMessages":{"type":"integer","minimum":1,"description":"How many recent messages one turn may carry."}},
                 "required":["type","bindings"],"additionalProperties":false}
                """, Subject: "instance"), CreateChannelAsync),

            (new(ChannelActions.Destroy, "Destroy one channel instance. Its directory goes to the bin and the Sessions "
                    + "that serve it are archived with it.", Capabilities.ChannelsControl, true,
                ParamsJson: AddressArgs("The channel to destroy."), Subject: "address"), DestroyChannelAsync)
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    private static string AddressArgs(string description) =>
        """{"type":"object","properties":{"address":{"type":"string","description":"DESCRIPTION"}},"required":["address"],"additionalProperties":false}"""
            .Replace("DESCRIPTION", description, StringComparison.Ordinal);

    private ActionHandler ChangeRunningAsync(bool running) => async (arguments, ct) =>
    {
        var target = ReadAddress(arguments, "address");
        if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
        var changed = running
            ? await StartAsync(target.Value, ct).ConfigureAwait(false)
            : await StopAsync(target.Value, ct).ConfigureAwait(false);
        return changed.Ok
            ? Result<JsonElement>.Success(Json(new { address = target.Value.ToString(), state = running ? "running" : "stopped" }))
            : Result<JsonElement>.Failure(changed.Error!);
    };

    private async ValueTask<Result<JsonElement>> SendThroughChannelAsync(JsonElement arguments, CancellationToken ct)
    {
        var target = ReadAddress(arguments, "address");
        if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
        if (!TryReadText(arguments, "text", out var text) || text.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "channels.send requires a non-empty 'text'."));
        var replyTo = TryReadText(arguments, "replyTo", out var reply) ? reply : null;

        var channel = Get(target.Value);
        if (!channel.Ok || channel.Value is null) return Result<JsonElement>.Failure(channel.Error!);
        if (!channel.Value.Running)
            return Result<JsonElement>.Failure(new(ErrorCode.Conflict, $"Channel is not running: {target.Value}"));
        // Refused rather than guessed: picking which conversation a message was meant for is the kind of guess that
        // ends up in the wrong group.
        if (channel.Value.Bindings.Count != 1)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                $"Channel has {channel.Value.Bindings.Count} bindings; channels.send needs exactly one."));

        var binding = channel.Value.Bindings[0];
        // The Adapter manager is asked for by name, like everywhere else: modules do not hold each other (C-19).
        if (_catalog.Find(new ModuleId(ModuleNames.AdapterManager)) is not IAdapterManager adapters)
            return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "No Adapter manager answers in this process."));
        var adapter = adapters.Get(binding.Adapter);
        if (!adapter.Ok || adapter.Value is null) return Result<JsonElement>.Failure(adapter.Error!);

        var sent = await adapter.Value.SendAsync(new OutboundMessage(binding.PlatformConversationId, text, replyTo), ct)
            .ConfigureAwait(false);
        if (!sent.Ok || sent.Value is null) return Result<JsonElement>.Failure(sent.Error!);
        return Result<JsonElement>.Success(Json(new
        {
            channel = target.Value.ToString(),
            adapter = binding.Adapter.ToString(),
            conversation = binding.PlatformConversationId,
            sent = sent.Value.Sent,
            messageId = sent.Value.AttemptId,
            detail = sent.Value.Detail
        }));
    }

    /// <summary>
    /// Creates one channel from a form's worth of arguments.
    ///
    /// The address is built from the type and the instance name, never from an argument that spells it out: a caller
    /// that can say "channel.conversation#x" while naming type "channel.other" is a caller that can make the two
    /// disagree before the spec is even stored.
    /// </summary>
    private async ValueTask<Result<JsonElement>> CreateChannelAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!TryReadText(arguments, "type", out var typeId) || typeId.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "channels.create requires 'type'."));
        var type = typeId.Trim();

        var instanceName = TryReadText(arguments, "instance", out var asked) && asked.Trim().Length > 0
            ? asked.Trim()
            : ModuleId.NewSuffix();
        var address = BuildAddress(type, instanceName);
        if (!address.Ok) return Result<JsonElement>.Failure(address.Error!);
        var name = TryReadText(arguments, "name", out var nameText) && nameText.Trim().Length > 0 ? nameText.Trim() : instanceName;

        var bindings = new List<ChannelBinding>();
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("bindings", out var bindingItems))
        {
            if (bindingItems.ValueKind != JsonValueKind.Array)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "channels.create 'bindings' must be an array."));
            foreach (var item in bindingItems.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("adapter", out var adapterText) || adapterText.ValueKind != JsonValueKind.String ||
                    !Address.TryParse(adapterText.GetString(), out var adapterAddress) || !adapterAddress.IsInstance)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "Each binding needs 'adapter' as an instance address."));
                if (!item.TryGetProperty("conversation", out var conversationText) || conversationText.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(conversationText.GetString()))
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "Each binding needs a non-empty 'conversation'."));
                bindings.Add(new ChannelBinding(adapterAddress, conversationText.GetString()!.Trim()));
            }
        }

        int? maxMessages = null;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("maxMessages", out var window) &&
            window.ValueKind != JsonValueKind.Null)
        {
            if (window.ValueKind != JsonValueKind.Number || !window.TryGetInt32(out var parsed) || parsed <= 0)
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "channels.create 'maxMessages' must be a positive integer."));
            maxMessages = parsed;
        }

        var spec = new ChannelSpec(address.Value, name, bindings, type, maxMessages);
        var created = await CreateAsync(spec, ct).ConfigureAwait(false);
        if (!created.Ok || created.Value is null)
            return Result<JsonElement>.Failure(created.Error ?? new(ErrorCode.Unavailable, "Channel could not be created."));
        // What else changed is part of the answer: a channel that appears also gets whatever a type-scoped mount rule
        // says belongs on it, and a caller that is not told has to go and look.
        return Result<JsonElement>.Success(Json(new
        {
            address = address.Value.ToString(),
            type,
            name,
            running = created.Value.Running,
            bindings = created.Value.Bindings.Select(binding => new
            {
                adapter = binding.Adapter.ToString(),
                conversation = binding.PlatformConversationId
            }),
            maxMessages
        }));
    }

    private async ValueTask<Result<JsonElement>> DestroyChannelAsync(JsonElement arguments, CancellationToken ct)
    {
        var target = ReadAddress(arguments, "address");
        if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
        var destroyed = await DestroyAsync(target.Value, ct).ConfigureAwait(false);
        return destroyed.Ok
            ? Result<JsonElement>.Success(Json(new { address = target.Value.ToString(), destroyed = true }))
            : Result<JsonElement>.Failure(destroyed.Error!);
    }

    private static Result<Address> BuildAddress(string typeId, string instance)
    {
        if (typeId.Contains('#') || instance.Contains('#') || instance.Any(char.IsWhiteSpace))
            return Result<Address>.Failure(new(ErrorCode.Invalid, $"Not a usable instance name: {instance}"));
        if (!Address.TryParse($"{typeId}#{instance}", out var address) || !address.IsInstance)
            return Result<Address>.Failure(new(ErrorCode.Invalid, $"Not an instance address: {typeId}#{instance}"));
        return Result<Address>.Success(address);
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
