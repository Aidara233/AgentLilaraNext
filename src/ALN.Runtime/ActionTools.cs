using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The action table, offered to a model: what this process can do, and one way to ask it.
///
/// The point of two tools instead of forty-two is that the table grows without the tool list growing with it. Every
/// action is a verb some module declared; a projection per verb would mean the request's prefix changes whenever a
/// module does, and a changed prefix is a warm cache thrown away. Two names that never change, and a table that can
/// grow underneath them, is the shape that survives the next fifty verbs.
///
/// What this does not do is re-describe any verb. The arguments come from the action's own schema, the authorization
/// from the actor, the audit and the signal from the control plane's single execution point. A second place to keep a
/// verb's rules is a second place for them to be wrong.
/// </summary>
public sealed class ActionTools(IControlPlane plane) : ISessionToolSource
{
    public const string SourceId = "system.action-tools";
    public const string ListToolName = "actions_list";
    public const string CallToolName = "actions_call";

    /// <summary>
    /// Who the model is when it calls an action. The id is not a display name: it is what the audit record says, so a
    /// change made by a model can be told apart from one made by a person with the same intent.
    /// </summary>
    public const string ActorId = "model";

    /// <summary>
    /// What a model may do. Everything: this host's stance is that the model is an operator of this system, not a
    /// guest of it, so the grant is the whole table rather than a list somebody keeps in step by hand. What is kept is
    /// the record — every call writes down who, what and against which subject.
    /// </summary>
    public static readonly IReadOnlyList<string> ModelCapabilities = [Actor.Any];

    private readonly IControlPlane _plane = plane;

    public string Id => SourceId;

    public static readonly ModelToolDefinition ListDefinition = new(ListToolName,
        "Every action this process offers: its id, what it changes, which capability guards it, and the arguments it "
        + "takes. Filter with 'prefix' (e.g. \"channels.\") when you know roughly what you are after. Look things up "
        + "here instead of guessing an id: an id that does not exist comes back as unknown, and the arguments belong "
        + "to the action, not to actions_call.",
        """
        {"type":"object","properties":{
          "prefix":{"type":"string","description":"Only actions whose id starts with this. Optional."},
          "schemas":{"type":"boolean","description":"Include each action's full argument schema. Default false: only the required field names."}},
         "additionalProperties":false}
        """);

    public static readonly ModelToolDefinition CallDefinition = new(CallToolName,
        "Run one action by id. The arguments are that action's own — take them from actions_list. Reversible changes "
        + "(listing, reading state) cost nothing; the rest change this running system from now on, and the answer says "
        + "what changed. Unknown and denied are different answers: unknown means the action does not exist, denied means "
        + "it does and you may not call it.",
        """
        {"type":"object","properties":{
          "action":{"type":"string","description":"The action id, exactly as actions_list shows it."},
          "args":{"type":"object","description":"That action's arguments. Empty object when it takes none."}},
         "required":["action"],"additionalProperties":false}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The level travels with the round, not with this object. It is the one number an operator may change while the
        // Session runs, and this source is registered once for the whole process: reading it at construction made the
        // change visible on disk, in the audit and in the signal while the model kept the authority it started with —
        // which is the opposite of what setting a level is for. Both tools take it from the same request, so the list
        // and the call cannot disagree about what this caller may reach.
        var level = request.Level;
        IReadOnlyList<SessionToolOffer> offers =
        [
            new(new ListActionTool(this, level), ListDefinition, ActsExternally: false),
            // Calling an action can talk to the outside world (channels.send), but the tool itself is not the reply:
            // whatever the model wants a person to read, it still has to say.
            new(new CallActionTool(this, level), CallDefinition, ActsExternally: false)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private sealed class ListActionTool(ActionTools owner, AccessLevel level) : ISessionTool
    {
        public string Name => ListToolName;
        public bool EndsTurn => false;

        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var arguments = document.RootElement;
            var prefix = Text(arguments, "prefix") ?? "";
            var schemas = arguments.ValueKind == JsonValueKind.Object &&
                          arguments.TryGetProperty("schemas", out var flag) && flag.ValueKind == JsonValueKind.True;

            var actions = owner._plane.Catalog
                .Where(action => action.Id.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            if (actions.Length == 0)
                return ValueTask.FromResult(Result<SessionToolResult>.Success(new(prefix.Length == 0
                    ? "This process offers no actions."
                    : $"No action starts with \"{prefix}\". {owner._plane.Catalog.Count} action(s) exist; list them all with no prefix.")));

            var text = new StringBuilder();
            text.Append(actions.Length).Append(prefix.Length == 0 ? " action(s):" : $" action(s) starting with \"{prefix}\":");
            foreach (var action in actions)
            {
                text.Append("\n- ").Append(action.Id)
                    .Append(" [").Append(action.Capability).Append(']')
                    .Append(action.Mutating ? " changes state" : " read-only");
                // Only the lines this caller cannot reach name their level. A list where every line carries a number
                // the reader already satisfies buries the one line that says "not yours to run".
                if (level < action.RequiredLevel)
                    text.Append("\n  needs ").Append(AccessLevels.Describe(action.RequiredLevel))
                        .Append(" — somebody at that level runs this one.");
                text.Append("\n  ").Append(action.Summary);
                var required = RequiredOf(action.ParamsJson);
                text.Append("\n  args: ").Append(required.Length == 0 ? "none" : string.Join(", ", required));
                if (schemas && action.ParamsJson is { Length: > 0 }) text.Append("\n  schema: ").Append(action.ParamsJson);
            }
            return ValueTask.FromResult(Result<SessionToolResult>.Success(new(text.ToString())));
        }
    }

    private sealed class CallActionTool(ActionTools owner, AccessLevel level) : ISessionTool
    {
        public string Name => CallToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var arguments = document.RootElement;
            var action = Text(arguments, "action");
            if (string.IsNullOrWhiteSpace(action))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "actions_call requires 'action'."));
            var actionId = action.Trim();

            // The arguments are passed through untouched: validating them here would be a second description of them,
            // and the handler that owns the action already knows what it accepts.
            var payload = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("args", out var args)
                ? args.Clone()
                : JsonSerializer.SerializeToElement(new { });

            var actor = new Actor(ActorId, ActorKind.Model, ModelCapabilities, level);
            var result = await owner._plane.InvokeAsync(actor, actionId, payload, cancellationToken).ConfigureAwait(false);

            var text = new StringBuilder();
            text.Append(result.Ok ? "ok: " : "error: ").Append(actionId);
            if (!result.Ok)
            {
                // The code is the part a caller can act on: unknown means it does not exist, denied means it does,
                // invalid means the arguments are wrong. Flattening them into "failed" throws that away.
                text.Append("\ncode: ").Append(CodeName(result.Error?.Code));
                text.Append("\n").Append(result.Error?.Message);
                if (!string.IsNullOrWhiteSpace(result.Error?.Detail)) text.Append("\n").Append(result.Error!.Detail);
                if (result.Error?.Code == ErrorCode.Invalid && !actionId.StartsWith("unknown", StringComparison.Ordinal))
                    text.Append("\nIf the action exists, check its arguments with actions_list.");
                return Result<SessionToolResult>.Success(new(text.ToString()));
            }

            text.Append("\n").Append(Compact(result.Value));
            return Result<SessionToolResult>.Success(new(text.ToString()));
        }
    }

    private static string CodeName(ErrorCode? code) => code switch
    {
        null => "unknown",
        ErrorCode.Invalid => "invalid",
        ErrorCode.Denied => "denied",
        ErrorCode.Conflict => "conflict",
        ErrorCode.Unavailable => "unavailable",
        _ => code.Value.ToString().ToLowerInvariant()
    };

    /// <summary>The required field names, read out of the action's own schema: one description, not two.</summary>
    private static string[] RequiredOf(string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson)) return [];
        try
        {
            using var document = JsonDocument.Parse(paramsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("required", out var required) &&
                   required.ValueKind == JsonValueKind.Array
                ? required.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToArray()
                : [];
        }
        catch (JsonException) { return []; }
    }

    private static string Compact(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => "(no result)",
        JsonValueKind.String => value.GetString() ?? "",
        _ => value.GetRawText()
    };

    private static string? Text(JsonElement arguments, string property) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var found) &&
        found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;
}

/// <summary>
/// The module that offers the action tools: one instance, one tool source, no configuration of its own.
///
/// A module for the same reason the script tools are one: a model's tools are a module's business here, and whether
/// the model can reach the action table should be answerable by looking at the graph.
/// </summary>
[Module(TypeId, Category = "actions")]
public sealed class ActionToolsModule(ModuleId id, ActionTools tools, ISessionExtensionRegistry extensions) : IKernelModule
{
    public const string TypeId = "actions.control";

    private bool _registered;

    public ModuleId Id { get; } = id;

    public static Result<IKernelModule> FromManifest(ModuleManifest manifest, IControlPlane plane,
        ISessionExtensionRegistry extensions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Type, TypeId, StringComparison.Ordinal))
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid, $"This factory builds {TypeId}, not {manifest.Type}."));
        return Result<IKernelModule>.Success(new ActionToolsModule(manifest.Id, new ActionTools(plane), extensions));
    }

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = extensions.Register(tools);
        if (!registered.Ok) return ValueTask.FromResult(registered);
        _registered = true;
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>Nothing to warm up: the tools read the table as it is at the moment they are called.</summary>
    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            extensions.Unregister(tools.Id);
            _registered = false;
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}
