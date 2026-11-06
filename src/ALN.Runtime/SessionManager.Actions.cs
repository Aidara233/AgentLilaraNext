using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The session verbs. Only one today, and it is a read: Sessions are the only thing in the data root that a script can
/// mint without the operator ever writing a spec, so they are also the only thing that needs collecting.
/// </summary>
public sealed partial class SessionManager
{
    public Result RegisterActions(IControlPlane plane)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        return plane.Register(
            new(SessionActions.List, "Sessions on disk: whose they are, when they last spoke, how much room they take.",
                Capabilities.SessionsRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
            async (_, ct) =>
            {
                var listed = await ListAsync(ct).ConfigureAwait(false);
                if (!listed.Ok || listed.Value is null) return Result<JsonElement>.Failure(listed.Error!);
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    sessions = listed.Value.Select(session => new
                    {
                        id = session.Id,
                        channel = session.Channel,
                        lastActiveAt = session.LastActiveAt,
                        steps = session.Steps,
                        bytes = session.Bytes
                    })
                }));
            });
    }
}
