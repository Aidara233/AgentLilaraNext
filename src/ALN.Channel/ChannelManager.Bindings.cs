using System.Text.Json;
using ALN.Contracts;

namespace ALN.Channel;

/// <summary>
/// What a Channel is aimed at, kept as one table rather than one field per Channel.
///
/// The table is one state key holding every aim, replaced whole. That shape is what makes a move atomic: pointing
/// Channel B at a conversation takes it away from Channel A, and one write that either lands or does not is the only
/// version where a crash cannot leave the same conversation answered by two Channels — or by none.
///
/// A key with no value means "this Channel is aimed at nothing", which is different from no key at all ("nobody has
/// ever said"). The difference matters at startup: no key is where a spec file is allowed to say what was meant, and
/// a null key is a decision that has already been taken and must not be undone by re-reading the file.
///
/// It is state, not spec: "which conversation this Channel serves" changes at runtime, while "what this Channel is"
/// does not. The spec file keeps the second answer and is still written once (C-03, with this being its documented
/// exception); this table keeps the first.
/// </summary>
public sealed partial class ChannelManager
{
    /// <summary>Where the table lives. One key, replaced whole — a half-written aim list is a Channel aimed twice.</summary>
    public const string BindingsScope = "channel.bindings";
    public const string BindingsKey = "table";

    private static readonly JsonSerializerOptions BindingJson = new(JsonSerializerDefaults.Web);

    /// <summary>What a move did, in the words a caller needs to hear it in.</summary>
    public sealed record BindingChange(Address Channel, ChannelBinding? Binding, ChannelBinding? Dropped, Address? TakenFrom);

    /// <summary>Reads the table. Absent or unreadable is an empty table, not a failure: nothing has been said yet.</summary>
    private async ValueTask<Dictionary<string, ChannelBinding?>> ReadBindingsAsync(CancellationToken cancellationToken)
    {
        var table = new Dictionary<string, ChannelBinding?>(StringComparer.Ordinal);
        if (_state is null) return table;
        var read = await _state.ReadAsync(BindingsScope, BindingsKey, cancellationToken).ConfigureAwait(false);
        if (!read.Ok || string.IsNullOrWhiteSpace(read.Value)) return table;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredBindings>(read.Value, BindingJson);
            if (stored?.Channels is null) return table;
            foreach (var (address, document) in stored.Channels)
            {
                if (!Address.TryParse(address, out var parsed) || !parsed.IsInstance) continue;
                if (document is null) { table[address] = null; continue; }
                if (string.IsNullOrWhiteSpace(document.Adapter) || string.IsNullOrWhiteSpace(document.Conversation)) continue;
                if (!Address.TryParse(document.Adapter, out var adapter) || !adapter.IsInstance) continue;
                table[address] = new ChannelBinding(adapter, document.Conversation);
            }
        }
        catch (JsonException) { /* an unreadable table is an empty one; the spec file still says what was meant */ }
        return table;
    }

    private async ValueTask<Result> WriteBindingsAsync(IReadOnlyDictionary<string, ChannelBinding?> table,
        CancellationToken cancellationToken)
    {
        if (_state is null) return Result.Success();
        var stored = new StoredBindings(1, table.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is { } binding ? new StoredBinding(binding.Adapter.ToString(), binding.PlatformConversationId) : null,
            StringComparer.Ordinal));
        return await _state.WriteAsync(BindingsScope, BindingsKey, JsonSerializer.Serialize(stored, BindingJson),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Points one Channel at one conversation, taking it away from wherever it was.
    ///
    /// Both ends are let go in the same write, because a conversation belongs to exactly one Channel and a Channel
    /// serves exactly one conversation. The answer names what was dropped — including the other Channel that has just
    /// stopped receiving anything — because a caller that cannot see that has no way to notice it did the wrong thing,
    /// and will simply try again.
    /// </summary>
    public async ValueTask<Result<BindingChange>> ConnectAsync(Address address, ChannelBinding binding,
        CancellationToken cancellationToken)
    {
        if (address.Instance is null)
            return Result<BindingChange>.Failure(new(ErrorCode.Invalid, "A Channel address is required."));
        if (!binding.Adapter.IsInstance || string.IsNullOrWhiteSpace(binding.PlatformConversationId))
            return Result<BindingChange>.Failure(new(ErrorCode.Invalid, "A binding needs an Adapter instance and a conversation."));

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ChannelInstance? channel;
            ChannelInstance? takenFrom;
            ChannelBinding? dropped;
            lock (_gate)
            {
                _channels.TryGetValue(IdOf(address), out channel);
                if (channel is null)
                    return Result<BindingChange>.Failure(new(ErrorCode.Invalid, $"Channel not found: {address}"));
                dropped = channel.Binding;
                takenFrom = _channels.Values.FirstOrDefault(other => !ReferenceEquals(other, channel) && other.Binding == binding);
            }

            var table = await ReadBindingsAsync(cancellationToken).ConfigureAwait(false);
            table[address.ToString()] = binding;
            // The conversation is one Channel's: the old owner is told it has none in the very same write. Removing the
            // row instead would let its spec file put the conversation back on the next boot.
            if (takenFrom is not null) table[takenFrom.Address.ToString()] = null;

            var written = await WriteBindingsAsync(table, cancellationToken).ConfigureAwait(false);
            if (!written.Ok) return Result<BindingChange>.Failure(written.Error!);

            lock (_gate)
            {
                channel.Aim(binding);
                takenFrom?.Aim(null);
            }
            return Result<BindingChange>.Success(new BindingChange(address, binding, dropped, takenFrom?.Address));
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// Lets go of the conversation. The Channel keeps existing and keeps its history: it simply stops receiving, which
    /// the answer says out loud — "it worked" and "it can no longer hear anything" are easy to confuse.
    /// </summary>
    public async ValueTask<Result<BindingChange>> DisconnectAsync(Address address, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ChannelInstance? channel;
            ChannelBinding? dropped;
            lock (_gate)
            {
                _channels.TryGetValue(IdOf(address), out channel);
                if (channel is null)
                    return Result<BindingChange>.Failure(new(ErrorCode.Invalid, $"Channel not found: {address}"));
                dropped = channel.Binding;
            }

            var table = await ReadBindingsAsync(cancellationToken).ConfigureAwait(false);
            table[address.ToString()] = null;
            var written = await WriteBindingsAsync(table, cancellationToken).ConfigureAwait(false);
            if (!written.Ok) return Result<BindingChange>.Failure(written.Error!);

            lock (_gate) channel.Aim(null);
            return Result<BindingChange>.Success(new BindingChange(address, null, dropped, null));
        }
        finally { _lifecycle.Release(); }
    }

    private sealed record StoredBindings(int Schema, IReadOnlyDictionary<string, StoredBinding?> Channels);
    private sealed record StoredBinding(string Adapter, string Conversation);
}
