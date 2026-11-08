namespace ALN.Contracts;

public static class EventNames
{
    public const string AdapterEnvelope = "adapter.envelope";
    public const string ChannelMessageAccepted = "channel.message.accepted";
    /// <summary>Sent to a Manager or module target so it can start itself; the payload is a LifecycleSignal.</summary>
    public const string LifecycleStart = "lifecycle.start";
    /// <summary>Sent to a Manager or module target so it can stop itself.</summary>
    public const string LifecycleStop = "lifecycle.stop";
    /// <summary>Published once the whole tree finished starting, for observers that want the whole picture.</summary>
    public const string LifecycleStarted = "lifecycle.started";
    /// <summary>
    /// A Session began working on one turn, and finished it. Not lifecycle: these say "it is talking right now", where
    /// <see cref="LifecycleStart"/> says "it is here". Dispatched to the Channel the turn is about, because a Session has
    /// no instance address a mount could name.
    ///
    /// <see cref="SessionTurnFinished"/> is sent whether the turn succeeded, failed, was cancelled or was refused as
    /// busy-too-long: a listener that counted beginnings and endings must not be left with an odd number because one
    /// turn died.
    /// </summary>
    public const string SessionTurnStarted = "session.turn.started";
    public const string SessionTurnFinished = "session.turn.finished";
    /// <summary>
    /// A stored schedule came due. The payload names the row, the instant it was meant to fire and how late it is —
    /// "late" because a host that was down owes nobody a replay, but the script being handed the event should know.
    /// </summary>
    public const string ScheduleDue = "schedule.due";

    /// <summary>
    /// Every event name this host can produce, in one place, so "may a script listen for this?" has an answer that
    /// cannot drift from the constants above.
    ///
    /// The list is the vocabulary, not a permission: what a script may declare is decided here, and what it can
    /// actually *reach* is decided by where it is mounted (a mount names one instance, and an event is dispatched to
    /// one instance). The old rule was the other way round — a hand-written whitelist of three names — which meant an
    /// event the host had been publishing all along (`lifecycle.start`) was one a script could not listen for, and the
    /// refusal said "unknown event" about something that was in this file.
    /// </summary>
    public static readonly string[] All =
    [
        AdapterEnvelope, ChannelMessageAccepted, LifecycleStart, LifecycleStop, LifecycleStarted, ScheduleDue,
        SessionTurnStarted, SessionTurnFinished
    ];

    public static bool IsKnown(string? name) => name is not null && Array.IndexOf(All, name) >= 0;

    /// <summary>The vocabulary as one sentence, for a refusal that has to tell the author what it could have said.</summary>
    public static string Describe() => string.Join(", ", All);
}
