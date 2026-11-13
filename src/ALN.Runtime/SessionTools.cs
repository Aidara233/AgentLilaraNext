using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// Two verbs a Session needs in order to know the others exist: <c>sessions_roster</c> (who is here, and what each
/// one is for) and <c>sessions_message</c> (hand one of them something).
///
/// Both already existed as actions — <c>sessions.list</c> and <c>sessions.message</c> — and both were reachable only
/// by reading the action table first. That is the right shape for a verb used twice a month; it is the wrong shape for
/// one used twice a turn. A model that has to look up an id and a schema before it can ask a neighbouring Session for
/// something will, in practice, not ask: it will guess, or it will do the job badly itself.
///
/// <b>The message half goes through the control plane</b>, exactly as <c>actions_call</c> would: same handler, same
/// level gate, same audit line, same refusal wording. This class is a door with a name on it, not a second
/// implementation. The roster half reads the manager directly, because the manager is the only thing that knows what a
/// Session is (<see cref="ISessionManager.RosterAsync"/>); the two halves therefore differ in kind, and the comments
/// say which is which so a later hand does not "make them consistent" by duplicating one of them.
///
/// Neither tool acts on the outside world (<c>ActsExternally: false</c>): handing a message to another Session puts
/// nothing into this conversation, so this turn's text is still the reply.
/// </summary>
public sealed class SessionTools(ISessionManager sessions, IControlPlane plane) : ISessionToolSource
{
    public const string SourceId = "system.session-tools";
    public const string RosterToolName = "sessions_roster";
    public const string MessageToolName = "sessions_message";

    /// <summary>
    /// How many lines the roster prints before it starts counting. Not a policy about how many Sessions may exist —
    /// it is about one answer staying readable: forty Sessions is a page the model skims, and a skimmed roster makes
    /// it ask the wrong one.
    /// </summary>
    private const int RosterLines = 40;

    private readonly ISessionManager _sessions = sessions;
    private readonly IControlPlane _plane = plane;

    public string Id => SourceId;

    public static readonly ModelToolDefinition RosterDefinition = new(RosterToolName,
        "Every Session in this process, including this one: its id, what it was made for (its objective), which "
        + "conversation it answers, what it is trusted with, how far it has got, and whether it is busy right now. "
        + "Start here before asking another Session for anything, and before deciding that something is your job: "
        + "another Session may already be the one responsible for it. The ids are what sessions_message takes.",
        """{"type":"object","properties":{},"additionalProperties":false}""");

    public static readonly ModelToolDefinition MessageDefinition = new(MessageToolName,
        "Hand one Session a message. It lands in that Session's timeline quoted, with your name and your level on it — "
        + "it is a request, not an instruction, and the receiver decides what to do with it. With wake=true that "
        + "Session also runs a turn now; a Session that is in the middle of a turn is skipped rather than queued, and "
        + "the message waits for its next turn (the answer says which happened). Use sessions_roster for the id and "
        + "for what each one is for; this puts nothing into the conversation you are answering.",
        """
        {"type":"object","properties":{
          "session":{"type":"string","description":"The Session id, exactly as sessions_roster shows it."},
          "text":{"type":"string","description":"What to hand over. Written for that Session to read; it is quoted, not attributed to the user."},
          "wake":{"type":"boolean","description":"Also run a turn there now. Default false: it reads this next time it runs."}},
         "required":["session","text"],"additionalProperties":false}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Both tools read who is calling from the request, not from the constructor: one source serves every Session in
        // the process, so a Session id or a level captured at construction would be the first Session's, forever
        // (docs/07 §9: nothing per-round may be cached in an object registered once).
        IReadOnlyList<SessionToolOffer> offers =
        [
            new(new RosterTool(this, request.SessionId), RosterDefinition, ActsExternally: false),
            new(new MessageTool(this, request.SessionId, request.Level), MessageDefinition, ActsExternally: false)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    /// <summary>
    /// The roster. Reads, annotates and prints — it decides nothing.
    ///
    /// The "you" line comes first and is separated from the rest because it is the answer to a different question.
    /// The list answers "who else is here"; the first block answers "what is this conversation's job", which is the
    /// thing a model gets wrong when it wakes into a group chat with no idea why it exists.
    /// </summary>
    private sealed class RosterTool(SessionTools owner, string? selfId) : ISessionTool
    {
        public string Name => RosterToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            var roster = await owner._sessions.RosterAsync(cancellationToken).ConfigureAwait(false);
            if (!roster.Ok || roster.Value is null) return Result<SessionToolResult>.Failure(roster.Error!);
            var entries = roster.Value;

            var text = new StringBuilder();
            var self = selfId is null ? null : entries.FirstOrDefault(entry => entry.Id == selfId);
            if (self is not null) text.Append(Line(self, isSelf: true));
            else if (selfId is not null)
                text.Append("You are ").Append(selfId)
                    .Append(", which has no directory on disk yet — nothing about this conversation has been written "
                        + "down, so its objective is whatever this request says it is.\n");

            // The reader is not repeated below: its own block above is the whole of what it needs to know about
            // itself, and printing it twice would make the one line marked "you" ambiguous.
            var others = entries.Where(entry => entry.Id != selfId).ToArray();
            if (others.Length == 0)
            {
                text.Append("No other Session exists on this host — sessions_message has nobody to hand anything to, "
                    + "so whatever this turn needs done, do it here.");
                return Result<SessionToolResult>.Success(new(text.ToString()));
            }

            text.Append('\n').Append(others.Length).Append(others.Length == 1 ? " other Session" : " other Sessions")
                .Append(" in this process (sessions_message hands one of them a message; wake=true also runs it now, "
                    + "and a busy one is skipped rather than queued):\n");
            foreach (var entry in others.Take(RosterLines)) text.Append(Line(entry, isSelf: false));
            if (others.Length > RosterLines)
                text.Append("… and ").Append(others.Length - RosterLines).Append(" more; ask for one by id when you know it.\n");
            return Result<SessionToolResult>.Success(new(text.ToString().TrimEnd()));
        }

        /// <summary>
        /// One Session as one block. Line-broken rather than tabular on purpose: a model reads a paragraph per peer
        /// better than a wide row, and an objective is a sentence.
        /// </summary>
        private static string Line(SessionRosterEntry entry, bool isSelf)
        {
            var text = new StringBuilder();
            text.Append("- ").Append(entry.Id).Append(isSelf ? "   ← you" : "");
            text.Append("\n  level: ").Append(AccessLevels.Describe(entry.Level));
            text.Append(" · ").Append(entry.Channel is { Length: > 0 } channel ? "serving " + channel : "serving no Channel");
            if (entry.ModelProfileId is { Length: > 0 } profile) text.Append(" · model ").Append(profile);
            text.Append("\n  objective: ")
                .Append(entry.Objective.Length > 0 ? entry.Objective : "(not written down: its module.json cannot be read)");
            text.Append("\n  ").Append(entry.Steps).Append(" step(s)");
            if (entry.LastActiveAt is { } at) text.Append(" · last active ").Append(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            text.Append(entry.Busy ? " · busy right now (a wake-up would be skipped, not queued)" : " · idle");
            text.Append('\n');
            return text.ToString();
        }
    }

    /// <summary>
    /// The message. Goes through the control plane as <c>sessions.message</c>, so a script, a person and the model all
    /// reach one handler, one level gate and one audit line.
    ///
    /// The one thing this tool adds is the translation of the answer into what the caller actually wants to know:
    /// whether the other side will read this now or later. "Busy" comes back from the manager as a retryable
    /// <see cref="ErrorCode.Conflict"/> <b>after</b> the message was written down — reporting that as a plain failure
    /// would teach the model to send it again, and the second copy is the one nobody wanted.
    /// </summary>
    private sealed class MessageTool(SessionTools owner, string? selfId, AccessLevel level) : ISessionTool
    {
        public string Name => MessageToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var arguments = document.RootElement;
            var target = Text(arguments, "session");
            if (string.IsNullOrWhiteSpace(target))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "sessions_message requires 'session'."));
            var text = Text(arguments, "text");
            if (string.IsNullOrWhiteSpace(text))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "sessions_message requires 'text'."));
            var wake = arguments.ValueKind == JsonValueKind.Object &&
                       arguments.TryGetProperty("wake", out var flag) && flag.ValueKind == JsonValueKind.True;
            var session = target.Trim();

            // Sending to oneself is not a message, it is a loop with extra steps: with wake it would ask for a turn the
            // current turn already has the gate for, and without it it writes into one's own timeline a note read only
            // after this turn ends. Saying so is cheaper than either.
            if (selfId is not null && string.Equals(session, selfId, StringComparison.Ordinal))
                return Result<SessionToolResult>.Success(new(
                    "That is this Session (" + session + "); nothing was sent. Whatever you want to remember or do, "
                    + "say it in the answer or do it in this turn — a message to yourself is only read after this turn ends."));

            var payload = JsonSerializer.SerializeToElement(new { id = session, text, wake });
            // The same identity actions_call uses for the model, in the Session it is acting in: a record that only said
            // "model" could not be joined back to the message that caused it (see ActionTools.ActorIdFor).
            var actor = new Actor(ActionTools.ActorIdFor(selfId), ActorKind.Model, ActionTools.ModelCapabilities, level);
            var handed = await owner._plane.InvokeAsync(actor, SessionActions.Message, payload, cancellationToken)
                .ConfigureAwait(false);

            var answer = new StringBuilder();
            if (handed.Ok && handed.Value.ValueKind == JsonValueKind.Object)
            {
                var step = handed.Value.TryGetProperty("step", out var stepValue) && stepValue.TryGetInt32(out var number)
                    ? number
                    : 0;
                answer.Append("ok: handed to ").Append(session).Append(" (step ").Append(step).Append("). ");
                answer.Append(wake
                    ? "It was woken and ran a turn now — it will read this as somebody else's request, not as the user's words."
                    : "It will see this next time it runs; nothing was woken.");
                return Result<SessionToolResult>.Success(new(answer.ToString()));
            }

            // A busy receiver: the message is already in its timeline, and only the wake was skipped. Not a failure to
            // retry — a note that will be read late.
            if (handed.Error?.Code == ErrorCode.Conflict)
                return Result<SessionToolResult>.Success(new(
                    "handed to " + session + " and it is waiting there, but that Session is in the middle of a turn, so it "
                    + "was not woken (a wake-up is skipped rather than queued). It will read this when its turn ends. "
                    + "Nothing needs sending again."));

            answer.Append("error: sessions_message → ").Append(session).Append('\n')
                .Append("code: ").Append(handed.Error?.Code.ToString().ToLowerInvariant() ?? "unknown").Append('\n')
                .Append(handed.Error?.Message ?? "the message was not accepted");
            if (handed.Error?.Code == ErrorCode.Invalid)
                answer.Append("\nIf the id looks right, run sessions_roster: a Session id that is not in it is not one "
                    + "this process knows.");
            return Result<SessionToolResult>.Success(new(answer.ToString()));
        }

        private static string? Text(JsonElement arguments, string property) =>
            arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var found) &&
            found.ValueKind == JsonValueKind.String
                ? found.GetString()
                : null;
    }
}

/// <summary>
/// The module that offers the two tools: one instance, one source, no configuration of its own.
///
/// A module for the same reason the script and action tools are: whether a Session may see — and write to — the rest
/// of the graph should be answerable by looking at the graph, not by reading the host's source. Take the instance out
/// and the tools are gone from every round.
/// </summary>
[Module(TypeId, Category = "session")]
public sealed class SessionToolsModule(ModuleId id, SessionTools tools, ISessionExtensionRegistry extensions) : IKernelModule
{
    public const string TypeId = "sessions.tools";

    private bool _registered;

    public ModuleId Id { get; } = id;

    public static Result<IKernelModule> FromManifest(ModuleManifest manifest, ISessionManager sessions,
        IControlPlane plane, ISessionExtensionRegistry extensions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Type, TypeId, StringComparison.Ordinal))
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid, $"This factory builds {TypeId}, not {manifest.Type}."));
        return Result<IKernelModule>.Success(new SessionToolsModule(manifest.Id, new SessionTools(sessions, plane), extensions));
    }

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = extensions.Register(tools);
        if (!registered.Ok) return ValueTask.FromResult(registered);
        _registered = true;
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>Nothing to warm up: both tools read the roster and the action table as they are when they are called.</summary>
    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            extensions.Unregister(tools.Id);
            _registered = false;
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}
