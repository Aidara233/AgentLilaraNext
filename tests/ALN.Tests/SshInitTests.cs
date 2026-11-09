using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using ALN.Ssh;

namespace ALN.Tests;

/// <summary>
/// <c>ssh_init</c> and the two tools around it: one live device per Session, replaced rather than multiplied, and a
/// private key that exists exactly as long as the configuration that uses it.
/// </summary>
public sealed class SshInitTests
{
    private const string Session = "channel.conversation#chat-1";

    private readonly FakeSshAccess _access = new();
    private readonly MemorySecretStore _keys = new();
    private readonly ManualClock _clock = new();

    private SshTools Tools(SshOptions? options = null) =>
        new(_access, _keys, null, null, options ?? SshOptions.Default, _clock);

    private static ValueTask<Result<SessionToolResult>> Init(SshTools tools, string args, string session = Session) =>
        tools.InitAsync(session, args, default);

    [Fact]
    public async Task Init_generates_a_key_reports_the_host_fingerprint_and_hands_back_the_public_line()
    {
        var tools = Tools();
        var made = await Init(tools, """{"device":"prod-web","host":"10.0.0.7","user":"deploy"}""");

        Assert.True(made.Ok, made.Error?.Message);
        var text = made.Value!.Content;
        // The public half is the deliverable; the private half is never in the answer, only in the store.
        Assert.Contains("ssh-ed25519 ", text, StringComparison.Ordinal);
        Assert.Contains("authorized_keys", text, StringComparison.Ordinal);
        Assert.Contains(_access.HostKey.ToString(), text, StringComparison.Ordinal);

        var live = tools.Find(Session);
        Assert.NotNull(live);
        Assert.Equal("prod-web", live!.Device);
        Assert.Equal("10.0.0.7", live.Target.Host);
        Assert.Equal(22, live.Target.Port);
        Assert.Equal("deploy", live.Target.User);
        // No fingerprint was given, so the one the device showed is pinned — that is what "first sighting" means here.
        Assert.Equal(_access.HostKey, live.Pinned);
        Assert.True((await _keys.ExistsAsync(live.Key.Name, default)).Value);
        Assert.DoesNotContain("PRIVATE", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_second_init_replaces_the_device_and_clears_the_old_key()
    {
        var tools = Tools();
        Assert.True((await Init(tools, """{"device":"one","host":"10.0.0.1","user":"a"}""")).Ok);
        var first = tools.Find(Session)!;

        var second = await Init(tools, """{"device":"two","host":"10.0.0.2","user":"b"}""");
        Assert.True(second.Ok, second.Error?.Message);
        Assert.Contains("one", second.Value!.Content, StringComparison.Ordinal); // says what it replaced

        var live = tools.Find(Session)!;
        Assert.Equal("two", live.Device);
        // The old key is gone, not merely unreferenced.
        Assert.False((await _keys.ExistsAsync(first.Key.Name, default)).Value);
        Assert.True((await _keys.ExistsAsync(live.Key.Name, default)).Value);
    }

    [Fact]
    public async Task A_pasted_private_key_is_refused_and_the_refusal_says_what_to_do_instead()
    {
        var tools = Tools();
        foreach (var field in new[] { "key", "keyText", "privateKey", "password", "passphrase" })
        {
            var refused = await Init(tools, $$"""{"host":"10.0.0.1","user":"a","{{field}}":"-----BEGIN OPENSSH PRIVATE KEY-----"}""");
            Assert.False(refused.Ok);
            Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
            // The refusal must not repeat the value back: it is already in the transcript once.
            Assert.DoesNotContain("BEGIN OPENSSH", refused.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN OPENSSH", refused.Error.Detail ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("replace", refused.Error.Detail, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Null(tools.Find(Session));
    }

    [Fact]
    public async Task An_unreachable_device_still_gets_a_configuration_and_a_key_to_hand_over()
    {
        // The user has not opened the firewall yet. That is not a reason to make them repeat themselves.
        _access.FailProbe = SshErrorKind.Timeout;
        var tools = Tools();
        var made = await Init(tools, """{"device":"later","host":"10.0.0.9","user":"root"}""");

        Assert.True(made.Ok, made.Error?.Message);
        Assert.Contains("ssh-ed25519 ", made.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("还没有主机指纹", made.Value.Content, StringComparison.Ordinal);

        var live = tools.Find(Session)!;
        Assert.Null(live.Pinned); // nothing was seen, so nothing is pinned yet
    }

    [Fact]
    public async Task A_pinned_fingerprint_that_the_device_does_not_match_refuses_the_setup_and_leaves_no_key()
    {
        var tools = Tools();
        var refused = await Init(tools,
            """{"host":"10.0.0.1","user":"a","fingerprint":"SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""");

        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        // Both fingerprints, so a person can tell "reinstalled" from "somebody in the middle".
        Assert.Contains("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", refused.Error.Detail, StringComparison.Ordinal);
        Assert.Contains(_access.HostKey.Value, refused.Error.Detail, StringComparison.Ordinal);
        Assert.Null(tools.Find(Session));
    }

    [Fact]
    public async Task A_matching_pinned_fingerprint_is_accepted_and_kept()
    {
        var tools = Tools();
        var made = await Init(tools,
            $$"""{"host":"10.0.0.1","user":"a","fingerprint":"{{_access.HostKey}}"}""");
        Assert.True(made.Ok, made.Error?.Message);
        Assert.Equal(_access.HostKey, tools.Find(Session)!.Pinned);
    }

    [Theory]
    [InlineData("""{"user":"a"}""", "host")]
    [InlineData("""{"host":"10.0.0.1"}""", "user")]
    [InlineData("""{"host":"ssh://10.0.0.1","user":"a"}""", "hostname")]
    [InlineData("""{"host":"10.0.0.1:22","user":"a"}""", "hostname")]
    [InlineData("""{"host":"10.0.0.1","user":"a","port":0}""", "port")]
    [InlineData("""{"host":"10.0.0.1","user":"a","policy":{"mode":"allowlist"}}""", "commands")]
    [InlineData("""{"host":"10.0.0.1","user":"a","policy":{"mode":"sometimes"}}""", "mode")]
    [InlineData("""{"host":"10.0.0.1","user":"a","fingerprint":"nope"}""", "fingerprint")]
    public async Task Bad_arguments_are_refused_with_a_message_that_names_the_field(string json, string expected)
    {
        var refused = await Init(Tools(), json);
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);
        Assert.Contains(expected, refused.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hosts_reads_local_state_and_says_so_instead_of_connecting()
    {
        var tools = Tools();
        var empty = await tools.HostsAsync(Session, default);
        Assert.True(empty.Ok);
        Assert.Contains("没有指向任何设备", empty.Value!.Content, StringComparison.Ordinal);

        Assert.True((await Init(tools, """{"device":"prod","host":"10.0.0.7","user":"deploy"}""")).Ok);
        var shown = await tools.HostsAsync(Session, default);
        var text = shown.Value!.Content;
        Assert.Contains("prod", text, StringComparison.Ordinal);
        Assert.Contains("deploy@10.0.0.7:22", text, StringComparison.Ordinal);
        Assert.Contains("已钉住", text, StringComparison.Ordinal);
        // Reading never goes to the network.
        Assert.Empty(_access.Commands);
    }

    [Fact]
    public async Task Hosts_of_one_session_never_shows_another_sessions_device()
    {
        var tools = Tools();
        Assert.True((await Init(tools, """{"host":"10.0.0.1","user":"a"}""", "s-a")).Ok);

        var other = await tools.HostsAsync("s-b", default);
        Assert.Contains("没有指向任何设备", other.Value!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_clears_the_key_and_the_configuration()
    {
        var tools = Tools();
        Assert.True((await Init(tools, """{"host":"10.0.0.1","user":"a"}""")).Ok);
        var live = tools.Find(Session)!;

        var removed = await tools.RemoveAsync(Session, default);
        Assert.True(removed.Ok);
        Assert.Contains("已忘掉", removed.Value!.Content, StringComparison.Ordinal);
        Assert.Null(tools.Find(Session));
        Assert.False((await _keys.ExistsAsync(live.Key.Name, default)).Value);

        // Removing again is a no-op with an answer, not an error: "make sure it is gone" has to be repeatable.
        var again = await tools.RemoveAsync(Session, default);
        Assert.True(again.Ok);
        Assert.Contains("本来就没有", again.Value!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_idle_configuration_loses_its_key()
    {
        var tools = Tools(new SshOptions { IdleTimeout = TimeSpan.FromHours(2) });
        Assert.True((await Init(tools, """{"host":"10.0.0.1","user":"a"}""")).Ok);
        var live = tools.Find(Session)!;

        _clock.Advance(TimeSpan.FromMinutes(90));
        // Any tool call sweeps first: the moment a stale key could be used is the moment it must be gone.
        await tools.ToolsAsync(Request(Session), default);
        Assert.NotNull(tools.Find(Session));

        _clock.Advance(TimeSpan.FromMinutes(91));
        await tools.ToolsAsync(Request(Session), default);
        Assert.Null(tools.Find(Session));
        Assert.False((await _keys.ExistsAsync(live.Key.Name, default)).Value);
    }

    [Fact]
    public async Task Stopping_the_module_clears_every_live_configuration()
    {
        var tools = Tools();
        Assert.True((await Init(tools, """{"host":"10.0.0.1","user":"a"}""", "s-a")).Ok);
        Assert.True((await Init(tools, """{"host":"10.0.0.2","user":"b"}""", "s-b")).Ok);

        await tools.SweepAllAsync();

        Assert.Null(tools.Find("s-a"));
        Assert.Null(tools.Find("s-b"));
    }

    [Fact]
    public async Task The_offers_are_the_full_toolset_with_acting_split_out()
    {
        var tools = Tools();
        var offers = await tools.ToolsAsync(Request(Session), default);
        Assert.True(offers.Ok);
        Assert.Equal(
            ["ssh_init", "ssh_hosts", "ssh_remove", "ssh_run", "ssh_job", "ssh_put", "ssh_get"],
            offers.Value!.Select(offer => offer.Tool.Name).ToArray());
        // Setting up and looking at a configuration answers in the model's own words; reaching the machine does not.
        Assert.False(offers.Value!.Single(offer => offer.Tool.Name == "ssh_init").ActsExternally);
        Assert.False(offers.Value!.Single(offer => offer.Tool.Name == "ssh_hosts").ActsExternally);
        Assert.True(offers.Value!.Single(offer => offer.Tool.Name == "ssh_run").ActsExternally);
        // Tunnels are off by default and need an operator, so they are not in this list at all.
        Assert.DoesNotContain(offers.Value!, offer => offer.Tool.Name == "ssh_tunnel");
    }

    [Theory]
    [InlineData("any", "", "systemctl restart nginx", true)]
    [InlineData("allowlist", "systemctl status", "systemctl status nginx", true)]
    [InlineData("allowlist", "systemctl status", "systemctl restart nginx", false)]
    [InlineData("allowlist", "journalctl -u * -n 200", "journalctl -u nginx -n 200", true)]
    [InlineData("allowlist", "journalctl -u * -n 200", "journalctl -u nginx -n 50", false)]
    [InlineData("allowlist", "df", "df -h", true)]
    public void An_allowlist_admits_prefixes_and_globs_and_nothing_else(string mode, string entry, string command,
        bool admitted)
    {
        var policy = mode == "any" ? SshCommandPolicy.Any : new SshCommandPolicy("allowlist", [entry]);
        Assert.Equal(admitted, policy.Admits(command));
    }

    private static RoundContextRequest Request(string sessionId) => new(sessionId, Guid.NewGuid(), 1);

    /// <summary>A clock a test can move, because "two idle hours" should not take two hours.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
