using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The session verbs: what exists, and what one of them is trusted with.
///
/// Listing is a read, because Sessions are the only thing in the data root that a script can mint without the operator
/// ever writing a spec — something has to be able to say what is there. Raising one is not a read: it changes what a
/// running Session may touch, so it is an operator's verb, and the plain-language promise that "a level may only be
/// given away at or below your own" is enforced once, by the control plane, from <see cref="ActionDescriptor.AssignsLevel"/>.
/// </summary>
public sealed partial class SessionManager
{
    public Result RegisterActions(IControlPlane plane)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        var listed = plane.Register(
            new(SessionActions.List, "Sessions on disk: whose they are, when they last spoke, how much room they take.",
                Capabilities.SessionsRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
            async (_, ct) =>
            {
                var list = await ListAsync(ct).ConfigureAwait(false);
                if (!list.Ok || list.Value is null) return Result<JsonElement>.Failure(list.Error!);
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    sessions = list.Value.Select(session => new
                    {
                        id = session.Id,
                        channel = session.Channel,
                        lastActiveAt = session.LastActiveAt,
                        steps = session.Steps,
                        bytes = session.Bytes
                    })
                }));
            });
        if (!listed.Ok) return listed;

        // Handing a Session a message. Agent-level on purpose and with no level check inside: a shallow Session asking
        // a deeper one is how work gets escalated ("you are allowed to do this, please do"), and the thing that keeps it
        // from being a promotion is the record — the message says who sent it and how deep they were, and it is a
        // request rather than an instruction.
        var delivered = plane.Register(
            new(SessionActions.Message,
                "Hand one Session a message: it lands in that Session's timeline marked with who sent it and how deep "
                + "the sender was, and 'wake' asks it to look now (a busy Session is skipped, not queued). Works in any "
                + "direction: a shallow Session may ask a deeper one for something it is not allowed to do itself — the "
                + "deeper one decides, and the request carries no authority either way.",
                Capabilities.SessionsWrite, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The Session id, as sessions.list shows it."},
                  "text":{"type":"string","description":"What to hand over. It is quoted as somebody else's words, not as the user's."},
                  "wake":{"type":"boolean","description":"Also run a turn now. Default false: it will see this next time it runs."}},
                 "required":["id","text"],"additionalProperties":false}
                """,
                Subject: "id"),
            async (actor, arguments, ct) =>
            {
                if (!arguments.TryReadText("id", out var id) || id.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "sessions.message requires 'id'."));
                if (!arguments.TryReadText("text", out var text) || text.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "sessions.message requires 'text'."));
                var wake = arguments.TryGetProperty("wake", out var wakeJson) && wakeJson.ValueKind == JsonValueKind.True;
                // A model's id already says what it is and which Session it is acting in ("model:<session id>", see
                // ActionTools.ActorIdFor), so prefixing it again would read as a Session called "model:…". The other
                // kinds carry a bare name and get their prefix here.
                var sender = actor.Kind switch
                {
                    ActorKind.Script => $"script:{actor.Id}",
                    ActorKind.Model when actor.Id.StartsWith(ActionTools.ActorId + ":", StringComparison.Ordinal) => actor.Id,
                    _ => $"session:{actor.Id}"
                };
                var sent = await DeliverAsync(id.Trim(), text, sender, actor.Level, wake, ct).ConfigureAwait(false);
                if (!sent.Ok) return Result<JsonElement>.Failure(sent.Error!);
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    id = id.Trim(),
                    from = sender,
                    fromLevel = AccessLevels.Describe(actor.Level),
                    wake,
                    step = sent.Value!.StepNumber,
                    text = sent.Value.Text
                }));
            });
        if (!delivered.Ok) return delivered;

        var setLevel = plane.Register(
            new(SessionActions.SetLevel,
                "Raise or lower what one Session may reach: agent, operator or full. Takes effect on the next turn — no "
                + "restart, no reload — because the level is read when a turn is assembled. A level above your own is "
                + "refused: raising a Session is delegating, and nobody delegates upwards.",
                Capabilities.SessionsControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The Session id, as sessions.list shows it."},
                  "level":{"type":"string","enum":["agent","operator","full"],
                           "description":"agent = what the model is by default; operator = what a person at the API does; full = the kernel."}},
                 "required":["id","level"],"additionalProperties":false}
                """,
                Subject: "id", AssignsLevel: "level"),
            async (arguments, ct) =>
            {
                if (!arguments.TryReadText("id", out var id) || id.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "sessions.setLevel requires 'id'."));
                if (!arguments.TryReadText("level", out var text) ||
                    !Enum.TryParse<AccessLevel>(text.Trim(), ignoreCase: true, out var level))
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                        "sessions.setLevel requires 'level': agent, operator or full."));

                var changed = await SetLevelAsync(id.Trim(), level, ct).ConfigureAwait(false);
                if (!changed.Ok) return Result<JsonElement>.Failure(changed.Error!);
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    id = id.Trim(),
                    previous = JsonNamingPolicy.CamelCase.ConvertName(changed.Value.ToString()),
                    level = JsonNamingPolicy.CamelCase.ConvertName(level.ToString())
                }));
            });

        if (!setLevel.Ok) return setLevel;

        // Moving a conversation to a different model while it runs. Operator-level, because it decides where a
        // Session's words go — and, like handing over a level, it is a decision about somebody else's conversation.
        return plane.Register(
            new(SessionActions.Rebind,
                "Point one Session at another model profile. Takes effect on the next model step, and is refused while "
                + "the Session is in the middle of a turn — that turn was assembled against the profile it started with. "
                + "This is how a live conversation moves to a different model or endpoint without being restarted: the "
                + "history stays, the next request goes somewhere else.",
                Capabilities.SessionsControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The Session id, as sessions.list shows it."},
                  "profile":{"type":"string","description":"A model profile id, as models.list shows it."}},
                 "required":["id","profile"],"additionalProperties":false}
                """,
                Subject: "id"),
            async (arguments, ct) =>
            {
                if (!arguments.TryReadText("id", out var id) || id.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "sessions.rebind requires 'id'."));
                if (!arguments.TryReadText("profile", out var profile) || profile.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "sessions.rebind requires 'profile'."));

                var rebound = await RebindAsync(id.Trim(), profile.Trim(), ct).ConfigureAwait(false);
                if (!rebound.Ok || rebound.Value is null) return Result<JsonElement>.Failure(rebound.Error!);
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    id = id.Trim(),
                    previous = rebound.Value.PreviousProfile,
                    profile = rebound.Value.Profile,
                    changed = rebound.Value.Changed
                }));
            });
    }
}
