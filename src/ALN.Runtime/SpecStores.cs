using System.Text.Json;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>
/// Both spec stores share one layout, because a spec is a spec:
/// <code>
/// &lt;root&gt;/&lt;type&gt;/&lt;instance&gt;/&lt;file&gt;    adapters/qq/qq-1/adapter.json
///                                        channels/conversation/qq-123456/channel.json
/// </code>
/// The type segment is the last part of the type id (<c>adapter.qq</c> → <c>qq</c>), since the parent
/// directory already says what kind of thing this is. One instance is one directory, so anything an
/// instance needs beside its spec has an obvious place — and transport state does not belong here at
/// all; it lives under <c>&lt;dataRoot&gt;/io</c>. Configuration and IO have different lifetimes and
/// different backup rules, so they get different trees.
/// </summary>
internal static class SpecTree
{
    /// <summary>One spec document plus where it was found, so the store can name the path in an error.</summary>
    internal sealed record Entry<T>(string Path, string TypeDirectory, string InstanceDirectory, T Document);

    internal static string TypeSegment(string typeId)
    {
        var separator = typeId.LastIndexOf('.');
        return separator >= 0 ? typeId[(separator + 1)..] : typeId;
    }

    internal static async ValueTask<Result> WriteAsync<T>(string root, string fileName, Address address, T document,
        JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(root, fileName, address);
        if (directory is null)
            return Result.Failure(new(ErrorCode.Invalid, $"Spec address is not a file-safe instance: {address}"));
        return await WriteFileAsync(directory, fileName, document, options, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<Result> DeleteAsync(string root, string fileName, Address address,
        CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(root, fileName, address);
        if (directory is null)
            return Result.Failure(new(ErrorCode.Invalid, $"Spec address is not a file-safe instance: {address}"));
        var deleted = await DeleteFileAsync(directory, fileName, cancellationToken).ConfigureAwait(false);
        if (!deleted.Ok) return deleted;
        // Remove the instance directory, then the type directory if nothing else is left in it: an
        // emptied tree that reappears on every start is how storage layouts rot.
        RemoveIfEmpty(directory);
        RemoveIfEmpty(Path.GetDirectoryName(directory)!);
        return Result.Success();
    }

    internal static async ValueTask<Result<IReadOnlyList<Entry<T>>>> LoadAllAsync<T>(string root, string fileName,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return Result<IReadOnlyList<Entry<T>>>.Success([]);
        try
        {
            // No compatibility pass for other layouts: a spec file sitting anywhere else is an error that
            // names the right place, not a second code path to maintain.
            foreach (var stray in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
                return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid,
                    $"Spec file is not in the nested layout: {stray}; specs live at <type>/<instance>/{fileName}."));

            var entries = new List<Entry<T>>();
            foreach (var typeDirectory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                foreach (var instanceDirectory in Directory.EnumerateDirectories(typeDirectory).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Path.Combine(instanceDirectory, fileName);
                    if (!File.Exists(path)) continue;
                    SafeFileSystem.VerifyNewOrRegularFile(path, root);
                    var document = JsonSerializer.Deserialize<T>(
                        await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (document is null)
                        return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid, $"Spec is empty: {path}"));
                    entries.Add(new Entry<T>(path, Path.GetFileName(typeDirectory), Path.GetFileName(instanceDirectory), document));
                }
            }
            return Result<IReadOnlyList<Entry<T>>>.Success(entries);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<Entry<T>>>.Failure(new(ErrorCode.Invalid, "Specs could not be read.", ex.Message));
        }
    }

    /// <summary>
    /// The directory is the authority on where a spec lives; a file that disagrees with the folder it
    /// sits in is a hand-editing mistake that must be named, not reconciled.
    /// </summary>
    internal static Result<Address> ReadAddress<T>(Entry<T> entry, Func<T, string?> address)
    {
        var text = address(entry.Document);
        if (!Address.TryParse(text, out var parsed) || !parsed.IsInstance)
            return Result<Address>.Failure(new(ErrorCode.Invalid, $"Stored address is invalid: {text} ({entry.Path})"));
        var expectedType = TypeSegment(parsed.TypeId);
        if (!string.Equals(expectedType, entry.TypeDirectory, StringComparison.Ordinal) ||
            !string.Equals(parsed.Instance, entry.InstanceDirectory, StringComparison.Ordinal))
            return Result<Address>.Failure(new(ErrorCode.Invalid,
                $"Spec does not match its directory: {parsed} is in {entry.TypeDirectory}/{entry.InstanceDirectory}; " +
                $"expected {expectedType}/{parsed.Instance}."));
        return Result<Address>.Success(parsed);
    }

    private static string? DirectoryFor(string root, string fileName, Address address)
    {
        if (!address.IsInstance) return null;
        var type = TypeSegment(address.TypeId);
        if (!IsToken(type) || !IsToken(address.Instance)) return null;
        _ = fileName;
        return Path.Combine(root, type, address.Instance!);
    }

    /// <summary>
    /// Write-then-rename, so a crash cannot leave a half-parsed spec behind. <c>IsToken</c> is checked
    /// here as well: a file name that could climb directories is not a file name.
    /// </summary>
    private static async ValueTask<Result> WriteFileAsync<T>(string directory, string fileName, T document,
        JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        if (!IsToken(fileName))
            return Result.Failure(new(ErrorCode.Invalid, $"Spec file name is not a file-safe token: {fileName}"));
        try
        {
            SafeFileSystem.EnsureDirectory(directory);
            var path = Path.Combine(directory, fileName);
            SafeFileSystem.VerifyNewOrRegularFile(path, directory);
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

    private static async ValueTask<Result> DeleteFileAsync(string directory, string fileName,
        CancellationToken cancellationToken)
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

    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') &&
        value is not "." and not "..";

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
}

/// <summary>Adapter specs: one directory per instance, holding <c>adapter.json</c>.</summary>
public sealed class FileAdapterSpecStore(string root) : IAdapterSpecStore
{
    /// <summary>File name inside each instance directory.</summary>
    public const string FileName = "adapter.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ValueTask<Result> SaveAsync(AdapterSpec spec, CancellationToken cancellationToken) =>
        SpecTree.WriteAsync(root, FileName, spec.Address, new AdapterSpecDocument(
            spec.Address.ToString(), spec.Descriptor.PlatformId, spec.Descriptor.AccountId,
            spec.Descriptor.Direction.ToString(), spec.Settings), JsonOptions, cancellationToken);

    public ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken) =>
        SpecTree.DeleteAsync(root, FileName, address, cancellationToken);

    public async ValueTask<Result<IReadOnlyList<AdapterSpec>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var entries = await SpecTree.LoadAllAsync<AdapterSpecDocument>(root, FileName, cancellationToken).ConfigureAwait(false);
        if (!entries.Ok) return Result<IReadOnlyList<AdapterSpec>>.Failure(entries.Error!);

        var specs = new List<AdapterSpec>();
        foreach (var entry in entries.Value!)
        {
            var address = SpecTree.ReadAddress(entry, document => document.Address);
            if (!address.Ok) return Result<IReadOnlyList<AdapterSpec>>.Failure(address.Error!);
            if (!Enum.TryParse<AdapterDirection>(entry.Document.Direction, ignoreCase: true, out var direction))
                return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid,
                    $"Stored Adapter direction is invalid: {entry.Document.Direction} ({entry.Path})"));
            specs.Add(new AdapterSpec(address.Value!, new AdapterDescriptor(entry.Document.PlatformId,
                entry.Document.AccountId, direction), entry.Document.Settings));
        }
        return Result<IReadOnlyList<AdapterSpec>>.Success(specs);
    }

    /// <summary>
    /// <c>Settings</c> is written only when present, so a spec that doesn't need any stays a short file.
    /// </summary>
    public sealed record AdapterSpecDocument(string Address, string PlatformId, string AccountId, string Direction,
        IReadOnlyDictionary<string, string>? Settings = null);
}

/// <summary>Channel specs: one directory per channel, holding <c>channel.json</c>.</summary>
public sealed class FileChannelSpecStore(string root) : IChannelSpecStore
{
    /// <summary>File name inside each instance directory.</summary>
    public const string FileName = "channel.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ValueTask<Result> SaveAsync(ChannelSpec spec, CancellationToken cancellationToken) =>
        SpecTree.WriteAsync(root, FileName, spec.Address, new ChannelSpecDocument(
            spec.Address.ToString(), spec.Name,
            spec.Bindings.Select(b => new ChannelBindingDocument(b.Adapter.ToString(), b.PlatformConversationId)).ToArray(),
            spec.MaxMessages),
            JsonOptions, cancellationToken);

    public ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken) =>
        SpecTree.DeleteAsync(root, FileName, address, cancellationToken);

    public async ValueTask<Result<IReadOnlyList<ChannelSpec>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var entries = await SpecTree.LoadAllAsync<ChannelSpecDocument>(root, FileName, cancellationToken).ConfigureAwait(false);
        if (!entries.Ok) return Result<IReadOnlyList<ChannelSpec>>.Failure(entries.Error!);

        var specs = new List<ChannelSpec>();
        foreach (var entry in entries.Value!)
        {
            var address = SpecTree.ReadAddress(entry, document => document.Address);
            if (!address.Ok) return Result<IReadOnlyList<ChannelSpec>>.Failure(address.Error!);
            var bindings = new List<ChannelBinding>();
            foreach (var binding in entry.Document.Bindings ?? [])
            {
                if (!Address.TryParse(binding.Adapter, out var adapter) || !adapter.IsInstance)
                    return Result<IReadOnlyList<ChannelSpec>>.Failure(new(ErrorCode.Invalid,
                        $"Stored Channel binding address is invalid: {binding.Adapter} ({entry.Path})"));
                bindings.Add(new ChannelBinding(adapter, binding.PlatformConversationId));
            }
            specs.Add(new ChannelSpec(address.Value!, entry.Document.Name, bindings, entry.Document.MaxMessages));
        }
        return Result<IReadOnlyList<ChannelSpec>>.Success(specs);
    }

    public sealed record ChannelBindingDocument(string Adapter, string PlatformConversationId);
    public sealed record ChannelSpecDocument(string Address, string Name, ChannelBindingDocument[]? Bindings,
        int? MaxMessages = null);
}
