using ALN.Contracts;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// The single file everything a program writes down lives in. These pin the properties the rest of the system
/// leans on: absent is not an error, a repeated platform message is a conflict rather than a second row, and
/// a transaction that fails leaves nothing behind.
/// </summary>
public sealed class SqliteStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-sqlite-" + Guid.NewGuid().ToString("N"));
    private static readonly Address Owner = new("channel", "conversation", "chat-1");
    private readonly List<SqliteStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SqliteStore OpenStore()
    {
        var store = new SqliteStore(Path.Combine(_root, "aln.db"));
        _stores.Add(store);
        return store;
    }

    [Fact]
    public async Task State_round_trips_and_absence_is_told_apart_from_failure()
    {
        var store = OpenStore();
        Assert.True((await store.WriteAsync("graph", "mounts", """{"rules":[]}""", default)).Ok);

        var read = await store.ReadAsync("graph", "mounts", default);
        Assert.Equal("""{"rules":[]}""", read.Value);
        Assert.Null(read.Error);

        // Absent: Result<string?>.Ok is false because the value is null, but there is no error to report.
        var missing = await store.ReadAsync("graph", "nothing-here", default);
        Assert.False(missing.Ok);
        Assert.Null(missing.Error);
        Assert.Null(missing.Value);

        Assert.True((await store.WriteAsync("graph", "mounts", """{"rules":[1]}""", default)).Ok);
        Assert.Equal("""{"rules":[1]}""", (await store.ReadAsync("graph", "mounts", default)).Value);
        Assert.True((await store.DeleteAsync("graph", "mounts", default)).Ok);
        Assert.True((await store.DeleteAsync("graph", "mounts", default)).Ok);   // forgetting twice is fine
        Assert.Null((await store.ReadAsync("graph", "mounts", default)).Value);
    }

    [Fact]
    public async Task State_scope_and_key_must_be_tokens()
    {
        var store = OpenStore();
        Assert.Equal(ErrorCode.Invalid, (await store.ReadAsync("graph/../etc", "x", default)).Error!.Code);
        Assert.Equal(ErrorCode.Invalid, (await store.WriteAsync("graph", "", "{}", default)).Error!.Code);
    }

    [Fact]
    public async Task A_repeated_platform_message_is_a_conflict_not_a_second_row()
    {
        var store = OpenStore();
        Assert.True((await store.AppendAsync(Message("m1", "p1", "hello"), default)).Ok);

        var duplicate = await store.AppendAsync(Message("m2", "p1", "hello again"), default);
        Assert.Equal(ErrorCode.Conflict, duplicate.Error!.Code);

        // A different owner recording the same platform id is a different fact, and is allowed.
        Assert.True((await store.AppendAsync(Message("m3", "p1", "hello", new("adapter", "file", "file-1")), default)).Ok);

        Assert.Single((await store.ReadAsync(Owner, default)).Value!);
        Assert.True((await store.ContainsAsync(Owner, "p1", default)).Value);
        Assert.False((await store.ContainsAsync(Owner, "p2", default)).Value);
    }

    [Fact]
    public async Task Messages_come_back_oldest_first_and_only_for_their_owner()
    {
        var store = OpenStore();
        Assert.True((await store.AppendAsync(Message("m2", "p2", "second", at: new(2026, 1, 2, 3, 5, 0, TimeSpan.Zero)), default)).Ok);
        Assert.True((await store.AppendAsync(Message("m1", "p1", "first", at: new(2026, 1, 2, 3, 4, 0, TimeSpan.Zero)), default)).Ok);
        Assert.True((await store.AppendAsync(Message("a1", "p1", "adapter's", new("adapter", "file", "file-1")), default)).Ok);

        Assert.Equal(["first", "second"], (await store.ReadAsync(Owner, default)).Value!.Select(m => m.Text));
        Assert.Single((await store.ReadAsync(new("adapter", "file", "file-1"), default)).Value!);
        Assert.Empty((await store.ReadAsync(new("channel", "conversation", "nobody"), default)).Value!);
    }

    [Fact]
    public async Task A_failed_transaction_leaves_nothing_behind()
    {
        var store = OpenStore();
        var refused = await store.AtomicallyAsync(async token =>
        {
            Assert.True((await store.WriteAsync("channel.pending", "chat-1", """{"messages":[]}""", token)).Ok);
            Assert.True((await store.AppendAsync(Message("m1", "p1", "inside the doomed transaction"), token)).Ok);
            return Result.Failure(new(ErrorCode.Unavailable, "nope"));
        }, default);

        Assert.False(refused.Ok);
        // Both halves are gone: that is the point of the transaction, and why accept uses it.
        Assert.Null((await store.ReadAsync("channel.pending", "chat-1", default)).Value);
        Assert.Empty((await store.ReadAsync(Owner, default)).Value!);
    }

    [Fact]
    public async Task A_committed_transaction_is_visible_to_the_next_connection()
    {
        var store = OpenStore();
        var committed = await store.AtomicallyAsync(async token =>
        {
            Assert.True((await store.WriteAsync("channel.pending", "chat-1", """{"messages":[1]}""", token)).Ok);
            Assert.True((await store.AppendAsync(Message("m1", "p1", "kept"), token)).Ok);
            return Result.Success();
        }, default);
        Assert.True(committed.Ok, committed.Error?.Message);

        // A second connection to the same file is what a restart looks like.
        var reopened = OpenStore();
        Assert.Equal("""{"messages":[1]}""", (await reopened.ReadAsync("channel.pending", "chat-1", default)).Value);
        Assert.Single((await reopened.ReadAsync(Owner, default)).Value!);
        Assert.Equal(1, reopened.CountMessages());
        Assert.Equal(1, reopened.CountStateKeys());
    }

    [Fact]
    public async Task Audit_records_are_kept_in_order()
    {
        var store = OpenStore();
        await store.AppendAsync(new AuditRecord(DateTimeOffset.UtcNow, Guid.NewGuid(), "api", Owner, "channels.stop", "denied"), default);
        await store.AppendAsync(new AuditRecord(DateTimeOffset.UtcNow, Guid.NewGuid(), "api", Owner, "channels.stop", "ok"), default);

        Assert.Equal(["denied", "ok"], store.ReadAudit().Select(record => record.Outcome));
    }

    [Fact]
    public async Task Journal_entries_are_their_own_rows_and_capacity_drops_the_oldest()
    {
        var store = OpenStore();
        var owner = new Address("adapter", "qq", "1");
        for (var index = 0; index < 4; index++)
            Assert.True((await store.PutAsync(InboundScope, owner, $"k{index}", $"v{index}", capacity: 3, default)).Ok);

        // One row per item; the oldest fell off because the caller said "at most three".
        Assert.Equal(["k1", "k2", "k3"], (await store.ListAsync(InboundScope, owner, default)).Value!.Select(entry => entry.Key));
        Assert.Equal(3, (await store.CountAsync(InboundScope, default)).Value);

        // Rewriting the same key replaces the row rather than adding one.
        Assert.True((await store.PutAsync(InboundScope, owner, "k3", "v3-again", null, default)).Ok);
        Assert.Equal("v3-again", (await store.ListAsync(InboundScope, owner, default)).Value!.Last().Value);

        Assert.True((await store.RemoveAsync(InboundScope, owner, "k2", default)).Ok);
        Assert.Equal(2, (await store.CountAsync(InboundScope, default)).Value);
        Assert.Equal(2, (await store.RemoveAllAsync(InboundScope, owner, default)).Value);
        Assert.Empty((await store.ListAsync(InboundScope, owner, default)).Value!);
    }

    [Fact]
    public async Task Journal_entries_belong_to_one_owner()
    {
        var store = OpenStore();
        var mine = new Address("adapter", "qq", "1");
        var theirs = new Address("adapter", "qq", "2");
        Assert.True((await store.PutAsync(InboundScope, mine, "k", """{"a":1}""", null, default)).Ok);
        Assert.True((await store.PutAsync(InboundScope, theirs, "k", """{"a":2}""", null, default)).Ok);

        Assert.Equal("""{"a":1}""", Assert.Single((await store.ListAsync(InboundScope, mine, default)).Value!).Value);
        Assert.Equal("""{"a":2}""", Assert.Single((await store.ListAsync(InboundScope, theirs, default)).Value!).Value);
    }

    private const string InboundScope = "qq.inbound";

    private static StoredMessage Message(string id, string platformId, string text, Address? owner = null,
        DateTimeOffset? at = null) =>
        new(id, owner ?? Owner, "chat-1", platformId, "alice", text, at ?? new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
}
