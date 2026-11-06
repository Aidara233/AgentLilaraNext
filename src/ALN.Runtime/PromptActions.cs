using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Prompts;

namespace ALN.Runtime;

/// <summary>
/// The prompt verbs: what groups exist, one file's text, and writing one back.
///
/// A prompt is a file, and the library is what knows which roots are read and which copy wins. Registering these next
/// to the library keeps that knowledge in one place; the host used to carry a third opinion about it, and an editor
/// that writes the copy nobody reads is the failure this whole area exists to avoid.
/// </summary>
public static class PromptActions
{
    public const string List = "prompts.list";
    public const string Get = "prompts.get";
    public const string Set = "prompts.set";

    public static Result Register(IControlPlane plane, IPromptLibrary? prompts)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(List, "Prompt groups and the files in them, with their origin and fingerprint. A prompt that cannot be "
                    + "read is reported rather than skipped: a Session reading it would answer as somebody else.",
                Capabilities.PromptsRead, false, ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
                (_, _) => ValueTask.FromResult(prompts is null
                    ? Unavailable("The prompt library is unavailable.")
                    : ReadCatalog(prompts))),

            (new(Get, "Read one prompt file's text so it can be edited, with the path it came from.", Capabilities.PromptsRead, false,
                ParamsJson: """
                {"type":"object","properties":{
                  "group":{"type":"string"},"name":{"type":"string"}},
                 "required":["group","name"],"additionalProperties":false}
                """, Subject: "name"), (arguments, ct) => ReadAsync(arguments, prompts, ct)),

            (new(Set, "Write one prompt file into the data root — the copy that wins. Writing anywhere else is writing a "
                    + "copy nobody reads.", Capabilities.PromptsWrite, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "group":{"type":"string"},"name":{"type":"string"},
                  "text":{"type":"string","description":"The whole file. Required even when empty, so clearing is a decision."}},
                 "required":["group","name","text"],"additionalProperties":false}
                """, Subject: "name"), (arguments, ct) => WriteAsync(arguments, prompts, ct))
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    private static Result<JsonElement> Unavailable(string message) =>
        Result<JsonElement>.Failure(new(ErrorCode.Unavailable, message));

    private static Result<JsonElement> ReadCatalog(IPromptLibrary prompts)
    {
        var catalog = prompts.List();
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            roots = prompts.Roots,
            groups = catalog.Groups.Select(group => new
            {
                group = group.Group,
                prompts = group.Entries.Select(entry => new
                {
                    name = entry.Name,
                    bytes = entry.Bytes,
                    fingerprint = entry.Fingerprint,
                    path = entry.Path
                })
            }),
            warnings = catalog.Warnings
        }));
    }

    private static async ValueTask<Result<JsonElement>> ReadAsync(JsonElement arguments, IPromptLibrary? prompts,
        CancellationToken ct)
    {
        if (prompts is null) return Unavailable("The prompt library is unavailable.");
        if (!arguments.TryReadText("group", out var group) || group.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "prompts.get requires 'group'."));
        if (!arguments.TryReadText("name", out var name) || name.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "prompts.get requires 'name'."));
        group = group.Trim();
        name = name.Trim();

        // The catalogue is what knows where the winning copy lives; resolving alone answers with the text but not with
        // the path an editor has to show.
        var catalog = prompts.List();
        var known = catalog.Groups.FirstOrDefault(candidate => string.Equals(candidate.Group, group, StringComparison.Ordinal));
        var entry = known?.Entries.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (entry is null)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                $"No prompt '{name}' in group '{group}'. Available: {(known is null ? catalog.Groups.Count == 0 ? "(no groups)" : string.Join(", ", catalog.Groups.Select(g => g.Group)) : string.Join(", ", known.Entries.Select(e => e.Name)))}."));

        var selection = new PromptSelection(new Dictionary<string, string>(StringComparer.Ordinal) { [group] = name });
        var resolved = await prompts.ResolveAsync(selection, ct).ConfigureAwait(false);
        if (!resolved.Ok || resolved.Value is null) return Result<JsonElement>.Failure(resolved.Error!);
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            group,
            name,
            path = entry.Path,
            bytes = entry.Bytes,
            fingerprint = entry.Fingerprint,
            text = resolved.Value.Text
        }));
    }

    private static async ValueTask<Result<JsonElement>> WriteAsync(JsonElement arguments, IPromptLibrary? prompts,
        CancellationToken ct)
    {
        if (prompts is null) return Unavailable("The prompt library is unavailable.");
        if (!arguments.TryReadText("group", out var group) || group.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "prompts.set requires 'group'."));
        if (!arguments.TryReadText("name", out var name) || name.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "prompts.set requires 'name'."));
        // Required even when empty-looking, so that clearing a prompt is a decision somebody made rather than a field
        // the client forgot to send.
        if (!arguments.TryGetProperty("text", out var textItem) || textItem.ValueKind != JsonValueKind.String)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "prompts.set requires 'text' as a string."));

        var saved = await prompts.SaveAsync(group.Trim(), name.Trim(), textItem.GetString() ?? "", ct).ConfigureAwait(false);
        if (!saved.Ok || saved.Value is null) return Result<JsonElement>.Failure(saved.Error!);
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            group = group.Trim(),
            name = name.Trim(),
            path = saved.Value,
            bytes = System.Text.Encoding.UTF8.GetByteCount(textItem.GetString() ?? "")
        }));
    }
}
