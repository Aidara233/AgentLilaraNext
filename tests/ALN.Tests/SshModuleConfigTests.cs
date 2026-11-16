using ALN.Contracts;
using ALN.Runtime;
using ALN.Ssh;

namespace ALN.Tests;

/// <summary>
/// What an <c>ssh.local</c> manifest may say, and what the defaults are when it says nothing.
///
/// The interesting number here is the transfer ceiling, because it used to be a constant compiled into the module and
/// nobody could lift it: a deployment that needed to move a 2 GiB image had no argument to make. Now the file says what
/// it wants, zero means "no ceiling", and an absent key is not a silent 64 MiB.
/// </summary>
public sealed class SshModuleConfigTests : IDisposable
{
    private readonly FakeSshAccess _access = new();
    private readonly MemorySecretStore _keys = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-ssh-cfg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Result<IKernelModule> Build(string? config)
    {
        var manifest = new ModuleManifest(new ModuleId(SshModule.TypeId), SshModule.TypeId, Enabled: true,
            ConfigJson: config);
        return SshModule.FromManifest(manifest, _access, _keys, new SessionExtensionRegistry(),
            binaryFiles: new LocalFileSystemAccess(_root, ["workspace"], ["workspace"]));
    }

    private SshOptions OptionsOf(string? config)
    {
        var built = Build(config);
        Assert.True(built.Ok, built.Error?.Message);
        return ((SshModule)built.Value!).Tools.Options;
    }

    [Fact]
    public void With_no_configuration_a_transfer_has_no_ceiling()
    {
        // Zero, not 64 MiB: there is nothing on this path that collects the file in memory, so the module has no reason
        // to name a maximum on somebody else's behalf.
        Assert.Equal(0, OptionsOf(null).MaxTransferBytes);
        Assert.Equal(0, OptionsOf("""{}""").MaxTransferBytes);
    }

    [Fact]
    public void The_default_config_the_host_writes_says_the_same_thing()
    {
        // What a fresh data root gets: the module's own file, parsed back through the same code.
        var options = OptionsOf(SshModule.DefaultConfig);
        Assert.Equal(0, options.MaxTransferBytes);
        Assert.Equal(600, options.CommandSeconds);
        Assert.Equal(262144, options.MaxOutputBytes);
    }

    [Fact]
    public void A_deployment_can_still_ask_for_a_ceiling()
    {
        Assert.Equal(2048, OptionsOf("""{"maxTransferBytes":2048}""").MaxTransferBytes);
    }

    [Fact]
    public void A_negative_ceiling_is_refused_rather_than_read_as_no_ceiling()
    {
        // "No ceiling" already has a spelling. A negative number is a typo, and reading it as the widest possible
        // setting would be the wrong way to be surprised.
        var built = Build("""{"maxTransferBytes":-1}""");
        Assert.False(built.Ok);
        Assert.Equal(ErrorCode.Invalid, built.Error!.Code);
        Assert.Contains("maxTransferBytes", built.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ceiling_reaches_the_tools_that_move_bytes()
    {
        var built = Build("""{"maxTransferBytes":1234}""");
        Assert.True(built.Ok, built.Error?.Message);
        // The number the module parsed is the one both directions are given: nothing re-reads a constant on the way.
        Assert.Equal(1234, ((SshModule)built.Value!).Tools.Options.MaxTransferBytes);
    }
}
