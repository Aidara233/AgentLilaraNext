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

    public ValueTask<Result> StartAsync(CancellationToken cancellationToken)
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

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
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

public sealed class FileAdapter : AdapterBase
{
    private readonly IFileMailbox _mailbox;
    private readonly IEventSink _sink;

    public FileAdapter(AdapterSpec spec, IFileMailbox mailbox, IEventSink sink)
        : base(spec.Address, spec.Descriptor)
    {
        _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public async ValueTask<Result<int>> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
            return Result<int>.Failure(new(ErrorCode.Unavailable, "FileAdapter is not running."));

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
                    var conversation = root.TryGetProperty("platformConversationId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    var messageId = root.TryGetProperty("messageId", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    if (string.IsNullOrWhiteSpace(conversation)) validationError = "platformConversationId must be non-empty.";
                    if (validationError is null && string.IsNullOrWhiteSpace(text)) validationError = "text must be non-empty and a string.";
                    if (validationError is null)
                        envelope = new(Address, Descriptor.PlatformId, conversation!, messageId, text!, DateTimeOffset.UtcNow);
                }
            }
            catch (JsonException ex) { validationError = $"Invalid JSON: {ex.Message}"; }

            if (validationError is not null)
            {
                try
                {
                    if (!IsRunning) return Result<int>.Failure(new(ErrorCode.Unavailable, "FileAdapter is not running."));
                    await _mailbox.CompleteAsync(input.Token, false, validationError, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { return Result<int>.Failure(new(ErrorCode.Internal, "Unable to record rejected input.", ex.Message, true)); }
                continue;
            }

            Result published;
            try { published = await _sink.PublishAsync(envelope!, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { return Result<int>.Failure(new(ErrorCode.Internal, "Event publication failed.", ex.Message, true)); }
            if (!published.Ok) continue;
            try
            {
                await _mailbox.CompleteAsync(input.Token, true, null, cancellationToken).ConfigureAwait(false);
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
