using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>
/// Adapter specs: one module directory per instance, named after its id, holding <c>module.json</c> (docs/22 §2).
///
/// The address is the id, so it is not repeated inside the payload: a file that names itself twice is a file that
/// can disagree with its own directory.
/// </summary>
public sealed class FileAdapterSpecStore(string root, IModuleTypeCatalog? types = null) : IAdapterSpecStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Whether a stored record is this store's: asked of the type tree ("is this type an adapter"), never of the
    /// spelling of the type id (C-04/C-06). With no type tree nothing can be classified, so every record is read and
    /// the parse decides — guessing by prefix is what this replaced.
    /// </summary>
    private bool Belongs(string? type, string category) =>
        types is null || (type is not null && types.IsAssignableTo(type, category));

    public ValueTask<Result> SaveAsync(AdapterSpec spec, CancellationToken cancellationToken) =>
        SpecTree.WriteAsync(root, new ModuleId(spec.Address.ToString()), spec.TypeId,
            new AdapterSpecDocument(spec.Descriptor.PlatformId, spec.Descriptor.AccountId,
                spec.Descriptor.Direction.ToString(), spec.Settings), JsonOptions, cancellationToken);

    public ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken) =>
        SpecTree.DeleteAsync(root, address.ToModuleId(), cancellationToken);

    public async ValueTask<Result<IReadOnlyList<AdapterSpec>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var entries = await SpecTree
            .LoadAllAsync<AdapterSpecDocument>(root, type => Belongs(type, "adapter"), cancellationToken)
            .ConfigureAwait(false);
        if (!entries.Ok) return Result<IReadOnlyList<AdapterSpec>>.Failure(entries.Error!);

        var specs = new List<AdapterSpec>();
        foreach (var entry in entries.Value!)
        {
            if (!Address.TryParse(entry.Id.Value, out var address) || !address.IsInstance)
                return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid,
                    $"Stored Adapter id is not an instance address: {entry.Id} ({entry.Path})"));
            if (!Enum.TryParse<AdapterDirection>(entry.Config.Direction, ignoreCase: true, out var direction))
                return Result<IReadOnlyList<AdapterSpec>>.Failure(new(ErrorCode.Invalid,
                    $"Stored Adapter direction is invalid: {entry.Config.Direction} ({entry.Path})"));
            specs.Add(new AdapterSpec(address,
                new AdapterDescriptor(entry.Config.PlatformId, entry.Config.AccountId, direction),
                entry.Type, entry.Config.Settings));
        }
        return Result<IReadOnlyList<AdapterSpec>>.Success(specs);
    }

    /// <summary>
    /// <c>Settings</c> is written only when present, so a spec that doesn't need any stays a short file.
    /// </summary>
    public sealed record AdapterSpecDocument(string PlatformId, string AccountId, string Direction,
        IReadOnlyDictionary<string, string>? Settings = null);
}

/// <summary>Channel specs: one module directory per channel, named after its id, holding <c>module.json</c>.</summary>
public sealed class FileChannelSpecStore(string root, IModuleTypeCatalog? types = null) : IChannelSpecStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Same rule as the adapter store: the type tree says what a record is, its spelling does not.</summary>
    private bool Belongs(string? type, string category) =>
        types is null || (type is not null && types.IsAssignableTo(type, category));

    public ValueTask<Result> SaveAsync(ChannelSpec spec, CancellationToken cancellationToken) =>
        SpecTree.WriteAsync(root, new ModuleId(spec.Address.ToString()), spec.TypeId, new ChannelSpecDocument(
                spec.Name,
                spec.Bindings.Select(b => new ChannelBindingDocument(b.Adapter.ToString(), b.PlatformConversationId)).ToArray(),
                spec.MaxMessages), JsonOptions, cancellationToken);

    public ValueTask<Result> DeleteAsync(Address address, CancellationToken cancellationToken) =>
        SpecTree.DeleteAsync(root, address.ToModuleId(), cancellationToken);

    public async ValueTask<Result<IReadOnlyList<ChannelSpec>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var entries = await SpecTree
            .LoadAllAsync<ChannelSpecDocument>(root, type => Belongs(type, "channel"), cancellationToken)
            .ConfigureAwait(false);
        if (!entries.Ok) return Result<IReadOnlyList<ChannelSpec>>.Failure(entries.Error!);

        var specs = new List<ChannelSpec>();
        foreach (var entry in entries.Value!)
        {
            if (!Address.TryParse(entry.Id.Value, out var address) || !address.IsInstance)
                return Result<IReadOnlyList<ChannelSpec>>.Failure(new(ErrorCode.Invalid,
                    $"Stored Channel id is not an instance address: {entry.Id} ({entry.Path})"));
            var bindings = new List<ChannelBinding>();
            foreach (var binding in entry.Config.Bindings ?? [])
            {
                if (!Address.TryParse(binding.Adapter, out var adapter))
                    return Result<IReadOnlyList<ChannelSpec>>.Failure(new(ErrorCode.Invalid,
                        $"Stored Channel binding address is invalid: {binding.Adapter} ({entry.Path})"));
                bindings.Add(new ChannelBinding(adapter, binding.PlatformConversationId));
            }
            specs.Add(new ChannelSpec(address, entry.Config.Name,
                bindings, entry.Type, entry.Config.MaxMessages));
        }
        return Result<IReadOnlyList<ChannelSpec>>.Success(specs);
    }

    public sealed record ChannelBindingDocument(string Adapter, string PlatformConversationId);
    public sealed record ChannelSpecDocument(string Name, ChannelBindingDocument[]? Bindings,
        int? MaxMessages = null);
}
