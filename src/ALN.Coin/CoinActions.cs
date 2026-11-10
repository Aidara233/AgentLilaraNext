using System.Text.Json;
using ALN.Contracts;

namespace ALN.Coin;

/// <summary>
/// The two verbs a script uses to move Lilara's money, and nothing else uses them.
///
/// Their capability is <c>coin.control</c>, which <b>is not in <see cref="AccessLevels"/></b> — so it answers
/// <see cref="AccessLevel.Full"/>, which is exactly the intended depth: a script may move money when it could already
/// do anything else. A model cannot (it is <c>agent</c>), and neither can a person in a group chat, because a card at
/// <c>full</c> can only be written by hand.
///
/// This is also why buying something and getting richer is allowed: the goods script credits the purse through this
/// same door, and the audit shows two entries (the debit and the credit) rather than one net number.
/// </summary>
public static class CoinActions
{
    public const string Credit = "coin.credit";
    public const string Debit = "coin.debit";
    public const string Control = "coin.control";

    private const string ParamsJson = """
        {"type":"object","properties":{
          "amount":{"type":"integer","description":"多少枚，必须大于 0"},
          "reason":{"type":"string","description":"一句话说明为什么动这笔钱，会进审计"}
        },"required":["amount","reason"]}
        """;

    public static Result Register(IControlPlane plane, CoinLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(plane);
        ArgumentNullException.ThrowIfNull(ledger);

        var credit = plane.Register(
            new ActionDescriptor(Credit, "给钱包加钱（脚本的备用资金走这里）", Control, Mutating: true,
                ParamsJson: ParamsJson, Subject: "amount"),
            async (actor, arguments, cancellationToken) =>
            {
                var (amount, reason, error) = Read(arguments);
                if (error is not null) return Result<JsonElement>.Failure(error);
                var credited = await ledger.CreditAsync(amount, $"script:{reason}", actor.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (!credited.Ok || credited.Value is null) return Result<JsonElement>.Failure(credited.Error!);
                return Result<JsonElement>.Success(JsonSerializer.SerializeToElement(
                    new { balance = credited.Value.Balance, revision = credited.Value.Revision }));
            });
        if (!credit.Ok) return credit;

        var debit = plane.Register(
            new ActionDescriptor(Debit, "从钱包扣钱（要 full）", Control, Mutating: true,
                ParamsJson: ParamsJson, Subject: "amount"),
            async (actor, arguments, cancellationToken) =>
            {
                var (amount, reason, error) = Read(arguments);
                if (error is not null) return Result<JsonElement>.Failure(error);
                var debited = await ledger.DebitAsync(amount, $"script:{reason}", actor.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (!debited.Ok || debited.Value is null) return Result<JsonElement>.Failure(debited.Error!);
                return Result<JsonElement>.Success(JsonSerializer.SerializeToElement(
                    new { balance = debited.Value.Balance, revision = debited.Value.Revision }));
            });
        if (!debit.Ok) return debit;
        return Result.Success();
    }

    private static (long Amount, string Reason, Error? Error) Read(JsonElement arguments)
    {
        var amount = arguments.TryGetProperty("amount", out var amountJson) ? amountJson.GetInt64() : 0;
        var reason = arguments.TryGetProperty("reason", out var reasonJson) ? reasonJson.GetString() : null;
        if (amount <= 0) return (0, string.Empty, new(ErrorCode.Invalid, "amount 必须是大于 0 的整数。"));
        if (string.IsNullOrWhiteSpace(reason)) return (0, string.Empty, new(ErrorCode.Invalid, "reason 不能为空：钱动了就要有理由。"));
        if (reason!.Length > 120) return (0, string.Empty, new(ErrorCode.Invalid, "reason 太长（上限 120 字）。"));
        return (amount, reason, null);
    }
}
