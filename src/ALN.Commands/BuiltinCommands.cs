using System.Text.Json;
using ALN.Contracts;

namespace ALN.Commands;

/// <summary>
/// The four instructions of the first release. Each one is a shell around something that already exists: the point of
/// an instruction is to be reachable from a group chat, not to be a second implementation of a verb.
/// </summary>
public static class BuiltinCommands
{
    public const string Help = "help";
    public const string WhoAmI = "whoami";
    public const string Status = "status";
    public const string Reload = "reload";

    // Action ids, spelled here rather than referenced: ALN.Commands is a pipeline stage, and taking a dependency on
    // the runtime assembly to borrow two string constants would drag the whole host into it.
    private const string SystemInfoAction = "system.info";
    private const string ScriptsReloadAction = "scripts.reload";

    public static Result Register(CommandRegistry registry, IControlPlane plane)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(plane);

        CommandNode[] nodes =
        [
            new(Help, "看看这里", AccessLevel.Agent, Usage: "//help [名字]", Body: (context, _) =>
                ValueTask.FromResult(RenderHelp(registry, context))),

            new(WhoAmI, "我是谁（这个系统眼里的）", AccessLevel.Agent, Body: (context, _) =>
                ValueTask.FromResult(Result<string>.Success(RenderWhoAmI(context)))),

            new(Status, "五行现状", AccessLevel.Agent, Body: async (context, cancellationToken) =>
                await RenderStatusAsync(plane, context, cancellationToken).ConfigureAwait(false)),

            // Operator and above. Someone who cannot run this gets one sentence explaining what it needs — the depth
            // is the refusal, decided before the body runs.
            new(Reload, "重新载入脚本目录", AccessLevel.Operator, Body: async (context, cancellationToken) =>
                await RenderReloadAsync(plane, context, cancellationToken).ConfigureAwait(false))
        ];

        foreach (var node in nodes)
        {
            var registered = registry.Register(node);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    private static Result<string> RenderHelp(CommandRegistry registry, CommandContext context)
    {
        if (context.Arguments.Count == 0) return Result<string>.Success(registry.Render(context.CallerLevel));
        var path = string.Join('.', context.Arguments.Select(word => word.ToLowerInvariant()));
        return Result<string>.Success(registry.Render(path, context.CallerLevel));
    }

    private static string RenderWhoAmI(CommandContext context)
    {
        var account = $"{context.Adapter} / {context.Sender ?? "?"}";
        if (context.Person is not { } person)
            return $"你：未登记（{AccessLevels.Describe(context.CallerLevel)}）· {account}\n"
                   + "这张表是人写的：identify/<id>/info.json";
        var self = person.Self ? "（这是我自己的账号）" : string.Empty;
        return $"你：{person.Label}{self} · {account}";
    }

    private static async ValueTask<Result<string>> RenderStatusAsync(IControlPlane plane, CommandContext context,
        CancellationToken cancellationToken)
    {
        var info = await plane.InvokeAsync(context.Actor, SystemInfoAction, JsonSerializer.SerializeToElement(new { }),
            cancellationToken).ConfigureAwait(false);
        if (!info.Ok)
            return Result<string>.Failure(info.Error ?? new(ErrorCode.Unavailable, "读不到 system.info。"));
        return Result<string>.Success("现状（详见 system.info）：\n" + Flatten(info.Value, 6));
    }

    private static async ValueTask<Result<string>> RenderReloadAsync(IControlPlane plane, CommandContext context,
        CancellationToken cancellationToken)
    {
        var reloaded = await plane.InvokeAsync(context.Actor, ScriptsReloadAction,
            JsonSerializer.SerializeToElement(new { }), cancellationToken).ConfigureAwait(false);
        if (!reloaded.Ok)
            return Result<string>.Failure(reloaded.Error ?? new(ErrorCode.Unavailable, "重载失败。"));
        return Result<string>.Success("已重新载入脚本：\n" + Flatten(reloaded.Value, 5));
    }

    /// <summary>
    /// One level of a JSON answer, one line each. Deliberately shallow: an instruction that dumps a whole tree into a
    /// group chat is an instruction nobody reads twice, and the full shape is one action call away.
    /// </summary>
    private static string Flatten(JsonElement json, int maxLines)
    {
        if (json.ValueKind != JsonValueKind.Object)
            return "  " + Spell(json);
        var lines = new List<string>();
        foreach (var property in json.EnumerateObject().Take(maxLines))
            lines.Add($"  {property.Name}: {Spell(property.Value)}");
        var total = json.EnumerateObject().Count();
        if (total > maxLines) lines.Add($"  …共 {total} 项");
        return string.Join('\n', lines);
    }

    private static string Spell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => $"共 {value.GetArrayLength()} 项",
        JsonValueKind.Object => "…",
        _ => "null"
    };
}
