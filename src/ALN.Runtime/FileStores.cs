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
/// <summary>
/// Adapter specs on disk, nested the way the code is nested:
/// <code>
/// &lt;root&gt;/&lt;type&gt;/&lt;instance&gt;/adapter.json      e.g. adapters/qq/qq-1/adapter.json
/// </code>
/// The type segment is the last part of the type id (<c>adapter.qq</c> → <c>qq</c>), because the parent
/// directory already says "adapter". One instance is one directory, so whatever an adapter needs beside
/// its spec has an obvious place — and transport state (mailboxes, spools) does not belong here at all;
/// it lives under <c>&lt;dataRoot&gt;/io</c>. Configuration and IO have different lifetimes and different
/// backup rules, so they get different trees.
/// </summary>
public sealed class FileAdapterSpecStore(string root) : IAdapterSpecStore
{
    /// <summary>File name inside each instance directory.</summary>
    public const string FileName = "adapter.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private IReadOnlyList<string> _legacy = [];

    /// <summary>
    /// Spec files still sitting in the old flat layout (<c>&lt;root&gt;/&lt;instance&gt;.json</c>). They are
    /// ignored rather than guessed at: a file that is silently interpreted is a file that is silently
    /// half-migrated. The host prints these so the move is a decision somebody makes, not a surprise.
    /// </summary>
    public IReadOnlyList<string> LegacyLayout => _legacy;

    public async ValueTask<Result> SaveAsync(AdapterSpec spec, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(spec.Address);
        if (directory is null)
            return Result.Failure(new(ErrorCode.Invalid, $"Adapter address is not a file-safe instance: {spec.Address}"));
        return await SpecFile.WriteAsync(directory, FileName, new AdapterSpecDocument(
            spec.Address.ToString(), spec.Descriptor.PlatformId, spec.Descriptor.AccountId,
            spec.Descriptor.Direction.ToString(), spec.Settings), JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(address);
        if (directory is null)
            return Result.Failure(new(ErrorCode.Invalid, $"Adapter address is not a file-safe instance: {address}"));
        var deleted = await SpecFile.DeleteAsync(directory, FileName, cancellationToken).ConfigureAwait(false);
        if (!deleted.Ok) return deleted;
        // Remove the instance directory, then the type directory if nothing else is left in it: an
        // emptied tree that reappears on every start is how storage layouts rot.
        RemoveIfEmpty(directory);
        RemoveIfEmpty(Path.GetDirectoryName(directory)!);
        return Result.Success();
    }

    public async ValueTask<Result<IReadOnlyList<AdapterSpec>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return Result<IReadOnlyList<AdapterSpec>>.Success([]);
        try
        {
            var legacy = new List<string>();
            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
                legacy.Add(path);
            _legacy = legacy;

            var specs = new List<AdapterSpec>();
            foreach (var typeDirectory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                foreach (var instanceDirectory in Directory.EnumerateDirectories(typeDirectory).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Path.Combine(instanceDirectory, FileName);
                    if (!File.Exists(path)) continue;
                    SafeFileSystem.VerifyNewOrRegularFile(path, root);
                    var document = JsonSerializer.Deserialize<AdapterSpecDocument>(
                        await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
                    if (document is null)
                        return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid, $"Adapter spec is empty: {path}"));
                    if (!Address.TryParse(document.Address, out var address) || !address.IsInstance)
                        return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid, $"Stored Adapter address is invalid: {document.Address} ({path})"));
                    if (!Enum.TryParse<AdapterDirection>(document.Direction, ignoreCase: true, out var direction))
                        return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid, $"Stored Adapter direction is invalid: {document.Direction} ({path})"));
                    // The directory is the authority on where a spec lives; a file that disagrees with the
                    // folder it sits in is a hand-editing mistake that must be named, not reconciled.
                    var expectedType = TypeSegment(address.TypeId);
                    if (!string.Equals(expectedType, Path.GetFileName(typeDirectory), StringComparison.Ordinal) ||
                        !string.Equals(address.Instance, Path.GetFileName(instanceDirectory), StringComparison.Ordinal))
                        return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid,
                            $"Adapter spec does not match its directory: {document.Address} is in {Path.GetRelativePath(root, path)}; expected {expectedType}/{address.Instance}/{FileName}."));
                    specs.Add(new AdapterSpec(address, new AdapterDescriptor(document.PlatformId, document.AccountId, direction),
                        document.Settings));
                }
            }
            return Result<IReadOnlyList<AdapterSpec>>.Success(specs);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid, "Adapter specs could not be read.", ex.Message));
        }
    }

    /// <summary><c>adapter.qq</c> → <c>qq</c>: the prefix is implied by the parent directory.</summary>
    internal static string TypeSegment(string typeId)
    {
        var separator = typeId.LastIndexOf('.');
        return separator >= 0 ? typeId[(separator + 1)..] : typeId;
    }

    private string? DirectoryFor(Address address)
    {
        if (!address.IsInstance) return null;
        var type = TypeSegment(address.TypeId);
        if (!SpecFile.IsToken(type) || !SpecFile.IsToken(address.Instance)) return null;
        return Path.Combine(root, type, address.Instance!);
    }

    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory that cannot be removed is not a failure: the spec is gone, which is what was asked.
        }
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
        return await WriteAsync(root, address.Instance + ".json", document, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes one document into <paramref name="directory"/> under a fixed file name.</summary>
    public static async ValueTask<Result> WriteAsync<T>(string directory, string fileName, T document,
        JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        if (!IsToken(fileName))
            return Result.Failure(new(ErrorCode.Invalid, $"Spec file name is not a file-safe token: {fileName}"));
        try
        {
            var path = Path.Combine(directory, fileName);
            SafeFileSystem.EnsureDirectory(directory);
            SafeFileSystem.VerifyNewOrRegularFile(path, directory);
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
        return await DeleteAsync(root, address.Instance + ".json", cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<Result> DeleteAsync(string directory, string fileName, CancellationToken cancellationToken)
    {
        if (!IsToken(fileName))
            return Result.Failure(new(ErrorCode.Invalid, $"Spec file name is not a file-safe token: {fileName}"));
        try
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                SafeFileSystem.VerifyNewOrRegularFile(path, directory);
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

    internal static bool IsToken(string? value) =>
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
