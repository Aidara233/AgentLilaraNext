using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The adapter verbs, declared by the manager that implements them.
///
/// Same reason as the Channel's: the host used to implement every verb in the system, so it had to know how an adapter
/// spec is validated, what a direction typo should do, and which type tree decides whether a type can be built at all.
/// The manager already knew all three.
/// </summary>
public sealed partial class AdapterManager
{
    public const string ToolHint = "Create one instance with adapters.create (not modules.load: that one loads an "
        + "existing directory into the graph).";

    /// <summary>Registers this module's actions. Called once, by whoever built the manager.</summary>
    public Result RegisterActions(IControlPlane plane)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(AdapterActions.List, "List adapters without credentials: what each one claims to be, and — when the "
                    + "adapter can tell — who it actually is and whether the connection is up.", Capabilities.AdaptersRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ActionArgs.JsonOf(List().Select(a => new
                {
                    address = a.Address.ToString(),
                    platform = a.Descriptor.PlatformId,
                    // The declared label, and — when the adapter can learn it — who it actually is.
                    account = Identity(a.Address)?.AccountId ?? a.Descriptor.AccountId,
                    declaredAccount = a.Descriptor.AccountId,
                    displayName = Identity(a.Address)?.DisplayName,
                    direction = a.Descriptor.Direction.ToString(),
                    state = a.State.ToString(),
                    // Whether the connection behind the state is actually up. Null when the adapter type has no opinion,
                    // so "false" never means "unknown".
                    connected = Health(a.Address)?.Connected,
                    detail = Health(a.Address)?.HealthDetail
                }))))),

            (new(AdapterActions.Start, "Start one adapter.", Capabilities.AdaptersControl, true,
                ParamsJson: AddressArgs("The adapter to start."), Subject: "address"),
                (arguments, ct) => ChangeRunningAsync(arguments, ct, running: true)),
            (new(AdapterActions.Stop, "Stop one adapter.", Capabilities.AdaptersControl, true,
                ParamsJson: AddressArgs("The adapter to stop."), Subject: "address"),
                (arguments, ct) => ChangeRunningAsync(arguments, ct, running: false)),

            (new(AdapterActions.Create, "Create one adapter instance from its own arguments. " + ToolHint
                    + " The instance is named by the caller, or after its type with a fresh suffix.", Capabilities.AdaptersControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "type":{"type":"string","description":"A concrete adapter type, e.g. adapter.file or adapter.qq."},
                  "instance":{"type":"string","description":"The instance name. Default: a fresh suffix."},
                  "platform":{"type":"string","description":"Platform id. Default: the type's own name (\"file\")."},
                  "account":{"type":"string","description":"Account id on that platform. Default: the instance name."},
                  "direction":{"type":"string","enum":["inbound","outbound","both"],"description":"Which half may be used. Default both."},
                  "settings":{"type":"object","description":"Flat string map owned by the adapter type. Optional."}},
                 "required":["type"],"additionalProperties":false}
                """, Subject: "instance"), CreateAdapterAsync),
            (new(AdapterActions.Destroy, "Destroy one adapter instance and forget its spec. Its mailbox directory stays on "
                    + "disk: the mail in it is not the manager's to throw away.", Capabilities.AdaptersControl, true,
                ParamsJson: AddressArgs("The adapter to destroy."), Subject: "address"), DestroyAdapterAsync)
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

    private async ValueTask<Result<JsonElement>> ChangeRunningAsync(JsonElement arguments, CancellationToken ct, bool running)
    {
        var target = arguments.ReadAddress("address");
        if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
        var changed = running
            ? await StartAsync(target.Value, ct).ConfigureAwait(false)
            : await StopAsync(target.Value, ct).ConfigureAwait(false);
        return changed.Ok
            ? Result<JsonElement>.Success(ActionArgs.JsonOf(new { address = target.Value.ToString(), state = running ? "running" : "stopped" }))
            : Result<JsonElement>.Failure(changed.Error!);
    }

    /// <summary>
    /// Creates one adapter from a form's worth of arguments.
    ///
    /// The address is built from the type and the instance name, never from an argument that spells it out (see
    /// <see cref="ActionArgs.BuildAddress"/>). The type is checked against the type tree here rather than only in the
    /// action, so a create from anywhere is judged the same way.
    /// </summary>
    private async ValueTask<Result<JsonElement>> CreateAdapterAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryReadText("type", out var typeId) || typeId.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "adapters.create requires 'type'."));
        var type = typeId.Trim();
        if (!_types.IsAssignableTo(type, "adapter"))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"Not an adapter type: {type}"));
        if (!_types.IsConcrete(type))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"Adapter type is not registered in this process: {type}"));

        // C-01: an instance is named when it is created and never renamed. A caller may name it; if it does not, the name
        // is the type's own prefix plus a fresh suffix — readable to a person, never parsed by the code.
        var instanceName = arguments.TryReadText("instance", out var asked) && asked.Trim().Length > 0
            ? asked.Trim()
            : ModuleId.NewSuffix();
        var address = ActionArgs.BuildAddress(type, instanceName);
        if (!address.Ok) return Result<JsonElement>.Failure(address.Error!);

        var platform = arguments.TryReadText("platform", out var platformText) && platformText.Trim().Length > 0
            ? platformText.Trim()
            : address.Value.Kind!;
        var account = arguments.TryReadText("account", out var accountText) && accountText.Trim().Length > 0
            ? accountText.Trim()
            : instanceName;
        // Direction decides which half of IChatAdapter anyone may call, so a typo must be refused rather than defaulted:
        // "inboud" quietly becoming Both turns an inbound-only account into a sender.
        var direction = AdapterDirection.Both;
        if (arguments.TryReadText("direction", out var directionText) && directionText.Trim().Length > 0)
        {
            if (!Enum.TryParse<AdapterDirection>(directionText.Trim(), ignoreCase: true, out direction) || !Enum.IsDefined(direction))
                return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                    $"adapters.create 'direction' must be inbound, outbound or both, not '{directionText}'."));
        }

        var settings = arguments.ReadTextMap("settings", out var settingsError);
        if (settingsError is not null) return Result<JsonElement>.Failure(settingsError);

        var spec = new AdapterSpec(address.Value, new AdapterDescriptor(platform, account, direction), type, settings);
        var created = await CreateAsync(spec, ct).ConfigureAwait(false);
        if (!created.Ok || created.Value is null)
            return Result<JsonElement>.Failure(created.Error ?? new(ErrorCode.Unavailable, "Adapter could not be created."));
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            address = address.Value.ToString(),
            type,
            platform,
            account,
            direction = direction.ToString().ToLowerInvariant(),
            // An adapter is created stopped and has to be started to do anything; saying so here is the difference
            // between "your adapter is broken" and "your adapter is waiting".
            note = "created stopped: call adapters.start to connect it"
        }));
    }

    private async ValueTask<Result<JsonElement>> DestroyAdapterAsync(JsonElement arguments, CancellationToken ct)
    {
        var target = arguments.ReadAddress("address");
        if (!target.Ok) return Result<JsonElement>.Failure(target.Error!);
        var destroyed = await DestroyAsync(target.Value, ct).ConfigureAwait(false);
        return destroyed.Ok
            ? Result<JsonElement>.Success(ActionArgs.JsonOf(new { address = target.Value.ToString(), destroyed = true }))
            : Result<JsonElement>.Failure(destroyed.Error!);
    }

    private IAdapterIdentity? Identity(Address address)
    {
        var found = Get(address);
        return found.Ok ? found.Value as IAdapterIdentity : null;
    }

    /// <summary>The optional health half: the state says whether somebody asked it to run, this says whether it is up.</summary>
    private IAdapterHealth? Health(Address address)
    {
        var found = Get(address);
        return found.Ok ? found.Value as IAdapterHealth : null;
    }
}
