using System.Globalization;
using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The tools a Session uses to reach one device over SSH: <c>ssh_init</c>, <c>ssh_hosts</c>, <c>ssh_remove</c>.
///
/// The shape worth explaining is "one live configuration per Session". A Session talks to one machine at a time; asking
/// it to juggle several would mean every call carrying a target, every target needing its own key, and every "which one
/// did you mean" becoming a round. Reaching another machine is <c>ssh_init</c> again, which replaces the previous
/// configuration outright — key cleared, connection closed, jobs killed — and says so in its answer. Saved
/// configurations (the ones that survive a restart and that any Session could touch) are a different channel with a
/// different gate; this class only ever sees the live one.
///
/// Nothing here decides whether a command is allowed to reach a device: that is the policy on the configuration. What
/// this class enforces is narrower and just as important — which device a Session can be pointing at, and that the
/// private key for it exists only for as long as the configuration does.
/// </summary>
public sealed class SshTools(ISshAccess access, ISecretStore liveKeys, IAuditSink? audit, ISignalLog? signals,
    SshOptions options, TimeProvider? clock = null, IStateStore? savedStore = null,
    ISecretStore? savedKeys = null, IBinaryFileAccess? binaryFiles = null, IJobRegistry? jobs = null)
    : ISessionToolSource
{
    public const string SourceId = "system.ssh-tools";

    public const string InitToolName = "ssh_init";
    public const string HostsToolName = "ssh_hosts";
    public const string RemoveToolName = "ssh_remove";
    public const string RunToolName = "ssh_run";
    public const string JobToolName = "ssh_job";

    /// <summary>What a background command is called in the ledger. The tool and the ledger are two views of one thing, and
    /// the kind is how a reader tells an ssh job from a file download without knowing either tool.</summary>
    private const string JobKind = "ssh.run";
    public const string PutToolName = "ssh_put";
    public const string GetToolName = "ssh_get";
    public const string TunnelToolName = "ssh_tunnel";

    /// <summary>
    /// Where a Session's live configuration lives between calls. One entry per Session: the invalidation rule is
    /// "another init replaces it", and a dictionary with one value per key is the cheapest way to make that true.
    /// </summary>
    private readonly Dictionary<string, SshLiveConfiguration> _live = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ISshAccess _access = access;
    private readonly ISecretStore _keys = liveKeys;
    /// <summary>
    /// The ledger a background command is written into, so that "it was still running when the host stopped" is a fact
    /// somebody can read afterwards. Optional: the work is this class's, and the row is a record of it — a host without
    /// a ledger still runs commands, it just forgets them across a restart (docs/28 §9).
    /// </summary>
    private readonly IJobRegistry? _jobs = jobs;

    /// <summary>The policy a configuration was created with; also the reason a saved one must declare one.</summary>
    public SshOptions Options { get; } = options;

    public string Id => SourceId;

    public async ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Idle configurations are dropped here rather than by a background sweep: the only moment a stale key matters
        // is the moment something could use it, and that is this call.
        await SweepAsync().ConfigureAwait(false);

        var offers = new List<SessionToolOffer>(8)
        {
            // None of these three acts on the world: init sets up a configuration and answers with a key to hand over,
            // hosts reads local state, remove throws it away.
            new(new InitTool(this, request.SessionId), InitDefinition, ActsExternally: false),
            new(new HostsTool(this, request.SessionId), HostsDefinition, ActsExternally: false),
            new(new RemoveTool(this, request.SessionId), RemoveDefinition, ActsExternally: false),
            // These reach another machine, so a turn that used one did something outside this process — that is what
            // ActsExternally tells the loop when it decides whether the text was a reply.
            new(new RunTool(this, request.SessionId), RunDefinition),
            new(new JobTool(this, request.SessionId), JobDefinition),
            new(new PutTool(this, request.SessionId), PutDefinition),
            new(new GetTool(this, request.SessionId), GetDefinition)
        };
        // Offered only when this module allows tunnels at all and the caller is deep enough: a tool that can only be
        // refused buys a wasted round and teaches the model to distrust the list (docs/28).
        if (Options.AllowTunnel && request.Level >= AccessLevel.Operator)
            offers.Add(new(new TunnelTool(this, request.SessionId, request.Level), TunnelDefinition,
                MinLevel: AccessLevel.Operator));

        // The saved channel is offered only to a caller deep enough to use it: agent-level sessions must not even see
        // that stored credentials exist, let alone their names.
        if (savedStore is not null && savedKeys is not null && request.Level >= AccessLevel.Operator)
        {
            offers.Add(new(new SaveTool(this, request.SessionId, request.Level), SaveDefinition,
                ActsExternally: false, MinLevel: AccessLevel.Operator));
            offers.Add(new(new LoadTool(this, request.SessionId, request.Level), LoadDefinition,
                ActsExternally: false, MinLevel: AccessLevel.Operator));
            offers.Add(new(new ForgetTool(this, request.SessionId, request.Level), ForgetDefinition,
                ActsExternally: false, MinLevel: AccessLevel.Operator));
        }

        return Result<IReadOnlyList<SessionToolOffer>>.Success(offers);
    }

    public static readonly ModelToolDefinition RunDefinition = new(RunToolName,
        "Run one command on the current device and return its exit code, stdout and stderr. The device's command policy "
        + "is enforced here, so a refused command tells you what is allowed. The output is capped and says so when it "
        + "was; the command stops when the turn is interrupted. A non-zero exit code is not a failure of this tool: the "
        + "command ran and failed, which is a different thing from not reaching the machine. For anything longer than "
        + "the configured seconds, use wait=false and poll with ssh_job.",
        """
        {"type":"object","properties":{
          "command":{"type":"string","description":"The shell command, run without a pty."},
          "wait":{"type":"boolean","description":"false = start it in the background and return a job id. Default true."}},
         "required":["command"]}
        """);

    public static readonly ModelToolDefinition JobDefinition = new(JobToolName,
        "Look at or stop a background command started with ssh_run {wait:false}. Actions: list, output, kill. "
        + "Output is a bounded buffer read from an offset, so \"nothing new\" and \"nothing yet\" stay distinguishable.",
        """
        {"type":"object","properties":{
          "action":{"type":"string","enum":["list","output","kill"],"description":"What to do."},
          "id":{"type":"string","description":"Job id, from ssh_run or list. Required for output and kill."},
          "offset":{"type":"integer","description":"Start reading here; use the offset the last read returned. Default 0."}},
         "required":["action"]}
        """);

    public static readonly ModelToolDefinition PutDefinition = new(PutToolName,
        "Upload one file from a file-system scope to the device, bytes as they are — text, tarball, key, image. The "
        + "local side is a scope name plus a relative path (the same two arguments fs_read takes), and the scope has to "
        + "be one that accepts binary. Nothing about the file is read as text on the way, so its content is unchanged.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Registered file area the local file is in (e.g. workspace)."},
          "path":{"type":"string","description":"Local file path, relative to the scope."},
          "remotePath":{"type":"string","description":"Absolute path on the device, inside a directory the policy allows."}},
         "required":["scope","path","remotePath"]}
        """);

    public static readonly ModelToolDefinition GetDefinition = new(GetToolName,
        "Download one file from the device into a file-system scope, bytes as they are. Refused when the remote file is "
        + "larger than the transfer limit: a silently truncated file is indistinguishable from a complete one once it is "
        + "on disk. The bytes are written whole or not at all.",
        """
        {"type":"object","properties":{
          "remotePath":{"type":"string","description":"Absolute path on the device, inside a directory the policy allows."},
          "scope":{"type":"string","description":"Registered file area to write into (e.g. workspace)."},
          "path":{"type":"string","description":"Local file path, relative to the scope."}},
         "required":["remotePath","scope","path"]}
        """);

    public static readonly ModelToolDefinition TunnelDefinition = new(TunnelToolName,
        "Open a local loopback port that forwards to somewhere the device can reach. Bound to 127.0.0.1 with a port the "
        + "operating system picks, and reported back. Only available when the module allows tunnels and the caller is "
        + "an operator: a forward listening on every interface would turn this host into an open relay.",
        """
        {"type":"object","properties":{
          "remoteHost":{"type":"string","description":"Host as seen from the device."},
          "remotePort":{"type":"integer","description":"Port on that host."}},
         "required":["remoteHost","remotePort"]}
        """);

    public static readonly ModelToolDefinition InitDefinition = new(InitToolName,
        "Point this session at one SSH device and get a key for it. Generates a fresh ed25519 key pair, tries to reach "
        + "the device to learn its host-key fingerprint, and returns the PUBLIC key — you must hand that to the user so "
        + "they can add it to the device's ~/.ssh/authorized_keys. There is no way to send a private key: if a user pastes "
        + "one into the conversation, tell them it is already in the transcript and should be replaced, then use this tool "
        + "instead. Calling it again replaces the previous device: its key is cleared and its connection closed. "
        + "Fields other than the key are yours to fill: device is the name a human sees, host is what you connect to.",
        """
        {"type":"object","properties":{
          "device":{"type":"string","description":"Human-readable name for this machine, e.g. 'prod-web'. Defaults to host."},
          "host":{"type":"string","description":"Hostname or IP address. No scheme, no port, no path."},
          "user":{"type":"string","description":"Login name on that machine."},
          "port":{"type":"integer","description":"TCP port. Default 22."},
          "policy":{"type":"object","description":"What may happen on the device. Omit for 'any'.",
            "properties":{
              "mode":{"type":"string","enum":["any","allowlist"],"description":"'allowlist' refuses anything not listed."},
              "commands":{"type":"array","items":{"type":"string"},"description":"Prefixes or glob patterns, one per entry. Required when mode is allowlist."},
              "paths":{"type":"array","items":{"type":"string"},"description":"Absolute remote path prefixes ssh_put/ssh_get may touch. In allowlist mode a file operation outside them is refused."}}}},
          "fingerprint":{"type":"string","description":"Host key fingerprint (SHA256:…) if the user already knows it. Pinned; a different one is refused."}},
         "required":["host","user"]}
        """);

    public static readonly ModelToolDefinition HostsDefinition = new(HostsToolName,
        "Show the device this session is currently pointed at: name, target, policy, whether the host key is pinned, and "
        + "what went wrong last time. Reads local state only — it does not connect, so it cannot tell you whether the key "
        + "is installed yet. For that, run something short.",
        """{"type":"object","properties":{}}""");

    public static readonly ModelToolDefinition RemoveDefinition = new(RemoveToolName,
        "Forget the current device, clear its key and close the connection. Use it when the work is done.",
        """{"type":"object","properties":{}}""");

    /// <summary>
    /// Argument names that mean "here is a private key". They are refused rather than ignored: a field silently dropped
    /// would leave the caller believing a credential was installed, and the value is already in the transcript by then.
    /// </summary>
    private static readonly string[] SecretShapedArguments =
        ["key", "keyText", "privateKey", "private_key", "password", "passphrase", "identity", "identityFile"];

    public async ValueTask<Result<SessionToolResult>> InitAsync(string sessionId, string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var root = ToolArguments.Parse(argumentsJson);
        if (root is null) return ToolArguments.BadJson(InitToolName);
        var args = root.Value;

        foreach (var forbidden in SecretShapedArguments)
        {
            if (!args.TryGetProperty(forbidden, out _)) continue;
            return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                $"ssh_init does not take \"{forbidden}\": a private key never enters through a tool call.",
                "Generate one with ssh_init and give the user the public line, or have an operator install the key on "
                + "the host and save the device. If a key was pasted into the conversation, consider it exposed and "
                + "replace it on the device."));
        }

        var host = ToolArguments.Text(args, "host")?.Trim();
        var user = ToolArguments.Text(args, "user")?.Trim();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "ssh_init needs both \"host\" (what to connect to) and \"user\" (the login name on it)."));
        if (host.Contains("://", StringComparison.Ordinal) || host.Contains(' ') || host.Contains('/') ||
            host.Contains('@') || host.Contains(':'))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "\"host\" is a hostname or IP address only: no scheme, no port, no user, no path.",
                $"Got: \"{host}\". Put the port in \"port\" and the login name in \"user\"."));

        var port = ToolArguments.Number(args, "port") ?? 22;
        if (port is < 1 or > 65_535)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "\"port\" must be between 1 and 65535."));
        var device = ToolArguments.Text(args, "device")?.Trim();
        if (string.IsNullOrWhiteSpace(device)) device = host;

        SshFingerprint? pinned = null;
        if (ToolArguments.Text(args, "fingerprint") is { } fingerprintText)
        {
            if (!TryParseFingerprint(fingerprintText, out pinned, out var bad)) return Result<SessionToolResult>.Failure(bad!);
        }

        var policy = ParsePolicy(args);
        if (!policy.Ok) return Result<SessionToolResult>.Failure(policy.Error!);

        // The previous configuration goes before the new one arrives: a Session must never be able to use a device it
        // has stopped pointing at, not even for the few milliseconds a nested await would leave it reachable.
        var replaced = await DropAsync(sessionId).ConfigureAwait(false);

        var keyName = KeyNameFor(sessionId);
        var made = await _access.CreateKeyAsync(keyName, _keys, cancellationToken).ConfigureAwait(false);
        if (!made.Ok) return Result<SessionToolResult>.Failure(made.Error!);

        var target = new SshTarget(host, port, user, pinned);
        var probe = await _access.ProbeAsync(target, Options.ProbeTimeout, cancellationToken).ConfigureAwait(false);

        // A pin the device does not match is not a warning: it is the machine-in-the-middle case, and the only useful
        // answer is "nothing was created, here are both fingerprints".
        if (pinned is not null && probe.Ok && probe.Value!.HostKey is { } seen && seen != pinned)
        {
            await _keys.DeleteAsync(keyName, cancellationToken).ConfigureAwait(false);
            return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                "The device presented a different host key than the one you asked to pin. Nothing was set up.",
                $"expected {pinned}, got {seen}"));
        }

        var observed = probe.Ok ? probe.Value!.HostKey : null;
        var live = new SshLiveConfiguration(sessionId, device, target, policy.Value!, pinned ?? observed,
            made.Value!.Handle, made.Value.PublicKey, _clock.GetUtcNow());
        lock (_gate) _live[sessionId] = live;

        var text = new StringBuilder();
        text.AppendLine(replaced is null
            ? $"设备已就绪：{device}（{user}@{host}:{port}）。"
            : $"设备已切换：{device}（{user}@{host}:{port}）。上一台 {replaced} 的配置与密钥已作废。");
        text.AppendLine($"公钥（交给用户，加到目标机 ~/.ssh/authorized_keys）：\n{made.Value.PublicKey}");
        text.AppendLine($"密钥指纹：{made.Value.Fingerprint}（用户可用它在目标机上核对 authorized_keys）");
        if (observed is { } fingerprint)
        {
            text.AppendLine(pinned is null
                ? $"主机指纹：{fingerprint}（这次连到的；已钉住，下次不一致会直接拒绝。请让用户核对。）"
                : $"主机指纹：{fingerprint}（与你给的指纹一致，已钉住）");
        }
        else if (pinned is null)
        {
            text.AppendLine($"此刻连不上（{probe.Error?.Message}），所以还没有主机指纹；第一次真正连上时会钉住。" +
                            "配置已经建好，把公钥交给用户即可。");
        }
        text.AppendLine($"命令策略：{policy.Value!.Describe()}。");
        text.Append("下一步：把公钥交给用户；用户装好后直接跑一条极短的命令（例如 `true`）验证，失败会告诉你是哪一类。");
        return ToolArguments.Ok(text.ToString());
    }

    public ValueTask<Result<SessionToolResult>> HostsAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SshLiveConfiguration? live;
        lock (_gate) _live.TryGetValue(sessionId, out live);
        if (live is null)
            return ValueTask.FromResult(ToolArguments.Ok(
                "这个会话当前没有指向任何设备。用 ssh_init 建一个（会生成密钥，需要用户把公钥装到目标机上）。"));

        var text = new StringBuilder();
        text.AppendLine($"当前设备：{live.Device}（{live.Target.User}@{live.Target.Host}:{live.Target.Port}）");
        text.AppendLine($"命令策略：{live.Policy.Describe()}。");
        text.AppendLine(live.Pinned is { } pinned
            ? $"主机指纹：{pinned}（已钉住）"
            : "主机指纹：尚未钉住（还没成功连上过）");
        text.AppendLine($"公钥指纹：{live.Key.Fingerprint}");
        text.AppendLine($"建立于：{live.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}，最后一次用到：{live.LastTouched.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        if (live.LastError is { } last) text.AppendLine($"上次失败：{last}");
        return ValueTask.FromResult(ToolArguments.Ok(text.ToString().TrimEnd()));
    }

    public async ValueTask<Result<SessionToolResult>> RemoveAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        var removed = await DropAsync(sessionId).ConfigureAwait(false);
        return ToolArguments.Ok(removed is null
            ? "这个会话本来就没有指向任何设备。"
            : $"已忘掉 {removed}：密钥已清除，连接已关闭。");
    }

    // ---- the saved channel: operator only --------------------------------------------------------------------------

    public const string SavedScope = "ssh.saved";

    public static readonly ModelToolDefinition SaveDefinition = new("ssh_save",
        "Make the current device permanent, so it survives a restart and can be loaded again later. Operator only: this "
        + "is the step that turns \"a machine this conversation was handed\" into \"a credential stored on this host\". "
        + "The device's policy must be spelled out — an allowlist, not \"anything\" — because a saved device has no "
        + "conversation to narrow it.",
        """
        {"type":"object","properties":{
          "device":{"type":"string","description":"Name to save under. Defaults to the current device's name."},
          "policy":{"type":"object","description":"Required unless the current device already has one.",
            "properties":{
              "mode":{"type":"string","enum":["any","allowlist"]},
              "commands":{"type":"array","items":{"type":"string"}}}}}
        """);

    public static readonly ModelToolDefinition LoadDefinition = new("ssh_load",
        "Load a saved device into this session, replacing whatever it is pointed at now. Operator only: it uses a "
        + "credential that is already stored on this host, which is exactly the thing a session may not do on its own.",
        """{"type":"object","properties":{"device":{"type":"string","description":"Saved device name."}},"required":["device"]}""");

    public static readonly ModelToolDefinition ForgetDefinition = new("ssh_forget",
        "Delete a saved device and its key from this host. Operator only. This is the only way a stored credential goes "
        + "away, so it is also the way to answer \"did we ever store one?\".",
        """{"type":"object","properties":{"device":{"type":"string","description":"Saved device name."}},"required":["device"]}""");

    public async ValueTask<Result<SessionToolResult>> SaveAsync(string sessionId, string? argumentsJson,
        AccessLevel level, CancellationToken cancellationToken)
    {
        var root = ToolArguments.Parse(argumentsJson);
        if (root is null) return ToolArguments.BadJson("ssh_save");
        if (level < AccessLevel.Operator)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                "ssh_save 需要 operator 等级：把一台机器变成这台宿主上长期存在的凭据，是人的决定。"));
        if (savedStore is null || savedKeys is null)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "这个宿主没有配置长期存储，不能保存设备。"));

        var live = Find(sessionId);
        if (live is null) return NoDevice();

        var device = ToolArguments.Text(root.Value, "device")?.Trim();
        if (string.IsNullOrWhiteSpace(device)) device = live.Device;
        if (!IsPlainName(device))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, PlainNameRule));

        // A saved device has no conversation to fall back on, so "anything goes" is not a policy it may inherit: the
        // caller has to spell out what this machine may be used for, even if the answer is "anything".
        var declared = root.Value.TryGetProperty("policy", out _);
        var policy = live.Policy;
        if (declared)
        {
            var parsed = ParsePolicy(root.Value);
            if (!parsed.Ok) return Result<SessionToolResult>.Failure(parsed.Error!);
            policy = parsed.Value!;
        }
        if (!declared && policy.Mode == "any")
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "保存的设备必须显式写命令策略：长期存在的凭据没有一句对话给它兜底。",
                """传 {"policy":{"mode":"allowlist","commands":["df","systemctl status nginx"]}}；确实要全权就写 {"policy":{"mode":"any"}}。"""));
        if (policy.Mode != "any" && policy.Commands.Count == 0)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "allowlist 模式必须给出命令。"));

        var keyName = $"saved-{device}";
        var opened = await _keys.OpenAsync(live.Key.Name, cancellationToken).ConfigureAwait(false);
        if (!opened.Ok || opened.Value?.Content is null)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                "当前设备的内存密钥已经不在（可能过期了），重新 ssh_init 之后再保存。"));
        byte[] material;
        await using (var content = opened.Value.Content)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            material = buffer.ToArray();
        }
        var stored = await savedKeys.PutAsync(keyName, material, cancellationToken).ConfigureAwait(false);
        if (!stored.Ok) return Result<SessionToolResult>.Failure(stored.Error!);

        var saved = new SavedSshDevice(device, live.Target.Host, live.Target.Port, live.Target.User, live.Pinned,
            policy, keyName, live.PublicKey);
        var written = await savedStore.WriteAsync(SavedScope, StateName.Encode(device),
            JsonSerializer.Serialize(saved), cancellationToken).ConfigureAwait(false);
        if (!written.Ok)
        {
            await savedKeys.DeleteAsync(keyName, cancellationToken).ConfigureAwait(false);
            return Result<SessionToolResult>.Failure(written.Error!);
        }

        await AuditAsync(sessionId, live, "ssh.save", "ok", Summarize(device, "saved", null), cancellationToken)
            .ConfigureAwait(false);
        Signal(live, "save", "ok", detail: device);
        return ToolArguments.Ok(
            $"已保存：{device}（{live.Target.User}@{live.Target.Host}:{live.Target.Port}），命令策略 {policy.Describe()}。\n"
            + "密钥写进了宿主的 ssh/keys（0600）。从这一刻起它能跨重启存在，也只有 operator 能再次加载它。");
    }

    public async ValueTask<Result<SessionToolResult>> LoadAsync(string sessionId, string? argumentsJson,
        AccessLevel level, CancellationToken cancellationToken)
    {
        var root = ToolArguments.Parse(argumentsJson);
        if (root is null) return ToolArguments.BadJson("ssh_load");
        if (level < AccessLevel.Operator)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                "ssh_load 需要 operator 等级：它用的是宿主上已经存着的凭据。"));
        if (savedStore is null || savedKeys is null)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "这个宿主没有配置长期存储。"));

        var device = ToolArguments.Text(root.Value, "device")?.Trim();
        if (string.IsNullOrWhiteSpace(device) || !IsPlainName(device))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "ssh_load needs a saved \"device\" name that is a plain name (letters, digits, '-', '_', '.')."));

        var read = await savedStore.ReadAsync(SavedScope, StateName.Encode(device!), cancellationToken)
            .ConfigureAwait(false);
        // The state store reports "never written" as a successful read with a null value, so absence is read off
        // Error — never off Ok, which is false for a null value by design (docs/17).
        if (read.Error is not null) return Result<SessionToolResult>.Failure(read.Error);
        if (read.Value is null)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, $"没有保存过叫 \"{device}\" 的设备。"));

        SavedSshDevice? saved;
        try { saved = JsonSerializer.Deserialize<SavedSshDevice>(read.Value); }
        catch (JsonException ex) { saved = null; _ = ex; }
        if (saved is null)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Internal, $"\"{device}\" 的保存记录读不出来。"));

        var material = await savedKeys.OpenAsync(saved.KeyName, cancellationToken).ConfigureAwait(false);
        if (!material.Ok || material.Value?.Content is null)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                $"\"{device}\" 的私钥不在了（ssh/keys 里没有它）。用 ssh_forget 清掉这条记录，或重新 init。"));
        byte[] bytes;
        await using (var content = material.Value.Content)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        // The previous device goes first, exactly like ssh_init: a session is never pointed at two machines.
        var replaced = await DropAsync(sessionId).ConfigureAwait(false);

        var keyName = KeyNameFor(sessionId);
        var put = await _keys.PutAsync(keyName, bytes, cancellationToken).ConfigureAwait(false);
        if (!put.Ok) return Result<SessionToolResult>.Failure(put.Error!);

        var live = new SshLiveConfiguration(sessionId, saved.Device,
            new SshTarget(saved.Host, saved.Port, saved.User, saved.Pinned), saved.Policy, saved.Pinned,
            new SshKeyHandle(keyName, new SshFingerprint("SHA256", "loaded")), saved.PublicKey, _clock.GetUtcNow());
        Store(live);

        await AuditAsync(sessionId, live, "ssh.load", "ok", saved.Device, cancellationToken).ConfigureAwait(false);
        Signal(live, "load", "ok", detail: saved.Device);
        return ToolArguments.Ok(
            (replaced is null ? "" : $"上一台 {replaced} 的配置与密钥已作废。\n")
            + $"已加载：{saved.Device}（{saved.User}@{saved.Host}:{saved.Port}），命令策略 {saved.Policy.Describe()}。\n"
            + (saved.Pinned is null ? "这台设备没有钉住的主机指纹（保存时还没连上过）。" : $"主机指纹：{saved.Pinned}"));
    }

    public async ValueTask<Result<SessionToolResult>> ForgetAsync(string sessionId, string? argumentsJson,
        AccessLevel level, CancellationToken cancellationToken)
    {
        var root = ToolArguments.Parse(argumentsJson);
        if (root is null) return ToolArguments.BadJson("ssh_forget");
        if (level < AccessLevel.Operator)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                "ssh_forget 需要 operator 等级：删掉的是宿主上存着的凭据。"));
        if (savedStore is null || savedKeys is null)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "这个宿主没有配置长期存储。"));

        var device = ToolArguments.Text(root.Value, "device")?.Trim();
        if (string.IsNullOrWhiteSpace(device) || !IsPlainName(device))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "ssh_forget needs a saved \"device\" name."));

        var read = await savedStore.ReadAsync(SavedScope, StateName.Encode(device!), cancellationToken)
            .ConfigureAwait(false);
        SavedSshDevice? saved = null;
        if (read.Error is null && read.Value is not null)
        {
            try { saved = JsonSerializer.Deserialize<SavedSshDevice>(read.Value); }
            catch (JsonException) { saved = null; }
        }

        var deleted = await savedStore.DeleteAsync(SavedScope, StateName.Encode(device!), cancellationToken)
            .ConfigureAwait(false);
        if (!deleted.Ok) return Result<SessionToolResult>.Failure(deleted.Error!);
        await savedKeys.DeleteAsync(saved?.KeyName ?? $"saved-{device}", cancellationToken).ConfigureAwait(false);

        var live = Find(sessionId);
        if (live is not null) Signal(live, "forget", "ok", detail: device);
        return ToolArguments.Ok(saved is null
            ? $"没有保存过叫 \"{device}\" 的设备（记录已清，密钥也已尝试删除）。"
            : $"已删除保存的设备 {device}：记录与私钥都不在这台宿主上了。");
    }

    // ---- the tools that reach the device ---------------------------------------------------------------------------

    public async ValueTask<Result<SessionToolResult>> RunAsync(string sessionId, string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var root = ToolArguments.Parse(argumentsJson);
        if (root is null) return ToolArguments.BadJson(RunToolName);
        var command = ToolArguments.Text(root.Value, "command")?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "ssh_run needs a \"command\"."));

        var live = Find(sessionId);
        if (live is null) return NoDevice();

        if (!live.Policy.Admits(command))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                $"这个东西的策略不允许这条命令。{live.Policy.Describe()}", command));

        var connected = await EnsureConnectionAsync(live, cancellationToken).ConfigureAwait(false);
        if (!connected.Ok || connected.Value is null)
        {
            NoteUse(sessionId, connected.Error?.Message);
            await AuditAsync(sessionId, live, "ssh.connect", "failed", connected.Error?.Message, cancellationToken)
                .ConfigureAwait(false);
            return Result<SessionToolResult>.Failure(connected.Error!);
        }
        var current = connected.Value!;

        var limits = new SshLimits(Options.MaxOutputBytes, Options.CommandSeconds);
        var background = root.Value.TryGetProperty("wait", out var wait)
                         && wait.ValueKind == JsonValueKind.False;
        if (background) return await StartJobAsync(sessionId, current, command, limits, cancellationToken).ConfigureAwait(false);

        var started = _clock.GetUtcNow();
        var ran = await current.Connection!.RunAsync(command, limits, cancellationToken).ConfigureAwait(false);
        var elapsed = _clock.GetUtcNow() - started;
        NoteUse(sessionId, ran.Ok ? null : ran.Error?.Message);
        await AuditAsync(sessionId, current, "ssh.exec", ran.Ok ? "ok" : "failed",
            Summarize(command, ran.Ok ? $"exit={ran.Value!.ExitCode}" : ran.Error?.Message, ran.Value?.StandardOutput),
            cancellationToken).ConfigureAwait(false);
        Signal(current, "exec", ran.Ok ? (ran.Value!.ExitCode == 0 ? "ok" : "command") : "failed",
            elapsed.TotalMilliseconds);
        if (!ran.Ok) return Result<SessionToolResult>.Failure(ran.Error!);

        var result = ran.Value!;
        var text = new StringBuilder();
        text.AppendLine($"exit={result.ExitCode}（{elapsed.TotalSeconds:F1}s）");
        if (result.StandardOutput.Length > 0) text.AppendLine("stdout:").AppendLine(result.StandardOutput.TrimEnd());
        if (result.StandardError.Length > 0) text.AppendLine("stderr:").AppendLine(result.StandardError.TrimEnd());
        if (result.Truncated) text.AppendLine("（输出被截断：这里是上限之内的部分。）");
        if (result.ExitCode != 0)
            text.AppendLine("这条命令是连上之后自己失败的——机器是通的，问题在命令或它看到的东西。");
        return ToolArguments.Ok(text.ToString().TrimEnd());
    }

    private async ValueTask<Result<SessionToolResult>> StartJobAsync(string sessionId, SshLiveConfiguration live,
        string command, SshLimits limits, CancellationToken cancellationToken)
    {
        var jobs = live.Jobs ?? [];
        if (jobs.Count >= Options.MaxJobs)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Conflict,
                $"这个设备上已经有 {jobs.Count} 个后台命令在跑（上限 {Options.MaxJobs}）。先用 ssh_job kill 掉一个。"));

        var started = await live.Connection!.StartAsync(command, limits, cancellationToken).ConfigureAwait(false);
        if (!started.Ok) return Result<SessionToolResult>.Failure(started.Error!);

        Store(live with { Jobs = [.. jobs, started.Value!], LastUsedAt = _clock.GetUtcNow() });
        // The row goes into the ledger after the work exists, and a failure to write it is not a failure to run the
        // command: the command is already going, and refusing it now would be refusing something that happened.
        if (_jobs is not null)
        {
            await _jobs.OpenAsync(new(JobKind, command, sessionId, started.Value!.Id), cancellationToken).ConfigureAwait(false);
            // And from now on nobody has to ask how it went. Without this the row only converges when somebody looks,
            // which is fine for the row and useless for the session: "it finished" is worth hearing while it is still a
            // surprise (docs/28 §9.4). Not awaited — the turn that started it should not wait for it either.
            _ = WatchAsync(sessionId, started.Value!, CancellationToken.None);
        }
        await AuditAsync(sessionId, live, "ssh.job.start", "ok", Summarize(command, "started", null), cancellationToken)
            .ConfigureAwait(false);
        Signal(live, "job.start", "started");
        return ToolArguments.Ok($"已在后台启动：job {started.Value!.Id}\n命令：{command}\n" +
                               "用 ssh_job {action:\"output\", id:…} 看输出，用 kill 停掉。");
    }

    /// <summary>
    /// Brings one ledger row up to date with the job it describes, when there is anything to update.
    ///
    /// Only the ending is written. A running job's progress would be a guess — this layer knows the output it has handed
    /// out, not how much of the work is done — and a made-up percentage is worse than none. So the row says "running"
    /// until somebody looks and sees it is not.
    /// </summary>
    private async ValueTask MirrorAsync(string sessionId, ISshJob job, CancellationToken cancellationToken)
    {
        if (_jobs is null || !job.Completed) return;
        var outcome = job.ExitCode is { } code && code != 0
            ? $"exit={code}"
            : job.ExitCode is { } ok ? $"exit={ok}（成功）" : "结束";
        await _jobs.CompleteAsync(sessionId, job.Id, $"ssh 上那条命令{outcome}。", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for one background command to end, then writes the ending.
    ///
    /// This is the only pushed thing about a job. Everything else — the output, the exit code, the row's progress — is
    /// pulled, and converges at the moments somebody asks (docs/28 §9.2). That is enough for a record; it is not enough
    /// for the session, because the row being written is what makes the ledger wake it. So the ending is written as soon
    /// as it happens, by a task nobody is waiting on.
    ///
    /// Public rather than private only because a test cannot wait for a fire-and-forget task: calling it twice is
    /// harmless, since a row that is already over is not moved again.
    /// </summary>
    public async ValueTask WatchAsync(string sessionId, ISshJob job, CancellationToken cancellationToken)
    {
        try { await job.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; } // The watcher was cancelled, not the command: nothing to record.
        catch (Exception ex)
        {
            // The row still says running and the next poll writes the truth; saying so here is for whoever is looking at
            // the signals, since the session will be told whatever the ledger ends up holding.
            signals?.Write(new()
            {
                Id = Guid.NewGuid(),
                ParentId = SignalScope.CurrentId,
                Group = "ssh",
                Name = sessionId,
                From = "ssh",
                To = "job",
                Mechanism = "ssh.job.watch",
                Outcome = "failed",
                Level = SignalLevel.Warn,
                Detail = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["job"] = job.Id, ["message"] = ex.Message
                }
            });
            return;
        }
        await MirrorAsync(sessionId, job, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends one configuration's work on purpose: the row is written as killed <b>before</b> the process is stopped, so
    /// the watcher that notices the ending finds a row that is already over instead of recording a deliberate stop as
    /// "it finished".
    /// </summary>
    private async ValueTask<Result> StopAsync(string sessionId, ISshJob job, CancellationToken cancellationToken)
    {
        if (_jobs is not null)
            await _jobs.KillAsync(sessionId, job.Id, cancellationToken).ConfigureAwait(false);
        var killed = Result.Success();
        try
        {
            var outcome = await job.KillAsync(CancellationToken.None).ConfigureAwait(false);
            if (!outcome.Ok) killed = outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            killed = Result.Failure(new(ErrorCode.Unavailable, "这条命令没能停下来。", ex.Message));
        }
        // Cleanup finishes either way: a job that refused to die must not leave a disposed-less handle behind, and the
        // row is already closed, because nobody is going to look at this job again.
        await job.DisposeAsync().ConfigureAwait(false);
        return killed;
    }

    public async ValueTask<Result<SessionToolResult>> JobAsync(string sessionId, string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var root = ToolArguments.Parse(argumentsJson);
        if (root is null) return ToolArguments.BadJson(JobToolName);
        var live = Find(sessionId);
        if (live is null) return NoDevice();

        var action = ToolArguments.Text(root.Value, "action")?.Trim().ToLowerInvariant() ?? "list";
        var jobs = live.Jobs ?? [];
        switch (action)
        {
            case "list":
            {
                if (jobs.Count == 0) return ToolArguments.Ok("这个设备上没有后台命令。");
                var text = new StringBuilder();
                foreach (var job in jobs)
                {
                    text.AppendLine($"{job.Id}  exit={(job.Completed ? job.ExitCode?.ToString() ?? "?" : "running")}  {job.Command}");
                    // Looking is one of the ways to learn it ended. It is no longer the only one — the watcher writes the
                    // ending when it happens — but a poll is still the moment the row is brought up to date if that
                    // write failed (docs/28 §9.2).
                    await MirrorAsync(sessionId, job, cancellationToken).ConfigureAwait(false);
                }
                return ToolArguments.Ok(text.ToString().TrimEnd());
            }
            case "output":
            {
                var id = ToolArguments.Text(root.Value, "id")?.Trim();
                var job = jobs.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
                if (job is null)
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                        $"没有这个 job：\"{id}\"。用 ssh_job {{\"action\":\"list\"}} 看现有的。"));
                var offset = ToolArguments.Number(root.Value, "offset") ?? 0;
                var chunk = job.Read(offset, Options.MaxOutputBytes);
                NoteUse(sessionId);
                await MirrorAsync(sessionId, job, cancellationToken).ConfigureAwait(false);
                var text = new StringBuilder();
                text.AppendLine($"job {job.Id}：{(chunk.Completed ? $"结束，exit={chunk.ExitCode}" : "还在跑")}" +
                                $"（从 {chunk.Offset} 开始，取了 {chunk.Length} 字符，下次用 offset={chunk.Offset + chunk.Length}）");
                text.Append(chunk.Text.Length == 0 ? "（这一段没有新输出）" : chunk.Text);
                return ToolArguments.Ok(text.ToString().TrimEnd());
            }
            case "kill":
            {
                var id = ToolArguments.Text(root.Value, "id")?.Trim();
                var job = jobs.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
                if (job is null)
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, $"没有这个 job：\"{id}\"。"));
                // Row first, process second: stopped on purpose is a fact about the work, and recording it before the
                // process ends is what keeps the watcher from turning it into "it finished". The row says so and it will
                // not be announced — whoever asked for the kill knows.
                var killed = await StopAsync(sessionId, job, cancellationToken).ConfigureAwait(false);
                Store(live with { Jobs = jobs.Where(candidate => candidate != job).ToArray(), LastUsedAt = _clock.GetUtcNow() });
                await AuditAsync(sessionId, live, "ssh.job.kill", killed.Ok ? "ok" : "failed", job.Command, cancellationToken)
                    .ConfigureAwait(false);
                return killed.Ok
                    ? ToolArguments.Ok($"已停掉 job {job.Id}。")
                    : Result<SessionToolResult>.Failure(killed.Error!);
            }
            default:
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    "\"action\" must be list, output or kill."));
        }
    }

    public async ValueTask<Result<SessionToolResult>> PutAsync(string sessionId, string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var root = ToolArguments.Parse(argumentsJson);
        if (root is null) return ToolArguments.BadJson(PutToolName);
        var scope = ToolArguments.Text(root.Value, "scope");
        var path = ToolArguments.Text(root.Value, "path");
        var remotePath = ToolArguments.Text(root.Value, "remotePath");
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(remotePath))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "ssh_put needs \"scope\", \"path\" and \"remotePath\"."));
        if (binaryFiles is null)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "这个宿主没有开启字节传输，不能传文件（连文本也不行：文件进出只走一条路）。"));

        var live = Find(sessionId);
        if (live is null) return NoDevice();
        // A command policy that does not cover paths is a policy with a side door: writing ~/.ssh/authorized_keys is one
        // upload and it outlives every restriction the command list imposes.
        if (!live.Policy.AdmitsPath(remotePath!))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                $"这个东西的策略不允许写这个远端路径。{live.Policy.Describe()}",
                "allowlist 模式下文件只能在 policy.paths 列出的前缀里读写；没列 = 不能。"));
        var connected = await EnsureConnectionAsync(live, cancellationToken).ConfigureAwait(false);
        if (!connected.Ok) return Result<SessionToolResult>.Failure(connected.Error!);
        var current = connected.Value!;

        // The local half is a scope plus a relative path — the same two arguments fs_read takes, and a scope that has to
        // accept bytes. The file itself is streamed: nothing about a 40 MiB tarball ever enters the conversation.
        var local = await binaryFiles.OpenReadAsync(scope!, path!, cancellationToken).ConfigureAwait(false);
        if (!local.Ok) return Result<SessionToolResult>.Failure(local.Error!);

        long bytes;
        await using (var content = local.Value!)
        {
            bytes = content.Length;
            var uploaded = await current.Connection!.UploadAsync(content, remotePath!, cancellationToken).ConfigureAwait(false);
            NoteUse(sessionId, uploaded.Ok ? null : uploaded.Error?.Message);
            await AuditAsync(sessionId, current, "ssh.put", uploaded.Ok ? "ok" : "failed",
                Summarize($"{scope}/{path} -> {remotePath}", $"{bytes} bytes", null), cancellationToken)
                .ConfigureAwait(false);
            return uploaded.Ok
                ? ToolArguments.Ok($"已上传 {bytes} 字节：{scope}/{path} → {remotePath}")
                : Result<SessionToolResult>.Failure(uploaded.Error!);
        }
    }

    public async ValueTask<Result<SessionToolResult>> GetAsync(string sessionId, string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var root = ToolArguments.Parse(argumentsJson);
        if (root is null) return ToolArguments.BadJson(GetToolName);
        var remotePath = ToolArguments.Text(root.Value, "remotePath");
        var scope = ToolArguments.Text(root.Value, "scope");
        var path = ToolArguments.Text(root.Value, "path");
        if (string.IsNullOrWhiteSpace(remotePath) || string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(path))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "ssh_get needs \"remotePath\", \"scope\" and \"path\"."));
        if (binaryFiles is null)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "这个宿主没有开启字节传输，不能取文件。"));

        var live = Find(sessionId);
        if (live is null) return NoDevice();
        if (!live.Policy.AdmitsPath(remotePath!))
            return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                $"这个东西的策略不允许读这个远端路径。{live.Policy.Describe()}",
                "allowlist 模式下文件只能在 policy.paths 列出的前缀里读写；没列 = 不能。"));
        var connected = await EnsureConnectionAsync(live, cancellationToken).ConfigureAwait(false);
        if (!connected.Ok) return Result<SessionToolResult>.Failure(connected.Error!);
        var current = connected.Value!;

        // Refused when larger than the cap rather than truncated: a short file that looks complete is how a "backup"
        // turns out not to be one. The bytes go straight into the scope; they are never read as text on the way.
        var fetched = await current.Connection!.DownloadAsync(remotePath!, Options.MaxTransferBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!fetched.Ok) return Result<SessionToolResult>.Failure(fetched.Error!);

        long bytes;
        await using (var stream = fetched.Value!)
        {
            bytes = stream.Length;
            var saved = await binaryFiles.SaveAsync(scope!, path!, stream, cancellationToken).ConfigureAwait(false);
            NoteUse(sessionId, saved.Ok ? null : saved.Error?.Message);
            await AuditAsync(sessionId, current, "ssh.get", saved.Ok ? "ok" : "failed",
                Summarize($"{remotePath} -> {scope}/{path}", $"{bytes} bytes", null),
                cancellationToken).ConfigureAwait(false);
            return saved.Ok
                ? ToolArguments.Ok($"已取回 {bytes} 字节：{remotePath} → {scope}/{path}")
                : Result<SessionToolResult>.Failure(saved.Error!);
        }
    }

    public async ValueTask<Result<SessionToolResult>> TunnelAsync(string sessionId, string? argumentsJson,
        AccessLevel level, CancellationToken cancellationToken)
    {
        var root = ToolArguments.Parse(argumentsJson);
        if (root is null) return ToolArguments.BadJson(TunnelToolName);
        if (level < AccessLevel.Operator)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Denied,
                "ssh_tunnel 需要 operator 等级：转发会把一台机器的可达面借给这台宿主。"));

        var live = Find(sessionId);
        if (live is null) return NoDevice();
        var remoteHost = ToolArguments.Text(root.Value, "remoteHost")?.Trim();
        var remotePort = ToolArguments.Number(root.Value, "remotePort") ?? 0;
        if (string.IsNullOrWhiteSpace(remoteHost) || remotePort is < 1 or > 65_535)
            return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                "ssh_tunnel needs \"remoteHost\" and \"remotePort\" (1-65535)."));

        var connected = await EnsureConnectionAsync(live, cancellationToken).ConfigureAwait(false);
        if (!connected.Ok) return Result<SessionToolResult>.Failure(connected.Error!);
        var current = connected.Value!;

        var opened = await current.Connection!.ForwardLocalAsync(remoteHost!, remotePort, cancellationToken)
            .ConfigureAwait(false);
        if (!opened.Ok) return Result<SessionToolResult>.Failure(opened.Error!);

        Store(current with { Tunnels = [..current.Tunnels ?? [], opened.Value!], LastUsedAt = _clock.GetUtcNow() });
        await AuditAsync(sessionId, current, "ssh.tunnel", "ok", $"{remoteHost}:{remotePort}", cancellationToken)
            .ConfigureAwait(false);
        return ToolArguments.Ok(
            $"已开一条本地回环转发：127.0.0.1:{opened.Value!.LocalPort} → {remoteHost}:{remotePort}（只能本机访问）。\n"
            + "它会随这个设备的配置一起关闭（ssh_remove / 换设备 / 空闲过期）。");
    }

    /// <summary>
    /// Opens the connection if it is not open yet, and pins the device the first time it answers.
    ///
    /// The pin is what makes "first sighting" mean something after the fact: initialisation may have run before the
    /// device was reachable, in which case there was nothing to pin — the first successful connection supplies it, and
    /// every later connection refuses a device that presents something else.
    /// </summary>
    private async ValueTask<Result<SshLiveConfiguration>> EnsureConnectionAsync(SshLiveConfiguration live,
        CancellationToken cancellationToken)
    {
        if (live.Connection is not null) return Result<SshLiveConfiguration>.Success(live);

        var opened = await _access.ConnectAsync(live.Target, live.Key, _keys, Options.ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!opened.Ok || opened.Value is null)
            return Result<SshLiveConfiguration>.Failure(opened.Error ?? new(ErrorCode.Unavailable, "连接失败。"));

        var pinned = live.Pinned ?? opened.Value.HostKey;
        var updated = live with
        {
            Connection = opened.Value,
            // The pin travels into the target as well, so a reconnect after an idle disconnect is checked too.
            Target = live.Target with { Pinned = pinned },
            Pinned = pinned,
            LastUsedAt = _clock.GetUtcNow(),
            LastError = null,
        };
        Store(updated);
        return Result<SshLiveConfiguration>.Success(updated);
    }

    private static Result<SessionToolResult> NoDevice() =>
        Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
            "这个会话还没有设备。先用 ssh_init 建一个（会生成密钥，需要用户把公钥装到目标机上）。"));

    private void Store(SshLiveConfiguration configuration)
    {
        lock (_gate) _live[configuration.SessionId] = configuration;
    }

    /// <summary>
    /// One audit record per command, carrying the command itself but never its output.
    ///
    /// The command is the thing that was done — "who ran what on which machine" is unanswerable without it. Output is
    /// not, because output is where secrets are: a config file, a key, an environment dump.
    /// </summary>
    private async ValueTask AuditAsync(string sessionId, SshLiveConfiguration live, string action, string outcome,
        string? detail, CancellationToken cancellationToken)
    {
        if (audit is null) return;
        await audit.AppendAsync(new(DateTimeOffset.UtcNow, Guid.Empty, $"session:{sessionId}",
            new Address("ssh", "device", live.Device), action, outcome, Truncate(detail, 400)), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? Summarize(string command, string? outcome, string? output)
    {
        var text = outcome is null ? command : $"{command} → {outcome}";
        return output is null ? text : $"{text} (stdout {Encoding.UTF8.GetByteCount(output)} bytes, not recorded)";
    }

    /// <summary>
    /// The signal line for one hop to a device: which device, which verb, how it ended, how long it took. Deliberately
    /// no command text and no output — the signal log is a live window people read, not a transcript.
    /// </summary>
    private void Signal(SshLiveConfiguration live, string verb, string outcome, double durationMs = 0,
        string? detail = null) =>
        signals?.Write(new()
        {
            Id = Guid.NewGuid(),
            ParentId = SignalScope.CurrentId,
            Group = "ssh",
            Name = live.Device,
            From = "session",
            To = $"ssh:{live.Device}",
            Mechanism = $"ssh.{verb}",
            Outcome = outcome,
            Level = outcome is "ok" or "started" ? SignalLevel.Info : SignalLevel.Warn,
            DurationMs = durationMs,
            Detail = detail is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["detail"] = detail }
        });

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// A device name becomes a file name in the key store, so it is checked here as well as there: letters, digits,
    /// '-', '_' and '.', no separators, bounded length. The rule is repeated rather than shared because this assembly
    /// does not reference the transport one — the runtime talks to <c>ISshAccess</c>, never to the SSH library.
    /// </summary>
    private static bool IsPlainName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private const string PlainNameRule =
        "名字只能有字母、数字、'-'、'_'、'.'，最长 64 个字符，且以字母或数字开头。";

    /// <summary>
    /// Drops a Session's live configuration: jobs first, then the connection, then the key. In that order because a
    /// job holding a dead channel can only fail, and a key removed while a connection still used it would be a lie
    /// about what is possible.
    /// </summary>
    /// <returns>The device name that was dropped, or null when there was nothing.</returns>
    private async Task<string?> DropAsync(string sessionId)
    {
        SshLiveConfiguration? live;
        lock (_gate)
        {
            if (!_live.Remove(sessionId, out live)) return null;
        }
        // The configuration is going away, and so is everything it was running. Rows first, for the same reason the kill
        // action does it first: a job stopped on purpose must not reach the ledger as "it finished", and a watcher that
        // wakes up mid-cleanup would do exactly that.
        foreach (var job in live.Jobs ?? [])
            await StopAsync(sessionId, job, CancellationToken.None).ConfigureAwait(false);
        await live.CloseAsync().ConfigureAwait(false);
        await _keys.DeleteAsync(live.Key.Name, CancellationToken.None).ConfigureAwait(false);
        return live.Device;
    }

    /// <summary>
    /// Drops configurations nobody has touched for a while. This is what keeps "memory only" from turning into "grows
    /// forever": a Session that initialised a device a week ago should not still be holding its key.
    /// </summary>
    public async ValueTask SweepAsync()
    {
        var cutoff = _clock.GetUtcNow() - Options.IdleTimeout;
        List<string> stale;
        lock (_gate)
        {
            stale = _live.Where(pair => pair.Value.LastTouched < cutoff).Select(pair => pair.Key).ToList();
        }
        foreach (var sessionId in stale) await DropAsync(sessionId).ConfigureAwait(false);
    }

    /// <summary>Drops every live configuration: what stopping the module needs, since nothing should outlive it.</summary>
    public async ValueTask SweepAllAsync()
    {
        List<string> all;
        lock (_gate) all = _live.Keys.ToList();
        foreach (var sessionId in all) await DropAsync(sessionId).ConfigureAwait(false);
    }

    /// <summary>Records what the last attempt did, so <c>ssh_hosts</c> can answer "what went wrong" without connecting.</summary>
    public void NoteUse(string sessionId, string? error = null)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(sessionId, out var live))
                _live[sessionId] = live with { LastUsedAt = _clock.GetUtcNow(), LastError = error };
        }
    }

    /// <summary>The live configuration of one Session, for the tools that actually run something.</summary>
    public SshLiveConfiguration? Find(string sessionId)
    {
        lock (_gate) return _live.TryGetValue(sessionId, out var live) ? live : null;
    }

    /// <summary>Replaces a Session's configuration (used when a saved one is loaded, and by tests).</summary>
    public void Adopt(SshLiveConfiguration configuration)
    {
        lock (_gate) _live[configuration.SessionId] = configuration;
    }

    private static string KeyNameFor(string sessionId)
    {
        // The name reaches a file name in the persistent store and a dictionary key here, so it must not carry anything
        // a session id can carry. A short readable prefix plus randomness is cheaper than sanitising, and it stays
        // unique across sessions.
        var prefix = StateName.Encode(sessionId);
        if (prefix.Length > 8) prefix = prefix[..8];
        return $"live-{prefix}-{Guid.NewGuid():N}"[..24];
    }

    private static bool TryParseFingerprint(string text, out SshFingerprint? value, out Error? error)
    {
        value = null;
        error = null;
        var trimmed = text.Trim();
        // Accept the shape ssh-keyscan writes ("SHA256:abc…") and the bare base64, because both are what a person
        // copies, and refusing one of them would be a typo the user cannot see.
        var withoutPrefix = trimmed.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase) ? trimmed[7..] : trimmed;
        if (withoutPrefix.Length is >= 16 and <= 128 && withoutPrefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
        {
            value = new SshFingerprint("SHA256", withoutPrefix);
            return true;
        }
        error = new(ErrorCode.Invalid, "\"fingerprint\" must look like SHA256:base64… (the line ssh-keyscan prints).");
        return false;
    }

    private static Result<SshCommandPolicy> ParsePolicy(JsonElement args)
    {
        if (!args.TryGetProperty("policy", out var policy) || policy.ValueKind == JsonValueKind.Null)
            return Result<SshCommandPolicy>.Success(SshCommandPolicy.Any);

        if (policy.ValueKind != JsonValueKind.Object)
            return Result<SshCommandPolicy>.Failure(new(ErrorCode.Invalid, "\"policy\" must be an object."));

        var mode = ToolArguments.Text(policy, "mode")?.Trim().ToLowerInvariant() ?? "any";
        if (mode is not ("any" or "allowlist"))
            return Result<SshCommandPolicy>.Failure(new(ErrorCode.Invalid,
                "\"policy.mode\" must be \"any\" or \"allowlist\"."));

        var commands = new List<string>();
        if (policy.TryGetProperty("commands", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    return Result<SshCommandPolicy>.Failure(new(ErrorCode.Invalid,
                        "Every entry in \"policy.commands\" must be a non-empty string."));
                commands.Add(item.GetString()!.Trim());
            }
        }

        var paths = new List<string>();
        if (policy.TryGetProperty("paths", out var pathList) && pathList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pathList.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    return Result<SshCommandPolicy>.Failure(new(ErrorCode.Invalid,
                        "Every entry in \"policy.paths\" must be a non-empty string."));
                // Prefixes are compared against a normalized absolute path, so a prefix that is not one can never match
                // — refusing it here is the difference between "no access" and "a typo that looks like no access".
                var prefix = SshCommandPolicy.NormalizeRemotePath(item.GetString());
                if (prefix is null)
                    return Result<SshCommandPolicy>.Failure(new(ErrorCode.Invalid,
                        $"\"policy.paths\" entries must be absolute paths without '..' (got \"{item.GetString()}\")."));
                paths.Add(prefix.EndsWith('/') ? prefix : prefix + "/");
            }
        }

        // An allowlist with nothing in it is not "anything goes" — it is a configuration that can never work, and
        // saying so now is cheaper than a session that discovers it one refused command at a time.
        if (mode == "allowlist" && commands.Count == 0)
            return Result<SshCommandPolicy>.Failure(new(ErrorCode.Invalid,
                "\"policy.mode\" is allowlist but \"policy.commands\" is empty: nothing would ever be allowed."));

        return Result<SshCommandPolicy>.Success(mode == "any"
            ? SshCommandPolicy.Any
            : new SshCommandPolicy(mode, commands, paths));
    }

    // ---- tool wrappers ---------------------------------------------------------------------------------------------
    //
    // The three are bound to one Session's id when the round asks for them: a tool object cannot ask "which session am
    // I in", and the whole of this module is scoped by that answer.

    private sealed class InitTool(SshTools tools, string sessionId) : ISessionTool
    {
        public string Name => InitToolName;
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.InitAsync(sessionId, call.ArgumentsJson, cancellationToken);
    }

    private sealed class HostsTool(SshTools tools, string sessionId) : ISessionTool
    {
        public string Name => HostsToolName;
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.HostsAsync(sessionId, cancellationToken);
    }

    private sealed class RemoveTool(SshTools tools, string sessionId) : ISessionTool
    {
        public string Name => RemoveToolName;
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.RemoveAsync(sessionId, cancellationToken);
    }

    private sealed class RunTool(SshTools tools, string sessionId) : ISessionTool
    {
        public string Name => RunToolName;
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.RunAsync(sessionId, call.ArgumentsJson, cancellationToken);
    }

    private sealed class JobTool(SshTools tools, string sessionId) : ISessionTool
    {
        public string Name => JobToolName;
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.JobAsync(sessionId, call.ArgumentsJson, cancellationToken);
    }

    private sealed class PutTool(SshTools tools, string sessionId) : ISessionTool
    {
        public string Name => PutToolName;
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.PutAsync(sessionId, call.ArgumentsJson, cancellationToken);
    }

    private sealed class GetTool(SshTools tools, string sessionId) : ISessionTool
    {
        public string Name => GetToolName;
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.GetAsync(sessionId, call.ArgumentsJson, cancellationToken);
    }

    private sealed class TunnelTool(SshTools tools, string sessionId, AccessLevel level) : ISessionTool
    {
        public string Name => TunnelToolName;
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.TunnelAsync(sessionId, call.ArgumentsJson, level, cancellationToken);
    }

    private sealed class SaveTool(SshTools tools, string sessionId, AccessLevel level) : ISessionTool
    {
        public string Name => "ssh_save";
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.SaveAsync(sessionId, call.ArgumentsJson, level, cancellationToken);
    }

    private sealed class LoadTool(SshTools tools, string sessionId, AccessLevel level) : ISessionTool
    {
        public string Name => "ssh_load";
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.LoadAsync(sessionId, call.ArgumentsJson, level, cancellationToken);
    }

    private sealed class ForgetTool(SshTools tools, string sessionId, AccessLevel level) : ISessionTool
    {
        public string Name => "ssh_forget";
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
            => tools.ForgetAsync(sessionId, call.ArgumentsJson, level, cancellationToken);
    }
}

/// <summary>
/// A device that outlives the conversation: what a saved record holds.
///
/// It holds a <b>name</b> for the key, not the key: the private half lives in the host's key directory, so a state dump
/// or a copied <c>aln.db</c> leaks where a key is, not what it is.
/// </summary>
public sealed record SavedSshDevice(string Device, string Host, int Port, string User, SshFingerprint? Pinned,
    SshCommandPolicy Policy, string KeyName, string PublicKey);

/// <summary>What a live device is, in one place: the target, the policy, the key it is authenticated with.</summary>
public sealed record SshLiveConfiguration(
    string SessionId,
    string Device,
    SshTarget Target,
    SshCommandPolicy Policy,
    SshFingerprint? Pinned,
    SshKeyHandle Key,
    string PublicKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt = null,
    string? LastError = null,
    ISshConnection? Connection = null,
    IReadOnlyList<ISshJob>? Jobs = null,
    IReadOnlyList<ISshTunnel>? Tunnels = null)
{
    /// <summary>When this was last touched; a configuration nobody uses stops holding a key (docs/27 §1).</summary>
    public DateTimeOffset LastTouched => LastUsedAt ?? CreatedAt;

    /// <summary>Jobs first, then tunnels, then the channel: a job or forward whose channel is gone can only fail.</summary>
    public async Task CloseAsync()
    {
        foreach (var job in Jobs ?? [])
        {
            try { await job.KillAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* A job that will not die must not stop the cleanup. */ }
            await job.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var tunnel in Tunnels ?? [])
        {
            try { await tunnel.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* same: cleanup must finish */ }
        }
        if (Connection is not null) await Connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Which commands reach a device. Two modes and no expressions to interpret: <c>any</c> for "a person just handed this
/// machine over", <c>allowlist</c> for everything that outlives the conversation.
/// </summary>
public sealed record SshCommandPolicy(string Mode, IReadOnlyList<string> Commands,
    IReadOnlyList<string>? Paths = null)
{
    public static readonly SshCommandPolicy Any = new("any", []);

    /// <summary>
    /// A command is admitted when it matches one entry as a literal prefix or as a glob. Prefix matching is what makes
    /// an allowlist usable ("systemctl status" covers every unit); globs cover the rest without pretending to be a
    /// sandbox, which this is not — anyone who can write an arbitrary command can chain it.
    /// </summary>
    public bool Admits(string command)
    {
        if (Mode == "any") return true;
        var trimmed = command.Trim();
        return Commands.Any(entry => Matches(entry, trimmed));
    }

    /// <summary>
    /// Whether one remote path may be read from or written to.
    ///
    /// This exists because a command policy alone can be walked around: a device whose allowlist is "systemctl status"
    /// would still accept a file written to <c>~/.ssh/authorized_keys</c>, and that is a permanent key — a bigger
    /// grant than any command. So in <c>allowlist</c> mode file access is <b>denied unless a path prefix was
    /// declared</b>, which is the same shape as the command list: nothing is reachable until it is named.
    /// </summary>
    public bool AdmitsPath(string remotePath)
    {
        if (Mode == "any") return true;
        var normalized = NormalizeRemotePath(remotePath);
        if (normalized is null) return false;
        foreach (var declared in Paths ?? [])
        {
            // Whole segments only: "/srv/app" must not admit "/srv/application", and the directory itself (without a
            // trailing slash) has to be admitted as well as anything under it.
            if (declared == "/") return true;
            var prefix = declared.TrimEnd('/');
            if (prefix.Length == 0) continue;
            if (string.Equals(normalized, prefix, StringComparison.Ordinal)) return true;
            if (normalized.StartsWith(prefix + "/", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Absolute, no <c>..</c>, no empty segments — the remote side is somebody else's file system, so the shape rule is
    /// checked here rather than hoped for. Null when it is not a usable absolute path.
    /// </summary>
    public static string? NormalizeRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/')) return null;
        if (trimmed.Contains('\0')) return null;
        var segments = trimmed.Split('/');
        if (segments.Any(segment => segment == "..")) return null;
        var cleaned = "/" + string.Join('/', segments.Where(segment => segment.Length > 0));
        return cleaned.Length == 0 ? "/" : cleaned;
    }

    private static bool Matches(string pattern, string command)
    {
        if (pattern.Length == 0) return false;
        if (!pattern.Contains('*')) return command.StartsWith(pattern, StringComparison.Ordinal);
        var regex = "^" + string.Join(".*", pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(command, regex,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    public string Describe() => Mode == "any"
        ? "any（这台设备可以跑任何命令）"
        : $"allowlist（命令：{string.Join("、", Commands)}；远端路径：{(Paths is { Count: > 0 } paths ? string.Join("、", paths) : "未声明，文件读写全部拒绝")}）";
}

/// <summary>How much a module lets its tools do, in one place so it comes from configuration rather than from code.</summary>
public sealed record SshOptions
{
    public static readonly SshOptions Default = new();

    /// <summary>How long to wait for "hello" from a device. Short on purpose: this is a probe, not a command.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one authentication attempt may take before it is a timeout.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a live configuration keeps its key while nobody uses it.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Output cap for one command, in bytes.</summary>
    public int MaxOutputBytes { get; init; } = 64 * 1024;

    /// <summary>Wall clock for one command.</summary>
    public int CommandSeconds { get; init; } = 30;

    /// <summary>
    /// Largest file ssh_get will pull in, and the ceiling the local side enforces on the way out. Matches the byte
    /// surface's own limit so a transfer can never be refused after it has already crossed the network.
    /// </summary>
    public long MaxTransferBytes { get; init; } = IBinaryFileAccess.MaxTransferBytes;

    /// <summary>Background commands one device may have at once. A cap, not a queue: a Session that piles jobs up is
    /// hiding work it has lost track of.</summary>
    public int MaxJobs { get; init; } = 4;

    /// <summary>Whether <c>ssh_tunnel</c> may be opened at all for this module (a device may still refuse it).</summary>
    public bool AllowTunnel { get; init; }
}
