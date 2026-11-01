using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Hands out the messages a Channel has accepted since the last hand-out, and remembers where it stopped.
///
/// The cursor is a count, not a timestamp: the history files are append-only and per-Channel, so "how many
/// have I already given away" is stable across restarts and cannot go backwards. It is stored next to the
/// history it indexes (<c>messages/&lt;channel&gt;/cursor.json</c>) because the two must move together —
/// a cursor without its history would replay old messages into a fresh Session.
/// </summary>
public sealed class FileChannelDeltaSource(IMessageStore messages, string root) : IChannelDeltaSource
{
    private const string CursorFileName = "cursor.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<Result<ChannelDelta>> TakeDeltaAsync(Address channel, CancellationToken cancellationToken)
    {
        if (!channel.IsInstance || channel.Module != "channel")
            return Result<ChannelDelta>.Failure(new(ErrorCode.Invalid, $"Delta source needs a channel instance: {channel}"));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await messages.ReadAsync(channel, cancellationToken).ConfigureAwait(false);
            if (!read.Ok) return Result<ChannelDelta>.Failure(read.Error!);
            var all = read.Value!;
            var consumed = ReadCursor(channel);
            if (consumed > all.Count) consumed = all.Count;
            var pending = all.Skip(consumed).ToArray();
            if (pending.Length > 0) WriteCursor(channel, all.Count);
            return Result<ChannelDelta>.Success(new(channel, pending));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result<ChannelDelta>.Failure(new(ErrorCode.Unavailable, "Channel delta could not be taken.", ex.Message));
        }
        finally { _gate.Release(); }
    }

    private string? CursorPath(Address channel)
    {
        var instance = channel.Instance;
        if (instance is null || instance.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return null;
        return Path.Combine(root, instance, CursorFileName);
    }

    private int ReadCursor(Address channel)
    {
        var path = CursorPath(channel);
        if (path is null || !File.Exists(path)) return 0;
        try
        {
            var document = JsonSerializer.Deserialize<Cursor>(File.ReadAllBytes(path), JsonOptions);
            return document?.Consumed ?? 0;
        }
        catch (JsonException)
        {
            // A corrupt cursor replays rather than skips: the Session timeline deduplicates by message id,
            // so replaying is visible and harmless, while skipping silently loses what was said.
            return 0;
        }
    }

    private void WriteCursor(Address channel, int consumed)
    {
        var path = CursorPath(channel);
        if (path is null) return;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".partial";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Cursor(consumed), JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Cursor(int Consumed);
}
