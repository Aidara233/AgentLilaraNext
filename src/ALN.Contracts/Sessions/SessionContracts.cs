using ALN.Contracts.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ALN.Contracts.Sessions;

public enum SessionStatus { Running, Dormant }

/// <summary>Immutable behavior and prompt snapshot created with one session.</summary>
/// <param name="Prompts">
/// Which prompt file answers each group, e.g. <c>{ "1身份": "标准", "5安全": "标准" }</c> — at most one
/// file per group, because a dictionary cannot hold the same key twice. Empty or null means no system
/// message at all, which is a legal choice (a script that only performs an action does not need a persona)
/// and is said out loud in the signal log rather than left to be guessed.
///
/// Only the *names* are frozen here, never the text: prompts are files a person edits, and editing one must
/// change the next turn rather than break the session. What stays immutable is which prompt was chosen.
/// </param>
/// <summary>
/// Unknown members are refused rather than ignored: a spec that still says <c>systemPrompt</c> must fail loudly
/// instead of running with no prompt at all, and a typo in a field name must not silently drop a requirement.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionSpec(
    string ModelProfileId,
    string Objective,
    string FormatVersion,
    IReadOnlyDictionary<string, string>? Prompts = null,
    IReadOnlyList<ModelToolDefinition>? Tools = null,
    /// <summary>
    /// What this Session is trusted with. A Session created by nobody in particular is <see cref="AccessLevel.Agent"/>,
    /// and that is the safe default on purpose: an operator raises a Session on purpose, and nobody raises their own.
    /// It changes without a restart (sessions.setLevel) because it is one field, read when a turn is assembled.
    /// </summary>
    [property: JsonConverter(typeof(AccessLevelJson))] AccessLevel Level = AccessLevel.Agent,
    /// <summary>
    /// What this Session is allowed to call, under the ceiling its level already sets. Two lists because they answer
    /// different questions: <c>only</c> is "this turn needs three tools, not twenty-one" — a focus decision;
    /// <c>deny</c> is "not this one, ever" — a deployment decision about one Session. A level says what a caller may
    /// reach at all; a policy says what this caller needs, which is always narrower.
    /// </summary>
    SessionToolPolicy? ToolPolicy = null);

/// <summary>
/// A narrowing of the tools a Session is offered, applied on top of its level. Absent means "everything the level
/// allows", which is what every Session created before this field existed gets.
/// </summary>
public sealed record SessionToolPolicy(IReadOnlyList<string>? Only = null, IReadOnlyList<string>? Deny = null)
{
    /// <summary>Whether a tool by that name survives this policy. Having nothing to say means yes.</summary>
    public bool Admits(string name)
    {
        if (Only is { Count: > 0 } only && !only.Contains(name, StringComparer.Ordinal)) return false;
        return Deny is not { } deny || !deny.Contains(name, StringComparer.Ordinal);
    }
}

/// <summary>
/// One registered person this Session has met, and when he last said something here.
///
/// The time is what makes the roster answer the question it is asked: a conversation with forty people in its history
/// must still put the two who are talking now in front of the model, and only the last-spoken time can tell it which
/// two those are (docs/31 §5).
/// </summary>
public sealed record PersonSeen(string Id, DateTimeOffset At);

/// <summary>
/// Reads and writes <see cref="SessionState.People"/>, and tolerates the shape it had before it carried times.
///
/// A plain <c>string[]</c> is still on disk in every Session written before this release. Changing the property's type
/// without this would make those files fail to deserialize — and a Session whose state cannot be read is treated as
/// "not mine" and quietly rebuilt, which is the worst way to lose a conversation. A bare id reads as
/// <see cref="DateTimeOffset.MinValue"/>: "he spoke here, we did not write down when", which sorts last and is honest
/// about what the file actually said.
/// </summary>
public sealed class PersonSeenJson : JsonConverter<IReadOnlyList<PersonSeen>?>
{
    public override IReadOnlyList<PersonSeen>? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("people must be an array of ids or of {id, at} objects.");
        var people = new List<PersonSeen>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                if (reader.GetString() is { Length: > 0 } legacy) people.Add(new PersonSeen(legacy, DateTimeOffset.MinValue));
                continue;
            }
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }
            string? id = null;
            var at = DateTimeOffset.MinValue;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var name = reader.GetString();
                if (!reader.Read()) break;
                if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String)
                    id = reader.GetString();
                else if (string.Equals(name, "at", StringComparison.OrdinalIgnoreCase) &&
                         reader.TokenType == JsonTokenType.String &&
                         DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture,
                             DateTimeStyles.RoundtripKind, out var parsed))
                    at = parsed;
                else
                    reader.Skip();
            }
            if (!string.IsNullOrWhiteSpace(id)) people.Add(new PersonSeen(id, at));
        }
        return people;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<PersonSeen>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var person in value)
        {
            writer.WriteStartObject();
            writer.WriteString("id", person.Id);
            writer.WriteString("at", person.At);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

/// <summary>Persisted state. Phase is intentionally transient and is not stored here.</summary>
public sealed record SessionState(
    SessionStatus Status,
    int StepNumber,
    long Revision,
    bool Locked,
    SessionUsage Usage,
    int SchemaVersion,
    /// <summary>
    /// The Channel address this Session answers for, as text. A Session's id is its type plus a fresh suffix —
    /// the name is just a name (C-01) — so this field is the only way anybody, an operator or a maintenance
    /// action, can tell which Channel a directory belongs to.
    /// </summary>
    string? Channel = null,
    /// <summary>
    /// When this Session last finished a turn. Absence of a turn is not evidence of disuse, so freshness has
    /// to be recorded rather than guessed from file timestamps: a backup or a virus scanner rewrites those.
    /// </summary>
    DateTimeOffset? LastActiveAt = null,
    /// <summary>
    /// When this Session asked to be woken on its own, or null when it is only waiting for somebody to write.
    /// Stored rather than held in a timer so that a restart cannot lose the promise: the sweep reads this and
    /// nothing else. Any inbound activation clears it — a declaration that says "release me if nobody comes"
    /// is answered by somebody coming.
    /// </summary>
    DateTimeOffset? WaitUntil = null,
    /// <summary>
    /// Whether the deadline above ends with this Session being archived. Meaningless without a deadline, which
    /// is why "release without a timeout" is refused instead of being treated as "release never".
    /// </summary>
    bool WaitRelease = false,
    /// <summary>
    /// The registered people this Session has met, each with the last time he said something here.
    ///
    /// Kept because the roster in the round context has to be <i>this conversation's</i> people, and ordered by who is
    /// still talking rather than by who arrived first: a conversation that has met forty people must still show the
    /// two in it now (docs/31 §5). Absent on a Session written before this field existed — and a file written when it
    /// was a plain list of ids still reads, through <see cref="PersonSeenJson"/>.
    /// </summary>
    [property: JsonConverter(typeof(PersonSeenJson))] IReadOnlyList<PersonSeen>? People = null);

public sealed record SessionUsage(long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0)
{
    public SessionUsage Add(ModelUsage usage) => new(
        InputTokens + usage.InputTokens,
        OutputTokens + usage.OutputTokens,
        CacheReadTokens + (usage.CacheReadTokens ?? 0),
        CacheWriteTokens + (usage.CacheWriteTokens ?? 0));
}

/// <summary>A committed, immutable timeline record.</summary>
/// <param name="CoversUpTo">
/// Set on a checkpoint: the id of the last record its summary stands for. Everything up to and including that record is
/// still in the timeline — a checkpoint is a view, not a deletion — but no request after it needs to carry it, and
/// having the id here is what makes "does this summary already cover that?" a question with an answer.
/// </param>
public sealed record SessionHistoryEntry(
    string Id,
    string ActivationId,
    int StepNumber,
    string Role,
    string? Content,
    DateTimeOffset At,
    IReadOnlyList<ModelToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    ModelUsage? Usage = null,
    StopReason? StopReason = null,
    IReadOnlyList<SessionChannelMessage>? ChannelMessages = null,
    string? CoversUpTo = null,
    /// <summary>
    /// Set when this record did not come from the Channel and did not come from the model: another component handed it
    /// over. Kept as fields rather than only in the text, because "who sent this, and how deep were they" is the question
    /// a person asks afterwards, and a sentence in the middle of a paragraph is not an answer you can query.
    /// </summary>
    SessionRelay? Relay = null,
    /// <summary>
    /// Pictures this record brought with it, as references. The bytes stay where they are; this is the pointer, and it
    /// is small enough that a timeline can hold thousands of them.
    ///
    /// Old files do not have it and do not need it: an absent field on a history line reads back as null, so a timeline
    /// written before pictures existed keeps loading.
    /// </summary>
    IReadOnlyList<MediaRef>? Attachments = null);

/// <summary>
/// A message one component handed to a Session: the sender as the host spells it (<c>script:&lt;id&gt;@&lt;target&gt;</c>,
/// <c>session:&lt;id&gt;</c>), the sender's level as the host knew it, and when.
///
/// The level is recorded because the message is <b>not</b> authority. Handing a message over is not a capability and
/// cannot cross a level boundary in either direction: a shallow script may ask a deep Session for something, and the
/// deep Session decides — with the sender's depth in front of it — whether the request deserves to be acted on. That is
/// what makes asking upward possible without making it a promotion.
/// </summary>
public sealed record SessionRelay(string From, AccessLevel FromLevel, DateTimeOffset At);

public sealed record SessionChannelMessage(
    string Id,
    string Source,
    string? Sender,
    string Content,
    DateTimeOffset At,
    /// <summary>
    /// What came with it that is not words, by reference. Kept on the message rather than in the text because the
    /// text is what everything downstream reads when it cannot look: the rendering puts a receipt in the text and the
    /// parts beside it (docs/28 §1, §4).
    /// </summary>
    IReadOnlyList<MediaRef>? Media = null,
    /// <summary>
    /// The person this came from, when the identity table knew him (docs/31 §4). On the timeline record so that a
    /// transcript read later can still say which human it was — the same reason the two name fields exist beside it.
    /// </summary>
    string? Person = null);

/// <summary>Durable channel messages accepted for one Session activation, plus the Channel they came from.</summary>
public sealed record SessionTurnInput(
    IReadOnlyList<SessionChannelMessage> Messages,
    Address? Channel = null,
    ChannelBinding? Binding = null,
    /// <summary>
    /// Set when nobody spoke and the Session is running because its own <c>wait</c> came due: the instant it asked
    /// for. The turn then carries a note saying so — a model that finds itself running with no new messages will
    /// otherwise invent a reason for it, and "the user just asked…" is the reason it invents.
    /// </summary>
    DateTimeOffset? WokenAt = null,
    /// <summary>
    /// Set when a stored schedule is what woke the Session: the row's id, as <c>schedules.list</c> shows it. The note
    /// then says a rule came due rather than "your own wait expired" — the two are different promises and the model
    /// should not have to guess which it is keeping.
    /// </summary>
    string? Schedule = null,
    /// <summary>
    /// What that rule was written down to do, in the words of whoever wrote it. Carried into the note because the row's
    /// id is not a reason: "sch-4f2a" tells a model nothing about why it is awake, and asking it to look the row up
    /// spends a round on a question the turn already knows the answer to. Still data from a file, not somebody
    /// speaking — the note says so.
    /// </summary>
    string? SchedulePayload = null,
    /// <summary>
    /// Set when another component handed this Session a message and asked for it to be looked at now: the sender, its
    /// level, and when. The note this turn carries says the message was handed over rather than spoken, because that is
    /// the difference between a request and an instruction.
    /// </summary>
    SessionRelay? Relay = null,
    /// <summary>
    /// Set when the turn exists because work this Session started has ended. The note then says a job is over instead
    /// of "your own wait expired" — the session asked for neither, and being told the wrong reason is worse than being
    /// told none. <b>What</b> ended is deliberately absent: the job table has it, and the turn reads it from there.
    /// </summary>
    bool JobEnded = false);

/// <summary>
/// Why a Session is being run without anybody writing to it. Named rather than passed as text: the reason decides
/// what the turn carries, and a caller that can spell the reason cannot get the accompanying note wrong.
/// </summary>
public enum ActivationReason
{
    /// <summary>The deadline this Session declared with <c>wait</c> has passed.</summary>
    DeadlineDue,
    /// <summary>
    /// A stored schedule fired for this Session. Different from <see cref="DeadlineDue"/> in who asked: a deadline is
    /// the Session's own <c>wait</c>, a schedule is a row somebody else wrote down. The turn says which one it was,
    /// because "why am I awake" is the first thing a model gets wrong when the answer is "nobody said".
    /// </summary>
    ScheduleDue,

    /// <summary>
    /// Another component handed this Session a message and asked for it to be looked at now. The message is already in
    /// the timeline; the turn exists so that "somebody sent me something" is not mistaken for "the user just asked".
    /// </summary>
    Relayed,

    /// <summary>
    /// Something this Session started and then stopped waiting for is over: a background command, a download. Unlike
    /// the other reasons, this one is not about a promise to wake up — it is about work that outlived the turn which
    /// started it. The details are not carried here because the job table holds them, and the turn reads them there;
    /// a second account of the same ending would be one too many (<c>docs/28 §9.4</c>).
    /// </summary>
    JobFinished
}

public sealed record RoundContextRequest(
    string SessionId,
    Guid ActivationId,
    int StepNumber,
    Address? Channel = null,
    ChannelBinding? Binding = null,
    /// <summary>
    /// The Session's level this round. Carried per round rather than captured once, because it is the one number an
    /// operator may change while the Session runs: a source that cached it would keep offering what it was allowed
    /// to offer when it started.
    /// </summary>
    AccessLevel Level = AccessLevel.Agent,
    /// <summary>
    /// The registered people this Session has met, by id and last-spoken time (<see cref="SessionState.People"/>).
    /// Carried on the request so the roster source never has to reach into the Session's own state — a source asks what
    /// it was given.
    /// </summary>
    IReadOnlyList<PersonSeen>? People = null);

/// <summary>Ephemeral context to render into this model request; it is never persisted in Session history.</summary>
public sealed record RoundContextContribution(string Id, string Section, string Content, int Order = 0);

public sealed record SessionTimelineContribution(string Id, string? Sender, string Content, DateTimeOffset At,
    /// <summary>What arrived with it that is not words, by reference (docs/28 §1) — a pointer, never the bytes.</summary>
    IReadOnlyList<MediaRef>? Media = null,
    /// <summary>
    /// The person the message came from, when the identity table knew him (docs/31 §4). Carried so that the turn can
    /// write it into the timeline and remember that it has met this person — the roster is built from that, and a
    /// roster rebuilt from message text would have to parse names, which is exactly what the table exists to avoid.
    /// </summary>
    string? Person = null);

public interface ISessionTimelineWriter
{
    void Add(SessionTimelineContribution contribution);
}

public interface IRoundContextWriter
{
    void Add(RoundContextContribution contribution);
}

/// <summary>What one turn produced, whether or not it spoke: the text, the calls it made, how it stopped.</summary>
public sealed record SessionTurnResult(
    string SessionId,
    int StepNumber,
    string Text,
    IReadOnlyList<ModelToolCall> ToolCalls,
    StopReason StopReason,
    ModelUsage Usage,
    bool DuplicateActivation = false);

/// <summary>
/// A Session, by name: the thing a caller addresses, not a record on disk. Handles are made on demand and are
/// not kept: a Session nobody talks to costs nothing, and one that is talked to is reached the same way.
/// </summary>
public interface ISession : IModule
{
    /// <summary>
    /// What this Session was created as. Not a per-turn argument — a Session <b>is</b> its spec, and a caller that
    /// names an existing Session gets the spec that Session has rather than the one it passed.
    /// </summary>
    SessionSpec Spec { get; }

    SessionState State { get; }

    /// <summary>
    /// Run one turn: model steps and tool rounds until a tool ends the turn. <paramref name="tools"/> is the
    /// per-turn toolbox the host built (see <see cref="ISessionToolbox"/>); null means "only the built-in tools".
    /// </summary>
    ValueTask<Result<SessionTurnResult>> RunTurnAsync(
        Guid activationId,
        SessionTurnInput input,
        ISessionToolbox? tools,
        CancellationToken cancellationToken);
}

/// <summary>
/// Which registered people one Session keeps in front of the model, decided by the model itself.
///
/// <b>It is not a list of everybody who spoke.</b> A group conversation is mostly people who will never be seen again,
/// and a roster assembled from traffic is a roster of whoever happened to be loud — the assistant ends up carrying a
/// dozen names it has no use for and no way to tell which two of them matter. So joining is a decision somebody makes
/// on purpose, and the one who can make it is the one having the conversation (docs/31 §5).
///
/// What the host still does on its own is the arithmetic: when somebody already on the list speaks again, his "last
/// heard" time is refreshed, because that is what keeps the roster ordered by who is still here. Recording a fact is not
/// deciding who matters.
/// </summary>
public interface ISessionPeopleRoster
{
    /// <summary>
    /// Puts one person on this Session's roster, if he is not already there. Joining is not a decision the host can make
    /// for the caller: the person must already exist in the identity table, and this verb never creates one.
    /// </summary>
    ValueTask<Result<IReadOnlyList<PersonSeen>>> RememberAsync(string sessionId, string personId,
        CancellationToken cancellationToken);

    /// <summary>Takes one person off the roster. Removing somebody who is not on it is a no-op, not an error.</summary>
    ValueTask<Result<IReadOnlyList<PersonSeen>>> ForgetAsync(string sessionId, string personId,
        CancellationToken cancellationToken);
}

public interface ISessionManager
{
    /// <summary>
    /// The Session by that name, created from <paramref name="spec"/> when nothing exists under it yet.
    ///
    /// The spec is used <b>only</b> by creation. An existing Session keeps the one it has — that is what makes a
    /// Session an instance rather than a function of its caller: whoever addresses it by name gets the same
    /// Session back, and asking again with a different spec cannot silently rewrite what it is. When the two
    /// differ the stored one wins and the difference is recorded in the signal log, so it is visible without
    /// making the Session refuse to run for the rest of its life.
    /// </summary>
    ValueTask<Result<ISession>> OpenAsync(string sessionId, SessionSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// The Session that serves this Channel: the one whose own record says so, or a new one named by its type and a
    /// fresh suffix. Which Channel a Session answers for is data on the Session (C-20), never hashed into its id
    /// (C-01) — otherwise changing what the Session serves silently moves its identity.
    /// </summary>
    ValueTask<Result<ISession>> OpenForChannelAsync(string channelKey, SessionSpec spec, CancellationToken cancellationToken);

    /// <summary>An existing Session, or a failure when there is none. Never creates one.</summary>
    ValueTask<Result<ISession>> GetAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a turn that nobody wrote to. <paramref name="reason"/> decides what the turn carries — for a due
    /// deadline, an empty input plus a note saying exactly that.
    ///
    /// It does <b>not</b> queue: a Session that is already running a turn is left alone and the caller is told so
    /// (<see cref="ErrorCode.Conflict"/>, retryable), because a Session that collected seven wake-ups while busy
    /// would wake to seven notes and nothing to say.
    /// </summary>
    ValueTask<Result<SessionTurnResult>> ActivateAsync(string sessionId, ActivationReason reason,
        DateTimeOffset? at, CancellationToken cancellationToken, string? schedule = null,
        string? schedulePayload = null, SessionRelay? relay = null);

    /// <summary>
    /// Hands one Session a message from another component, and optionally asks it to look now.
    ///
    /// <b>No level check, in either direction.</b> That is the point: a shallow script asking a deep Session for
    /// something is the ordinary way work gets escalated, and a host that refused it would make "please do this, you are
    /// allowed to" impossible. What keeps that from being a promotion is that the record carries the sender and the
    /// sender's level (<see cref="SessionRelay"/>), the turn is told it was handed something rather than spoken to, and
    /// nothing about the receiver's own level, tools or policy changes. A request is a request.
    ///
    /// <paramref name="wake"/> is the caller saying what it wants: false means "it will see this next time it runs",
    /// true means "and it should run now" — which is skipped rather than queued when the Session is busy, exactly as
    /// every other wake-up is.
    /// </summary>
    ValueTask<Result<SessionTurnResult>> DeliverAsync(string sessionId, string text, string from,
        AccessLevel fromLevel, bool wake, CancellationToken cancellationToken);

    /// <summary>
    /// Points one Session at another model profile, at a turn boundary.
    ///
    /// The spec is stored-wins by design — addressing a Session cannot rewrite what it is — but "this conversation
    /// should keep going somewhere else" is a deliberate operator decision, not an accident of who addressed it. It
    /// takes effect on the next model step (the profile is looked up by id every step), and it is refused while a turn
    /// is running, because that turn was assembled against the profile it started with.
    /// </summary>
    ValueTask<Result<SessionRebind>> RebindAsync(string sessionId, string profileId, CancellationToken cancellationToken);

    Result<SessionState> GetState(string sessionId);

    /// <summary>
    /// Deletes every Session that belongs to one Channel, and says how many went. A Channel's Sessions are
    /// derived from it — the id is a hash of the Channel and the spec is immutable — so a Channel re-created
    /// with the same address would otherwise inherit a Session built from a script that no longer exists.
    /// </summary>
    ValueTask<Result<int>> DeleteChannelAsync(string channel, CancellationToken cancellationToken);

    /// <summary>
    /// Every Session on disk, with the facts a keep-or-archive decision needs. Reads the session directory —
    /// deliberately not done at startup: an instance nobody talks to should cost nothing.
    /// </summary>
    ValueTask<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Acts on the deadlines Sessions declared when they called <c>wait</c>. Called by the host on startup and
    /// then on a timer: it reads state and nothing else, so it cannot drift from what a Session actually asked
    /// for. A Session with no deadline is not this method's business and is left untouched.
    /// </summary>
    ValueTask<Result<SessionSweepReport>> SweepAsync(DateTimeOffset now, ISessionDeadlineHandler? handler,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves one Session's data out of its living place into <paramref name="destinationDirectory"/>, holding
    /// its turn gate meanwhile. The layout stays the manager's business — a caller says "take it away", not
    /// "here are the files".
    /// </summary>
    ValueTask<Result> ArchiveAsync(string sessionId, string destinationDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Moves archived data back into place. Refuses when that place is occupied: restoring into a Session that
    /// was rebuilt since would splice two different conversations into one.
    /// </summary>
    ValueTask<Result> RestoreAsync(string sessionId, string sourceDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// What a Session's lifecycle event carries: the instance signal every other lifecycle event carries, plus which
/// Session it is about.
///
/// The signal says <c>start</c> or <c>stop</c> and the instant; the id says which of a manager's instances it was.
/// Both are needed: a Channel is served by one Session, so a script mounted on the Channel can tell "my conversation's
/// Session came up" from "the Channel came up".
///
/// Why these events go to the Channel and not to the Session: a Session's id is its type plus a fresh suffix, and
/// an event is addressed by an instance address — a Session has none a mount could name. The Channel is the address
/// the Session answers for, and it is where the scripts already are.
/// </summary>
public sealed record SessionLifecycleNotice(string SessionId, LifecycleSignal Signal);

/// <summary>
/// The answer to "move this Session to that model": where it was, where it is, and whether anything moved. Unchanged is
/// a success, not an error — a caller retrying after a timeout should not be told it did something wrong.
/// </summary>
public sealed record SessionRebind(string? PreviousProfile, string Profile, bool Changed);

/// <summary>
/// What one turn's beginning or end looks like from outside: which Session, why it ran, how far it got, and — when it
/// has finished — how it stopped.
///
/// <paramref name="Reason"/> is the activation reason (a message, a deadline, a schedule, a relay): "why did it wake"
/// is the question a listener asks first, and it is not recoverable from anything else in the event.
/// </summary>
public sealed record SessionTurnNotice(string SessionId, string Reason, int StepNumber,
    string? StopReason = null, string? Detail = null);

/// <summary>The action ids the session manager answers to. Same rule as the other modules: the id is part of the interface.</summary>
public static class SessionActions
{
    public const string List = "sessions.list";

    /// <summary>
    /// Raise or lower what one Session may reach. The only level anyone can change at runtime: an interface's level is
    /// a fact about the code, so it takes a rebuild (and per script a re-mount, where the interface surface is settled).
    /// </summary>
    public const string SetLevel = "sessions.setLevel";

    /// <summary>
    /// Point one Session at another model profile: <c>id</c> and <c>profile</c>. Takes effect at a turn boundary — a
    /// Session in the middle of a turn is refused rather than silently moved under itself, because the half-finished
    /// turn was assembled against the profile it started with.
    /// </summary>
    public const string Rebind = "sessions.rebind";

    /// <summary>
    /// Hand one Session a message: <c>id</c> (or <c>channel</c>), <c>text</c>, and <c>wake</c> for "and look now".
    /// The message lands in that Session's timeline marked with who sent it and how deep the sender was — a request,
    /// not an instruction, whatever level either side sits at.
    /// </summary>
    public const string Message = "sessions.message";
}
