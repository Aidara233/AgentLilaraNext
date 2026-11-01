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
