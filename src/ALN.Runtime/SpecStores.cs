using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>Adapter specs: one directory per instance, named after its id, holding <c>spec.json</c>.</summary>
public sealed class FileAdapterSpecStore(string root) : IAdapterSpecStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string TypePrefix = "adapter.";

    public ValueTask<Result> SaveAsync(AdapterSpec spec, CancellationToken cancellationToken) =>
        SpecTree.WriteAsync(root, new ModuleId(spec.Address.ToString()), new AdapterSpecDocument(
                spec.Address.ToString(), spec.Descriptor.PlatformId, spec.Descriptor.AccountId,
                spec.Descriptor.Direction.ToString(), spec.Settings,
                // Transitional: callers do not all pass a type yet, so the address it is still shaped like fills
                // in. That fallback has nothing to read once the address carries nothing, and dies with it.
                spec.TypeId ?? spec.Address.TypeId), JsonOptions, cancellationToken);

    public ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken) =>
        SpecTree.DeleteAsync(root, new ModuleId(address.ToString()), cancellationToken);

    public async ValueTask<Result<IReadOnlyList<AdapterSpec>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var entries = await SpecTree
            .LoadAllAsync<AdapterSpecDocument>(root, type => type.StartsWith(TypePrefix, StringComparison.Ordinal), cancellationToken)
            .ConfigureAwait(false);
        if (!entries.Ok) return Result<IReadOnlyList<AdapterSpec>>.Failure(entries.Error!);

        var specs = new List<AdapterSpec>();
        foreach (var entry in entries.Value!)
        {
            if (!Address.TryParse(entry.Document.Address, out var address))
                return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid,
                    $"Stored Adapter address is invalid: {entry.Document.Address} ({entry.Path})"));
            if (!Enum.TryParse<AdapterDirection>(entry.Document.Direction, ignoreCase: true, out var direction))
                return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid,
                    $"Stored Adapter direction is invalid: {entry.Document.Direction} ({entry.Path})"));
            specs.Add(new AdapterSpec(address,
                new AdapterDescriptor(entry.Document.PlatformId, entry.Document.AccountId, direction),
                entry.Document.Settings, entry.Document.Type));
        }
        return Result<IReadOnlyList<AdapterSpec>>.Success(specs);
    }

    /// <summary>
    /// <c>Settings</c> is written only when present, so a spec that doesn't need any stays a short file.
    /// </summary>
    public sealed record AdapterSpecDocument(string Address, string PlatformId, string AccountId, string Direction,
        IReadOnlyDictionary<string, string>? Settings = null, string? Type = null);
}

/// <summary>Channel specs: one directory per channel, named after its id, holding <c>spec.json</c>.</summary>
public sealed class FileChannelSpecStore(string root) : IChannelSpecStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string TypePrefix = "channel.";

    public ValueTask<Result> SaveAsync(ChannelSpec spec, CancellationToken cancellationToken) =>
        SpecTree.WriteAsync(root, new ModuleId(spec.Address.ToString()), new ChannelSpecDocument(
                spec.Address.ToString(), spec.Name,
                spec.Bindings.Select(b => new ChannelBindingDocument(b.Adapter.ToString(), b.PlatformConversationId)).ToArray(),
                spec.MaxMessages, spec.TypeId ?? spec.Address.TypeId), JsonOptions, cancellationToken);

    public ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken) =>
        SpecTree.DeleteAsync(root, new ModuleId(address.ToString()), cancellationToken);

    public async ValueTask<Result<IReadOnlyList<ChannelSpec>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var entries = await SpecTree
            .LoadAllAsync<ChannelSpecDocument>(root, type => type.StartsWith(TypePrefix, StringComparison.Ordinal), cancellationToken)
            .ConfigureAwait(false);
        if (!entries.Ok) return Result<IReadOnlyList<ChannelSpec>>.Failure(entries.Error!);

        var specs = new List<ChannelSpec>();
        foreach (var entry in entries.Value!)
        {
            if (!Address.TryParse(entry.Document.Address, out var address))
                return Result<IReadOnlyList<ChannelSpec>>.Failure(new(ErrorCode.Invalid,
                    $"Stored Channel address is invalid: {entry.Document.Address} ({entry.Path})"));
            var bindings = new List<ChannelBinding>();
            foreach (var binding in entry.Document.Bindings ?? [])
            {
                if (!Address.TryParse(binding.Adapter, out var adapter))
                    return Result<IReadOnlyList<ChannelSpec>>.Failure(new(ErrorCode.Invalid,
                        $"Stored Channel binding address is invalid: {binding.Adapter} ({entry.Path})"));
                bindings.Add(new ChannelBinding(adapter, binding.PlatformConversationId));
            }
            specs.Add(new ChannelSpec(address, entry.Document.Name,
                bindings, entry.Document.MaxMessages, entry.Document.Type));
        }
        return Result<IReadOnlyList<ChannelSpec>>.Success(specs);
    }

    public sealed record ChannelBindingDocument(string Adapter, string PlatformConversationId);
    public sealed record ChannelSpecDocument(string Address, string Name, ChannelBindingDocument[]? Bindings,
        int? MaxMessages = null, string? Type = null);
}
