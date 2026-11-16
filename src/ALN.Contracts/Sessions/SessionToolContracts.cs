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
/// <param name="Attachments">
/// Pictures this result brought back, as references — not bytes. The tool reports what it found; whether a model may
/// be shown it is the Session's decision, because only the Session knows which model is being talked to (docs/28 §5).
///
/// The bytes are read once, at the moment the message is built, and they are not stored on the tool result: a result
/// lives in the timeline, and a timeline full of pictures is a history that cannot be re-read cheaply (see
/// <see cref="ALN.Contracts.MediaRef"/>).
/// </param>
public sealed record SessionToolResult(string Content, string? SpokenText = null,
    IReadOnlyList<MediaRef>? Attachments = null);

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
/// Absent tools are not an error here, but they do not all mean the same thing. A name the spec declares and no
/// module implements is an assembly mistake, and the turn fails naming it — a tool nobody can run buys a wasted
/// round at best. A tool this build <i>does</i> implement, in a turn whose context does not fit it (<c>speak</c>
/// with no message to answer), is still offered and refuses the call with a sentence the model can act on
/// (docs/08 §5.0): a tool that is merely missing teaches the model nothing, and the retry it costs is spent on a
/// mistake the model was never told about.
/// </summary>
public interface ISessionToolbox
{
    IReadOnlyList<string> Names { get; }

    Result<ISessionTool> Find(string name);
}
