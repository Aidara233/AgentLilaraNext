namespace ALN.Contracts;

public static class EventNames
{
    public const string AdapterEnvelope = "adapter.envelope";
    public const string ChannelMessageAccepted = "channel.message.accepted";
    public const string ClockPulse = "clock.pulse";
    /// <summary>Sent to a Manager or module target so it can start itself; the payload is a LifecycleSignal.</summary>
    public const string LifecycleStart = "lifecycle.start";
    /// <summary>Sent to a Manager or module target so it can stop itself.</summary>
    public const string LifecycleStop = "lifecycle.stop";
    /// <summary>Published once the whole tree finished starting, for observers that want the whole picture.</summary>
    public const string LifecycleStarted = "lifecycle.started";
}
