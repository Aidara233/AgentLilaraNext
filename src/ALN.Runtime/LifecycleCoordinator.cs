using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The core's only startup action: hand one signal to every manager.
///
/// Not a pipeline. Participants start <b>concurrently</b> and a failure is <b>isolated</b>: it is
/// reported, its dependents are skipped (visibly, never silently absent), and everything that does
/// not depend on it keeps running. Ordering exists only where the data model truly has a
/// dependency — a Channel is bound to an Adapter, so it declares that and waits one wave.
/// </summary>
public sealed class LifecycleCoordinator(IEventSink events, ISignalLog signalLog, Address? coreAddress = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<Entry> _entries = [];
    private readonly object _gate = new();
    private bool _started;

    public Address Core { get; } = coreAddress ?? new Address("core", "host", "1");
    public IReadOnlyList<string> Order
    {
        get { lock (_gate) return _entries.Select(e => e.Participant.Name).ToArray(); }
    }

    /// <summary>
    /// Registers a manager. <paramref name="dependsOn"/> is a statement about data, not a startup
    /// policy: name another participant only when this one cannot work until that one exists.
    /// </summary>
    public Result Register(ILifecycleParticipant participant, params string[] dependsOn)
    {
        if (participant is null || string.IsNullOrWhiteSpace(participant.Name))
            return Result.Failure(new(ErrorCode.Invalid, "Lifecycle participant requires a name."));
        lock (_gate)
        {
            if (_started) return Result.Failure(new(ErrorCode.Conflict, "Lifecycle already started; register participants before StartAsync."));
            if (_entries.Any(e => e.Participant.Name == participant.Name))
                return Result.Failure(new(ErrorCode.Conflict, $"Lifecycle participant is already registered: {participant.Name}"));
            _entries.Add(new(participant, dependsOn ?? []));
        }
        return Result.Success();
    }

    public async ValueTask<LifecycleReport> StartAsync(CancellationToken cancellationToken)
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_started) return new LifecycleReport([]);
            _started = true;
            entries = _entries.ToArray();
        }
        var signal = new LifecycleSignal(LifecyclePhase.Start, DateTimeOffset.UtcNow);
        var report = await RunWavesAsync(entries, signal, cancellationToken).ConfigureAwait(false);
        // One observer event for the whole tree, even when part of it failed: the state is worth knowing.
        await events.PublishAsync(new(EventNames.LifecycleStarted, Core, Guid.NewGuid(),
            JsonSerializer.Serialize(signal, JsonOptions)), cancellationToken).ConfigureAwait(false);
        return report;
    }

    private async ValueTask<LifecycleReport> RunWavesAsync(Entry[] entries, LifecycleSignal signal,
        CancellationToken cancellationToken)
    {
        var pending = new List<Entry>(entries);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        var outcomes = new List<LifecycleOutcome>();

        while (pending.Count > 0)
        {
            // Dependents of something that did not start are skipped with a reason; they are not
            // attempted and not silently dropped.
            foreach (var blocked in pending.Where(e => e.DependsOn.Any(unavailable.Contains)).ToArray())
            {
                var missing = string.Join(", ", blocked.DependsOn.Where(unavailable.Contains));
                outcomes.Add(new(blocked.Participant.Name, "skipped", $"Dependency did not start: {missing}"));
                Record(blocked.Participant.Name, "skipped", $"Dependency did not start: {missing}");
                unavailable.Add(blocked.Participant.Name);
                pending.Remove(blocked);
            }
            if (pending.Count == 0) break;

            var ready = pending.Where(e => e.DependsOn.All(completed.Contains)).ToArray();
            if (ready.Length == 0)
            {
                foreach (var stuck in pending)
                {
                    outcomes.Add(new(stuck.Participant.Name, "skipped", "Dependency cycle or unknown dependency."));
                    Record(stuck.Participant.Name, "skipped", "Dependency cycle or unknown dependency.");
                }
                break;
            }

            // One wave starts together: independent managers never wait for each other.
            var started = await Task.WhenAll(ready.Select(e => StartOneAsync(e.Participant, signal, cancellationToken)))
                .ConfigureAwait(false);
            for (var i = 0; i < ready.Length; i++)
            {
                var outcome = started[i];
                outcomes.Add(outcome);
                if (outcome.Outcome == "ok") completed.Add(ready[i].Participant.Name);
                else unavailable.Add(ready[i].Participant.Name);
                pending.Remove(ready[i]);
            }
        }
        return new LifecycleReport(outcomes);
    }

    private async Task<LifecycleOutcome> StartOneAsync(ILifecycleParticipant participant, LifecycleSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await participant.StartAsync(signal, cancellationToken).ConfigureAwait(false);
            var outcome = result.Ok
                ? new LifecycleOutcome(participant.Name, "ok")
                : new LifecycleOutcome(participant.Name, "failed", result.Error?.Message);
            Record(participant.Name, outcome.Outcome, outcome.Message);
            return outcome;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Record(participant.Name, "failed", ex.Message);
            return new LifecycleOutcome(participant.Name, "failed", ex.Message);
        }
    }

    private void Record(string name, string outcome, string? message)
    {
        signalLog.Write(new()
        {
            Group = "lifecycle",
            Name = name,
            Mechanism = "start",
            Outcome = outcome,
            Level = outcome == "ok" ? SignalLevel.Info : SignalLevel.Warn,
            Detail = message is null ? null : new Dictionary<string, string> { ["message"] = message }
        });
    }

    /// <summary>
    /// Shutdown mirrors startup: waves in reverse, so users stop before what they depend on.
    /// Failures are collected, never allowed to strand the rest of the tree mid-shutdown.
    /// </summary>
    public async ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        var signal = new LifecycleSignal(LifecyclePhase.Stop, DateTimeOffset.UtcNow);
        var started = entries.Reverse().ToArray();
        var failures = new List<string>();
        foreach (var entry in started)
        {
            try
            {
                var result = await entry.Participant.StopAsync(signal, cancellationToken).ConfigureAwait(false);
                if (!result.Ok) failures.Add($"{entry.Participant.Name}: {result.Error?.Message}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add($"{entry.Participant.Name}: {ex.Message}"); }
            signalLog.Write(new()
            {
                Group = "lifecycle",
                Name = entry.Participant.Name,
                Mechanism = "stop",
                Outcome = "ok",
                Level = SignalLevel.Info
            });
        }
        return failures.Count == 0
            ? Result.Success()
            : Result.Failure(new(ErrorCode.Unavailable, "Some participants failed to stop.", string.Join("; ", failures)));
    }

    private sealed record Entry(ILifecycleParticipant Participant, string[] DependsOn);
}
