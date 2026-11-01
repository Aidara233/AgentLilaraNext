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
            await RecordAsync(actor, actionId, "unknown", null, cancellationToken).ConfigureAwait(false);
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"Unknown action: {actionId}"));
        }
        if (!actor.Allows(entry.Descriptor.Capability))
        {
            var reason = $"Actor {actor.Id} lacks capability {entry.Descriptor.Capability}.";
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, Guid.Empty, actor.Id,
                new Address("control", "action", actionId), actionId, "denied", reason), cancellationToken).ConfigureAwait(false);
            await RecordAsync(actor, actionId, "denied", reason, cancellationToken).ConfigureAwait(false);
            return Result<JsonElement>.Failure(new(ErrorCode.Denied, reason));
        }

        try
        {
            var result = await entry.Handler(arguments, cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, Guid.Empty, actor.Id,
                new Address("control", "action", actionId), actionId, result.Ok ? "allowed" : "failed",
                result.Ok ? null : result.Error?.Message), cancellationToken).ConfigureAwait(false);
            await RecordAsync(actor, actionId, result.Ok ? "ok" : "failed", result.Error?.Message, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, Guid.Empty, actor.Id,
                new Address("control", "action", actionId), actionId, "failed", ex.Message), cancellationToken).ConfigureAwait(false);
            await RecordAsync(actor, actionId, "failed", ex.Message, cancellationToken).ConfigureAwait(false);
            return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, $"Action failed: {actionId}", ex.Message));
        }
    }

    private ValueTask RecordAsync(Actor actor, string action, string outcome, string? message,
        CancellationToken cancellationToken)
    {
        signalLog.Write(new()
        {
            Group = "control",
            Name = action,
            From = actor.Id,
            Mechanism = "invoke",
            Outcome = outcome,
            Level = outcome == "ok" ? SignalLevel.Info : SignalLevel.Warn,
            Detail = message is null ? null : new Dictionary<string, string> { ["message"] = message }
        });
        return ValueTask.CompletedTask;
    }

    private sealed record Entry(ActionDescriptor Descriptor, ActionHandler Handler);
}
