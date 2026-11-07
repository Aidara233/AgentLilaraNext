using System.Text.Json;
using System.Text.Json.Nodes;

namespace ALN.Runtime;

/// <summary>
/// What seeding did: which shipped scripts were copied, which were left alone because the operator already has one
/// under that name, and which could not be written. A failure here is reported, never thrown: a copy that could not
/// be made is a script the operator can still add by hand, not a reason to refuse to start.
/// </summary>
public sealed record ScriptSeedReport(
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Kept,
    IReadOnlyList<(string Id, string Reason)> Failed)
{
    public bool AddedAnything => Copied.Count > 0;
}

/// <summary>
/// Puts a copy of each shipped script into <c>&lt;dataRoot&gt;/scripts/trusted</c>, once, and never touches it
/// again (C-03, the same rule <see cref="ModuleManifestStore.WriteIfMissingAsync"/> follows for module files).
///
/// Why copy at all, rather than keep reading the folder next to the binary. A script is behaviour a person edits,
/// and the folder next to the binary is not theirs: the next deploy replaces it wholesale, so an edit there lasts
/// until the next build and looks like it was forgotten. A copy in the data root is the only place where "edit it,
/// then reload" means anything — and the only place an operator can see what actually runs.
///
/// The copy is deliberately not byte-identical: <c>trustDomain</c> is rewritten from <c>builtin</c> to
/// <c>trusted</c>. That is the point of copying. "trusted" means a person put it there and may change it, and a
/// manifest claiming to ship from a folder a person can edit is exactly what the catalogue refuses.
/// </summary>
public sealed class ScriptSeed(string programRoot, string dataRoot)
{
    public const string ManifestFileName = "manifest.json";
    public const string SourceFileName = "main.js";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string BundledRoot => Path.Combine(programRoot, "resources", "scripts", "builtin");
    public string TrustedRoot => Path.Combine(dataRoot, "scripts", "trusted");

    /// <summary>Where a script is written before anyone decides it may run (docs/25). A scope, not a catalogue root...</summary>
    public string DraftRoot => Path.Combine(dataRoot, "scripts", "draft");

    public async ValueTask<ScriptSeedReport> CopyMissingAsync(CancellationToken cancellationToken)
    {
        var copied = new List<string>();
        var kept = new List<string>();
        var failed = new List<(string Id, string Reason)>();
        if (!Directory.Exists(BundledRoot)) return new(copied, kept, failed);

        foreach (var source in Directory.EnumerateDirectories(BundledRoot).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(source);
            var manifestPath = Path.Combine(source, ManifestFileName);
            var sourcePath = Path.Combine(source, SourceFileName);
            if (!File.Exists(manifestPath) || !File.Exists(sourcePath)) continue;

            var target = Path.Combine(TrustedRoot, id);
            // Already here: it is the operator's file now. Rewriting it would undo an edit silently — the one thing
            // this method exists to prevent — and would also make every start a potential change of behaviour.
            if (File.Exists(Path.Combine(target, ManifestFileName))) { kept.Add(id); continue; }

            try
            {
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false))
                    ?? throw new JsonException($"The shipped manifest is empty: {manifestPath}");
                manifest["trustDomain"] = "trusted";

                Directory.CreateDirectory(target);
                // The body lands first. A directory holding a manifest with no main.js is a catalogue that refuses
                // to load — and the host exits on that — so the order makes a crash in between leave a folder the
                // catalogue simply does not see, which is recoverable by the next start.
                await File.WriteAllBytesAsync(Path.Combine(target, SourceFileName),
                    await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(target, ManifestFileName),
                    manifest.ToJsonString(WriteOptions), cancellationToken).ConfigureAwait(false);
                copied.Add(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                failed.Add((id, ex.Message));
            }
        }

        return new(copied, kept, failed);
    }

    public const string GuideFileName = "GUIDE.md";

    /// <summary>
    /// The example the guide shows and a test loads. Kept as data rather than prose so that "what the reader is told to
    /// write" is the same string "what the host accepts" is checked against — a guide that has drifted is worse than no
    /// guide, because it teaches a shape that gets refused.
    /// </summary>
    public const string ExampleManifest = """
    {
      "id": "channel.say-hello",
      "version": 1,
      "targetType": "channel",
      "mount": "manual",
      "events": ["channel.message.accepted"],
      "entryPoint": "onEvent",
      "capabilities": ["channel.bound-send"],
      "trustDomain": "trusted"
    }
    """;

    public const string ExampleSource = """
    async function onEvent(event, ctx) {
      if (ctx.eventName !== "channel.message.accepted") return;
      const text = String(event.text ?? "");
      if (!text.startsWith("!hello")) return;
      await ctx.channel.reply({
        platformConversationId: event.binding.platformConversationId,
        text: "hello"
      });
    }
    """;

    /// <summary>
    /// The guide, written into the draft folder once and read with the same file tools that wrote the script. Not
    /// injected into every turn: it is reference material, needed while writing, and a page of it per turn would cost
    /// more than it ever saves.
    /// </summary>
    public static readonly string DraftGuide = $$"""
    # 在这里写脚本

    你现在在 `scripts/draft/`。这个目录你可以写，但写在这里的脚本**不会自己运行**：要 `scripts_reload` 让它被加载，
    再要一次显式的 `scripts_mount` 让它生效。

    **挂载之后它会从这个目录移到 `scripts/live/`**——目录本身就记录了它是否在生效。卸载掉最后一个挂载点，
    它会移回 `scripts/draft/`。`scripts/trusted/` 是系统/用户放的脚本，不在这条移动线上。

    ## 你的脚本是「模型级」的

    脚本能碰多深的接口，**由它住在哪个目录决定，不由 manifest 决定**（manifest 是你能写的文件，所以不能当证据）：
    `draft/` 和 `live/` 是模型级（agent），`trusted/` 和 `builtin/` 是受信级（full）。所以：

    - manifest 的 `capabilities` 里写一个超出你这个等级的（比如 `models.control`），这个脚本**会被拒绝加载**，原因会写在
      `scripts_reload` 的回答里；
    - 想让它拿到更深的能力，人把它挪进 `scripts/trusted/` 再 reload —— 那是人替它背书，不是你能自己做的；
    - 你的脚本也不能把比自己深的脚本挂到事件上。

    ## 除了 ctx，还能问动作表

    ```js
    const all = await ctx.actions.list();          // { ok, level, actions: [{ id, capability, level, reachable, mutating }] }
    const r = await ctx.actions.call("channels.list", {});   // { ok: true, result } 或 { ok: false, error: { code, message } }
    ```

    `call` 走的和控制面同一个入口：同一个能力名、同一次审计。够不着的时候它**返回**一句可读的拒绝（`code: denied`），
    不抛异常——你可以照着改道走。`capabilities` 里没声明的能力，调了也是 denied。

    ## 一个脚本是一个目录

    ```
    scripts/draft/<目录名>/
      manifest.json   描述它是什么
      main.js         它做什么
    ```

    目录名只是方便你自己找；脚本的身份是 manifest 里的 `id`。

    ## manifest.json

    | 字段 | 必填 | 说明 |
    |---|---|---|
    | `id` | 是 | 全库唯一。与已有的重复时你这份会被跳过并报告，不会顶掉别人 |
    | `version` | 是 | 整数，至少 1 |
    | `targetType` | 是 | `adapter` 或 `channel`：挂在哪一类实例上 |
    | `mount` | 是 | 草稿必须是 `manual`。`default` 会被拒绝——能写脚本的东西不能自己接管一类实例 |
    | `events` | 是 | `adapter.envelope`（adapter 收到消息）或 `channel.message.accepted`（频道收到消息） |
    | `entryPoint` | 是 | `main.js` 里的函数名。加载时会真的执行脚本并检查它确实是函数 |
    | `capabilities` | 是 | 你打算用的能力，见下表。越界会被拒绝加载；没声明的能力在运行时会失败 |
    | `trustDomain` | 是 | 必须是 `"trusted"`。写 `builtin` 会被拒绝：那是随程序发布、本地不可改的东西 |
    | `maxStatements` | 否 | 默认 10000，1–1000000 |
    | `timeoutMilliseconds` | 否 | 默认 1000，1–60000 |
    | `memoryLimitBytes` | 否 | 默认 16 MiB，1 MiB–256 MiB |

    ## 能力

    | 能力 | 你可以调用 | 用在哪 |
    |---|---|---|
    | `channel.accept` | `ctx.channel.accept()` | adapter 事件：把这条消息交给认领它的频道 |
    | `channel.route` | `ctx.channel.route(options)` | adapter 事件 |
    | `channel.bound-send` | `ctx.channel.reply(message)` | channel 事件：往这个频道回一句 |
    | `model.complete` | `ctx.model.complete(profileId, request)` | 一次模型调用，无会话、无工具 |
    | `session.run` | `ctx.session.run(spec, text)` | channel 事件：跑一轮会话并拿回结果 |
    | `session.activate` | `ctx.session.activate(spec)` | channel 事件：唤醒会话，不等结果 |
    | `self.adapter.send` | `ctx.self.adapter.send(message)` | adapter 事件 |
    | `self.adapter.poll` | `ctx.self.adapter.poll()` | adapter 事件 |

    不需要声明的两样：`ctx.log.info/warn/error(message, detail)` 和 `ctx.state.get/set/delete(key)`
    （持久记忆，跨重启，按挂载点隔离）。

    能力既是"我打算用什么"，也是"用别的会怎样"：没有声明的能力，调用时会被拒绝并留下一段审计记录。

    ## 沙箱

    脚本在一个解释器里跑，没有 CLR 互操作、没有文件、没有网络、没有 `require`。可用的只有 `event`、`ctx`、
    语言本身和 `JSON`。执行还受 `maxStatements` / `timeoutMilliseconds` / `memoryLimitBytes` 限制——超了是一次失败，
    不是一次等待。写循环之前先想清楚它会不会停下来。

    ## 最小可用例子

    `manifest.json`：

    ```
    {{ExampleManifest}}
    ```

    `main.js`：

    ```
    {{ExampleSource}}
    ```

    这个例子只在消息以 `!hello` 开头时回一句 `hello`，其余什么都不做。

    ## 常见的被拒原因

    - `id` 漏了 → 整个文件被跳过并报告，不会加载。
    - `trustDomain` 写了 `builtin` → 跳过：`builtin` 只属于随程序发布并放在程序目录里的脚本。
    - `mount` 写了 `default` → 拒绝：草稿不能自动挂载。
    - `targetType` 写了具体类型（如 `channel.conversation`）→ 拒绝：这里要的是抽象类型名。
    - `capabilities` 里有表外的东西 → 拒绝加载。
    - `entryPoint` 名字和函数名不一致 → 拒绝：加载时会检查函数是否存在。
    - 事件名拼错 → 拒绝。只有 `adapter.envelope` 和 `channel.message.accepted` 两个。
    - 语法错误 → 拒绝，原因里带解释器的话。

    被拒不会弄坏宿主：那个脚本不会出现，其余的照常运行；原因会在 `scripts_reload` 的返回里、在 `scripts_list` 里，
    以及信号里。

    ## 写完以后

    1. `scripts_reload` 让宿主重读所有文件夹。这时你的脚本出现在 `scripts_list` 里，**但仍未生效**；
       如果它没出现，`scripts_list` 会告诉你为什么。
    2. `instances_list` 查实例地址（可以带 `query` 或 `type` 过滤；返回里还会说每个实例上已经挂了哪些脚本）。
       地址是猜不出来的，别自己编。
    3. `scripts_mount { "script": "<id>", "target": "<实例地址>" }` 把它挂到那个实例上。挂载成功后它会从
       `scripts/draft/` 移到 `scripts/live/`，并应答那个实例的下一个事件。
    4. `scripts_unmount { "script": "<id>", "target": "<实例地址>" }` 停掉它；如果那是它最后一个挂载点，
       它会移回 `scripts/draft/`。

    这几步里，只有 `scripts_mount` 会让它生效。写在草稿里、被加载、出现在列表里，都不等于在生效——
    目录不同就是状态不同。
    """;

    /// <summary>Where the replaced guide is kept, one generation, so a refresh never destroys what was there.</summary>
    public const string GuidePreviousFileName = "GUIDE.previous.md";

    /// <summary>
    /// Keeps the guide in the draft folder current. Returns false when it was already current or could not be written:
    /// the guide is help, not a requirement, and a host that will not start because it could not write a .md file would
    /// be getting its priorities wrong.
    ///
    /// It used to be written once and never again, which made every existing deployment a place where this build's
    /// features were undocumented — the reader was taught an older program. A guide that has fallen behind is worse
    /// than no guide (it teaches a shape that gets refused), so it is refreshed. What it replaces is kept beside it as
    /// <see cref="GuidePreviousFileName"/>: the copy is the program's page, not the reader's record, but neither is
    /// anything thrown away to keep it true.
    /// </summary>
    public async ValueTask<bool> EnsureDraftGuideAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(DraftRoot, GuideFileName);
        try
        {
            Directory.CreateDirectory(DraftRoot);
            if (File.Exists(path))
            {
                var current = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (string.Equals(current, DraftGuide, StringComparison.Ordinal)) return false;
                await File.WriteAllTextAsync(Path.Combine(DraftRoot, GuidePreviousFileName), current, cancellationToken)
                    .ConfigureAwait(false);
            }
            await File.WriteAllTextAsync(path, DraftGuide, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
