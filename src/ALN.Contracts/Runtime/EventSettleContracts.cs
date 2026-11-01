namespace ALN.Contracts;

/// <summary>
/// The durable consequences of "this event was handled". Runs once per dispatch round, after the handlers
/// return (or fail), and is where the host closes the loop it opened earlier: acknowledging a mailbox file,
/// clearing an accepted-but-unfinished marker.
///
/// Kept separate from dispatch on purpose: dispatch belongs to the graph, settling belongs to whoever owns
/// the thing that has to be written down — and a settler that fails must not undo the work the handlers did.
/// </summary>
public interface IEventSettler
{
    ValueTask<Result> SettleAsync(RoutedEvent routedEvent, CancellationToken cancellationToken);
}
