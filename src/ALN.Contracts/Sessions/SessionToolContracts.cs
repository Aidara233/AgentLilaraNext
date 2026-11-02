using ALN.Contracts.Models;

namespace ALN.Contracts.Sessions;

/// <summary>
/// What a tool call produced.
/// </summary>
/// <param name="Content">What the model reads back as the tool result.</param>
/// <param name="SpokenText">
/// What the conversation now shows as the assistant's own words, if this tool said something. The session
/// timeline is what the model re-reads about itself, so a tool that speaks has to say so here — otherwise a
/// reply sent through a tool would vanish from the model's view of its own side of the conversation. Null for
/// tools that only look things up.
/// </param>
public sealed record SessionToolResult(string Content, string? SpokenText = null);

/// <summary>
/// One tool a Session may call.
///
/// The implementation belongs to the module that owns the capability — speaking belongs to the Channel side
/// (it alone knows the binding and the event being answered), stopping belongs to the session loop, sending a
/// file will belong to whatever owns media. The session routes calls; it does not know what any of them mean.
///
/// <paramref name="EndsTurn"/> is deliberately per tool and not per call: "wait" ends the turn every time it is
/// called, "speak" never does. A tool that sometimes means "stop" and sometimes does not would make the loop's
/// exit condition depend on an argument.
/// </summary>
public interface ISessionTool
{
    string Name { get; }

    bool EndsTurn { get; }

    ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken);
}

/// <summary>
/// The tools available in <b>one</b> turn, already bound to that turn's context. A host builds it per call —
/// a tool that needs the Channel message being answered cannot outlive that message, so there is nothing
/// long-lived here to register and unregister.
///
/// Absent tools are not an error: a name the spec declares but no module implements is simply not offered to
/// the model (and said out loud in the signal log), because offering a tool nobody can run only buys a wasted
/// round.
/// </summary>
public interface ISessionToolbox
{
    IReadOnlyList<string> Names { get; }

    Result<ISessionTool> Find(string name);
}
