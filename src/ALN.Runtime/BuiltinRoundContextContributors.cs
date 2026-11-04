using ALN.Contracts;
using ALN.Contracts.Memory;
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
        IRoundContextWriter roundContext, ITurnCommitSink commits, CancellationToken cancellationToken)
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
/// The floating memory line: what this conversation has hung on to, in one glance.
///
/// It exists because memory as a tool has a hole: a model that never calls <c>recall</c> cannot know there is
/// anything to recall, so "remember" quietly degrades into "store it and never look". One line per round closes
/// that hole without putting the store into the request: the count, the latest conclusion, and the one thing the
/// model can do about a store that has stopped being useful.
///
/// Deliberately not the entries themselves. Recall is a tool because choosing what to pull needs the model's own
/// sense of the question; flooding every round with thirty memories would pay that cost on every step and make
/// the useful three invisible.
/// </summary>
public sealed class MemoryContextContributor(IMemoryStore store) : IRoundContextContributor
{
    public const string ContributorId = "system.memory";

    public string Id => ContributorId;

    public async ValueTask ContributeAsync(RoundContextRequest request, ISessionTimelineWriter timeline,
        IRoundContextWriter roundContext, ITurnCommitSink commits, CancellationToken cancellationToken)
    {
        if (request.MemoryScope is not { Length: > 0 } scope) return;
        var counts = await store.CountEntriesAsync(scope, cancellationToken).ConfigureAwait(false);
        // A store that cannot be read says nothing rather than failing the round: memory is an advantage, and one
        // that is momentarily unavailable must not cost the conversation its answer.
        if (!counts.Ok || counts.Value is null || counts.Value.Total == 0) return;

        var lines = new List<string>
        {
            $"这个会话记住了 {counts.Value.Fragments} 条碎片、{counts.Value.Notes} 条笔记。要翻用 recall，要写用 remember。"
        };
        var recent = await store.RecentAsync(scope, IMemoryStore.DefaultLimit, cancellationToken).ConfigureAwait(false);
        if (recent.Ok && recent.Value is { } entries)
        {
            // The newest note, not the newest row: a conclusion is what a reader can act on, and the fragments
            // around it are chatter. When there is no note yet, saying so is the useful fact.
            var note = entries.FirstOrDefault(entry => entry.Kind == MemoryKind.Note);
            if (note is null) lines.Add("还没有笔记：碎片攒够了就整理成一两条（带原文证据），再 forget 掉被吸收的那些。");
            else
            {
                var when = note.At.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var text = note.Text.Trim().Replace('\n', ' ');
                lines.Add($"最近一条笔记（{when}）：{(text.Length > 200 ? text[..200] + "…" : text)}");
            }
        }
        if (MemoryGuidance.ConsolidationHint(counts.Value) is { } hint) lines.Add(hint);

        roundContext.Add(new RoundContextContribution(
            "memory",
            "memory",
            string.Join('\n', lines),
            Order: -90));
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
///
/// The Channel's window is applied here — the oldest messages beyond it are replaced by one line saying
/// how many were dropped. A model that is told "and there were 40 more before this" can ask about them;
/// a model that is silently handed the tail of a conversation cannot even tell that it is missing context.
/// </summary>
public sealed class ChannelDeltaContributor(IChannelDeltaSource source, IChannelManager channels) : IRoundContextContributor
{
    public const string ContributorId = "channel.delta";

    public string Id => ContributorId;

    public async ValueTask ContributeAsync(RoundContextRequest request, ISessionTimelineWriter timeline,
        IRoundContextWriter roundContext, ITurnCommitSink commits, CancellationToken cancellationToken)
    {
        if (request.Channel is not { } channel) return;
        var window = channels.Get(channel) is { Ok: true, Value: { } info } ? info.MaxMessages : null;
        var claimed = await source.ClaimDeltaAsync(channel, window, cancellationToken).ConfigureAwait(false);
        // A failed pull fails the round: silently sending the model a context that is missing what people
        // just said is worse than not answering this time.
        if (!claimed.Ok) throw new InvalidOperationException(claimed.Error!.Message);

        var claim = claimed.Value!;
        // The cursor moves when this turn has been written down, not now: a turn that dies still owes these
        // messages to the next one.
        commits.Add(claim.Commit);
        var value = claim.Delta;
        if (value.Omitted > 0)
            timeline.Add(new SessionTimelineContribution(
                $"omitted-{value.Messages[0].Id}",
                "system",
                $"省略更早的 {value.Omitted} 条消息（频道窗口上限）。",
                value.Messages[0].At));
        foreach (var message in value.Messages)
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
        IRoundContextWriter roundContext, ITurnCommitSink commits, CancellationToken cancellationToken)
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
