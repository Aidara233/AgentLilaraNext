using System.Text.Json;
using ALN.Contracts;
using ALN.Graph.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The script verbs: what is loaded, one package in full, switching one on or off, and reading the folders again.
///
/// They live next to the script library rather than in the host, because every one of them is a question about the
/// library: which folders are read, what a library is called, what "switched off" means. The reload is
/// <see cref="ScriptReload"/> — the same code the model's tool calls, because an answer that depends on the door is a
/// second implementation waiting to disagree.
/// </summary>
public static class ScriptActions
{
    public const string List = "scripts.list";
    public const string Get = "scripts.get";
    public const string Set = "scripts.set";
    public const string Reload = "scripts.reload";

    /// <summary>Registers the script verbs. The catalogue is optional: without it, reload says so instead of pretending.</summary>
    public static Result Register(IControlPlane plane, IGraphRuntime graph, IGraphScriptCatalog? catalog,
        ScriptLibrary? library = null)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        _ = library;
        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(List, "Scripts in the catalogue, with their library and whether they are switched on.", Capabilities.GraphRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
                (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    libraries = graph.Scripts.GroupBy(entry => LibraryOf(entry.Package))
                        .Select(group => new
                        {
                            library = group.Key, scripts = group.Count(), enabled = group.Count(entry => entry.Enabled)
                        }),
                    scripts = graph.Scripts.Select(entry => new
                    {
                        id = entry.Package.Manifest.Id,
                        library = LibraryOf(entry.Package),
                        mount = JsonNamingPolicy.CamelCase.ConvertName(entry.Package.Manifest.Mount.ToString()),
                        trust = JsonNamingPolicy.CamelCase.ConvertName(entry.Package.Manifest.TrustDomain.ToString()),
                        enabled = entry.Enabled,
                        mounted = graph.Mounted.Count(mounted => mounted.Package.Manifest.Id == entry.Package.Manifest.Id)
                    })
                })))),

            (new(Get, "One script package as loaded: its manifest and its source. This is what is running, not what is "
                    + "on disk — scripts.reload is what puts the two back together.", Capabilities.GraphRead, false,
                ParamsJson: """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""",
                Subject: "id"), (arguments, ct) => ReadAsync(arguments, graph, ct)),

            (new(Set, "Switch one script, or a whole library, on or off. target: a script id, or bundled / user / "
                    + "plugin:<id>. Switching off unmounts it: a script that is off does not answer.", Capabilities.GraphControl, false,
                ParamsJson: """
                {"type":"object","properties":{
                  "target":{"type":"string","description":"A script id, or a library: bundled / user / plugin:<id>."},
                  "enabled":{"type":"boolean","description":"Default true."}},
                 "required":["target"],"additionalProperties":false}
                """, Subject: "target"), async (arguments, ct) =>
            {
                if (!arguments.TryReadText("target", out var target) || target.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "scripts.set requires 'target'."));
                var enabled = !arguments.TryGetProperty("enabled", out var flag) || flag.ValueKind != JsonValueKind.False;
                var switched = await graph.SetScriptEnabledAsync(target.Trim(), enabled, ct).ConfigureAwait(false);
                return switched.Ok
                    ? Result<JsonElement>.Success(ActionArgs.JsonOf(new
                    {
                        target = target.Trim(), enabled, mounted = graph.Mounted.Count
                    }))
                    : Result<JsonElement>.Failure(switched.Error!);
            }),

            (new(Reload, "Read the script folders again and switch to what they now say. A script whose new body is "
                    + "refused keeps the body that already works. Publishing is agent's work: a reload applies what is "
                    + "already on disk, and a model can only write drafts — switching a script off is the deeper verb.",
                Capabilities.ScriptsPublish, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
                (_, ct) => ReloadAsync(graph, catalog, ct))
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    /// <summary>The library a package came from, as a person reads it: bundled, user, draft, live, plugin:&lt;id&gt;.</summary>
    private static string LibraryOf(ScriptPackage package) =>
        string.IsNullOrWhiteSpace(package.Origin) ? "user" : package.Origin;

    private static ValueTask<Result<JsonElement>> ReadAsync(JsonElement arguments, IGraphRuntime graph,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!arguments.TryReadText("id", out var id) || id.Trim().Length == 0)
            return ValueTask.FromResult(Result<JsonElement>.Failure(new(ErrorCode.Invalid, "scripts.get requires 'id'.")));
        var found = graph.Scripts.FirstOrDefault(entry =>
            string.Equals(entry.Package.Manifest.Id, id.Trim(), StringComparison.Ordinal));
        if (found.Package is null)
            return ValueTask.FromResult(Result<JsonElement>.Failure(new(ErrorCode.Invalid, $"No script named '{id.Trim()}'.")));
        var manifest = found.Package.Manifest;
        return ValueTask.FromResult(Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            id = manifest.Id,
            version = manifest.Version,
            library = LibraryOf(found.Package),
            targetType = manifest.TargetType,
            mount = JsonNamingPolicy.CamelCase.ConvertName(manifest.Mount.ToString()),
            trust = JsonNamingPolicy.CamelCase.ConvertName(manifest.TrustDomain.ToString()),
            entryPoint = manifest.EntryPoint,
            events = manifest.Events,
            capabilities = manifest.Capabilities,
            fingerprint = found.Package.Fingerprint,
            enabled = found.Enabled,
            source = found.Package.Source
        })));
    }

    private static async ValueTask<Result<JsonElement>> ReloadAsync(IGraphRuntime graph, IGraphScriptCatalog? catalog,
        CancellationToken ct)
    {
        if (catalog is null)
            return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "No script catalogue is wired to this process."));
        var reloaded = await ScriptReload.RunAsync(graph, catalog, ct).ConfigureAwait(false);
        if (!reloaded.Ok || reloaded.Value is null)
            return Result<JsonElement>.Failure(reloaded.Error ?? new(ErrorCode.Internal, "Reload failed."));
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            loaded = true,
            scripts = reloaded.Value.Scripts.Select(status => new { id = status.Id, library = status.Library, enabled = status.Enabled }).ToArray(),
            rejected = reloaded.Value.Rejected.Select(issue => new { path = issue.Path, reason = issue.Reason }).ToArray()
        }));
    }
}
