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

        return plane.Register(
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
    }
}
