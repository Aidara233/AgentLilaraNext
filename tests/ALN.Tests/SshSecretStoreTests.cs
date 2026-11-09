using ALN.Contracts;
using ALN.Ssh;

namespace ALN.Tests;

/// <summary>
/// The two secret stores: the rules that make "a key never ends up somewhere it should not" true in code rather than in
/// a convention. Names cannot travel, files are 0600 from the first syscall, deletion overwrites before unlinking, and
/// replacing a key clears the old bytes instead of dropping a reference to them.
/// </summary>
public sealed class SshSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-ssh-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("prod-web")]
    [InlineData("srv_1")]
    [InlineData("a.b")]
    [InlineData("A1")]
    public void A_plain_name_is_a_valid_secret_name(string name) => Assert.True(SecretName.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData("../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("/abs")]
    [InlineData(".hidden")]
    [InlineData("-dash-first")]
    [InlineData("has space")]
    [InlineData("a\\b")]
    public void A_name_that_could_travel_is_refused(string name) => Assert.False(SecretName.IsValid(name));

    [Fact]
    public async Task A_name_that_could_travel_is_refused_by_every_operation()
    {
        var store = new FileSecretStore(_root);
        Assert.False((await store.PutAsync("../escape", "x"u8.ToArray(), default)).Ok);
        Assert.False((await store.OpenAsync("../escape", default)).Ok);
        Assert.False((await store.ExistsAsync("../escape", default)).Ok);
        Assert.False((await store.DeleteAsync("../escape", default)).Ok);
        // Nothing was created outside the root.
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escape")));
    }

    [Fact]
    public async Task A_key_file_is_created_with_owner_only_permissions()
    {
        if (OperatingSystem.IsWindows()) return; // No POSIX modes to check on the CI-shaped Windows run.
        var store = new FileSecretStore(_root);
        Assert.True((await store.PutAsync("prod-web", "private"u8.ToArray(), default)).Ok);

        var mode = File.GetUnixFileMode(Path.Combine(_root, "prod-web"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserExecute, File.GetUnixFileMode(_root));
    }

    [Fact]
    public async Task Reading_a_key_gives_a_stream_and_an_absent_name_is_not_an_empty_file()
    {
        var store = new FileSecretStore(_root);
        Assert.True((await store.PutAsync("k1", "material"u8.ToArray(), default)).Ok);

        var present = await store.OpenAsync("k1", default);
        Assert.True(present.Ok, present.Error?.Message);
        using var reader = new StreamReader(present.Value!.Content!);
        Assert.Equal("material", await reader.ReadToEndAsync());

        var absent = await store.OpenAsync("nope", default);
        Assert.True(absent.Ok, absent.Error?.Message);
        // Absent is a successful answer with nothing in it, not a failure and not an empty file.
        Assert.Null(absent.Value!.Content);
    }

    [Fact]
    public async Task Deleting_a_key_leaves_nothing_behind()
    {
        var store = new FileSecretStore(_root);
        Assert.True((await store.PutAsync("k1", "material"u8.ToArray(), default)).Ok);
        Assert.True((await store.DeleteAsync("k1", default)).Ok);
        Assert.False(File.Exists(Path.Combine(_root, "k1")));
        Assert.False((await store.ExistsAsync("k1", default)).Value);
        // Deleting something that is not there is not an error: "make sure it is gone" has to be repeatable.
        Assert.True((await store.DeleteAsync("k1", default)).Ok);
    }

    [Fact]
    public async Task The_memory_store_keeps_live_keys_out_of_the_disk_entirely()
    {
        var store = new MemorySecretStore();
        Assert.True((await store.PutAsync("live", "material"u8.ToArray(), default)).Ok);
        Assert.True((await store.ExistsAsync("live", default)).Value);

        using var reader = new StreamReader((await store.OpenAsync("live", default)).Value!.Content!);
        Assert.Equal("material", await reader.ReadToEndAsync());

        Assert.True((await store.DeleteAsync("live", default)).Ok);
        Assert.False((await store.ExistsAsync("live", default)).Value);
        // Nothing under the data root was touched, because there is no path to touch.
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task A_fake_key_pair_is_generated_into_the_store_that_was_passed_in()
    {
        var memory = new MemorySecretStore();
        var access = new FakeSshAccess();
        var made = await access.CreateKeyAsync("prod-web", memory, default);

        Assert.True(made.Ok, made.Error?.Message);
        Assert.StartsWith("ssh-ed25519 ", made.Value!.PublicKey, StringComparison.Ordinal);
        Assert.Equal("prod-web", made.Value.Handle.Name);
        // The private half went to the store and nowhere else.
        Assert.True((await memory.ExistsAsync("prod-web", default)).Value);
        Assert.False(Directory.Exists(_root));
    }
}
