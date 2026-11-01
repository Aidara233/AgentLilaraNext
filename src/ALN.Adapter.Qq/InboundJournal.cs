using System.Text.Json;
using ALN.Contracts;

namespace ALN.Adapter.Qq;

/// <summary>
/// Messages the socket delivered but the process has not finished handing over.
///
/// OneBot over a forward WebSocket does not replay: once an event is read, this process is the only place it
/// exists. So it is written down before it is published, and removed only after the event round that carries
/// it is over. A restart publishes whatever is still here, with the original event id, and the acknowledgement
/// that follows is the same one the first delivery would have produced.
///
/// Bounded on purpose: an acknowledgement that never arrives must not fill the disk, so the oldest entries are
/// given up on — loudly.
/// </summary>
public sealed class InboundJournal(IStateStore? state, IModuleLogger log, string key, int capacity = 512)
{
    public const string Scope = "qq.inbound";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True when nothing will be written down: the adapter still works, just without the promise.</summary>
    public bool IsDurable => state is not null;

    public async ValueTask<Result> RememberAsync(AdapterEnvelope envelope, CancellationToken cancellationToken)
    {
        if (state is null) return Result.Success();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (entries.Error is { } readError) return Result.Failure(readError);
            var kept = entries.Value!.Where(existing => existing.EventId != envelope.EventId).ToList();
            kept.Add(envelope);
            if (kept.Count > capacity)
            {
                var dropped = kept.Count - capacity;
                kept.RemoveRange(0, dropped);
                log.Warn("inbound journal is full; dropping the oldest undelivered messages", new Dictionary<string, string>
                {
                    ["dropped"] = dropped.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["capacity"] = capacity.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            }
            return await WriteAsync(kept, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> ForgetAsync(Guid eventId, CancellationToken cancellationToken)
    {
        if (state is null) return Result.Success();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (entries.Error is { } readError) return Result.Failure(readError);
            var kept = entries.Value!.Where(existing => existing.EventId != eventId).ToList();
            if (kept.Count == entries.Value!.Count) return Result.Success();
            return await WriteAsync(kept, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result<IReadOnlyList<AdapterEnvelope>>> ReadAsync(CancellationToken cancellationToken)
    {
        if (state is null) return Result<IReadOnlyList<AdapterEnvelope>>.Success([]);
        var read = await state.ReadAsync(Scope, key, cancellationToken).ConfigureAwait(false);
        if (read.Error is { } readError) return Result<IReadOnlyList<AdapterEnvelope>>.Failure(readError);
        // Absent is not an error: Result<T>.Ok is false for a null value, so absence is read off Error.
        if (read.Value is null) return Result<IReadOnlyList<AdapterEnvelope>>.Success([]);
        try
        {
            var stored = JsonSerializer.Deserialize<JournalFile>(read.Value, JsonOptions);
            return Result<IReadOnlyList<AdapterEnvelope>>.Success(stored?.Events ?? []);
        }
        catch (JsonException ex)
        {
            // Refusing to boot over one unreadable state file helps nobody, and replaying a half-parsed
            // journal is worse than saying so and starting clean.
            log.Warn("inbound journal was unreadable; starting with an empty one", new Dictionary<string, string>
            {
                ["key"] = key,
                ["error"] = ex.Message
            });
            return Result<IReadOnlyList<AdapterEnvelope>>.Success([]);
        }
    }

    private async ValueTask<Result> WriteAsync(IReadOnlyList<AdapterEnvelope> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return await state!.DeleteAsync(Scope, key, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(new JournalFile(1, entries.ToArray()), JsonOptions);
        return await state!.WriteAsync(Scope, key, json, cancellationToken).ConfigureAwait(false);
    }

    private sealed record JournalFile(int Schema, IReadOnlyList<AdapterEnvelope> Events);
}
