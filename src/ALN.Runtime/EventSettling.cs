using ALN.Contracts;

namespace ALN.Runtime.Hosting;

/// <summary>
/// Who owes an acknowledgement for which event. Registered by an Adapter at publication time, claimed once
/// by the pump when the round is over. In memory on purpose: if the process dies, the registration dies with
/// it, and the mailbox's own reclaim is what makes the input come back.
/// </summary>
public sealed class InboundAckRegistry : IInboundAcks
{
    private readonly Dictionary<Guid, Func<CancellationToken, ValueTask>> _pending = new();
    private readonly object _gate = new();

    public int Count { get { lock (_gate) return _pending.Count; } }

    public void Expect(Guid eventId, Func<CancellationToken, ValueTask> settle)
    {
        lock (_gate) _pending[eventId] = settle;
    }

    public void Forget(Guid eventId)
    {
        lock (_gate) _pending.Remove(eventId);
    }

    public async ValueTask<Result> SettleAsync(Guid eventId, CancellationToken cancellationToken)
    {
        Func<CancellationToken, ValueTask>? settle;
        lock (_gate)
        {
            // Removing first: an acknowledgement happens once, even if the same event id is settled twice.
            if (!_pending.Remove(eventId, out settle)) return Result.Success();
        }
        try
        {
            await settle(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Result.Failure(new(ErrorCode.Internal, "Inbound acknowledgement failed.", ex.Message));
        }
    }
}

/// <summary>Several settlers, one hook. One failing does not stop the others: they owe different writes.</summary>
public sealed class EventSettlers : IEventSettler
{
    private readonly IEventSettler[] _settlers;

    public EventSettlers(params IEventSettler[] settlers) => _settlers = settlers;

    public async ValueTask<Result> SettleAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
    {
        Error? firstFailure = null;
        foreach (var settler in _settlers)
        {
            var settled = await settler.SettleAsync(routedEvent, cancellationToken).ConfigureAwait(false);
            if (!settled.Ok) firstFailure ??= settled.Error;
        }
        return firstFailure is null ? Result.Success() : Result.Failure(firstFailure);
    }
}

/// <summary>
/// A Channel message round is over: the message is no longer "accepted but unfinished", so a restart has
/// nothing to replay for it.
/// </summary>
public sealed class ChannelAcceptSettler(IChannelManager channels) : IEventSettler
{
    public ValueTask<Result> SettleAsync(RoutedEvent routedEvent, CancellationToken cancellationToken) =>
        routedEvent.Name == EventNames.ChannelMessageAccepted
            ? channels.CompleteAcceptAsync(routedEvent.Source, routedEvent.EventId, cancellationToken)
            : ValueTask.FromResult(Result.Success());
}

/// <summary>An Adapter event round is over: the mailbox input that produced it can be moved out of inflight.</summary>
public sealed class InboundAckSettler(IInboundAcks acks) : IEventSettler
{
    public ValueTask<Result> SettleAsync(RoutedEvent routedEvent, CancellationToken cancellationToken) =>
        acks.SettleAsync(routedEvent.EventId, cancellationToken);
}
