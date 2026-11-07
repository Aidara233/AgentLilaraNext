using ALN.Contracts.Models;

namespace ALN.Contracts.Sessions;

/// <summary>
/// What one timeline source brought back for this round: durable entries, and the promises that only become true
/// when the turn is written down.
///
/// The commits travel with the entries on purpose. A source that pulled something off a queue (the Channel delta)
/// must not move the cursor while collecting: a turn that dies still owes those messages to the next one. Keeping
/// the two in one return value means a source cannot hand over entries and forget to say what they cost.
/// </summary>
public sealed record SessionTimelineBatch(
    IReadOnlyList<SessionTimelineContribution> Entries,
    IReadOnlyList<ITurnCommit>? Commits = null);

/// <summary>
/// Context that is re-read on every round and never persisted: the clock, what memory holds, a module's own mood.
///
/// Returns what it has instead of writing into the request, and a failure means "this source has nothing to say
/// this round" — not "the turn fails". A hook may contribute or observe; it may not veto (docs/08 §10.6), and the
/// only way to make that true in code is for the return value to be data rather than a side effect.
/// </summary>
public interface IFloatingContextSource
{
    string Id { get; }

    ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(
        RoundContextRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Durable facts a source wants in this Session's timeline: the conversation's messages, the Channel's identity.
///
/// Separate from <see cref="IFloatingContextSource"/> because the two have different lifetimes, not because they
/// are different kinds of module: a floating line is re-rendered every round and must never enter history, and a
/// timeline entry is written once and replayed forever. A source that guessed wrong about which one it is would
/// either put a stale timestamp in every later request or lose a fact after one round.
/// </summary>
public interface ITimelineSource
{
    string Id { get; }

    ValueTask<Result<SessionTimelineBatch>> CollectAsync(RoundContextRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One tool a module offers to a round: the callable object, and the description the model is shown.
///
/// They travel together because a tool offered without its description is a name the model cannot use, and a
/// description without a tool is a name that wastes a round. <paramref name="ActsExternally"/> is what the loop
/// needs to keep two things apart: a step that only wrote something down is not a step that spoke to anyone, and
/// the difference decides whether this turn's text was its reply.
/// </summary>
public sealed record SessionToolOffer(ISessionTool Tool, ModelToolDefinition Definition, bool ActsExternally = true,
    /// <summary>
    /// How deep this tool sits. A tool is an interface like anything else: handing the model a name it can never call
    /// successfully buys a wasted round and teaches it to distrust the list, so a tool above the round's level is not
    /// offered at all.
    /// </summary>
    AccessLevel MinLevel = AccessLevel.Agent);

/// <summary>
/// The tools a module offers to this turn. The session routes calls; it does not know what any of them mean, and
/// it no longer knows any of their names.
///
/// Tools are gathered per round rather than registered once because a tool that needs the Channel message being
/// answered cannot outlive it. A source that fails to produce its tools costs this round those tools (and says so
/// in the signal log) — it does not cost the round.
/// </summary>
public interface ISessionToolSource
{
    string Id { get; }

    ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(
        RoundContextRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Every module that extends a session round, in three separate lists.
///
/// Three lists and not one list of "contributors": the three kinds have different failure semantics (degrade vs
/// degrade vs degrade), different lifetimes (per round / once / per round), and different outputs (ephemeral text /
/// durable entries / tools). One interface covering all three is how the old shape ended up with a single method
/// that wrote into two writers and could return nothing.
/// </summary>
public interface ISessionExtensionRegistry
{
    Result Register(IFloatingContextSource source);
    Result Register(ITimelineSource source);
    Result Register(ISessionToolSource source);

    /// <summary>Removes the module's sources of every kind. A module that goes away should not keep feeding rounds.</summary>
    Result Unregister(string id);

    IReadOnlyList<IFloatingContextSource> Floating { get; }
    IReadOnlyList<ITimelineSource> Timeline { get; }
    IReadOnlyList<ISessionToolSource> Tools { get; }
}
