using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// Floating clock context: the current time changes every round, so it is re-read and rendered
/// into this request only. It is deliberately never written to the timeline — history should not
/// accumulate stale timestamps.
/// </summary>
public sealed class TimeContextContributor(TimeProvider? timeProvider = null) : IRoundContextContributor
{
    public const string ContributorId = "system.time";
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Id => ContributorId;

    public ValueTask ContributeAsync(RoundContextRequest request, ISessionTimelineWriter timeline,
        IRoundContextWriter roundContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _time.GetLocalNow();
        roundContext.Add(new RoundContextContribution(
            "now",
            "time",
            $"当前时间：{now:yyyy-MM-dd HH:mm:ss zzz}（{_time.GetUtcNow():yyyy-MM-dd HH:mm:ss} UTC）",
            Order: -100));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Carries a Channel's unconsumed messages into the Session timeline at the moment the request is being
/// assembled — this is the second half of the two-phase activation. It writes to the timeline rather than
/// to the floating block because channel messages are durable facts: they belong in history, they render
/// through the same <c>&lt;channel_messages&gt;</c> path as before, and the stable message id makes a
/// replay idempotent.
///
/// An empty delta is normal, not an error: a signal can arrive for reasons that have nothing to do with
/// channels (a timer, a script), and the Session decides whether a turn with no new messages is worth
/// running.
/// </summary>
public sealed class ChannelDeltaContributor(IChannelDeltaSource source) : IRoundContextContributor
{
    public const string ContributorId = "channel.delta";

    public string Id => ContributorId;

    public async ValueTask ContributeAsync(RoundContextRequest request, ISessionTimelineWriter timeline,
        IRoundContextWriter roundContext, CancellationToken cancellationToken)
    {
        if (request.Channel is not { } channel) return;
        var delta = await source.TakeDeltaAsync(channel, cancellationToken).ConfigureAwait(false);
        // A failed pull fails the round: silently sending the model a context that is missing what people
        // just said is worse than not answering this time.
        if (!delta.Ok) throw new InvalidOperationException(delta.Error!.Message);
        foreach (var message in delta.Value!.Messages)
            timeline.Add(new SessionTimelineContribution(message.Id, message.Sender, message.Text, message.At));
    }
}

/// <summary>
/// Channel identity is fixed for the life of a session, so it is injected once — as the first
/// timeline entry, not as a per-round reminder. The stable contribution id makes the write
/// idempotent: replaying the first turn cannot produce a second copy.
/// </summary>
public sealed class ChannelInfoContributor : IRoundContextContributor
{
    public const string ContributorId = "system.channel-info";

    public string Id => ContributorId;

    public ValueTask ContributeAsync(RoundContextRequest request, ISessionTimelineWriter timeline,
        IRoundContextWriter roundContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Channel is not { } channel) return ValueTask.CompletedTask;
        // Only the opening turn carries it; later turns already have it in their history.
        if (request.StepNumber > 1) return ValueTask.CompletedTask;

        var lines = new List<string> { $"频道地址：{channel}" };
        if (request.Binding is { } binding)
        {
            lines.Add($"绑定适配器：{binding.Adapter}");
            lines.Add($"平台会话：{binding.PlatformConversationId}");
        }
        timeline.Add(new SessionTimelineContribution(
            "channel-info",
            "system",
            string.Join('\n', lines),
            DateTimeOffset.UtcNow));
        return ValueTask.CompletedTask;
    }
}
