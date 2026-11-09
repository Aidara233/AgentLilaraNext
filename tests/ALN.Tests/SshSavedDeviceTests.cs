using ALN.Contracts;
using ALN.Contracts.Sessions;
using ALN.Runtime;
using ALN.Ssh;

namespace ALN.Tests;

/// <summary>
/// The two channels and the gate between them.
///
/// A live device is something a conversation was handed; a saved device is a credential that sits on this host and can
/// be used by a later session. The whole point of <c>ssh_save</c> being an operator verb is that the second kind is the
/// thing an agent-level session must never be able to reach — so these tests are as much about what the model cannot
/// see as about what it can do.
/// </summary>
public sealed class SshSavedDeviceTests : IDisposable
{
    private const string Session = "channel.conversation#chat-1";

    private readonly FakeSshAccess _access = new();
    private readonly MemorySecretStore _live = new();
    private readonly MemorySecretStore _savedKeys = new();
    private readonly MemoryState _state = new();
    private readonly string _keyRoot = Path.Combine(Path.GetTempPath(), "aln-ssh-saved-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_keyRoot)) Directory.Delete(_keyRoot, recursive: true);
    }

    private SshTools Tools(bool withStorage = true) => new(_access, _live, null, null, SshOptions.Default, null,
        withStorage ? _state : null, withStorage ? _savedKeys : null);

    private async Task<SshTools> Ready(string policy = """ "policy":{"mode":"allowlist","commands":["df"]} """)
    {
        var tools = Tools();
        var made = await tools.InitAsync(Session, $$"""{"device":"prod","host":"10.0.0.7","user":"deploy",{{policy}}}""", default);
        Assert.True(made.Ok, made.Error?.Message);
        return tools;
    }

    [Fact]
    public async Task An_agent_cannot_even_see_that_saved_devices_exist()
    {
        var tools = Tools();
        var offers = await tools.ToolsAsync(
            new RoundContextRequest(Session, Guid.NewGuid(), 1, Level: AccessLevel.Agent), default);

        Assert.True(offers.Ok);
        Assert.DoesNotContain(offers.Value!, offer => offer.Tool.Name is "ssh_save" or "ssh_load" or "ssh_forget");

        // And calling one anyway is refused rather than executed.
        var refused = await tools.LoadAsync(Session, """{"device":"prod"}""", AccessLevel.Agent, default);
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);
        Assert.Contains("operator", refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operator_sees_all_three_verbs()
    {
        var tools = Tools();
        var offers = await tools.ToolsAsync(
            new RoundContextRequest(Session, Guid.NewGuid(), 1, Level: AccessLevel.Operator), default);

        var names = offers.Value!.Select(offer => offer.Tool.Name).ToArray();
        Assert.Contains("ssh_save", names);
        Assert.Contains("ssh_load", names);
        Assert.Contains("ssh_forget", names);
        Assert.All(offers.Value!.Where(offer => offer.Tool.Name.StartsWith("ssh_s", StringComparison.Ordinal)
                                                || offer.Tool.Name is "ssh_load" or "ssh_forget"),
            offer => Assert.Equal(AccessLevel.Operator, offer.MinLevel));
    }

    [Fact]
    public async Task Saving_moves_the_key_to_the_persistent_store_and_writes_a_record_that_holds_no_key()
    {
        var tools = await Ready();
        var saved = await tools.SaveAsync(Session, """{"device":"prod-web"}""", AccessLevel.Operator, default);

        Assert.True(saved.Ok, saved.Error?.Message);
        Assert.True((await _savedKeys.ExistsAsync("saved-prod-web", default)).Value);

        var record = _state.Values[("ssh.saved", "prod-web")];
        Assert.Contains("\"Device\":\"prod-web\"", record, StringComparison.Ordinal);
        // The record names the key that unlocks the device; it does not contain the key itself.
        Assert.Contains("saved-prod-web", record, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN OPENSSH", record, StringComparison.Ordinal);
        Assert.DoesNotContain("privateKey", record, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Saving_a_device_that_can_run_anything_is_refused_until_a_policy_is_given()
    {
        var tools = await Ready(policy: """ "policy":{"mode":"any"} """);

        var refused = await tools.SaveAsync(Session, "{}", AccessLevel.Operator, default);
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Invalid, refused.Error!.Code);

        var withPolicy = await tools.SaveAsync(Session,
            """{"policy":{"mode":"allowlist","commands":["systemctl status nginx"]}}""", AccessLevel.Operator, default);
        Assert.True(withPolicy.Ok, withPolicy.Error?.Message);
        Assert.Contains("allowlist", withPolicy.Value!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_device_can_be_loaded_into_a_session_and_runs_under_its_own_policy()
    {
        var saver = await Ready(policy: """ "policy":{"mode":"allowlist","commands":["df"]} """);
        Assert.True((await saver.SaveAsync(Session, """{"device":"prod"}""", AccessLevel.Operator, default)).Ok);

        // A *different* session, with nothing live, loads the stored device.
        var loader = new SshTools(_access, _live, null, null, SshOptions.Default, null, _state, _savedKeys);
        var loaded = await loader.LoadAsync("session-b", """{"device":"prod"}""", AccessLevel.Operator, default);

        Assert.True(loaded.Ok, loaded.Error?.Message);
        Assert.Equal("prod", loader.Find("session-b")!.Device);
        // The policy came with it: the saved allowlist is what admits commands now.
        Assert.Equal("allowlist", loader.Find("session-b")!.Policy.Mode);

        var refused = await loader.RunAsync("session-b", """{"command":"rm -rf /"}""", default);
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCode.Denied, refused.Error!.Code);

        var allowed = await loader.RunAsync("session-b", """{"command":"df -h"}""", default);
        Assert.True(allowed.Ok, allowed.Error?.Message);
    }

    [Fact]
    public async Task Loading_a_device_nobody_saved_says_so()
    {
        var tools = Tools();
        var missing = await tools.LoadAsync(Session, """{"device":"nope"}""", AccessLevel.Operator, default);

        Assert.False(missing.Ok);
        Assert.Equal(ErrorCode.Invalid, missing.Error!.Code);
        Assert.Contains("没有保存过", missing.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forgetting_removes_both_the_record_and_the_key()
    {
        var tools = await Ready();
        Assert.True((await tools.SaveAsync(Session, """{"device":"prod"}""", AccessLevel.Operator, default)).Ok);

        var forgotten = await tools.ForgetAsync(Session, """{"device":"prod"}""", AccessLevel.Operator, default);

        Assert.True(forgotten.Ok, forgotten.Error?.Message);
        Assert.False(_state.Values.ContainsKey(("ssh.saved", "prod")));
        Assert.False((await _savedKeys.ExistsAsync("saved-prod", default)).Value);
    }

    [Fact]
    public async Task Without_storage_the_saved_verbs_are_not_even_offered()
    {
        var tools = Tools(withStorage: false);
        var offers = await tools.ToolsAsync(
            new RoundContextRequest(Session, Guid.NewGuid(), 1, Level: AccessLevel.Full), default);

        Assert.DoesNotContain(offers.Value!, offer => offer.Tool.Name is "ssh_save" or "ssh_load" or "ssh_forget");
        var refused = await tools.SaveAsync(Session, "{}", AccessLevel.Full, default);
        Assert.False(refused.Ok);
        Assert.Contains("长期存储", refused.Error!.Message, StringComparison.Ordinal);
    }

    /// <summary>The smallest state store a host would keep, and the one a test can read back.</summary>
    private sealed class MemoryState : IStateStore
    {
        public Dictionary<(string Scope, string Key), string> Values { get; } = [];

        public ValueTask<Result<string?>> ReadAsync(string scope, string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<string?>.Success(
                Values.TryGetValue((scope, key), out var value) ? value : null));

        public ValueTask<Result> WriteAsync(string scope, string key, string json, CancellationToken cancellationToken)
        {
            Values[(scope, key)] = json;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> DeleteAsync(string scope, string key, CancellationToken cancellationToken)
        {
            Values.Remove((scope, key));
            return ValueTask.FromResult(Result.Success());
        }
    }
}
