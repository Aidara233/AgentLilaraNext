using System.Text;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The schedules, as a model sees them: one read-only tool.
///
/// Deliberately not a setter. "What will this host do on its own" is a question an assistant should answer from the
/// record rather than from the conversation, and answering it is the whole point of the tool. Writing a row is an
/// operator's act (<c>schedules.set</c>), because a row decides when something happens with nobody present — a model
/// that could schedule itself could spend a budget while nobody is watching.
///
/// A module for the same reason the script and action tools are: whether the model may see the schedule is answered by
/// the graph, not by this file.
/// </summary>
public sealed class ScheduleTools(ScheduleStore store) : ISessionToolSource
{
    private readonly ScheduleStore _store = store;

    public const string TypeId = "schedules.control";
    public const string SourceId = "system.schedule-tools";
    public const string ListToolName = "schedules_list";

    public string Id => SourceId;

    public static readonly ModelToolDefinition ListDefinition = new(ListToolName,
        "Every schedule this host will act on by itself: what runs, when it next runs, how many times it has run, and why "
        + "it stopped if it did. Read it before promising anybody that something will happen later — and read it instead "
        + "of guessing whether a reminder already exists. It cannot create or change one of these: that is an operator's.",
        """
        {"type":"object","properties":{
          "target":{"type":"string","description":"Only rows that fire at this Session or script id. Optional."}},
         "additionalProperties":false}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> offers =
        [
            new(new ListTool(this), ListDefinition, ActsExternally: false)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private sealed class ListTool(ScheduleTools owner) : ISessionTool
    {
        public string Name => ListToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            string? target = null;
            if (!string.IsNullOrWhiteSpace(call.ArgumentsJson))
            {
                using var document = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("target", out var filter) &&
                    filter.ValueKind == System.Text.Json.JsonValueKind.String)
                    target = filter.GetString();
            }

            var listed = await owner._store.ListAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.Ok)
                return Result<SessionToolResult>.Failure(listed.Error ?? new(ErrorCode.Unavailable, "Schedules could not be read."));

            var rows = listed.Value!
                .Where(row => target is null || string.Equals(row.TargetId, target, StringComparison.Ordinal))
                .ToArray();
            var text = new StringBuilder();
            if (rows.Length == 0)
            {
                text.Append("No schedule is stored");
                text.Append(target is null ? "." : $" for {target}.");
                text.Append(" Nothing here wakes up on its own; a Session's own `wait` is the only thing that would.");
                return Result<SessionToolResult>.Success(new(text.ToString()));
            }

            text.Append(rows.Length).Append(rows.Length == 1 ? " schedule:" : " schedules:");
            foreach (var row in rows.OrderBy(row => row.NextDue))
            {
                text.Append("\n- ").Append(row.Id).Append(" — ").Append(row.Description ?? row.Expression)
                    .Append(" → ").Append(row.TargetKind == ScheduleTargetKind.Session ? "session " : "script ")
                    .Append(row.TargetId);
                text.Append(row.Enabled
                    ? $"; next {Stamp(row.NextDue)}, ran {row.Runs} time(s)"
                    : $"; off — {row.DisabledReason ?? "switched off"}");
                if (row.Missed > 0) text.Append(", missed ").Append(row.Missed);
                if (row.Skipped > 0) text.Append(", skipped ").Append(row.Skipped);
                if (row.LastOutcome is { Length: > 0 }) text.Append(", last ").Append(row.LastOutcome);
            }
            return Result<SessionToolResult>.Success(new(text.ToString()));
        }

        /// <summary>The instant in the schedule's own zone: "next 2026-11-08 08:00 +08:00" is readable, UTC is not.</summary>
        private static string Stamp(DateTimeOffset? at)
        {
            if (at is not { } value) return "never";
            return value.ToString("yyyy-MM-dd HH:mm zzz");
        }
    }
}

/// <summary>The module wrapper: a type id, a factory, and registration into the session's tool sources.</summary>
public sealed class ScheduleToolsModule(ModuleId id, ScheduleTools tools, ISessionExtensionRegistry extensions) : IKernelModule
{
    private bool _registered;

    public ModuleId Id { get; } = id;

    public static Result<IKernelModule> FromManifest(ModuleManifest manifest, ScheduleStore store,
        ISessionExtensionRegistry extensions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Type, ScheduleTools.TypeId, StringComparison.Ordinal))
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                $"This factory builds {ScheduleTools.TypeId}, not {manifest.Type}."));
        return Result<IKernelModule>.Success(new ScheduleToolsModule(manifest.Id, new ScheduleTools(store), extensions));
    }

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = extensions.Register(tools);
        if (!registered.Ok) return ValueTask.FromResult(registered);
        _registered = true;
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            extensions.Unregister(tools.Id);
            _registered = false;
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}
