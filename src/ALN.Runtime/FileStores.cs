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

/// <summary>Adapter specs: one JSON file per instance under <c>&lt;root&gt;</c>.</summary>
public sealed class FileAdapterSpecStore(string root) : IAdapterSpecStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ValueTask<Result> SaveAsync(AdapterSpec spec, CancellationToken cancellationToken) =>
        SpecFile.WriteAsync(root, spec.Address, new AdapterSpecDocument(
            spec.Address.ToString(), spec.Descriptor.PlatformId, spec.Descriptor.AccountId,
            spec.Descriptor.Direction.ToString(), spec.Settings), JsonOptions, cancellationToken);

    public ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken) =>
        SpecFile.DeleteAsync(root, address, cancellationToken);

    public async ValueTask<Result<IReadOnlyList<AdapterSpec>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var documents = await SpecFile.LoadAllAsync<AdapterSpecDocument>(root, cancellationToken).ConfigureAwait(false);
        if (!documents.Ok) return Result<IReadOnlyList<AdapterSpec>>.Failure(documents.Error!);
        var specs = new List<AdapterSpec>();
        foreach (var document in documents.Value!)
        {
            if (!Address.TryParse(document.Address, out var address) || !address.IsInstance)
                return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid, $"Stored Adapter address is invalid: {document.Address}"));
            if (!Enum.TryParse<AdapterDirection>(document.Direction, ignoreCase: true, out var direction))
                return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid, $"Stored Adapter direction is invalid: {document.Direction}"));
            specs.Add(new AdapterSpec(address, new AdapterDescriptor(document.PlatformId, document.AccountId, direction),
                document.Settings));
        }
        return Result<IReadOnlyList<AdapterSpec>>.Success(specs);
    }

    /// <summary>
    /// <c>Settings</c> is written only when present, so a spec that doesn't need any stays a three-line
    /// file and existing files load unchanged.
    /// </summary>
    public sealed record AdapterSpecDocument(string Address, string PlatformId, string AccountId, string Direction,
        IReadOnlyDictionary<string, string>? Settings = null);
}

/// <summary>Channel specs: one JSON file per channel under <c>&lt;root&gt;</c>.</summary>
public sealed class FileChannelSpecStore(string root) : IChannelSpecStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ValueTask<Result> SaveAsync(ChannelSpec spec, CancellationToken cancellationToken) =>
        SpecFile.WriteAsync(root, spec.Address, new ChannelSpecDocument(
            spec.Address.ToString(), spec.Name,
            spec.Bindings.Select(b => new ChannelBindingDocument(b.Adapter.ToString(), b.PlatformConversationId)).ToArray()),
            JsonOptions, cancellationToken);

    public ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken) =>
        SpecFile.DeleteAsync(root, address, cancellationToken);

    public async ValueTask<Result<IReadOnlyList<ChannelSpec>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var documents = await SpecFile.LoadAllAsync<ChannelSpecDocument>(root, cancellationToken).ConfigureAwait(false);
        if (!documents.Ok) return Result<IReadOnlyList<ChannelSpec>>.Failure(documents.Error!);
        var specs = new List<ChannelSpec>();
        foreach (var document in documents.Value!)
        {
            if (!Address.TryParse(document.Address, out var address) || !address.IsInstance)
                return Result<IReadOnlyList<ChannelSpec>>.Failure(new(ErrorCode.Invalid, $"Stored Channel address is invalid: {document.Address}"));
            var bindings = new List<ChannelBinding>();
            foreach (var binding in document.Bindings ?? [])
            {
                if (!Address.TryParse(binding.Adapter, out var adapter) || !adapter.IsInstance)
                    return Result<IReadOnlyList<ChannelSpec>>.Failure(new(ErrorCode.Invalid, $"Stored Channel binding address is invalid: {binding.Adapter}"));
                bindings.Add(new ChannelBinding(adapter, binding.PlatformConversationId));
            }
            specs.Add(new ChannelSpec(address, document.Name, bindings));
        }
        return Result<IReadOnlyList<ChannelSpec>>.Success(specs);
    }

    public sealed record ChannelBindingDocument(string Adapter, string PlatformConversationId);
    public sealed record ChannelSpecDocument(string Address, string Name, ChannelBindingDocument[]? Bindings);
}

/// <summary>Shared layout for "one instance = one JSON file" specs.</summary>
internal static class SpecFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask<Result> WriteAsync<T>(string root, Address address, T document,
        JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        if (!address.IsInstance || !IsToken(address.Instance))
            return Result.Failure(new(ErrorCode.Invalid, $"Spec address is not a file-safe instance: {address}"));
        try
        {
            var path = Path.Combine(root, address.Instance + ".json");
            SafeFileSystem.EnsureDirectory(root);
            SafeFileSystem.VerifyNewOrRegularFile(path, root);
            // Write-then-rename so a crash cannot leave a half-parsed spec behind.
            var temporary = path + ".partial";
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(document, options), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new(ErrorCode.Unavailable, "Spec could not be written.", ex.Message));
        }
    }

    public static async ValueTask<Result> DeleteAsync(string root, Address address, CancellationToken cancellationToken)
    {
        if (!address.IsInstance || !IsToken(address.Instance))
            return Result.Failure(new(ErrorCode.Invalid, $"Spec address is not a file-safe instance: {address}"));
        try
        {
            var path = Path.Combine(root, address.Instance + ".json");
            if (File.Exists(path))
            {
                SafeFileSystem.VerifyNewOrRegularFile(path, root);
                File.Delete(path);
            }
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new(ErrorCode.Unavailable, "Spec could not be deleted.", ex.Message));
        }
    }

    public static async ValueTask<Result<IReadOnlyList<T>>> LoadAllAsync<T>(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return Result<IReadOnlyList<T>>.Success([]);
        try
        {
            var documents = new List<T>();
            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                SafeFileSystem.VerifyNewOrRegularFile(path, root);
                var document = JsonSerializer.Deserialize<T>(
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
                if (document is not null) documents.Add(document);
            }
            return Result<IReadOnlyList<T>>.Success(documents);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<T>>.Failure(new(ErrorCode.Invalid, "Specs could not be read.", ex.Message));
        }
    }

    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
        value is not "." and not "..";
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
