using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Archive;

namespace ALN.Archive;

/// <summary>
/// The bin's verbs, registered by the package that owns the bin.
///
/// Same two verbs for every kind of thing that piles up, so a script or a person learns them once: what could go in,
/// and putting it there or taking it back. Both writes are dry-run unless the caller says otherwise — a missing flag
/// must never be what took a conversation out of service.
/// </summary>
public static class ArchiveActions
{
    public const string List = "archive.list";
    public const string Move = "archive.move";
    public const string Restore = "archive.restore";

    public static Result Register(IControlPlane plane, IArchiveRegistry archive)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        if (archive is null) return Result.Failure(new(ErrorCode.Invalid, "An archive registry is required."));
        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(List, "What could be put away, and what is already in the bin. Read-only.", Capabilities.ArchiveRead, false,
                ParamsJson: """
                {"type":"object","properties":{
                  "kind":{"type":"string","description":"Optional: one kind of thing. Omit for all of them."}},
                 "additionalProperties":false}
                """), (arguments, ct) => ListAsync(arguments, archive, ct)),

            (new(Move, "Put candidates in the bin: one kind, all of it or the named ids. dryRun defaults to true.", Capabilities.ArchiveControl, true,
                ParamsJson: MoveParams("Put candidates in the bin."), Subject: "kind"),
                (arguments, ct) => MoveAsync(arguments, archive, ct)),
            (new(Restore, "Take items back out of the bin, by id. dryRun defaults to true.", Capabilities.ArchiveControl, true,
                ParamsJson: RestoreParams, Subject: "kind"), (arguments, ct) => RestoreAsync(arguments, archive, ct))
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    private static string MoveParams(string description) =>
        """{"type":"object","properties":{"kind":{"type":"string","description":"DESCRIPTION"},"ids":{"type":"array","items":{"type":"string"},"description":"Optional: only these. Omit for all candidates."},"dryRun":{"type":"boolean","description":"Default true. false actually moves them."}},"required":["kind"],"additionalProperties":false}"""
            .Replace("DESCRIPTION", description, StringComparison.Ordinal);

    private const string RestoreParams = """
        {"type":"object","properties":{
          "kind":{"type":"string"},
          "ids":{"type":"array","items":{"type":"string"},"description":"Required: putting everything back at once is a decision, not a default."},
          "dryRun":{"type":"boolean","description":"Default true. false actually restores them."}},
         "required":["kind","ids"],"additionalProperties":false}
        """;

    private static async ValueTask<Result<JsonElement>> ListAsync(JsonElement arguments, IArchiveRegistry archive,
        CancellationToken ct)
    {
        var kind = arguments.TryReadText("kind", out var requestedKind) ? requestedKind.Trim() : null;
        var candidates = await archive.CandidatesAsync(kind, ct).ConfigureAwait(false);
        if (!candidates.Ok || candidates.Value is null) return Result<JsonElement>.Failure(candidates.Error!);
        var archived = await archive.ArchivedAsync(kind, ct).ConfigureAwait(false);
        if (!archived.Ok || archived.Value is null) return Result<JsonElement>.Failure(archived.Error!);
        return Result<JsonElement>.Success(ActionArgs.JsonOf(new
        {
            kinds = archive.Kinds,
            candidates = candidates.Value.Select(CandidateOf).ToArray(),
            archived = archived.Value.Select(ItemOf).ToArray(),
            bin = new { items = archived.Value.Count, bytes = archived.Value.Sum(item => item.Bytes) }
        }));
    }

    private static async ValueTask<Result<JsonElement>> MoveAsync(JsonElement arguments, IArchiveRegistry archive,
        CancellationToken ct)
    {
        if (!arguments.TryReadText("kind", out var kind) || kind.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                $"archive.move requires 'kind' ({string.Join(", ", archive.Kinds)})."));
        var ids = ReadIds(arguments);
        var moved = await archive.ArchiveAsync(kind.Trim(), ids, IsDryRun(arguments), ct).ConfigureAwait(false);
        return moved.Ok && moved.Value is not null
            ? Result<JsonElement>.Success(ActionArgs.JsonOf(ReportOf(moved.Value)))
            : Result<JsonElement>.Failure(moved.Error!);
    }

    private static async ValueTask<Result<JsonElement>> RestoreAsync(JsonElement arguments, IArchiveRegistry archive,
        CancellationToken ct)
    {
        if (!arguments.TryReadText("kind", out var kind) || kind.Trim().Length == 0)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                $"archive.restore requires 'kind' ({string.Join(", ", archive.Kinds)})."));
        var ids = ReadIds(arguments);
        if (ids is null)
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                "archive.restore requires 'ids': putting everything back at once is a decision, not a default."));
        var restored = await archive.RestoreAsync(kind.Trim(), ids, IsDryRun(arguments), ct).ConfigureAwait(false);
        return restored.Ok && restored.Value is not null
            ? Result<JsonElement>.Success(ActionArgs.JsonOf(ReportOf(restored.Value)))
            : Result<JsonElement>.Failure(restored.Error!);
    }

    /// <summary>Absent means dry run: the flag has to be said out loud before anything leaves service.</summary>
    private static bool IsDryRun(JsonElement arguments) =>
        !(arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("dryRun", out var flag) &&
          flag.ValueKind == JsonValueKind.False);

    private static IReadOnlyList<string>? ReadIds(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("ids", out var ids) ||
            ids.ValueKind != JsonValueKind.Array) return null;
        var values = ids.EnumerateArray()
            .Where(id => id.ValueKind == JsonValueKind.String)
            .Select(id => id.GetString() ?? "")
            .Where(id => id.Length > 0)
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private static object CandidateOf(ArchiveCandidate candidate) => new
    {
        kind = candidate.Kind,
        id = candidate.Id,
        bytes = candidate.Bytes,
        lastActiveAt = candidate.LastUsedAt,
        owner = candidate.Owner,
        reason = candidate.Reason
    };

    private static object ItemOf(ArchivedItem item) => new
    {
        kind = item.Kind,
        id = item.Id,
        bytes = item.Bytes,
        archivedAt = item.ArchivedAt,
        owner = item.Owner,
        reason = item.Reason,
        note = item.Note,
        path = item.Path
    };

    private static object ReportOf(ArchiveReport report) => new
    {
        dryRun = report.DryRun,
        count = report.Count,
        done = report.Done,
        bytes = report.Bytes,
        items = report.Items.Select(item => new
        {
            kind = item.Kind,
            id = item.Id,
            action = item.Action,
            done = item.Done,
            bytes = item.Bytes,
            message = item.Message
        })
    };
}
