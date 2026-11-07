using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The single execution point. Every front-end — script bindings and the HTTP API alike — reaches
/// actions through here, so authorization, audit and signals cannot be skipped by adding a new
/// transport. It holds no feature logic: handlers are registered by the composition root.
/// </summary>
public sealed class ControlPlane(IAuditSink audit, ISignalLog signalLog) : IControlPlane
{
    private readonly Dictionary<string, Entry> _actions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IReadOnlyList<ActionDescriptor> Catalog
    {
        get { lock (_gate) return _actions.Values.Select(e => e.Descriptor).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray(); }
    }

    public Result Register(ActionDescriptor descriptor, ActionHandler handler)
    {
        if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.Id) || string.IsNullOrWhiteSpace(descriptor.Capability))
            return Result.Failure(new(ErrorCode.Invalid, "Action requires an id and a capability."));
        if (handler is null) return Result.Failure(new(ErrorCode.Invalid, "Action requires a handler."));
        lock (_gate)
        {
            if (!_actions.TryAdd(descriptor.Id, new(descriptor, handler)))
                return Result.Failure(new(ErrorCode.Conflict, $"Action is already registered: {descriptor.Id}"));
        }
        return Result.Success();
    }

    public async ValueTask<Result<JsonElement>> InvokeAsync(Actor actor, string action, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (actor is null) return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "Actor is required."));
        var actionId = action ?? "";
        Entry? entry;
        lock (_gate) _actions.TryGetValue(actionId, out entry);
        if (entry is null)
        {
            // Unknown actions are not an authorization problem: they simply do not exist.
            await RecordAsync(actor, actionId, "unknown", SubjectOf(null, arguments), null, cancellationToken).ConfigureAwait(false);
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"Unknown action: {actionId}"));
        }
        // Read before the call: a handler that fails may still have changed something, and the thing it was asked to
        // change is the half of the story that survives.
        var subject = SubjectOf(entry.Descriptor, arguments);
        if (!actor.Allows(entry.Descriptor.Capability) || actor.Level < entry.Descriptor.RequiredLevel)
        {
            // Two refusals that read differently, because a caller can act on exactly one of them: "not your kind of
            // work" is a wiring mistake or a role that has not been granted yet, while "not your depth" is a job for
            // somebody else. Flattening them into one message teaches the model to retry the wrong thing.
            var required = entry.Descriptor.RequiredLevel;
            var reason = actor.IsGranted(entry.Descriptor.Capability) && actor.Level < required
                ? $"Actor {actor.Id} is at {AccessLevels.Describe(actor.Level)}; {actionId} needs "
                  + $"{AccessLevels.Describe(required)}. Somebody at that level has to run this one."
                : $"Actor {actor.Id} lacks capability {entry.Descriptor.Capability}.";
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, Guid.Empty, actor.Id,
                new Address("control", "action", actionId), actionId, "denied", Describe(subject, reason)),
                cancellationToken).ConfigureAwait(false);
            await RecordAsync(actor, actionId, "denied", subject, reason, cancellationToken).ConfigureAwait(false);
            return Result<JsonElement>.Failure(new(ErrorCode.Denied, reason));
        }

        try
        {
            // "You may delegate" must not become "you may promote yourself". A verb that hands out a level names the
            // argument carrying it, and this is the one place that compares it with the caller's own — a handler that
            // had to remember the rule would be a hole nobody could see from the outside.
            if (entry.Descriptor.AssignsLevel is { Length: > 0 } assigns
                && AssignedLevel(assigns, arguments) is { } handed && handed > actor.Level)
            {
                var overreach = $"Actor {actor.Id} is at {AccessLevels.Describe(actor.Level)} and cannot hand out "
                    + $"{AccessLevels.Describe(handed)}: a level may only be given away at or below your own.";
                await audit.AppendAsync(new(DateTimeOffset.UtcNow, Guid.Empty, actor.Id,
                    new Address("control", "action", actionId), actionId, "denied", Describe(subject, overreach)),
                    cancellationToken).ConfigureAwait(false);
                await RecordAsync(actor, actionId, "denied", subject, overreach, cancellationToken).ConfigureAwait(false);
                return Result<JsonElement>.Failure(new(ErrorCode.Denied, overreach));
            }

            var result = await entry.Handler(arguments, cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, Guid.Empty, actor.Id,
                new Address("control", "action", actionId), actionId, result.Ok ? "allowed" : "failed",
                Describe(subject, result.Ok ? null : result.Error?.Message)), cancellationToken).ConfigureAwait(false);
            await RecordAsync(actor, actionId, result.Ok ? "ok" : "failed", subject, result.Error?.Message, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, Guid.Empty, actor.Id,
                new Address("control", "action", actionId), actionId, "failed", Describe(subject, ex.Message)),
                cancellationToken).ConfigureAwait(false);
            await RecordAsync(actor, actionId, "failed", subject, ex.Message, cancellationToken).ConfigureAwait(false);
            return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, $"Action failed: {actionId}", ex.Message));
        }
    }

    /// <summary>
    /// The level an argument asks to hand out, or null when it says nothing readable — absent and unreadable both mean
    /// "no opinion here", and the handler answers for its own arguments. Only a value above the caller's is this
    /// class's business.
    /// </summary>
    private static AccessLevel? AssignedLevel(string argument, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(argument, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) && Enum.IsDefined((AccessLevel)number)
                => (AccessLevel)number,
            JsonValueKind.String when Enum.TryParse<AccessLevel>(value.GetString(), ignoreCase: true, out var named)
                => named,
            _ => null
        };
    }

    /// <summary>
    /// The argument the action says names the thing it acts on, as text. An action that does not say which argument
    /// that is gets no subject rather than a guess: recording the wrong field is worse than recording none, because
    /// the record then reads like an answer.
    /// </summary>
    private static string? SubjectOf(ActionDescriptor? descriptor, JsonElement arguments)
    {
        if (descriptor?.Subject is not { Length: > 0 } name) return null;
        if (arguments.ValueKind != JsonValueKind.Object) return null;
        if (!arguments.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String
                => address.GetString(),
            _ => null
        };
    }

    private static string? Describe(string? subject, string? message) => (subject, message) switch
    {
        (null, null) => null,
        (null, { } only) => only,
        ({ } who, null) => $"subject={who}",
        ({ } who, { } why) => $"subject={who}; {why}"
    };

    private ValueTask RecordAsync(Actor actor, string action, string outcome, string? subject, string? message,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string>? detail = null;
        if (subject is not null || message is not null)
        {
            detail = new Dictionary<string, string>(StringComparer.Ordinal);
            // The subject is what makes "who changed what" a question with an answer. The arguments themselves are
            // deliberately not recorded: one of them may be a credential, and a log is the last place that belongs.
            if (subject is not null) detail["subject"] = subject;
            if (message is not null) detail["message"] = message;
        }
        signalLog.Write(new()
        {
            Group = "control",
            Name = action,
            From = actor.Id,
            Mechanism = "invoke",
            Outcome = outcome,
            Level = outcome == "ok" ? SignalLevel.Info : SignalLevel.Warn,
            Detail = detail
        });
        return ValueTask.CompletedTask;
    }

    private sealed record Entry(ActionDescriptor Descriptor, ActionHandler Handler);
}
