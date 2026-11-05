using System.Text.Json;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>
/// Wraps the event sink so every inbound Adapter message is recorded once, at the single boundary
/// adapters publish through. Adapters stay ignorant of storage.
/// </summary>
public sealed class RecordingEventSink(IEventSink inner, IMessageStore messages) : IEventSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<Result> PublishAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
    {
        if (routedEvent.Name == EventNames.AdapterEnvelope)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<AdapterEnvelope>(routedEvent.PayloadJson, JsonOptions);
                if (envelope is not null)
                    await messages.AppendAsync(new StoredMessage(envelope.EventId.ToString("N"), envelope.Adapter,
                        envelope.PlatformConversationId, envelope.PlatformMessageId, envelope.Sender, envelope.Text,
                        envelope.At, SenderName: envelope.SenderName, SenderRemark: envelope.SenderRemark),
                        cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) { /* a malformed envelope is still dispatched; recording must not block it */ }
        }
        return await inner.PublishAsync(routedEvent, cancellationToken).ConfigureAwait(false);
    }
}
