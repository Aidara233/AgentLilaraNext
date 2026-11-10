namespace ALN.Contracts;

/// <summary>
/// One node of the instruction tree: <c>order</c> is a namespace, <c>order.a</c> is a leaf somebody can actually run.
///
/// <b>A path is either a leaf or a namespace, never both.</b> That one rule is what makes parsing free of guesses:
/// <c>//order a</c> can only mean "the sub-instruction a of order", because <c>order</c> having a child means it cannot
/// itself be runnable. The alternative (like <c>git</c>, both a command and a namespace) would leave every word after
/// the first ambiguous — is it a sub-command or an argument? — and the answer would depend on registration order.
/// </summary>
/// <param name="Path">Dot-separated, lowercase: <c>order</c> or <c>order.a</c>. At most three segments.</param>
/// <param name="Summary">One line, shown by <c>//help</c>. Written for a person in a group chat, not for a log.</param>
/// <param name="MinLevel">
/// How deep an instruction this is: the caller must be at least this deep. A person's depth is the <c>level</c> on his
/// card (<see cref="Person.Level"/>); a stranger is <see cref="AccessLevel.Agent"/>. The default is
/// <see cref="AccessLevel.Operator"/> on purpose — an instruction nobody declared is something a person has to run.
/// </param>
/// <param name="Usage">How to call it, when the summary cannot say it in one line.</param>
/// <param name="Body">Null for a namespace. A leaf's answer is the text to send back; empty means "say nothing".</param>
/// <param name="Owner">Who registered it (<c>lib</c>, <c>script:&lt;instance&gt;</c>). Help and the audit both show it,
/// and unregistering an owner takes back everything it registered.</param>
public sealed record CommandNode(
    string Path,
    string Summary,
    AccessLevel MinLevel = AccessLevel.Operator,
    string? Usage = null,
    CommandHandler? Body = null,
    string Owner = "lib")
{
    public bool IsLeaf => Body is not null;

    public IReadOnlyList<string> Segments => Path.Split('.', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The last segment: what a person types after the parent namespace.</summary>
    public string Name => Segments.Count == 0 ? Path : Segments[^1];
}

/// <summary>
/// What a leaf gets to work with. Everything here was decided by the message that arrived, never by the instruction's
/// own arguments: an instruction cannot say who sent it (<c>//help --as=xiaolin</c> has no meaning here), because the
/// only place a caller's depth may come from is the identity table lookup that the envelope triggered.
/// </summary>
/// <param name="Command">The path that was typed: <c>order.a</c>.</param>
/// <param name="Arguments">The words after the leaf. Quoted runs arrive as one word, quotes removed.</param>
/// <param name="Person">Who sent it, when the identity table knows him. Null is an answer, not a failure (docs/31 §9.3).</param>
/// <param name="CallerLevel">That person's depth — <see cref="AccessLevel.Agent"/> for a stranger.</param>
public sealed record CommandContext(
    string Command,
    IReadOnlyList<string> Arguments,
    Person? Person,
    AccessLevel CallerLevel,
    Address Adapter,
    string PlatformConversationId,
    string? Sender,
    string? SenderName)
{
    /// <summary>The name the caller answered the door with: his card's name, the platform's name, or his account id.</summary>
    public string Display =>
        Person?.Name ?? (string.IsNullOrWhiteSpace(SenderName) ? Sender ?? "某人" : SenderName!);

    /// <summary>
    /// Who this instruction runs as. <b>A person, at his own depth</b> — not the Session, not a model. The audit then
    /// says something a person can act on ("xiaolin ran //reload"), and the delegated-level rule of the control plane
    /// applies to him exactly as it applies to an API token: he may hand out no more depth than he has.
    /// </summary>
    public Actor Actor => new($"command:{Person?.Id ?? Sender ?? "unknown"}", ActorKind.User, [Actor.Any], CallerLevel);
}

public delegate ValueTask<Result<string>> CommandHandler(CommandContext context, CancellationToken cancellationToken);

/// <summary>One instruction arriving from a Channel's adapter, before any Channel has seen it.</summary>
public sealed record CommandInvocation(
    string Text,
    Address Adapter,
    string PlatformConversationId,
    string? PlatformMessageId,
    Guid EventId,
    Person? Person,
    string? Sender,
    string? SenderName);

/// <summary>
/// What came of an instruction. <see cref="Reply"/> is null when nothing should be said back — a throttled caller is
/// dropped in silence, because answering every extra message would make the limit itself the amplifier.
/// </summary>
public sealed record CommandOutcome(string? Reply, string Outcome, string? Detail = null)
{
    public static CommandOutcome Said(string reply, string outcome = "ok") => new(reply, outcome);
    public static CommandOutcome Silent(string outcome = "ok", string? detail = null) => new(null, outcome, detail);
}

/// <summary>How a message got where it was going, when an interceptor had a say.</summary>
public enum EnvelopeDisposition
{
    /// <summary>Not this interceptor's business: dispatch it as if nobody had looked.</summary>
    Pass,

    /// <summary>Taken care of. Nothing else sees it — no Channel, no script, no Session.</summary>
    Handled
}

/// <summary>
/// A look at an event before it is dispatched. The one that exists today is instructions (<c>ALN.Commands</c>), which
/// is why this is declared here rather than in the event queue: the queue must not know what an instruction is.
///
/// The contract, for the pump: <see cref="EnvelopeDisposition.Pass"/> means "dispatch normally";
/// <see cref="EnvelopeDisposition.Handled"/> means "done, and still settle it"; a failed result means "this was mine
/// and it went wrong" — the pump records the failure and settles the event too, because an event that was answered
/// with an error is finished, not retryable.
/// </summary>
public interface IEnvelopeInterceptor
{
    ValueTask<Result<EnvelopeDisposition>> InterceptAsync(RoutedEvent routedEvent, CancellationToken cancellationToken);
}

/// <summary>
/// The instruction table. Registering is deliberately boring: a path, a line of help, a depth, a body. Nothing about
/// who may run it is decided at registration beyond the depth, and nothing at all is decided by who registered it —
/// a script's instruction runs as the script (its own depth), never as the person who typed it.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>
    /// Whether this text is addressed to the instruction system at all. A message that starts with the prefix is an
    /// instruction even when no such instruction exists — it is answered with an error rather than passed on, because
    /// a person who typed <c>//foo</c> meant the system, not the model.
    /// </summary>
    bool LooksLike(string? text);

    Result Register(CommandNode node);

    /// <summary>Takes back every node an owner registered. Unknown owner is not an error: unload is not a promise.</summary>
    Result Unregister(string owner);

    /// <summary>What this caller may see, for <c>//help</c>. Hidden instructions are not listed at all.</summary>
    IReadOnlyList<CommandNode> Visible(AccessLevel caller);

    ValueTask<Result<CommandOutcome>> InvokeAsync(CommandInvocation invocation, CancellationToken cancellationToken);
}
