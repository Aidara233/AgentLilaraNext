using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

public sealed class ModuleDataRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-module-data-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleDataRoot _data;
    private static readonly ModuleId Session = new("session#s-1");

    public ModuleDataRootTests()
    {
        Directory.CreateDirectory(_root);
        _data = new ModuleDataRoot(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_module_owns_one_directory_and_asking_where_it_is_does_not_create_it()
    {
        var directory = _data.Directory(Session);

        Assert.Equal(Path.Combine(_root, "modules", "session#s-1"), directory);
        Assert.False(Directory.Exists(directory));
        Assert.Equal(directory, _data.Ensure(Session));
        Assert.True(Directory.Exists(directory));
        Assert.Equal(directory, _data.Ensure(Session));
        Assert.Equal(Path.Combine(directory, "history.jsonl"), _data.File(Session, "history.jsonl"));
    }

    [Fact]
    public void A_file_name_may_not_carry_a_path()
    {
        // "history.jsonl" is data; "../../aln.db" is a request to write into somebody else's store.
        Assert.Throws<ArgumentException>(() => _data.File(Session, "../aln.db"));
        Assert.Throws<ArgumentException>(() => _data.File(Session, "sub/history.jsonl"));
        Assert.Throws<ArgumentException>(() => _data.File(Session, ".."));
        Assert.Throws<ArgumentException>(() => _data.File(Session, "..hidden"));
        Assert.Throws<ArgumentException>(() => _data.Ensure(new ModuleId("../outside")));
    }

    [Fact]
    public void Archiving_moves_the_directory_out_of_the_way_and_says_why()
    {
        var directory = _data.Ensure(Session);
        File.WriteAllText(Path.Combine(directory, "history.jsonl"), "{\"role\":\"user\"}");

        var archived = _data.Archive(Session, "channel destroyed");

        Assert.True(archived.Ok, archived.Error?.Message);
        // A re-created module must not inherit the old one's history by finding it in place.
        Assert.False(Directory.Exists(directory));
        Assert.True(Directory.Exists(archived.Value));
        Assert.Equal("{\"role\":\"user\"}", File.ReadAllText(Path.Combine(archived.Value!, "history.jsonl")));
        Assert.Equal("channel destroyed", File.ReadAllText(Path.Combine(archived.Value!, "archived-because.txt")));
        // Nothing to archive is not a failure: a module that never wrote anything simply has no directory.
        Assert.Null(_data.Archive(Session).Value);
    }

    [Fact]
    public async Task What_belongs_to_the_store_survives_the_directory_being_deleted()
    {
        // The split from docs/22 §2.2 in one test: history is the module's, memory is not.
        using var store = new SqliteStore(Path.Combine(_root, "aln.db"));
        var written = await store.AddAsync(new MemoryDraft("chat-1", MemoryKind.Fragment, "部署改到周五了", At: DateTimeOffset.UtcNow), default);
        Assert.True(written.Ok);
        var directory = _data.Ensure(Session);
        File.WriteAllText(Path.Combine(directory, "history.jsonl"), "{}");

        Assert.True(_data.Archive(Session).Ok);
        Assert.True(_data.Delete(Session).Ok);

        Assert.False(Directory.Exists(directory));
        Assert.Equal(1, store.CountMemoryEntries());
    }
}
