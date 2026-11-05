namespace ALN.Contracts;

/// <summary>Why a sink is being triggered.</summary>
public enum TriggerReason
{
    /// <summary>A schedule said so: the sink asked for a rhythm and this is one beat of it.</summary>
    Schedule,
    /// <summary>Something happened: an event, a script's decision, another module's nudge.</summary>
    Push,
    /// <summary>Somebody called it directly. No rhythm, no source — a request.</summary>
    Demand
}

/// <summary>One trigger: "look now", with enough context to know who said it and why.</summary>
/// <param name="Source">What produced it — <c>Clock</c>, a script id, an adapter. Read by logs, never parsed.</param>
public sealed record TriggerSignal(TriggerReason Reason, string Source, DateTimeOffset At, string? Detail = null);

/// <summary>
/// Something that wants to be woken up: an adapter that must look at its inbox, a module that must flush.
///
/// The abstraction is deliberately <b>not</b> "slave of the clock". A polling adapter needs a nudge, and the nudge
/// may come from a clock, from a script that decided it was time, from an HTTP callback, or from whoever called it
/// directly. Binding it to the clock would answer "who owns this adapter?" to get the answer to "when should it
/// look?", which are two different questions.
///
/// <see cref="PreferredInterval"/> is the sink's own declaration of rhythm: non-null means "a schedule is what I
/// want, at this pace", null means "no rhythm of mine — trigger me only when something happens". That declaration
/// is how a schedule source knows whom to add; nobody registers anybody.
/// </summary>
public interface ITriggerSink
{
    /// <summary>How often this sink wants to be triggered by a schedule; null when it has no rhythm of its own.</summary>
    TimeSpan? PreferredInterval { get; }

    /// <summary>The nudge. A failure describes this one attempt: it does not unregister the sink and does not throw.</summary>
    ValueTask<Result> OnTriggerAsync(TriggerSignal trigger, CancellationToken cancellationToken);
}

/// <summary>
/// Something that can trigger others — the clock today, a script rule or an inbound transport tomorrow.
///
/// Not a master: a source holds no ownership, and one sink may be attached to several sources at once (a schedule
/// plus a push). What it holds is a list of interested sinks, and it drops one when asked.
/// </summary>
public interface ITriggerSource
{
    /// <summary>The name that appears in <see cref="TriggerSignal.Source"/>. Not an address, not parsed.</summary>
    string Source { get; }

    /// <summary>
    /// True when this source owns a rhythm. A sink with an interval is attached only to sources that say so; a
    /// source that cannot schedule gets nothing from that declaration, because "wants a beat" is not "wants every
    /// source in the process to beat it" — that mistake is how one adapter ends up triggering twice per interval.
    /// </summary>
    bool CanSchedule { get; }

    Result Attach(ModuleId sink, ITriggerSink trigger);

    Result Detach(ModuleId sink);
}

/// <summary>
/// Something that can be asked to look now, without being on a schedule. A script uses it (an on-demand poll is how a
/// relay decides its own rhythm), and so does anything that wants to drain a queue once.
/// </summary>
public interface IPollingAdapter
{
    ValueTask<Result<int>> PollOnceAsync(CancellationToken cancellationToken = default);
}
