using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The core's only startup action: send one signal down an ordered chain of managers.
/// It does not create adapters, load channels or start timers itself — each manager starts its own
/// children when the signal reaches it, and forwards the same shape one level further.
/// </summary>
public sealed class LifecycleCoordinator(IEventSink events, ISignalLog signalLog, Address? coreAddress = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<ILifecycleParticipant> _participants = [];
    private readonly object _gate = new();
    private bool _started;

    public Address Core { get; } = coreAddress ?? new Address("core", "host", "1");
    public IReadOnlyList<string> Order
    {
        get { lock (_gate) return _participants.Select(p => p.Name).ToArray(); }
    }

    public Result Register(ILifecycleParticipant participant)
    {
        if (participant is null || string.IsNullOrWhiteSpace(participant.Name))
            return Result.Failure(new(ErrorCode.Invalid, "Lifecycle participant requires a name."));
        lock (_gate)
        {
            if (_started) return Result.Failure(new(ErrorCode.Conflict, "Lifecycle already started; register participants before StartAsync."));
            if (_participants.Any(p => p.Name == participant.Name))
                return Result.Failure(new(ErrorCode.Conflict, $"Lifecycle participant is already registered: {participant.Name}"));
            _participants.Add(participant);
        }
        return Result.Success();
    }

    /// <summary>Start order is registration order: dependencies must be registered before their users.</summary>
    public async ValueTask<Result> StartAsync(CancellationToken cancellationToken)
    {
        ILifecycleParticipant[] participants;
        lock (_gate)
        {
            if (_started) return Result.Success();
            _started = true;
            participants = _participants.ToArray();
        }
        var signal = new LifecycleSignal(LifecyclePhase.Start, DateTimeOffset.UtcNow);
        foreach (var participant in participants)
        {
            var started = await StartOneAsync(participant, signal, cancellationToken).ConfigureAwait(false);
            if (!started.Ok) return started;
        }
        // One observer event for the whole tree, so a script can react to "everything is up".
        await events.PublishAsync(new(EventNames.LifecycleStarted, Core, Guid.NewGuid(),
            JsonSerializer.Serialize(signal, JsonOptions)), cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private async ValueTask<Result> StartOneAsync(ILifecycleParticipant participant, LifecycleSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await participant.StartAsync(signal, cancellationToken).ConfigureAwait(false);
            signalLog.Write(new()
            {
                Group = "lifecycle",
                Name = participant.Name,
                Mechanism = "start",
                Outcome = result.Ok ? "ok" : "failed",
                Level = result.Ok ? SignalLevel.Info : SignalLevel.Warn,
                Detail = result.Ok ? null : new Dictionary<string, string> { ["message"] = result.Error?.Message ?? "" }
            });
            return result.Ok ? Result.Success() : result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            signalLog.Write(new()
            {
                Group = "lifecycle",
                Name = participant.Name,
                Mechanism = "start",
                Outcome = "failed",
                Level = SignalLevel.Warn,
                Detail = new Dictionary<string, string> { ["message"] = ex.Message }
            });
            return Result.Failure(new(ErrorCode.Unavailable, $"{participant.Name} failed to start.", ex.Message));
        }
    }

    /// <summary>Shutdown is the same signal in reverse order, so dependencies die before their users.</summary>
    public async ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        ILifecycleParticipant[] participants;
        lock (_gate) participants = _participants.AsEnumerable().Reverse().ToArray();
        var signal = new LifecycleSignal(LifecyclePhase.Stop, DateTimeOffset.UtcNow);
        Error? firstFailure = null;
        foreach (var participant in participants)
        {
            try
            {
                var result = await participant.StopAsync(signal, cancellationToken).ConfigureAwait(false);
                if (!result.Ok) firstFailure ??= result.Error;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                firstFailure ??= new(ErrorCode.Unavailable, $"{participant.Name} failed to stop.", ex.Message);
            }
            signalLog.Write(new()
            {
                Group = "lifecycle",
                Name = participant.Name,
                Mechanism = "stop",
                Outcome = "ok",
                Level = SignalLevel.Info
            });
        }
        return firstFailure is null ? Result.Success() : Result.Failure(firstFailure);
    }
}
