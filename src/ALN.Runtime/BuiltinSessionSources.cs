using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// Floating clock context: the current time changes every round, so it is re-read and rendered into this request
/// only. It is deliberately never written to the timeline — history should not accumulate stale timestamps.
/// </summary>
public sealed class TimeContextSource(TimeProvider? timeProvider = null) : IFloatingContextSource
{
    public const string SourceId = "system.time";
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Id => SourceId;

    public ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(
        RoundContextRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _time.GetLocalNow();
        IReadOnlyList<RoundContextContribution> sections =
        [
            new RoundContextContribution("now", "time",
                $"当前时间：{now:yyyy-MM-dd HH:mm:ss zzz}（{_time.GetUtcNow():yyyy-MM-dd HH:mm:ss} UTC）", Order: -100)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<RoundContextContribution>>.Success(sections));
    }
}

/// <summary>
/// Carries a Channel's unconsumed messages into the Session timeline at the moment the request is being assembled —
/// this is the second half of the two-phase activation. It writes to the timeline rather than to the floating block
/// because channel messages are durable facts: they belong in history, they render through the same
/// <c>&lt;channel_messages&gt;</c> path as before, and the stable message id makes a replay idempotent.
///
/// An empty delta is normal, not an error: a signal can arrive for reasons that have nothing to do with channels
/// (a timer, a script), and the Session decides whether a turn with no new messages is worth running.
///
/// The Channel's window is applied here — the oldest messages beyond it are replaced by one line saying how many
/// were dropped. A model that is told "and there were 40 more before this" can ask about them; a model that is
/// silently handed the tail of a conversation cannot even tell that it is missing context.
/// </summary>
public sealed class ChannelDeltaSource(IChannelDeltaSource source, IChannelManager channels) : ITimelineSource
{
    public const string SourceId = "channel.delta";

    public string Id => SourceId;

    public async ValueTask<Result<SessionTimelineBatch>> CollectAsync(
        RoundContextRequest request, CancellationToken cancellationToken)
    {
        if (request.Channel is not { } channel) return Result<SessionTimelineBatch>.Success(new([]));
        var window = channels.Get(channel) is { Ok: true, Value: { } info } ? info.MaxMessages : null;
        var claimed = await source.ClaimDeltaAsync(channel, window, cancellationToken).ConfigureAwait(false);
        // A delta that cannot be read is this source's failure, and the Session degrades: it does not send the model
        // a context that is silently missing what people just said, and it does not take the turn down either.
        if (!claimed.Ok || claimed.Value is null)
            return Result<SessionTimelineBatch>.Failure(claimed.Error ?? new(ErrorCode.Unavailable, "Channel delta unavailable."));

        var claim = claimed.Value;
        var entries = new List<SessionTimelineContribution>();
        var value = claim.Delta;
        if (value.Omitted > 0)
            entries.Add(new SessionTimelineContribution($"omitted-{value.Messages[0].Id}", "system",
                $"省略更早的 {value.Omitted} 条消息（频道窗口上限）。", value.Messages[0].At));
        foreach (var message in value.Messages)
            entries.Add(new SessionTimelineContribution(message.Id,
                // The name recorded with the message, then the id. Outbound rows have neither name nor sender, and
                // the composer already answers that case with an empty string.
                ChannelMessage.Compose(message.SenderName, message.Sender, message.SenderRemark),
                message.Text, message.At));

        // The cursor moves when this turn has been written down, not now: a turn that dies still owes these
        // messages to the next one.
        return Result<SessionTimelineBatch>.Success(new(entries, [claim.Commit]));
    }
}

/// <summary>
/// Channel identity is fixed for the life of a session, so it is injected once — as the first timeline entry, not
/// as a per-round reminder. The stable contribution id makes the write idempotent: replaying the first turn cannot
/// produce a second copy.
/// </summary>
public sealed class ChannelInfoSource : ITimelineSource
{
    public const string SourceId = "system.channel-info";

    public string Id => SourceId;

    public ValueTask<Result<SessionTimelineBatch>> CollectAsync(RoundContextRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Channel is not { } channel) return ValueTask.FromResult(Result<SessionTimelineBatch>.Success(new([])));
        // Only the opening turn carries it; later turns already have it in their history.
        if (request.StepNumber > 1) return ValueTask.FromResult(Result<SessionTimelineBatch>.Success(new([])));

        var lines = new List<string> { $"频道地址：{channel}" };
        if (request.Binding is { } binding)
        {
            lines.Add($"绑定适配器：{binding.Adapter}");
            lines.Add($"平台会话：{binding.PlatformConversationId}");
        }
        return ValueTask.FromResult(Result<SessionTimelineBatch>.Success(new(
        [
            new SessionTimelineContribution("channel-info", "system", string.Join('\n', lines), DateTimeOffset.UtcNow)
        ])));
    }
}
