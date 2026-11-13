using System.Text.Json;
using System.Text.Json.Serialization;

namespace ALN.Contracts;

public enum ActorKind { User = 0, Script = 1, System = 2, Model = 3 }

/// <summary>
/// How deep an interface sits, and how deep a caller is trusted. An <b>ordered</b> value, not a flag: a caller
/// reaches an interface when its level is at least the interface's level, so "what may the model touch" and "what
/// does this verb need" are two numbers compared once, rather than a list somebody keeps in step by hand.
///
/// The names are the roles, not numbers, because the question a refusal answers is a question about people: an
/// interface that says <see cref="Operator"/> is one an operator has to come and run.
/// </summary>
public enum AccessLevel
{
    /// <summary>The model itself, and everything it made: its scripts, the sessions it runs.</summary>
    Agent = 0,

    /// <summary>A person driving this process on purpose — the API, the web UI, a console session.</summary>
    Operator = 1,

    /// <summary>Inside the process: host code, built-in modules, and scripts a person vouched for by hand.</summary>
    Full = 2
}

/// <summary>
/// Which level each interface sits at. It is a table in code and nothing else: there is deliberately no action that
/// edits it, because an interface that said "you may raise levels" would be the only level that mattered.
/// Changing this table is a change to the program (a recompile), and per script a re-mount; a caller's own level is
/// the moving part, and it moves through <c>sessions.setLevel</c> without a restart.
///
/// An interface nobody registered is <see cref="AccessLevel.Full"/>: a verb that did not say how deep it is has not
/// said it is shallow, and guessing shallow is the one direction that cannot be taken back.
/// </summary>
public static class AccessLevels
{
    private static readonly Dictionary<string, AccessLevel> Table = new(StringComparer.Ordinal)
    {
        // Reading, saying, and the session's own line of work: a model is an operator's assistant, and this is the
        // part of operating that is its job.
        [Capabilities.SystemRead] = AccessLevel.Agent,
        [Capabilities.AdaptersRead] = AccessLevel.Agent,
        [Capabilities.ChannelsRead] = AccessLevel.Agent,
        [Capabilities.MessagesWrite] = AccessLevel.Agent,
        [Capabilities.SessionsRead] = AccessLevel.Agent,
        // Handing a Session a message is how work gets asked for, including upwards: a shallow component may ask a
        // deeper one. Recorded, attributed, and carrying no authority — see SessionRelay.
        [Capabilities.SessionsWrite] = AccessLevel.Agent,
        // 2026-11-12 改，原为 operator。这一格管的是"这个会话被信多深"，看起来最该由人说了算；但真正保护这条的
        // 不是这里的数字，而是控制面那一个点上的委派不变式（只许授出 ≤ 自己的等级，AssignsLevel）。降下来之后，
        // 一个 agent 会话能给自己造一个同级会话、能调整同级的别人，却依然授不出比它自己更深的任何东西。
        [Capabilities.SessionsControl] = AccessLevel.Agent,
        [Capabilities.ModelsRead] = AccessLevel.Agent,
        [Capabilities.SignalsRead] = AccessLevel.Agent,
        [Capabilities.ParamsRead] = AccessLevel.Agent,
        [Capabilities.ArchiveRead] = AccessLevel.Agent,
        [Capabilities.PromptsRead] = AccessLevel.Agent,
        [Capabilities.ModulesRead] = AccessLevel.Agent,
        [Capabilities.GraphRead] = AccessLevel.Agent,
        // Publishing a draft is the model's own work: the folders it can write are the folders a reload can pick up.
        [Capabilities.ScriptsPublish] = AccessLevel.Agent,
        // Reading the schedules is answering "when does this wake up"; writing one decides when the host acts alone.
        // 2026-11-12 改，写的那一半从 operator 降到 agent：一条定时规则是可读、可删、可改的数据，出事看得见；
        // 而它要能干出危险的事，得先有别的动词把那些事变得可能——闸门在动作上，不该在日程上再设一道。
        [Capabilities.SchedulesRead] = AccessLevel.Agent,
        [Capabilities.SchedulesControl] = AccessLevel.Agent,
        // Where this process may reach: reading the list is answering a question the model is asked.
        // 2026-11-12 改，改的那一半也降到 agent：`net.request` 早就是 agent 级的裸门（下面的理由仍然成立），
        // 那么"把这次要用的地址和凭据记下来"不比"现在就 POST 过去"更深。留一道 operator 只会让模型每次
        // 重新问一遍，而答案它本来就有。
        [Capabilities.NetRead] = AccessLevel.Agent,
        [Capabilities.NetControl] = AccessLevel.Agent,
        // The raw door: an address the caller names, no allowlist in front of it. Agent level because reading a page
        // and calling an endpoint are the same act, and the audit line is what stands in for the missing list.
        [Capabilities.NetRequest] = AccessLevel.Agent,

        // Changing the shape of the process.
        //
        // 2026-11-12 改，这一整族（适配器、频道、模型、参数、提示词、归档、图、模块）从 operator 降到 agent，
        // 原条文是"这是人的决定，按下去要写在审计里"。两件事分开看了：改的是**形状**，而形状本身已经是可逆的
        // ——销毁走回收站（C-25），包能换回旧版本，等级授不出比自己深的（委派不变式），审计一条没少（C-29）。
        // 于是"谁能改"这个问题不再是安全边界，剩下的只是"改坏了谁修"；对这一层，把一整族关起来换来的安全感
        // 比它挡掉的能力更贵。真正留住的例外是三条**机制级**的：换代码（packages.*，operator）、内核的图手术
        // （modules.load / modules.unload，operator）、远端执行（ssh 工具，operator）。例外写在各自的描述符上，
        // 不写在这里——一处是"这个动词危险"，另一处是"这一族不属于你"，混起来就再也读不出区别了。
        [Capabilities.AdaptersControl] = AccessLevel.Agent,
        // Reachable at agent level because the *verb's* depth is per-instance data (rawActions); the handler compares the
        // caller against that. Registering it deeper would make "the model may call get_group_list" impossible to say.
        [Capabilities.AdaptersRaw] = AccessLevel.Agent,
        [Capabilities.ChannelsControl] = AccessLevel.Agent,
        [Capabilities.ModelsControl] = AccessLevel.Agent,
        [Capabilities.ParamsWrite] = AccessLevel.Agent,
        [Capabilities.PromptsWrite] = AccessLevel.Agent,
        // Asking who somebody is costs nothing and answers a question the model would otherwise guess at.
        [Capabilities.IdentityRead] = AccessLevel.Agent,
        // Keeping the cards up to date is the model's own bookkeeping, deliberately at the same depth as reading them:
        // what it may not touch is the level, and that lives behind IdentityControl below (docs/31 §7.1).
        [Capabilities.IdentityWrite] = AccessLevel.Agent,
        // 唯一没有跟着降的一格，也是唯一一格"不是改进程、而是决定以后谁的话算数"的：给人定级。
        // 升一个陌生人的等级，等于把指令系统交给他；而这件事没有"问自己人"的机制可以兜底——会话可以提别人，
        // 那就等于一条群消息能把一个人提拔成 operator。所以它留在人这边。
        [Capabilities.IdentityControl] = AccessLevel.Operator,
        [Capabilities.ArchiveControl] = AccessLevel.Agent,
        [Capabilities.ModulesControl] = AccessLevel.Agent,
        [Capabilities.GraphControl] = AccessLevel.Agent,

        // What a mounted script may reach through its ctx. The names are spelled here rather than referenced because
        // ScriptCapabilities lives one assembly up; a test holds the two lists to each other.
        // Driving a session, answering, and calling a model are the script's own line of work.
        ["session.run"] = AccessLevel.Agent,
        ["session.activate"] = AccessLevel.Agent,
        // Making a Session for a conversation, and handing one a message. Both are agent-level for the same reason:
        // the inward door is how a shallow component asks a deeper one for something, and a door that only opened
        // downward would not be a request path, it would be a broadcast.
        ["session.open"] = AccessLevel.Agent,
        ["session.message"] = AccessLevel.Agent,
        ["model.complete"] = AccessLevel.Agent,
        ["self.adapter.send"] = AccessLevel.Agent,
        ["self.adapter.poll"] = AccessLevel.Agent,
        ["channel.bound-send"] = AccessLevel.Agent,
        // Wire a source into a Channel, and send a bound reply. 2026-11-12 改，前两个降到 agent：
        // 频道本身现在就是 agent 能造能改的（channels.control），再说"接线是部署的事"就没有对象了——
        // 造一个频道就等于接一次线，闸门留在这两格上只会让模型在造完频道之后卡在同一个动作的第二半。
        ["channel.accept"] = AccessLevel.Agent,
        ["channel.route"] = AccessLevel.Agent
    };

    /// <summary>The level an interface sits at. Unregistered is <see cref="AccessLevel.Full"/>, never Agent.</summary>
    public static AccessLevel Of(string? capability) =>
        capability is not null && Table.TryGetValue(capability, out var level) ? level : AccessLevel.Full;

    /// <summary>Every interface this table knows, so a test can prove no constant was forgotten.</summary>
    public static IReadOnlyCollection<string> Known => Table.Keys;

    /// <summary>"L1 operator" — the spelling a refusal and the action list both use, so they agree.</summary>
    public static string Describe(AccessLevel level) => $"L{(int)level} {level.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Who is invoking an action. A script carries its mount identity; a user carries an account id.
/// Capabilities are granted by whoever authenticated the actor, never by the caller's payload.
/// </summary>
public sealed record Actor(string Id, ActorKind Kind, IReadOnlyList<string> Capabilities,
    AccessLevel Level = AccessLevel.Agent)
{
    public const string Any = "*";

    public static Actor System { get; } = new("system", ActorKind.System, [Any], AccessLevel.Full);

    /// <summary>
    /// Whether this actor was granted the capability at all — the first half of the question, and the one that
    /// answers "is this your kind of work".
    /// </summary>
    public bool IsGranted(string capability)
    {
        if (string.IsNullOrEmpty(capability)) return false;
        foreach (var granted in Capabilities)
        {
            if (Matches(granted, capability)) return true;
            // graph.read / graph.control keep working for the release the action names are being forwarded for: the
            // actions those grants guarded are modules.* now, and a role file that says "graph.control" must not
            // quietly lose the right to mount. One-way on purpose — a new grant cannot buy an old name.
            if (FormerName(capability) is { } former && Matches(granted, former)) return true;
        }
        return false;
    }

    /// <summary>
    /// Reaching an interface takes two answers, and they are not the same question: the grant says "this is your kind
    /// of work", the level says "you are trusted this deep". A model that was granted everything still does not reach
    /// a kernel interface, and an operator trusted with everything still needs the grant. Either half alone is a hole:
    /// a grant list kept by hand drifts, and a level with no grant turns "operator" into "master key".
    /// </summary>
    public bool Allows(string capability) =>
        IsGranted(capability) && Level >= AccessLevels.Of(capability);

    /// <summary>The capability a modules.* name was called before the graph grew modules.</summary>
    private static string? FormerName(string capability) =>
        capability.StartsWith("modules.", StringComparison.Ordinal)
            ? "graph." + capability["modules.".Length..]
            : null;

    /// <summary>
    /// One '*' anywhere: "adapters.*", "*.read" or "*". Anything richer invites patterns nobody
    /// can reason about, so it is not supported and simply fails to match.
    /// </summary>
    private static bool Matches(string pattern, string value)
    {
        var star = pattern.IndexOf('*');
        if (star < 0) return string.Equals(pattern, value, StringComparison.Ordinal);
        if (pattern.IndexOf('*', star + 1) >= 0) return false;
        var prefix = pattern[..star];
        var suffix = pattern[(star + 1)..];
        return value.Length >= prefix.Length + suffix.Length &&
               value.StartsWith(prefix, StringComparison.Ordinal) &&
               value.EndsWith(suffix, StringComparison.Ordinal);
    }
}

/// <summary>
/// One externally reachable action. This is the only thing that crosses the boundary: internal
/// managers stay free to change, the catalog is the contract.
/// </summary>
/// <param name="ParamsJson">
/// The arguments this action takes, as a JSON Schema object — the same shape a model tool carries. It used to be
/// missing, and the only description of an action's arguments was prose inside <paramref name="Summary"/>; a caller
/// that cannot see the fields guesses them, and guessing a field name is the most common way a call fails for a
/// reason that has nothing to do with the call.
/// </param>
/// <param name="Subject">
/// Which argument names the thing being acted on (<c>id</c>, <c>address</c>, <c>script</c>…). Recorded instead of the
/// whole argument object: "who changed what" has to be answerable afterwards, and an argument object is the one
/// place a credential would end up in a log (<c>models.provider.save</c> carries a key).
/// </param>
/// <param name="MinLevel">
/// The level this verb needs, when it is not the one its capability sits at. Most verbs are their family's level;
/// <c>modules.edge.mount</c> is a model's business and <c>modules.load</c> is a person's, and both hang off
/// <c>modules.control</c>.
/// </param>
public sealed record ActionDescriptor(string Id, string Summary, string Capability, bool Mutating,
    string? ParamsJson = null, string? Subject = null, AccessLevel? MinLevel = null, string? AssignsLevel = null)
{
    /// <summary>The level a caller must be at: the table answers for the family, this answers for the verb.</summary>
    public AccessLevel RequiredLevel => MinLevel ?? AccessLevels.Of(Capability);
}

/// <summary>
/// Writes a level as its name and reads either the name or the number. A security setting that is a bare integer in a
/// file a person opens is a setting somebody will get wrong: "3" and "1" look equally ordinary.
/// </summary>
public sealed class AccessLevelJson() : JsonStringEnumConverter<AccessLevel>(JsonNamingPolicy.CamelCase);

/// <summary>
/// Single execution point for every front-end (scripts today, the HTTP API next). Authorization,
/// audit and signals happen here once, so no front-end can forget one of them.
/// </summary>
public interface IControlPlane
{
    IReadOnlyList<ActionDescriptor> Catalog { get; }

    /// <summary>
    /// Adds one action to the table. A module registers the verbs it implements, so "who owns this verb" is a fact
    /// about the module rather than a list in the host; the host still owns the execution point, and nothing can
    /// register an action without going through it.
    /// </summary>
    Result Register(ActionDescriptor descriptor, ActionHandler handler);

    /// <summary>
    /// Registers a verb that needs to know who is calling it. Same table, same authorization, same record — the only
    /// difference is that the body receives the actor the plane already built and authorized.
    /// </summary>
    Result Register(ActionDescriptor descriptor, AuthorizedActionHandler handler);

    ValueTask<Result<JsonElement>> InvokeAsync(Actor actor, string action, JsonElement arguments,
        CancellationToken cancellationToken);
}

/// <summary>A registered action body. Arguments are untrusted JSON; validate them here, not in transport.</summary>
public delegate ValueTask<Result<JsonElement>> ActionHandler(JsonElement arguments, CancellationToken cancellationToken);

/// <summary>
/// The same, for a verb whose <b>effect</b> depends on who is calling — a mount, whose result hands a script the events
/// of an instance. A handler that has to compare the caller's level with something else cannot do it from arguments
/// alone, which are only what the caller says about itself.
/// </summary>
public delegate ValueTask<Result<JsonElement>> AuthorizedActionHandler(Actor actor, JsonElement arguments,
    CancellationToken cancellationToken);

/// <summary>Well-known capability names. Roles map onto these; actions name exactly one.</summary>
public static class Capabilities
{
    public const string SystemRead = "system.read";
    public const string AdaptersRead = "adapters.read";
    public const string AdaptersControl = "adapters.control";
    /// <summary>
    /// The platform's own operations, passed through. Agent-level on purpose: the depth of a <i>verb</i> is the
    /// instance's own data (<c>rawActions</c>), so the handler is where that is decided — one place, per verb, written by
    /// a person. The action itself has to be reachable or that decision could never say "the model may call this one".
    /// </summary>
    public const string AdaptersRaw = "adapters.raw";
    public const string ChannelsRead = "channels.read";
    public const string ChannelsControl = "channels.control";
    public const string MessagesWrite = "messages.write";
    public const string GraphRead = "graph.read";
    public const string GraphControl = "graph.control";
    /// <summary>The module graph: which instances exist, and the edges between them (formerly graph.read).</summary>
    public const string ModulesRead = "modules.read";
    /// <summary>Changing the graph: load, unload, mount, unmount (formerly graph.control).</summary>
    public const string ModulesControl = "modules.control";
    public const string ParamsRead = "params.read";
    public const string ParamsWrite = "params.write";
    public const string ModelsRead = "models.read";
    /// <summary>Changing which provider or profile exists, or what a provider is configured with. Not a read.</summary>
    public const string ModelsControl = "models.control";
    public const string SignalsRead = "signals.read";
    public const string SessionsRead = "sessions.read";
    /// <summary>
    /// Handing one Session a message from somewhere else — a script, another Session, the operator. Not the same verb
    /// as <see cref="MessagesWrite"/>: that one speaks outward through a Channel, this one speaks <i>inward</i>, into a
    /// Session's own timeline, and it is deliberately allowed at every level in both directions.
    /// </summary>
    public const string SessionsWrite = "sessions.write";
    /// <summary>Changing what a Session is allowed to reach: its level. Never the level of an interface.</summary>
    public const string SessionsControl = "sessions.control";
    public const string ArchiveRead = "archive.read";
    public const string ArchiveControl = "archive.control";
    public const string PromptsRead = "prompts.read";
    /// Editing a prompt changes what the next turn says. A viewer with prompts.read may look, not rewrite.
    public const string PromptsWrite = "prompts.write";

    /// <summary>
    /// Who the people in a conversation are. A read, because "who is this" is a question an assistant should answer
    /// rather than guess from a nickname — the whole point of the identity table (docs/31).
    /// </summary>
    public const string IdentityRead = "identity.read";

    /// <summary>
    /// Keeping the table up to date: who somebody is, what he is called here, which accounts are his. Agent level on
    /// purpose — a person's own card is exactly the kind of thing an assistant is expected to maintain while talking to
    /// him, and the one field that would matter, <c>level</c>, is not in this family's reach at all (docs/31 §7.1).
    /// </summary>
    public const string IdentityWrite = "identity.write";

    /// <summary>
    /// What a card's <c>level</c> is, and reading the folder again. Operator level: the first is the number the
    /// instruction system will trust, and the second decides which accounts this process believes belong to whom.
    /// </summary>
    public const string IdentityControl = "identity.control";

    /// <summary>
    /// Asking the folders to be read again. Deliberately not <see cref="GraphControl"/>: a reload applies what is
    /// already on disk, and the only place a model can write is <c>scripts/draft</c>, so the verb can publish the
    /// model's own work and nothing else. Switching a script off, which does change what runs, stays at
    /// <see cref="GraphControl"/>.
    /// </summary>
    public const string ScriptsPublish = "scripts.publish";

    /// <summary>
    /// Seeing what this host will do on its own: the stored schedules. A read, because "when does this thing wake up"
    /// is a question an assistant should be able to answer rather than guess.
    /// </summary>
    public const string SchedulesRead = "schedules.read";

    /// <summary>
    /// Writing a schedule down. Deeper than reading it: a row decides *when* something happens without anybody present,
    /// which is a decision about the deployment rather than about one conversation.
    /// </summary>
    public const string SchedulesControl = "schedules.control";

    /// <summary>
    /// Seeing where this process may reach. A read, because "can you read that page" is a question an assistant should
    /// be able to answer rather than guess — and the names it answers with are the ones it is allowed to use.
    /// </summary>
    public const string NetRead = "net.read";

    /// <summary>
    /// Changing the list of places this process may reach, or the credential one of them uses. Deeper than reading it,
    /// for the reason every control is: an allowlist somebody can extend from a conversation is not an allowlist, and
    /// what travels through these targets leaves this machine.
    /// </summary>
    /// <summary>
    /// Changing the list of places this process may reach, or the credential one of them uses. Deeper than reading it,
    /// for the reason every control is: an allowlist somebody can extend from a conversation is not an allowlist, and
    /// what travels through these targets leaves this machine.
    /// </summary>
    public const string NetControl = "net.control";

    /// <summary>
    /// Sending one HTTP request to an address the caller names — the raw door, not the curated one.
    ///
    /// Agent level, deliberately: "fetch this page" and "call this endpoint" are the same shape of act, and a verb
    /// the model cannot use is a verb that only a person can use — which is what <c>net.control</c> is for. What
    /// differs from <see cref="NetRead"/> is that nothing here is pre-approved, so the record is the control.
    /// </summary>
    public const string NetRequest = "net.request";
}
