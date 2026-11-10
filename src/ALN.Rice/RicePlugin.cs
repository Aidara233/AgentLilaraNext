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
    public const string BuyHint = "买饭：coin_buy rice-100（2 枚硬币补 100 口），或等人 //rice topup。";

    public string Id => "rice.admission";

    public async ValueTask<Result<RoundAdmission>> AdmitAsync(RoundAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        var charged = await ledger.ChargeRoundAsync(request.SessionId, request.StepNumber, cancellationToken)
            .ConfigureAwait(false);

        // Refused means empty, and the person is told what to do about it. Any other failure — the store is down, the
        // larder is unreadable — admits: a plugin with a bug must not be able to starve every Session at once.
        if (!charged.Ok)
            return Result<RoundAdmission>.Success(charged.Error?.Code == ErrorCode.Denied
                ? RoundAdmission.No($"这一轮没有跑：白饭用完了（0 口）。{BuyHint}")
                : RoundAdmission.Yes);

        return Result<RoundAdmission>.Success(RoundAdmission.Yes);
    }
}

/// <summary>What the model can see about its own rations. Free, like every "am I allowed to act" question.</summary>
public sealed class RiceTools(RiceLedger ledger) : ISessionToolSource
{
    public const string Wallet = "rice_wallet";

    public string Id => "rice.tools";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ModelToolDefinition Definition = new(Wallet,
        "看看还能吃几顿：白饭按轮数算，每运行一轮消耗 1 口。免费。",
        """{"type":"object","properties":{}}""");

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        var session = string.IsNullOrWhiteSpace(request.SessionId) ? "session" : request.SessionId;
        IReadOnlyList<SessionToolOffer> offers =
            [new(new Tool(Definition, (call, ct) => ShowAsync(session, ct)), Definition, ActsExternally: false)];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private async ValueTask<Result<SessionToolResult>> ShowAsync(string sessionId, CancellationToken cancellationToken)
    {
        var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.Error is not null || read.Value is null)
            return Result<SessionToolResult>.Success(new SessionToolResult(
                JsonSerializer.Serialize(new { error = read.Error?.Message ?? "读不到米缸。" }, Json), null));
        var state = read.Value;
        return Result<SessionToolResult>.Success(new SessionToolResult(JsonSerializer.Serialize(new
        {
            bites = state.Bites,
            rounds = state.Bites,
            thisSessionAtRound = state.ChargedUpTo(sessionId),
            perRound = 1,
            bitesPerBowl = RiceUnits.BitesPerBowl
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
public sealed class RiceContextSource(RiceLedger ledger) : IFloatingContextSource
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
        var line = $"白饭：{RiceUnits.Describe(state.Bites)} · 每轮 1 口";
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
    public const string Control = "rice.control";

    private const string ParamsJson = """
        {"type":"object","properties":{
          "bites":{"type":"integer","description":"多少口，必须大于 0"},
          "reason":{"type":"string","description":"一句话说明为什么动米缸，会进审计"}
        },"required":["bites","reason"]}
        """;

    public static Result Register(IControlPlane plane, RiceLedger ledger)
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
        return take.Ok ? Result.Success() : take;
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
        RiceLedger ledger)
    {
        var verbs = RiceActions.Register(plane, ledger);
        if (!verbs.Ok) return verbs;
        var admission = extensions.Register(new RiceAdmission(ledger));
        if (!admission.Ok) return admission;
        var tools = extensions.Register(new RiceTools(ledger));
        if (!tools.Ok) return tools;
        var context = extensions.Register(new RiceContextSource(ledger));
        if (!context.Ok) return context;

        CommandNode[] nodes =
        [
            new("rice", "米缸：白饭按轮算（//rice <子指令>）", AccessLevel.Agent),
            new("rice.wallet", "还剩几口", AccessLevel.Agent, Body: async (context, cancellationToken) =>
            {
                var read = await ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (read.Error is not null || read.Value is null)
                    return Result<string>.Failure(read.Error ?? new(ErrorCode.Unavailable, "读不到米缸。"));
                return Result<string>.Success(
                    $"白饭：{RiceUnits.Describe(read.Value.Bites)} · 每轮 1 口 · 第 {read.Value.Revision} 版");
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
