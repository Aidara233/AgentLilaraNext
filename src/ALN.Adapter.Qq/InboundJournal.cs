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
/// One row per message, keyed by event id: appending is an insert, acknowledging is a delete, and
/// "what will a restart replay" is a select. The capacity is a safety valve applied by the store.
/// </summary>
public sealed class InboundJournal(IQueuedMessageStore? queue, IModuleLogger log, Address owner, int capacity = 512)
{
    public const string QueueName = "qq.inbound";
    public const int DefaultCapacity = 512;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>True when nothing will be written down: the adapter still works, just without the promise.</summary>
    public bool IsDurable => queue is not null;

    public async ValueTask<Result> RememberAsync(AdapterEnvelope envelope, CancellationToken cancellationToken)
    {
        if (queue is null) return Result.Success();
        // The payload is the envelope exactly as it would have been published: a replay must not have to
        // reconstruct anything. The columns beside it are there so a person can ask questions.
        var entry = new QueuedMessage(
            envelope.EventId.ToString("D"),
            envelope.PlatformMessageId,
            envelope.PlatformConversationId,
            envelope.Sender,
            envelope.Text ?? string.Empty,
            envelope.At,
            JsonSerializer.Serialize(envelope, JsonOptions));
        return await queue.EnqueueAsync(QueueName, owner, entry, capacity, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result> ForgetAsync(Guid eventId, CancellationToken cancellationToken)
    {
        if (queue is null) return Result.Success();
        return await queue.AcknowledgeAsync(QueueName, owner, eventId.ToString("D"), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<IReadOnlyList<AdapterEnvelope>>> ReadAsync(CancellationToken cancellationToken)
    {
        if (queue is null) return Result<IReadOnlyList<AdapterEnvelope>>.Success([]);
        var stored = await queue.ListAsync(QueueName, owner, cancellationToken).ConfigureAwait(false);
        if (stored.Error is { } readError) return Result<IReadOnlyList<AdapterEnvelope>>.Failure(readError);
        var envelopes = new List<AdapterEnvelope>(stored.Value!.Count);
        foreach (var entry in stored.Value!)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<AdapterEnvelope>(entry.Payload, JsonOptions);
                if (envelope is not null) envelopes.Add(envelope);
            }
            catch (JsonException ex)
            {
                // One unreadable row must not stop the others from being replayed, and it must not be replayed
                // as garbage either: say so and move on.
                log.Warn("an inbound journal entry was unreadable and was dropped", new Dictionary<string, string>
                {
                    ["event"] = entry.EventId,
                    ["error"] = ex.Message
                });
                await queue.AcknowledgeAsync(QueueName, owner, entry.EventId, cancellationToken).ConfigureAwait(false);
            }
        }
        return Result<IReadOnlyList<AdapterEnvelope>>.Success(envelopes);
    }
}
