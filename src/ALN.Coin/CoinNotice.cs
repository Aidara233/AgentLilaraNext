using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Coin;

/// <summary>
/// What a notice needs from the conversation it is telling: which Channel a conversation is bound to, which Session
/// serves that Channel, and how to put a line in it.
///
/// <b>Three questions, not two managers.</b> The host keeps <see cref="IChannelManager"/> and
/// <see cref="ISessionManager"/> for everything a Channel and a Session can do — thirteen methods each, lifecycles,
/// archives, sweeps. A notifier that reads one binding, finds one id and writes one message should not depend on all
/// that: it would be a dependency on the whole world to use three corners of it, and anything testing the notifier
/// would have to imitate the whole world too. This is the two corners, named by the module that uses them.
/// </summary>
public interface ICoinConversations
{
    /// <summary>The Channel a conversation is bound to, or a failure when nothing is bound.</summary>
    ValueTask<Result<Address>> ChannelOfAsync(Address adapter, string conversation, CancellationToken cancellationToken);

    /// <summary>The Session serving <paramref name="channel"/>, or a failure when none does yet.</summary>
    ValueTask<Result<string>> SessionOfAsync(Address channel, CancellationToken cancellationToken);

    /// <summary>Puts <paramref name="text"/> in <paramref name="session"/> and asks it to look now.</summary>
    ValueTask<Result> PostAsync(string session, string text, CancellationToken cancellationToken);
}

/// <summary>
/// The one implementation, and the three answers as this deployment gives them: bindings come from the Channel table,
/// the Session is the one whose <c>Channel</c> matches, and the note arrives through
/// <see cref="ISessionManager.DeliverAsync"/> — the door for "a component has something to say", which keeps it apart
/// from what people said.
/// </summary>
public sealed class SessionConversations(IChannelManager channels, ISessionManager sessions) : ICoinConversations
{
    /// <summary>Who the note is from: this component, not the person. The person is named inside the text.</summary>
    public const string Sender = "coin";

    public ValueTask<Result<Address>> ChannelOfAsync(Address adapter, string conversation,
        CancellationToken cancellationToken)
    {
        var bound = channels.FindBound(adapter, conversation);
        return ValueTask.FromResult(bound.Ok && bound.Value is not null
            ? Result<Address>.Success(bound.Value.Address)
            : Result<Address>.Failure(bound.Error ?? new(ErrorCode.Invalid, "没有 Channel 绑着这个对话。")));
    }

    public async ValueTask<Result<string>> SessionOfAsync(Address channel, CancellationToken cancellationToken)
    {
        var address = channel.ToString();
        var listed = await sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!listed.Ok || listed.Value is null)
            return Result<string>.Failure(listed.Error ?? new(ErrorCode.Unavailable, "读不到 Session 清单。"));

        // One note is enough. A second Session on the same Channel would be a second voice in a conversation, and that
        // is not a thing the purse gets to create by noticing something.
        var serving = listed.Value.FirstOrDefault(summary =>
            string.Equals(summary.Channel, address, StringComparison.Ordinal));
        return serving is null
            ? Result<string>.Failure(new(ErrorCode.Unavailable, $"还没有 Session 服务 {address}。"))
            : Result<string>.Success(serving.Id);
    }

    public async ValueTask<Result> PostAsync(string session, string text, CancellationToken cancellationToken) =>
        // Busy is the ordinary case for a conversation somebody is talking in: DeliverAsync writes the note down first
        // and only then asks for a turn, so Conflict is a skipped wake-up rather than a lost note.
        await sessions.DeliverAsync(session, text, Sender, AccessLevel.Agent, wake: true, cancellationToken)
            .ConfigureAwait(false) is var delivered && delivered.Ok
            ? Result.Success()
            : Result.Failure(delivered.Error ?? new(ErrorCode.Unavailable, "没能把消息交给 Session。"));
}

/// <summary>
/// Telling the conversation that somebody put a coin in: one note handed to the Session that serves the Channel this
/// instruction arrived in.
///
/// <b>Why the Session and not the Channel.</b> An instruction is taken out of the stream before any Channel sees it, so
/// there is no Channel row for the message that carried it — and writing one now would be inventing a message the person
/// never sent. What did happen is a fact about the purse, so it travels the way every other component's message travels:
/// a relay note, which says where it came from and lands in the one place the model actually reads.
///
/// <b>Best effort, always.</b> The coin is already in the purse by the time this runs, so a conversation that cannot be
/// found, an unbound Adapter or a Session busy with a turn must not turn a successful toss into a failure. Every one of
/// those outcomes leaves exactly one line in the signal log and nothing else.
/// </summary>
/// <param name="conversations">Null means this deployment wired none, and the note is simply not sent.</param>
public sealed class CoinNotice(ICoinConversations? conversations = null, ISignalLog? signals = null)
{
    /// <summary>
    /// Hands <paramref name="text"/> to the Session serving (<paramref name="adapter"/>, <paramref name="conversation"/>)
    /// and wakes it, so the note is something Lilara can answer now rather than something she reads a turn later.
    /// </summary>
    public async ValueTask SendAsync(Address adapter, string conversation, string text,
        CancellationToken cancellationToken)
    {
        if (conversations is null) return;
        if (string.IsNullOrWhiteSpace(conversation))
        {
            Skip("the instruction carried no conversation");
            return;
        }

        var channel = await conversations.ChannelOfAsync(adapter, conversation, cancellationToken).ConfigureAwait(false);
        if (!channel.Ok)
        {
            Skip(channel.Error?.Message ?? "no Channel is bound");
            return;
        }

        var session = await conversations.SessionOfAsync(channel.Value, cancellationToken).ConfigureAwait(false);
        if (!session.Ok || string.IsNullOrEmpty(session.Value))
        {
            Skip(session.Error?.Message ?? "no Session serves that Channel");
            return;
        }

        var posted = await conversations.PostAsync(session.Value, text, cancellationToken).ConfigureAwait(false);
        if (!posted.Ok) Skip($"the note for {session.Value} was not woken: {posted.Error?.Message}");
    }

    private void Skip(string reason) => signals?.Write(new SignalRecord
    {
        Group = "coin",
        Name = "notice",
        From = SessionConversations.Sender,
        To = "session",
        Mechanism = "coin.gift",
        Outcome = "skipped",
        Level = SignalLevel.Info,
        Detail = new Dictionary<string, string> { ["reason"] = reason }
    });
}
