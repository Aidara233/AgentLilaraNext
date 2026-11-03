using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Tests;

/// <summary>
/// "Open it if it is not there yet, then run a turn" — the shape almost every test wants.
///
/// The two steps are separate on purpose: opening is naming a Session (and, the first time, deciding what it is),
/// while running a turn is using it. Tests that care about that distinction call the two directly; the rest use
/// this and stay readable.
/// </summary>
internal static class SessionTestExtensions
{
    public static async Task<Result<SessionTurnResult>> TurnAsync(this ISessionManager sessions, string sessionId,
        SessionSpec spec, Guid activationId, SessionTurnInput input, ISessionToolbox? tools = null,
        CancellationToken cancellationToken = default)
    {
        var opened = await sessions.OpenAsync(sessionId, spec, cancellationToken);
        if (!opened.Ok || opened.Value is null) return Result<SessionTurnResult>.Failure(opened.Error!);
        return await opened.Value.RunTurnAsync(activationId, input, tools, cancellationToken);
    }
}
