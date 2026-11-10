using System.Text.Json;
using ALN.Contracts;

namespace ALN.Commands;

/// <summary>
/// The one look at an inbound Adapter event, before any Channel or script exists for it.
///
/// <b>Why here and not later.</b> An instruction is answered and then <i>gone</i>: no Channel row, no "accepted but
/// unfinished" marker, no delta, no Session turn. Everything downstream of this point would leave one of those behind —
/// a Channel writes history in <c>AcceptAsync</c>, and a script is handed the envelope before it decides to accept it.
/// So the instruction has to be taken out of the stream at the only place where the stream is still just an event.
///
/// The cost of being this early is that <b>nothing downstream deduplicates for us</b>: the Channel's duplicate check
/// keys on the platform message id and never sees this message. That is why the registry keeps its own counter.
/// </summary>
public sealed class CommandInterceptor(
    ICommandRegistry registry,
    IAdapterManager adapters,
    IPersonDirectory people) : IEnvelopeInterceptor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<Result<EnvelopeDisposition>> InterceptAsync(RoutedEvent routedEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routedEvent);
        if (!string.Equals(routedEvent.Name, EventNames.AdapterEnvelope, StringComparison.Ordinal))
            return Result<EnvelopeDisposition>.Success(EnvelopeDisposition.Pass);

        AdapterEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AdapterEnvelope>(routedEvent.PayloadJson, Json);
        }
        catch (JsonException)
        {
            // Not ours to judge: a payload this Layer cannot read is a bug in whoever published it, and the normal
            // path is where that failure gets reported with the whole event in hand.
            return Result<EnvelopeDisposition>.Success(EnvelopeDisposition.Pass);
        }

        if (envelope is null || !registry.LooksLike(envelope.Text))
            return Result<EnvelopeDisposition>.Success(EnvelopeDisposition.Pass);

        // The payload has to agree with the event it arrived in. Without this, a message could name somebody else's
        // Adapter and be answered through it — the same check the accept verb makes for the same reason.
        if (envelope.Adapter != routedEvent.Source || envelope.EventId != routedEvent.EventId)
            return Result<EnvelopeDisposition>.Failure(
                new(ErrorCode.Invalid, "Adapter payload does not match the routed event."));

        // Who is speaking comes from the envelope and the identity table, never from the text. A stranger answers
        // null, and null means "L0 agent" everywhere below this line (docs/31 §9.3).
        var person = people.Find(envelope.Adapter, envelope.Sender);

        var outcome = await registry.InvokeAsync(new CommandInvocation(envelope.Text, envelope.Adapter,
            envelope.PlatformConversationId, envelope.PlatformMessageId, envelope.EventId, person, envelope.Sender,
            envelope.SenderName), cancellationToken).ConfigureAwait(false);

        // A refused instruction is still answered: the person typed the prefix on purpose, and silence reads as
        // "the bot is broken", which is a worse thing to teach somebody than "you cannot run that".
        var reply = outcome.Ok ? outcome.Value!.Reply : outcome.Error?.Message;
        if (!string.IsNullOrWhiteSpace(reply))
        {
            var sent = await SendAsync(envelope, reply!, cancellationToken).ConfigureAwait(false);
            if (!sent.Ok) return Result<EnvelopeDisposition>.Failure(sent.Error!);
        }

        return outcome.Ok
            ? Result<EnvelopeDisposition>.Success(EnvelopeDisposition.Handled)
            : Result<EnvelopeDisposition>.Failure(outcome.Error!);
    }

    private async ValueTask<Result> SendAsync(AdapterEnvelope envelope, string text, CancellationToken cancellationToken)
    {
        var adapter = adapters.Get(envelope.Adapter);
        if (!adapter.Ok || adapter.Value is null)
            return Result.Failure(adapter.Error ?? new(ErrorCode.Unavailable,
                $"No Adapter answers at {envelope.Adapter}."));
        var sent = await adapter.Value
            .SendAsync(new OutboundMessage(envelope.PlatformConversationId, text, envelope.PlatformMessageId),
                cancellationToken).ConfigureAwait(false);
        if (!sent.Ok)
            return Result.Failure(sent.Error ?? new(ErrorCode.Unavailable, "The Adapter did not take the reply."));
        return sent.Value!.Sent
            ? Result.Success()
            : Result.Failure(new(ErrorCode.Unavailable, sent.Value.Detail ?? "The Adapter did not send the reply."));
    }
}
