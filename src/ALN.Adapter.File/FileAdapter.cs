using System.Text.Json;
using ALN.Contracts;

namespace ALN.Adapter.File;

public abstract class AdapterBase : IChatAdapter
{
    private int _state = (int)AdapterState.Created;

    protected AdapterBase(Address address, AdapterDescriptor descriptor)
    {
        Address = address;
        Descriptor = descriptor;
    }

    public Address Address { get; }
    public AdapterDescriptor Descriptor { get; }
    public AdapterState State => (AdapterState)Volatile.Read(ref _state);

    public virtual ValueTask<Result> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            var state = State;
            if (state == AdapterState.Running)
                return ValueTask.FromResult(Result.Success());
            if (state is not (AdapterState.Created or AdapterState.Stopped))
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Conflict, $"Adapter cannot start from {state}.")));
            if (Interlocked.CompareExchange(ref _state, (int)AdapterState.Running, (int)state) == (int)state)
                return ValueTask.FromResult(Result.Success());
        }
    }

    public virtual ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            var state = State;
            if (state == AdapterState.Stopped)
                return ValueTask.FromResult(Result.Success());
            if (state != AdapterState.Running && state != AdapterState.Created)
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Conflict, $"Adapter cannot stop from {state}.")));
            if (Interlocked.CompareExchange(ref _state, (int)AdapterState.Stopped, (int)state) == (int)state)
                return ValueTask.FromResult(Result.Success());
        }
    }

    protected bool IsRunning => State == AdapterState.Running;
    public abstract ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken);
}

public sealed class FileAdapter : AdapterBase, IPollingAdapter
{
    private readonly IFileMailbox _mailbox;
    private readonly IEventSink _sink;
    private readonly IClockManager _clock;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private Guid? _clockSubscriptionId;

    public FileAdapter(AdapterSpec spec, IFileMailbox mailbox, IEventSink sink, IClockManager clock)
        : base(spec.Address, spec.Descriptor)
    {
        _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public override async ValueTask<Result> StartAsync(CancellationToken cancellationToken)
    {
        var started = await base.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Ok) return started;
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
                published = await _sink.PublishAsync(new RoutedEvent(EventNames.AdapterEnvelope, Address, envelope!.EventId, payload), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) { return Result<int>.Failure(new(ErrorCode.Internal, "Event publication failed.", ex.Message, true)); }
            if (!published.Ok) continue;
            try
            {
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
