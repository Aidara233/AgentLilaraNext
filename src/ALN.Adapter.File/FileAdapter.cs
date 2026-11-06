using System.Text.Json;
using ALN.Contracts;

namespace ALN.Adapter.File;

public sealed class FileAdapter : AdapterBase, IPollingAdapter, ITriggerSink
{
    private readonly IFileMailbox _mailbox;
    private readonly IEventSink _sink;
    private readonly IInboundAcks? _acks;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _beatGate = new();
    private CancellationTokenSource? _beat;
    private Task? _beatLoop;

    public FileAdapter(AdapterSpec spec, IFileMailbox mailbox, IEventSink sink, IInboundAcks? acks = null)
        : base(spec.Address, spec.Descriptor)
    {
        _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _acks = acks;
    }

    /// <summary>
    /// A mailbox is a thing you look at, not a thing that tells you: the only way to learn about a new file is to
    /// look. So this adapter asks for a rhythm, and says how fast.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// No rhythm asked of anyone else, because this adapter keeps its own (see <see cref="BeatAsync"/>). Returning null
    /// says "do not schedule me" — which is true, and which is also the only claim that was ever honest: a beat list
    /// that never held an adapter instance was never going to start this one.
    /// </summary>
    public TimeSpan? PreferredInterval => null;

    /// <summary>
    /// One trigger: look at the inbox once. From a schedule, from a script that decided it was time, or from somebody
    /// calling directly — the adapter does not care which, and that is the point of the abstraction.
    /// </summary>
    public async ValueTask<Result> OnTriggerAsync(TriggerSignal trigger, CancellationToken cancellationToken)
    {
        _ = trigger;
        // A trigger that arrives while the adapter is stopped is not an error: a schedule outlives a stop, and the
        // next beat after a start will find the work.
        if (!IsRunning) return Result.Success();
        var polled = await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        return polled.Ok ? Result.Success() : Result.Failure(polled.Error!);
    }

    public override async ValueTask<Result> StartAsync(CancellationToken cancellationToken)
    {
        var started = await base.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Ok) return started;
        // Whatever was claimed by a process that is no longer here is unfinished: put it back where it will
        // be found again. Doing this before the first poll is what makes a forced exit recoverable.
        await _mailbox.ReclaimAsync(cancellationToken).ConfigureAwait(false);
        RestartBeat();
        return Result.Success();
    }

    public override async ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        // The beat first: once it is gone nothing is inside the poll gate, so taking it cannot wait on a tick.
        await StopBeatAsync().ConfigureAwait(false);
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await base.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally { _pollGate.Release(); }
    }

    private void RestartBeat()
    {
        lock (_beatGate)
        {
            _beat?.Cancel();
            _beat = new CancellationTokenSource();
            _beatLoop = Task.Run(() => BeatAsync(_beat.Token), CancellationToken.None);
        }
    }

    private async Task StopBeatAsync()
    {
        CancellationTokenSource? beat;
        Task? loop;
        lock (_beatGate)
        {
            beat = _beat;
            loop = _beatLoop;
            _beat = null;
            _beatLoop = null;
        }
        if (beat is null) return;
        beat.Cancel();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        beat.Dispose();
    }

    /// <summary>
    /// The adapter's own beat, because nobody else was giving it one.
    ///
    /// A mailbox is something you look at, and waiting to be told to look is waiting forever. The kernel's beat list holds
    /// kernel modules, and an Adapter instance is not one, so no schedule was ever attached here — which is how this
    /// adapter could sit started for hours while its inbox filled up. A failed poll is not fatal: the next tick retries,
    /// and <see cref="PollOnceAsync"/> reports its own result.
    /// </summary>
    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsRunning) continue;
                try { await PollOnceAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { /* tried, and will try again */ }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
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
            // The file's text is the message: nothing to parse, nothing to validate. The sender is the conversation,
            // because one folder is one person here — a private mailbox has no roster — and the message id was assigned
            // when the input was claimed, so the file name is not an identity and does not have to be well-formed.
            var text = input.Content.Trim();
            if (text.Length == 0)
            {
                try
                {
                    if (!IsRunning) return Result<int>.Failure(new(ErrorCode.Unavailable, "FileAdapter is not running."));
                    await _mailbox.CompleteAsync(input.ConversationId, input.Token, false, "Input is empty.", cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { return Result<int>.Failure(new(ErrorCode.Internal, "Unable to record rejected input.", ex.Message, true)); }
                continue;
            }

            var envelope = new AdapterEnvelope(Address, Descriptor.PlatformId, input.ConversationId, input.Token, text,
                DateTimeOffset.UtcNow, input.ConversationId);

            Result published;
            try
            {
                var payload = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                // Registered before publication: the pump may settle the event the instant it can read it,
                // and an acknowledgement that arrives after the settle would be lost.
                var eventId = envelope.EventId;
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
