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
public sealed class MemoryContextSource(IMemoryStore store) : IFloatingContextSource
{
    public const string SourceId = "system.memory";

    public string Id => SourceId;

    public async ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(
        RoundContextRequest request, CancellationToken cancellationToken)
    {
        var scope = ConversationScope.Of(request);
        var counts = await store.CountEntriesAsync(scope, cancellationToken).ConfigureAwait(false);
        // A store that cannot be read says nothing rather than failing the round: memory is an advantage, and one
        // that is momentarily unavailable must not cost the conversation its answer.
        if (!counts.Ok || counts.Value is null || counts.Value.Total == 0)
            return Result<IReadOnlyList<RoundContextContribution>>.Success([]);

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

        IReadOnlyList<RoundContextContribution> sections =
        [
            new RoundContextContribution("memory", "memory", string.Join('\n', lines), Order: -90)
        ];
        return Result<IReadOnlyList<RoundContextContribution>>.Success(sections);
    }
}

/// <summary>
/// Which conversation's memory a round may read. Not the Session id: a Session is meant to be replaced, and memory
/// that dies with it is not memory. Not the Channel object either: memory outlives Channels, and a Session that
/// woke itself up has no Channel but still has a scope.
/// </summary>
public static class ConversationScope
{
    public static string Of(RoundContextRequest request) =>
        request.Channel?.ToString() ?? request.SessionId;
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
            entries.Add(new SessionTimelineContribution(message.Id, message.Sender, message.Text, message.At));

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
