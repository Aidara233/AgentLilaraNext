using System.Text.Json;
using ALN.Contracts;

namespace ALN.Adapter.File;

public sealed class FileAdapter : AdapterBase, IPollingAdapter
{
    private readonly IFileMailbox _mailbox;
    private readonly IEventSink _sink;
    private readonly IClockManager _clock;
    private readonly IInboundAcks? _acks;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private Guid? _clockSubscriptionId;

    public FileAdapter(AdapterSpec spec, IFileMailbox mailbox, IEventSink sink, IClockManager clock, IInboundAcks? acks = null)
        : base(spec.Address, spec.Descriptor)
    {
        _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _acks = acks;
    }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public override async ValueTask<Result> StartAsync(CancellationToken cancellationToken)
    {
        var started = await base.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Ok) return started;
        // Whatever was claimed by a process that is no longer here is unfinished: put it back where it will
        // be found again. Doing this before the first poll is what makes a forced exit recoverable.
        await _mailbox.ReclaimAsync(cancellationToken).ConfigureAwait(false);
        if (_clockSubscriptionId.HasValue) return Result.Success();
        var registration = await _clock.RegisterAsync(Address, PollInterval, cancellationToken).ConfigureAwait(false);
        if (!registration.Ok)
        {
            await base.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return Result.Failure(registration.Error ?? new(ErrorCode.Internal, "Clock registration failed."));
        }
        _clockSubscriptionId = registration.Value;
        return Result.Success();
    }

    public override async ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_clockSubscriptionId is { } subscriptionId)
        {
            var unregistered = await _clock.UnregisterAsync(subscriptionId, Address, cancellationToken).ConfigureAwait(false);
            if (!unregistered.Ok) return unregistered;
            _clockSubscriptionId = null;
        }
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await base.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally { _pollGate.Release(); }
    }

    public async ValueTask<Result<int>> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
            return Result<int>.Failure(new(ErrorCode.Unavailable, "FileAdapter is not running."));
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning) return Result<int>.Failure(new(ErrorCode.Unavailable, "FileAdapter is not running."));
            return await PollCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pollGate.Release(); }
    }

    private async ValueTask<Result<int>> PollCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MailboxInput> inputs;
        try { inputs = await _mailbox.ReadPendingAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Result<int>.Failure(new(ErrorCode.Unavailable, "Unable to read mailbox.", ex.Message, true)); }

        var accepted = 0;
        foreach (var input in inputs)
        {
            if (!IsRunning) return Result<int>.Failure(new(ErrorCode.Unavailable, "FileAdapter is not running."));
            AdapterEnvelope? envelope = null;
            string? validationError = null;
            try
            {
                using var document = JsonDocument.Parse(input.Json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    validationError = "Input must be a JSON object.";
                }
                else
                {
                    var declaredConversation = root.TryGetProperty("platformConversationId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    var conversation = input.ConversationId;
                    var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    var messageId = root.TryGetProperty("messageId", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    var sender = root.TryGetProperty("sender", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(declaredConversation) && !StringComparer.Ordinal.Equals(declaredConversation, conversation))
                        validationError = "JSON conversation id must match its inbox directory.";
                    if (validationError is null && string.IsNullOrWhiteSpace(text)) validationError = "text must be non-empty and a string.";
                    if (validationError is null)
                        envelope = new(Address, Descriptor.PlatformId, conversation, messageId, text!, DateTimeOffset.UtcNow, sender);
                }
            }
            catch (JsonException ex) { validationError = $"Invalid JSON: {ex.Message}"; }

            if (validationError is not null)
            {
                try
                {
                    if (!IsRunning) return Result<int>.Failure(new(ErrorCode.Unavailable, "FileAdapter is not running."));
                    await _mailbox.CompleteAsync(input.ConversationId, input.Token, false, validationError, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { return Result<int>.Failure(new(ErrorCode.Internal, "Unable to record rejected input.", ex.Message, true)); }
                continue;
            }

            Result published;
            try
            {
                var payload = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                // Registered before publication: the pump may settle the event the instant it can read it,
                // and an acknowledgement that arrives after the settle would be lost.
                var eventId = envelope!.EventId;
                _acks?.Expect(eventId, settleToken =>
                    _mailbox.CompleteAsync(input.ConversationId, input.Token, true, null, settleToken));
                try
                {
                    published = await _sink.PublishAsync(new RoutedEvent(EventNames.AdapterEnvelope, Address, eventId, payload), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    _acks?.Forget(eventId);
                    throw;
                }
                if (!published.Ok)
                {
                    _acks?.Forget(eventId);
                    // A full queue is a retry, not a rejection: hand the claim back so the next poll offers it.
                    await _mailbox.ReleaseAsync(input.ConversationId, input.Token, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception ex) { return Result<int>.Failure(new(ErrorCode.Internal, "Event publication failed.", ex.Message, true)); }
            try
            {
                // With an acknowledger, the file stays claimed until the event round is over: publishing is
                // not handling, and a crash in between must leave the input recoverable rather than "done".
                if (_acks is null)
                    await _mailbox.CompleteAsync(input.ConversationId, input.Token, true, null, cancellationToken).ConfigureAwait(false);
                accepted++;
            }
            catch (Exception ex) { return Result<int>.Failure(new(ErrorCode.Internal, "Unable to complete mailbox input.", ex.Message, true)); }
        }
        return Result<int>.Success(accepted);
    }

    public override async ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        if (!IsRunning)
            return Result<SendResult>.Failure(new(ErrorCode.Unavailable, "FileAdapter is not running."));
        if (message is null || string.IsNullOrWhiteSpace(message.PlatformConversationId) || string.IsNullOrWhiteSpace(message.Text))
            return Result<SendResult>.Failure(new(ErrorCode.Invalid, "Outbound conversation and text must be non-empty."));
        try
        {
            var attemptId = await _mailbox.WriteOutputAsync(message, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(attemptId))
                return Result<SendResult>.Failure(new(ErrorCode.Internal, "Mailbox returned an empty attempt id."));
            return Result<SendResult>.Success(new(true, attemptId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Result<SendResult>.Failure(new(ErrorCode.Unavailable, "Unable to write mailbox output.", ex.Message, true)); }
    }
}
