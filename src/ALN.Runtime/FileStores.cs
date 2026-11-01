using System.Text.Json;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>
/// Per-owner message history: <c>&lt;root&gt;/&lt;kind&gt;/&lt;instance&gt;/&lt;yyyyMMdd&gt;.jsonl</c>.
/// Append-only and daily, so reading a day is one file and retention is a delete.
/// </summary>
public sealed class FileMessageStore(string root) : IMessageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private readonly SemaphoreSlim _append = new(1, 1);

    public async ValueTask<Result> AppendAsync(StoredMessage message, CancellationToken cancellationToken)
    {
        if (message is null || string.IsNullOrWhiteSpace(message.Id) || message.Text is null)
            return Result.Failure(new(ErrorCode.Invalid, "Stored message requires an id and text."));
        var directory = DirectoryFor(message.Owner);
        if (directory is null)
            return Result.Failure(new(ErrorCode.Invalid, $"Message owner is not a valid instance address: {message.Owner}"));
        await _append.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(directory, $"{DateOnly.FromDateTime(message.At.UtcDateTime):yyyyMMdd}.jsonl");
            SafeFileSystem.EnsureDirectory(root);
            SafeFileSystem.EnsureDirectory(directory);
            SafeFileSystem.VerifyNewOrRegularFile(path, root);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new(ErrorCode.Unavailable, "Message could not be appended.", ex.Message));
        }
        finally { _append.Release(); }
    }

    public async ValueTask<Result<IReadOnlyList<StoredMessage>>> ReadAsync(Address owner, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(owner);
        if (directory is null || !Directory.Exists(directory))
            return Result<IReadOnlyList<StoredMessage>>.Success([]);
        try
        {
            var messages = new List<StoredMessage>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl").Order(StringComparer.Ordinal))
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var message = JsonSerializer.Deserialize<StoredMessage>(line, JsonOptions);
                if (message is not null) messages.Add(message);
            }
            return Result<IReadOnlyList<StoredMessage>>.Success(messages.OrderBy(m => m.At).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<StoredMessage>>.Failure(new(ErrorCode.Unavailable, "Message history could not be read.", ex.Message));
        }
    }

    private string? DirectoryFor(Address owner)
    {
        if (!owner.IsInstance || !IsToken(owner.Instance) || !IsToken(owner.Kind)) return null;
        return Path.Combine(root, owner.Kind!, owner.Instance!);
    }

    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}

/// <summary>
/// Wraps the event sink so every inbound Adapter message is recorded once, at the single boundary
/// adapters publish through. Adapters stay ignorant of storage.
/// </summary>
public sealed class RecordingEventSink(IEventSink inner, IMessageStore messages) : IEventSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<Result> PublishAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
    {
        if (routedEvent.Name == EventNames.AdapterEnvelope)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<AdapterEnvelope>(routedEvent.PayloadJson, JsonOptions);
                if (envelope is not null)
                    await messages.AppendAsync(new StoredMessage(envelope.EventId.ToString("N"), envelope.Adapter,
                        envelope.PlatformConversationId, envelope.PlatformMessageId, envelope.Sender, envelope.Text,
                        envelope.At), cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) { /* a malformed envelope is still dispatched; recording must not block it */ }
        }
        return await inner.PublishAsync(routedEvent, cancellationToken).ConfigureAwait(false);
    }
}
