using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Hands out the messages a Channel has accepted since the last hand-out, windowed and remembered.
///
/// Two numbers matter and they are different:
/// <list type="bullet">
/// <item>the <b>cursor</b> — how many messages have ever been handed out, kept in the state store so a
/// restart does not replay the conversation;</item>
/// <item>the <b>window</b> — how many of those the model may actually see this time. Everything older is
/// dropped from the request and replaced by a count, because a group that was quiet for a day should not
/// arrive as four hundred messages in one prompt.</item>
/// </list>
/// The window never changes the cursor: elided messages are consumed, not deferred. Re-delivering them
/// later would make the same backlog reappear forever.
/// </summary>
public sealed class FileChannelDeltaSource(IMessageStore messages, IStateStore state, string scope = "channel.delta")
    : IChannelDeltaSource
{
    /// <summary>No window means "hand over whatever there is" — a caller's choice, not a silent default.</summary>
    public async ValueTask<Result<ChannelDelta>> TakeDeltaAsync(Address channel, int? maxMessages,
        CancellationToken cancellationToken)
    {
        if (!channel.IsInstance || channel.Module != "channel")
            return Result<ChannelDelta>.Failure(new(ErrorCode.Invalid, $"Delta source needs a channel instance: {channel}"));
        if (maxMessages is <= 0)
            return Result<ChannelDelta>.Failure(new(ErrorCode.Invalid, $"Delta window must be positive: {maxMessages}"));

        var read = await messages.ReadAsync(channel, cancellationToken).ConfigureAwait(false);
        if (read.Error is { } readError) return Result<ChannelDelta>.Failure(readError);
        var all = (read.Value ?? []).Where(message => message.Direction == MessageDirection.Inbound).ToArray();

        var key = channel.Instance!;
        var consumed = await ReadCursorAsync(key, cancellationToken).ConfigureAwait(false);
        if (consumed.Error is { } cursorError) return Result<ChannelDelta>.Failure(cursorError);
        var already = Math.Min(consumed.Value, all.Length);

        var pending = all.Skip(already).ToArray();
        if (pending.Length == 0) return Result<ChannelDelta>.Success(new(channel, [], 0));

        // Consume everything first, then decide how much of it is shown. Consuming only what is shown
        // would leave the elided head to be re-elided (and re-counted) on every later activation.
        var advanced = await WriteCursorAsync(key, all.Length, cancellationToken).ConfigureAwait(false);
        if (advanced.Error is { } writeError) return Result<ChannelDelta>.Failure(writeError);

        if (maxMessages is { } window && pending.Length > window)
            return Result<ChannelDelta>.Success(new(channel, pending[^window..], pending.Length - window));
        return Result<ChannelDelta>.Success(new(channel, pending, 0));
    }

    private async ValueTask<Result<int>> ReadCursorAsync(string key, CancellationToken cancellationToken)
    {
        var stored = await state.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (stored.Error is { } readError) return Result<int>.Failure(readError);
        if (stored.Value is null) return Result<int>.Success(0);
        try
        {
            var document = JsonSerializer.Deserialize<Cursor>(stored.Value, JsonOptions);
            return Result<int>.Success(Math.Max(0, document?.Consumed ?? 0));
        }
        catch (JsonException)
        {
            // An unreadable cursor replays rather than skips: the Session timeline deduplicates by message
            // id, so replaying is visible and harmless, while skipping silently loses what people said.
            return Result<int>.Success(0);
        }
    }

    private async ValueTask<Result> WriteCursorAsync(string key, int consumed, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new Cursor(consumed), JsonOptions);
        return await state.WriteAsync(scope, key, json, cancellationToken).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Cursor(int Consumed);
}
