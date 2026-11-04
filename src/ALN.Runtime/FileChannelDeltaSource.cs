using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// Hands out the messages a Channel has accepted since the last hand-out, windowed and remembered.
///
/// Two numbers matter and they are different:
/// <list type="bullet">
/// <item>the <b>cursor</b> — how many messages have been <b>delivered to a turn that finished</b>, kept in the state
/// store so a restart does not replay the conversation;</item>
/// <item>the <b>window</b> — how many of those the model may actually see this time. Everything older is
/// dropped from the request and replaced by a count, because a group that was quiet for a day should not
/// arrive as four hundred messages in one prompt.</item>
/// </list>
/// The window never changes the cursor: elided messages are delivered, not deferred. Re-delivering them
/// later would make the same backlog reappear forever.
///
/// Reading does not move the cursor at all — that is <see cref="ITurnCommit"/>'s job, and it runs only after the turn
/// that read them was written down. The cost of getting this wrong is lopsided: delivering twice costs a duplicate
/// the timeline already collapses, while delivering once and losing it costs a person's message, silently.
/// </summary>
public sealed class FileChannelDeltaSource(IMessageStore messages, IStateStore state, string scope = "channel.delta")
    : IChannelDeltaSource
{
    /// <summary>No window means "hand over whatever there is" — a caller's choice, not a silent default.</summary>
    public async ValueTask<Result<ChannelDeltaClaim>> ClaimDeltaAsync(Address channel, int? maxMessages,
        CancellationToken cancellationToken)
    {
        // Whatever it is called, this needs a concrete instance to read from. Deciding that it really is a channel
        // is the catalog's job, not a string test on the name.
        if (!channel.IsInstance)
            return Result<ChannelDeltaClaim>.Failure(new(ErrorCode.Invalid, $"Delta source needs a channel instance: {channel}"));
        if (maxMessages is <= 0)
            return Result<ChannelDeltaClaim>.Failure(new(ErrorCode.Invalid, $"Delta window must be positive: {maxMessages}"));

        var read = await messages.ReadAsync(channel, cancellationToken).ConfigureAwait(false);
        if (read.Error is { } readError) return Result<ChannelDeltaClaim>.Failure(readError);
        var all = (read.Value ?? []).Where(message => message.Direction == MessageDirection.Inbound).ToArray();

        // The whole name is the cursor key: it is unique, and taking a piece of it apart to build a key is exactly
        // what an opaque id forbids.
        var key = channel.ToString();
        var consumed = await ReadCursorAsync(state, scope, key, cancellationToken).ConfigureAwait(false);
        if (consumed.Error is { } cursorError) return Result<ChannelDeltaClaim>.Failure(cursorError);
        var already = Math.Min(consumed.Value, all.Length);

        var pending = all.Skip(already).ToArray();
        // Advanced when the turn commits, not now: if the request that carries these messages dies, they must still
        // be pending for the next one. The number is what was visible at this instant — messages arriving during
        // the turn belong to the next hand-out, not to this one.
        var commit = new AdvanceCursorCommit(state, scope, key, all.Length);
        if (pending.Length == 0) return Result<ChannelDeltaClaim>.Success(new(new(channel, [], 0), commit));

        // The window decides how much is shown, never how much is delivered: delivering only what is shown would
        // leave the elided head to be re-elided (and re-counted) on every later activation.
        if (maxMessages is { } window && pending.Length > window)
            return Result<ChannelDeltaClaim>.Success(new(new(channel, pending[^window..], pending.Length - window), commit));
        return Result<ChannelDeltaClaim>.Success(new(new(channel, pending, 0), commit));
    }

    /// <summary>
    /// Moves the cursor, and refuses to move it backwards: a turn that commits late must not undo a turn that
    /// committed after it and already re-read the conversation.
    /// </summary>
    private sealed class AdvanceCursorCommit(IStateStore state, string scope, string key, int consumed) : ITurnCommit
    {
        public async ValueTask<Result> CommitAsync(CancellationToken cancellationToken)
        {
            var current = await ReadCursorAsync(state, scope, key, cancellationToken).ConfigureAwait(false);
            if (current.Error is { } error) return Result.Failure(error);
            if (current.Value >= consumed) return Result.Success();
            var json = JsonSerializer.Serialize(new Cursor(consumed), JsonOptions);
            return await state.WriteAsync(scope, key, json, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<Result<int>> ReadCursorAsync(IStateStore state, string scope, string key,
        CancellationToken cancellationToken)
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Cursor(int Consumed);
}
