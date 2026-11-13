using System.Text.Json;
using ALN.Contracts;
using ALN.Graph.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The package verbs: switch a running package to another of its versions, or take one out.
///
/// These are the verbs a hot reload needs, and they are deliberately narrow. The caller names a package and a
/// version; it never names a path, because a path is a door to "load this dll from anywhere" and the answer to that
/// is no. Where the code lives is decided by <see cref="ModulePackageLayout"/> under the data root, which is the one
/// place that already knows how a package folder is shaped.
///
/// The order inside <c>packages.replace</c> is what makes it safe to press: the switch happens, then the pointer that
/// records which version is in force is written, then the script catalogue is read again. A failure before the pointer
/// leaves the old version running and recorded, so a restart boots what worked.
///
/// 2026-11-12：这三个动词所属的 <see cref="Capabilities.ModulesControl"/> 降到了 agent，所以它们各自钉了
/// <c>MinLevel: Operator</c>。理由不是"模型不该管包"，而是这一整族里只有它们是**把正在跑的代码换掉**：
/// 换坏了不是数据坏，是进程里那半边没了，回滚要另一个人再按一次。
///
/// 图与模块那一侧留了同样的分寸，只是理由不同：挂载已经放开（改的是路由），而 <c>modules.load</c> /
/// <c>modules.unload</c> 仍在 operator——它们是图的手术，会删掉一个目录、会让别人的边掉下来，而掉下来的边
/// 只被报告、不被修。
/// </summary>
public static class PackageActions
{
    public const string Replace = "packages.replace";
    public const string Use = "packages.use";
    public const string Unload = "packages.unload";

    /// <summary>How long a package gets to say it is ready to go before the caller's patience runs out.</summary>
    private static readonly TimeSpan DefaultDrain = TimeSpan.FromSeconds(30);

    public static Result Register(IControlPlane plane, IModulePackageMaintenance packages,
        IGraphRuntime? graph = null, IGraphScriptCatalog? scriptCatalog = null)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        if (packages is null) return Result.Failure(new(ErrorCode.Invalid, "A package loader is required."));
        var reload = graph is not null && scriptCatalog is not null
            ? new ScriptReloadAfterSwitch(graph, scriptCatalog)
            : null;

        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(Replace, "Switch a running package to a version that is already on disk. Writes down which version is "
                    + "in force only after the switch succeeded, then reads the script folders again so the behaviour "
                    + "that came with the new version is what runs. Fails back to the version that was running.",
                Capabilities.ModulesControl, true, MinLevel: AccessLevel.Operator,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The package id, e.g. sample.environment."},
                  "version":{"type":"string","description":"A directory beside the others: plugins/<id>/<version>/."},
                  "timeoutSeconds":{"type":"integer","minimum":1,"maximum":600,"description":"How long the package gets to finish its in-flight work. Default 30."},
                  "force":{"type":"boolean","description":"Stop waiting for a package that says it is not ready. Default false."}},
                 "required":["id","version"],"additionalProperties":false}
                """, Subject: "id"), (arguments, ct) => SwitchAsync(arguments, packages, reload, ct)),

            (new(Use, "Switch to a version that is already installed. Same as packages.replace, kept as its own verb "
                    + "because \"use the one I have\" and \"replace it with this\" are different intentions even when "
                    + "they are the same switch. Idempotent: asking for the version that is already running changes nothing.",
                Capabilities.ModulesControl, true, MinLevel: AccessLevel.Operator,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string"},
                  "version":{"type":"string"}},
                 "required":["id","version"],"additionalProperties":false}
                """, Subject: "id"), (arguments, ct) => SwitchAsync(arguments, packages, reload, ct)),

            (new(Unload, "Take one package out of this process. Refused while a package that depends on it is loaded, "
                    + "and refused by a package that says it still has work in flight. Its directory stays where it is.",
                Capabilities.ModulesControl, true, MinLevel: AccessLevel.Operator,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string"},
                  "force":{"type":"boolean","description":"Unload even if the package says it is not ready. Default false."},
                  "timeoutSeconds":{"type":"integer","minimum":1,"maximum":600}},
                 "required":["id"],"additionalProperties":false}
                """, Subject: "id"), async (arguments, ct) =>
            {
                if (!arguments.TryReadText("id", out var id) || id.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "packages.unload requires 'id'."));
                var force = arguments.TryGetProperty("force", out var forced) && forced.ValueKind == JsonValueKind.True;
                var unloaded = await packages.UnloadAsync(id.Trim(), Timeout(arguments, DefaultDrain), force, ct)
                    .ConfigureAwait(false);
                if (!unloaded.Ok || unloaded.Value is null) return Result<JsonElement>.Failure(unloaded.Error!);
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    id = id.Trim(),
                    completed = unloaded.Value.Completed,
                    forced = unloaded.Value.Forced,
                    affected = unloaded.Value.AffectedPackages,
                    unready = unloaded.Value.UnreadyParticipants,
                    errors = unloaded.Value.Errors
                }));
            })
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    private static async ValueTask<Result<JsonElement>> SwitchAsync(JsonElement arguments,
        IModulePackageMaintenance packages, ScriptReloadAfterSwitch? reload, CancellationToken ct)
    {
        if (!arguments.TryReadText("id", out var id) || id.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "packages.replace requires 'id'."));
        if (!arguments.TryReadText("version", out var version) || version.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "packages.replace requires 'version'."));
        var force = arguments.TryGetProperty("force", out var forced) && forced.ValueKind == JsonValueKind.True;

        // Asking for the version that is already running is not an error and not a reload: a caller that retries a
        // switch after a timeout must not be handed a refusal that reads like its own mistake.
        var current = packages.LoadedPackages.FirstOrDefault(package => package.Id == id.Trim());
        if (current is not null && current.Version == version.Trim())
            return Result<JsonElement>.Success(ActionArgs.JsonOf(new
            {
                id = id.Trim(), version = version.Trim(), changed = false, scripts = (object?)null
            }));

        var switched = await packages.ReplaceAsync(id.Trim(), version.Trim(), Timeout(arguments, DefaultDrain), force, ct)
            .ConfigureAwait(false);
        if (!switched.Ok || switched.Value is null) return Result<JsonElement>.Failure(switched.Error!);

        // The module is new; the scripts that shipped with it are still the old ones until the catalogue is read
        // again. Doing that here is the difference between "the package changed" and "the behaviour changed".
        var scripts = reload is null ? null : await reload.RunAsync(ct).ConfigureAwait(false);
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            id = switched.Value.PackageId,
            version = switched.Value.CurrentVersion,
            previous = switched.Value.PreviousVersion,
            reactivated = switched.Value.ReactivatedDependents,
            note = switched.Value.Note,
            changed = true,
            scripts
        }));
    }

    private static TimeSpan Timeout(JsonElement arguments, TimeSpan fallback) =>
        arguments.TryGetProperty("timeoutSeconds", out var value) && value.TryGetInt32(out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : fallback;

    /// <summary>
    /// Reading the script folders again, as a step of a switch rather than a separate thing to remember. A reload that
    /// fails is reported and does not fail the switch: the module is running, and the scripts that are running are the
    /// ones that already worked.
    /// </summary>
    private sealed class ScriptReloadAfterSwitch(IGraphRuntime graph, IGraphScriptCatalog catalog)
    {
        public async ValueTask<object> RunAsync(CancellationToken cancellationToken)
        {
            var reloaded = await ScriptReload.RunAsync(graph, catalog, cancellationToken).ConfigureAwait(false);
            if (!reloaded.Ok || reloaded.Value is null)
                return new { loaded = false, error = reloaded.Error?.Message };
            return new
            {
                loaded = true,
                scripts = reloaded.Value.Scripts.Select(status => new { id = status.Id, library = status.Library, enabled = status.Enabled }).ToArray(),
                rejected = reloaded.Value.Rejected.Select(issue => new { path = issue.Path, reason = issue.Reason }).ToArray()
            };
        }
    }
}
