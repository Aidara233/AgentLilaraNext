using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Rice;

/// <summary>
/// The limit itself: one bowl per round, and a refusal when the larder is empty.
///
/// A refusal is not a broken Session. <see cref="IRoundAdmission"/> closes the turn normally and the note becomes the
/// last thing said, so the next message wakes the Session exactly as before — a conversation is not stopped because
/// the wallet ran out.
/// </summary>
public sealed class RiceAdmission(RiceLedger ledger) : IRoundAdmission
{
    public const string BuyHint = "买饭：coin_buy rice-bowl（2 枚硬币补 100 口），或等人 //rice topup。";

    public string Id => "rice.admission";

    public async ValueTask<Result<RoundAdmission>> AdmitAsync(RoundAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        var charged = await ledger.ChargeRoundAsync(request.SessionId, request.StepNumber, request.Channel,
                // A request that carries no instant is a caller from before the question existed: it pays the way it
                // always did rather than claiming an allowance it cannot prove is still running.
                request.Now == default ? null : request.Now, cancellationToken)
            .ConfigureAwait(false);

        // The larder is a meter, not a gate: running dry must not stop a conversation. An empty larder goes
        // negative and the next purchase pays the debt back.
        _ = charged;
        return Result<RoundAdmission>.Success(RoundAdmission.Yes);
    }
}

/// <summary>What the model can see about its own rations. Free, like every "am I allowed to act" question.</summary>
public sealed class RiceTools(RiceLedger ledger, TimeProvider? time = null) : ISessionToolSource
{
    public const string Wallet = "rice_wallet";

    public string Id => "rice.tools";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ModelToolDefinition Definition = new(Wallet,
        "看看还能吃几顿：白饭按轮数算，每运行一轮消耗 1 口。有免单时会一并说明本频道还能免几轮。免费。",
        """{"type":"object","properties":{}}""");

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionToolOffer> offers =
            [new(new Tool(Definition, (call, ct) => ShowAsync(request, ct)), Definition, ActsExternally: false)];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private async ValueTask<Result<SessionToolResult>> ShowAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.Error is not null || read.Value is null)
            return Result<SessionToolResult>.Success(new SessionToolResult(
                JsonSerializer.Serialize(new { error = read.Error?.Message ?? "读不到米缸。" }, Json), null));

        var state = read.Value;
        var session = string.IsNullOrWhiteSpace(request.SessionId) ? "session" : request.SessionId;
        var now = (time ?? TimeProvider.System).GetLocalNow();
        return Result<SessionToolResult>.Success(new SessionToolResult(JsonSerializer.Serialize(new
        {
            bites = state.Bites,
            rounds = state.Bites,
            thisSessionAtRound = state.ChargedUpTo(session),
            perRound = 1,
            bitesPerBowl = RiceUnits.BitesPerBowl,
            // What this conversation can still spend without touching the larder. Kept apart from `rounds` instead of
            // being added into it: 86 bites plus 43 free rounds is not 129 of anything, and a model that read it that
            // way would plan for a turn count nobody promised.
            freeRoundsHere = state.Allowances.Where(entry => entry.Covers(request.Channel, now))
                .Sum(entry => entry.Remaining),
            waivers = state.Allowances
                .Where(entry => entry.Remaining > 0 && entry.Until > now)
                .Select(entry => new
                {
                    id = entry.Id,
                    channel = entry.Channel,
                    remaining = entry.Remaining,
                    until = entry.Until,
                    reason = entry.Reason
                })
                .ToArray()
        }, Json), null));
    }

    private sealed class Tool(ModelToolDefinition definition,
        Func<ModelToolCall, CancellationToken, ValueTask<Result<SessionToolResult>>> body) : ISessionTool
    {
        public string Name => definition.Name;
        public bool EndsTurn => false;
        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken) =>
            body(call, cancellationToken);
    }
}

/// <summary>One line every round: rations are only manageable if they are visible.</summary>
public sealed class RiceContextSource(RiceLedger ledger, TimeProvider? time = null) : IFloatingContextSource
{
    public string Id => "rice.context";

    public async ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        // A larder nobody can read is not worth a broken round: the line simply does not appear this round.
        if (read.Error is not null || read.Value is null)
            return Result<IReadOnlyList<RoundContextContribution>>.Success([]);

        var state = read.Value;
        // What is free here, in one clause, and only when there is something to say: this line is read every round, and
        // "免单剩 0 轮" repeating on every turn of the day is noise that makes the number that matters harder to see.
        var now = (time ?? TimeProvider.System).GetLocalNow();
        var free = state.Allowances.Where(entry => entry.Covers(request.Channel, now))
            .Sum(entry => entry.Remaining);
        var line = $"白饭：{RiceUnits.Describe(state.Bites)} · 每轮 1 口" +
                   (free > 0 ? $" · 本频道免单剩 {free} 轮" : "");
        return Result<IReadOnlyList<RoundContextContribution>>.Success(
            [new RoundContextContribution("rice.wallet", "rice", line)]);
    }
}

/// <summary>
/// The two verbs that move bites, and nothing else uses them. Their capability is <c>rice.control</c>, which is not in
/// <see cref="AccessLevels"/> and therefore answers <c>full</c>: a shop script may fill the larder exactly when it
/// could already do anything else, and a model cannot feed itself.
/// </summary>
public static class RiceActions
{
    public const string Add = "rice.add";
    public const string Take = "rice.take";
    public const string Waive = "rice.waive";
    public const string Unwaive = "rice.unwaive";
    public const string Control = "rice.control";

    private const string ParamsJson = """
        {"type":"object","properties":{
          "bites":{"type":"integer","description":"多少口，必须大于 0"},
          "reason":{"type":"string","description":"一句话说明为什么动米缸，会进审计"}
        },"required":["bites","reason"]}
        """;

    private const string WaiveParamsJson = """
        {"type":"object","properties":{
          "bites":{"type":"integer","description":"这次免单能抵多少轮。每轮 1 口，所以它也就是多少口"},
          "channel":{"type":"string","description":"只给这个频道免单，写实例地址，如 channel.conversation#qq-group-616565017。不写就是所有频道"},
          "until":{"type":"string","description":"到什么时候为止，带时区的 ISO8601，如 2026-11-10T23:59:59+08:00。不写就是今天 24 点（本地时区）"},
          "reason":{"type":"string","description":"一句话说明为什么免单，会进审计"}
        },"required":["bites","reason"]}
        """;

    private const string UnwaiveParamsJson = """
        {"type":"object","properties":{
          "id":{"type":"string","description":"要撤销的豁免 id"}
        },"required":["id"]}
        """;

    public static Result Register(IControlPlane plane, RiceLedger ledger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(plane);

        var add = plane.Register(
            new ActionDescriptor(Add, "往米缸里补饭（商店脚本走这里）", Control, Mutating: true,
                ParamsJson: ParamsJson, Subject: "bites"),
            async (actor, arguments, cancellationToken) =>
            {
                var (bites, reason, error) = Read(arguments);
                if (error is not null) return Result<JsonElement>.Failure(error);
                var added = await ledger.AddAsync(bites, $"script:{reason}", actor.Id, cancellationToken)
                    .ConfigureAwait(false);
                return added.Ok && added.Value is not null
                    ? Result<JsonElement>.Success(JsonSerializer.SerializeToElement(
                        new { bites = added.Value.Bites, revision = added.Value.Revision }))
                    : Result<JsonElement>.Failure(added.Error!);
            });
        if (!add.Ok) return add;

        var take = plane.Register(
            new ActionDescriptor(Take, "从米缸里拿走饭（要 full）", Control, Mutating: true,
                ParamsJson: ParamsJson, Subject: "bites"),
            async (actor, arguments, cancellationToken) =>
            {
                var (bites, reason, error) = Read(arguments);
                if (error is not null) return Result<JsonElement>.Failure(error);
                var taken = await ledger.TakeAsync(bites, $"script:{reason}", actor.Id, cancellationToken)
                    .ConfigureAwait(false);
                return taken.Ok && taken.Value is not null
                    ? Result<JsonElement>.Success(JsonSerializer.SerializeToElement(
                        new { bites = taken.Value.Bites, revision = taken.Value.Revision }))
                    : Result<JsonElement>.Failure(taken.Error!);
            });
        if (!take.Ok) return take;

        var waive = plane.Register(
            new ActionDescriptor(Waive, "让接下来 N 轮不扣米缸：今日免单，或者一笔临时白饭", Control, Mutating: true,
                ParamsJson: WaiveParamsJson, Subject: "bites"),
            async (actor, arguments, cancellationToken) =>
            {
                var now = (time ?? TimeProvider.System).GetLocalNow();
                var bites = arguments.TryGetProperty("bites", out var amount) ? amount.GetInt64() : 0;
                var reason = arguments.TryGetProperty("reason", out var why) ? why.GetString() : null;

                DateTimeOffset? until = null;
                if (arguments.TryGetProperty("until", out var untilElement) &&
                    untilElement.ValueKind == JsonValueKind.String)
                {
                    if (!DateTimeOffset.TryParse(untilElement.GetString(), out var parsed))
                        return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                            "until 要写成带时区的 ISO8601，例如 2026-11-10T23:59:59+08:00。"));
                    until = parsed;
                }

                var channel = arguments.TryGetProperty("channel", out var channelElement) &&
                              channelElement.ValueKind == JsonValueKind.String
                    ? channelElement.GetString()
                    : null;

                var made = MakeWaiver(bites, channel, until, reason, now);
                if (!made.Ok) return Result<JsonElement>.Failure(made.Error!);

                var granted = await ledger.GrantWaiverAsync(made.Value!, actor.Id, cancellationToken)
                    .ConfigureAwait(false);
                return granted.Ok && granted.Value is not null
                    ? Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new
                    {
                        id = made.Value!.Id,
                        channel = made.Value.Channel,
                        remaining = made.Value.Remaining,
                        until = made.Value.Until
                    }))
                    : Result<JsonElement>.Failure(granted.Error!);
            });
        if (!waive.Ok) return waive;

        var unwaive = plane.Register(
            new ActionDescriptor(Unwaive, "撤销一条免单（要 full）", Control, Mutating: true,
                ParamsJson: UnwaiveParamsJson, Subject: "id"),
            async (actor, arguments, cancellationToken) =>
            {
                var id = arguments.TryGetProperty("id", out var named) ? named.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "id 不能为空。"));
                var revoked = await ledger.RevokeWaiverAsync(id!, actor.Id, cancellationToken).ConfigureAwait(false);
                return revoked.Ok && revoked.Value is not null
                    ? Result<JsonElement>.Success(JsonSerializer.SerializeToElement(new { revoked = id }))
                    : Result<JsonElement>.Failure(revoked.Error!);
            });
        return unwaive.Ok ? Result.Success() : unwaive;
    }

    /// <summary>
    /// One allowance, built and checked in one place, because two callers write them (the <c>rice.waive</c> action and
    /// <c>//rice waive</c>) and a rule that lived in two places would eventually be two rules.
    ///
    /// <paramref name="until"/> defaults to the end of the day <paramref name="now"/> falls in, in <paramref name="now"/>'s
    /// own offset: 今日 is a local idea, and taking the offset from the caller's instant is what keeps it local instead
    /// of quietly turning it into UTC midnight.
    /// </summary>
    public static Result<RiceWaiver> MakeWaiver(long bites, string? channel, DateTimeOffset? until, string? reason,
        DateTimeOffset now)
    {
        if (bites <= 0) return Result<RiceWaiver>.Failure(new(ErrorCode.Invalid, "bites 必须是大于 0 的整数。"));
        if (string.IsNullOrWhiteSpace(reason)) return Result<RiceWaiver>.Failure(new(ErrorCode.Invalid, "reason 不能为空。"));

        string? named = null;
        if (!string.IsNullOrWhiteSpace(channel))
        {
            if (!Address.TryParse(channel, out var parsed) || !parsed.IsInstance)
                return Result<RiceWaiver>.Failure(new(ErrorCode.Invalid,
                    $"{channel} 不是实例地址。写成 channel.conversation#<名字>，或者不写这个参数（就是所有频道）。"));
            named = parsed.ToString();
        }

        // Same day, same offset: the last instant of 今日 rather than the first of tomorrow, so "until midnight" and
        // "all of today" are the same promise.
        var deadline = until ?? new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddDays(1);
        var id = "w-" + Guid.NewGuid().ToString("N")[..8];
        return Result<RiceWaiver>.Success(new RiceWaiver(id, named, bites, deadline, reason!.Trim(), now));
    }

    private static (long Bites, string Reason, Error? Error) Read(JsonElement arguments)
    {
        var bites = arguments.TryGetProperty("bites", out var amount) ? amount.GetInt64() : 0;
        var reason = arguments.TryGetProperty("reason", out var why) ? why.GetString() : null;
        if (bites <= 0) return (0, string.Empty, new(ErrorCode.Invalid, "bites 必须是大于 0 的整数。"));
        if (string.IsNullOrWhiteSpace(reason)) return (0, string.Empty, new(ErrorCode.Invalid, "reason 不能为空。"));
        return (bites, reason!, null);
    }
}

/// <summary>
/// The human side: <c>//rice</c> to look (agent), <c>//rice topup</c> to print food (full — filling the larder by hand
/// is the same kind of act as topping up the purse).
/// </summary>
public static class RiceService
{
    public static Result Install(ISessionExtensionRegistry extensions, ICommandRegistry commands, IControlPlane plane,
        RiceLedger ledger, TimeProvider? time = null)
    {
        var verbs = RiceActions.Register(plane, ledger, time);
        if (!verbs.Ok) return verbs;
        var admission = extensions.Register(new RiceAdmission(ledger));
        if (!admission.Ok) return admission;
        var tools = extensions.Register(new RiceTools(ledger, time));
        if (!tools.Ok) return tools;
        var context = extensions.Register(new RiceContextSource(ledger, time));
        if (!context.Ok) return context;

        CommandNode[] nodes =
        [
            new("rice", "米缸：白饭按轮算（//rice <子指令>）", AccessLevel.Agent),
            new("rice.wallet", "还剩几口", AccessLevel.Agent, Body: async (context, cancellationToken) =>
            {
                var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (read.Error is not null || read.Value is null)
                    return Result<string>.Failure(read.Error ?? new(ErrorCode.Unavailable, "读不到米缸。"));
                var now = (time ?? TimeProvider.System).GetLocalNow();
                var free = read.Value.Allowances.Where(entry => entry.Remaining > 0 && entry.Until > now).ToArray();
                return Result<string>.Success(
                    $"白饭：{RiceUnits.Describe(read.Value.Bites)} · 每轮 1 口 · 第 {read.Value.Revision} 版" +
                    (free.Length > 0
                        ? $" · 免单 {free.Length} 条共 {free.Sum(entry => entry.Remaining)} 轮（//rice waivers）"
                        : ""));
            }),
            new("rice.topup", "补饭（要 full）", AccessLevel.Full, Usage: "//rice topup <口数>",
                Body: async (context, cancellationToken) =>
                {
                    if (context.Arguments.Count == 0 || !long.TryParse(context.Arguments[0], out var bites))
                        return Result<string>.Failure(new(ErrorCode.Invalid, "用法：//rice topup <口数>"));
                    var added = await ledger.AddAsync(bites, "topup", context.Actor.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (!added.Ok || added.Value is null)
                        return Result<string>.Failure(added.Error ?? new(ErrorCode.Unavailable, "没补上。"));
                    return Result<string>.Success($"补了 {bites} 口，现在有 {added.Value.Bites} 口。");
                }),
            // A waiver is not a top-up: nothing is added to the larder, the next N rounds simply do not take anything
            // out of it. That is what makes "今日免单" and "今天的额外 500 口" the same statement (docs/34 §10).
            new("rice.waive", "给接下来 N 轮免单（要 full）", AccessLevel.Full,
                Usage: "//rice waive <口数> [频道地址]",
                Body: async (context, cancellationToken) =>
                {
                    if (context.Arguments.Count == 0 || !long.TryParse(context.Arguments[0], out var bites))
                        return Result<string>.Failure(new(ErrorCode.Invalid, "用法：//rice waive <口数> [频道地址]"));

                    var made = RiceActions.MakeWaiver(bites,
                        context.Arguments.Count > 1 ? context.Arguments[1] : null, until: null, reason: "//rice waive",
                        (time ?? TimeProvider.System).GetLocalNow());
                    if (!made.Ok || made.Value is null)
                        return Result<string>.Failure(made.Error ?? new(ErrorCode.Invalid, "没建成。"));

                    var granted = await ledger.GrantWaiverAsync(made.Value, context.Actor.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (!granted.Ok || granted.Value is null)
                        return Result<string>.Failure(granted.Error ?? new(ErrorCode.Unavailable, "没记上。"));
                    return Result<string>.Success($"免单 {made.Value.Remaining} 轮" +
                        (made.Value.Channel is { } only ? $"（只有 {only}）" : "（所有频道）") +
                        $"，到 {made.Value.Until:yyyy-MM-dd HH:mm zzz} 为止。id：{made.Value.Id}");
                }),
            new("rice.unwaive", "撤销一条免单（要 full）", AccessLevel.Full, Usage: "//rice unwaive <id>",
                Body: async (context, cancellationToken) =>
                {
                    if (context.Arguments.Count == 0)
                        return Result<string>.Failure(new(ErrorCode.Invalid, "用法：//rice unwaive <id>"));
                    var revoked = await ledger.RevokeWaiverAsync(context.Arguments[0], context.Actor.Id, cancellationToken)
                        .ConfigureAwait(false);
                    return revoked.Ok
                        ? Result<string>.Success($"撤销了 {context.Arguments[0]}。")
                        : Result<string>.Failure(revoked.Error ?? new(ErrorCode.Unavailable, "没撤销。"));
                }),
            new("rice.waivers", "现在有哪些免单", AccessLevel.Agent,
                Body: async (context, cancellationToken) =>
                {
                    var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (read.Error is not null || read.Value is null)
                        return Result<string>.Failure(read.Error ?? new(ErrorCode.Unavailable, "读不到米缸。"));

                    var now = (time ?? TimeProvider.System).GetLocalNow();
                    var live = read.Value.Allowances.Where(entry => entry.Remaining > 0 && entry.Until > now).ToArray();
                    if (live.Length == 0) return Result<string>.Success("没有生效的免单，每轮照常扣 1 口。");
                    return Result<string>.Success("免单：\n" + string.Join("\n", live.Select(entry =>
                        $"- {entry.Id}：{entry.Remaining} 轮" +
                        (entry.Channel is { } only ? $"（{only}）" : "（所有频道）") +
                        $"，到 {entry.Until:yyyy-MM-dd HH:mm zzz}，{entry.Reason}")));
                })
        ];

        foreach (var node in nodes)
        {
            var registered = commands.Register(node);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }
}
