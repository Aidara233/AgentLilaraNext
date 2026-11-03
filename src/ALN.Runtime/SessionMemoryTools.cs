using System.Globalization;
using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// Memory as three tools: <c>remember</c>, <c>recall</c>, <c>forget</c>.
///
/// They are built-ins of the session loop for the same reason <c>wait</c> is: memory needs no external capability,
/// and it must work in the one turn where the host hands over no toolbox at all — a turn the Session woke itself
/// up for, with nobody speaking. A tool that only exists when a Channel message is being answered would be
/// missing exactly when the Session is alone with what it remembers.
///
/// The scope is the conversation, not the Session: a Session is meant to be replaced, and memory that dies with it
/// is not memory. That is why <see cref="Scope"/> is passed in rather than derived from the id here.
/// </summary>
public sealed class SessionMemoryTools
{
    public const string RememberToolName = "remember";
    public const string RecallToolName = "recall";
    public const string ForgetToolName = "forget";

    /// <summary>Query length the model may quote. Long enough for a sentence, not for a transcript.</summary>
    private const int MaxQueryLength = 300;

    /// <summary>What one recall may carry back. Beyond this the answer is cut and says so.</summary>
    private const int MaxRecallChars = 12000;

    public static readonly ModelToolDefinition RememberDefinition = new(RememberToolName,
        "Write something down so it survives this session. Two kinds: a fragment (something that was said or seen — "
        + "cheap, may repeat, may be wrong) and a note (a conclusion drawn from several fragments). A note must "
        + "quote the original words as evidence, because a conclusion nobody can check is a rumour. Writing is how "
        + "you avoid re-deriving the same thing next time.",
        """
        {"type":"object","properties":{
          "text":{"type":"string","description":"What to remember, in your own words, one or two sentences."},
          "kind":{"type":"string","enum":["fragment","note"],
            "description":"fragment = what happened or what was said; note = what you concluded, after folding several fragments into one statement."},
          "evidence":{"type":"string",
            "description":"Verbatim quote(s) the entry rests on. Required for a note; useful for a fragment."}},
         "required":["text","kind"]}
        """);

    public static readonly ModelToolDefinition RecallDefinition = new(RecallToolName,
        "Read back what was written down for this conversation. The answer comes with the evidence each note rests "
        + "on and a count of how much is stored, so you can see whether the fragments have piled up without anybody "
        + "folding them into notes. An empty query returns what is recent rather than what matches.",
        """
        {"type":"object","properties":{
          "query":{"type":"string","description":"What to look for. Omit to see what is recent."},
          "kind":{"type":"string","enum":["all","fragment","note"],"description":"Restrict to one kind. Default all."},
          "limit":{"type":"integer","minimum":1,"maximum":50,"description":"How many entries at most. Default 20."}},
         "required":[]}
        """);

    public static readonly ModelToolDefinition ForgetDefinition = new(ForgetToolName,
        "Drop one entry by id. This is how consolidation finishes: once fragments are folded into a note, the "
        + "fragments are what makes the store grow without making it truer. Deleting what is already gone is fine.",
        """
        {"type":"object","properties":{
          "id":{"type":"string","description":"The id recall showed, e.g. mem-1a2b3c."}},
         "required":["id"]}
        """);

    public static readonly IReadOnlyList<ModelToolDefinition> Definitions = [RememberDefinition, RecallDefinition, ForgetDefinition];

    private static readonly string[] Names_ = [RememberToolName, RecallToolName, ForgetToolName];

    private readonly IMemoryStore _store;
    private readonly string _sessionId;
    private readonly TimeProvider _time;

    /// <param name="scope">Whose memory this is — the conversation, not the Session.</param>
    /// <param name="sessionId">Recorded as the writer, so a bad entry can be traced to the turn that made it.</param>
    public SessionMemoryTools(IMemoryStore store, string scope, string sessionId, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        _store = store;
        Scope = scope;
        _sessionId = sessionId;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The conversation whose memory these tools read and write.</summary>
    public string Scope { get; }

    public IReadOnlyList<string> Names => Names_;

    public Result<ISessionTool> Find(string name)
    {
        if (string.Equals(name, RememberToolName, StringComparison.Ordinal)) return Result<ISessionTool>.Success(new RememberTool(this));
        if (string.Equals(name, RecallToolName, StringComparison.Ordinal)) return Result<ISessionTool>.Success(new RecallTool(this));
        if (string.Equals(name, ForgetToolName, StringComparison.Ordinal)) return Result<ISessionTool>.Success(new ForgetTool(this));
        return Result<ISessionTool>.Failure(new(ErrorCode.Denied, $"No memory tool named '{name}'."));
    }

    /// <summary>
    /// "The fragments have piled up" — the one thing a reader cannot see from a list of hits, and the reason
    /// consolidation happens at all. Without it the store only grows: every fragment is written, nobody ever folds
    /// them, and recall keeps answering with thirty near-duplicates.
    /// </summary>
    private static string? GapHint(MemoryCounts counts) =>
        counts.Fragments >= 10 && counts.Fragments >= counts.Notes * 5
            ? $"碎片已经攒到 {counts.Fragments} 条、笔记还只有 {counts.Notes} 条。把反复出现的、互相印证的那些整理成一两条 note" +
              "（附上原文证据），再用 forget 删掉被吸收的碎片——不整理的话，下次 recall 只会捞回一堆重复的碎片。"
            : null;

    private static string CountsLine(MemoryCounts counts) =>
        $"这个会话的记忆：碎片 {counts.Fragments} 条，笔记 {counts.Notes} 条。";

    private sealed class RememberTool(SessionMemoryTools owner) : ISessionTool
    {
        public string Name => RememberToolName;

        /// <summary>Writing is not a way to end a turn: a Session that writes and stops has said nothing.</summary>
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            JsonElement arguments;
            try { arguments = Arguments.Parse(call.ArgumentsJson); }
            catch (JsonException ex) { return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "remember arguments are not JSON.", ex.Message)); }

            var text = Arguments.Text(arguments, "text")?.Trim();
            if (string.IsNullOrEmpty(text))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "remember needs text: what should be remembered."));

            var kindText = Arguments.Text(arguments, "kind")?.Trim().ToLowerInvariant() ?? "fragment";
            MemoryKind kind;
            switch (kindText)
            {
                case "fragment": kind = MemoryKind.Fragment; break;
                case "note": kind = MemoryKind.Note; break;
                default:
                    // Refused rather than defaulted: "note" quietly read as "fragment" would let a conclusion be
                    // filed as chatter, which is the difference this table exists to keep.
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                        $"remember.kind must be 'fragment' or 'note', not '{kindText}'."));
            }

            var evidence = Arguments.Text(arguments, "evidence");
            if (kind == MemoryKind.Note && string.IsNullOrWhiteSpace(evidence))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    "A note needs evidence: quote the words it was drawn from, so the next reader can check the conclusion instead of inheriting it."));

            var written = await owner._store.AddAsync(new MemoryDraft(
                owner.Scope, kind, text, evidence, owner._sessionId, owner._time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            if (!written.Ok || written.Value is null)
                return Result<SessionToolResult>.Failure(written.Error ?? new(ErrorCode.Unavailable, "remember could not write."));

            var counts = await owner._store.CountEntriesAsync(owner.Scope, cancellationToken).ConfigureAwait(false);
            var builder = new StringBuilder();
            builder.Append("记下了 ").Append(written.Value.Id).Append("（").Append(Label(kind)).Append("）。");
            if (counts.Ok && counts.Value is not null)
            {
                builder.Append(' ').Append(CountsLine(counts.Value));
                if (GapHint(counts.Value) is { } hint) builder.Append(' ').Append(hint);
            }
            return Result<SessionToolResult>.Success(new SessionToolResult(builder.ToString()));
        }
    }

    private sealed class RecallTool(SessionMemoryTools owner) : ISessionTool
    {
        public string Name => RecallToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            JsonElement arguments;
            try { arguments = Arguments.Parse(call.ArgumentsJson); }
            catch (JsonException ex) { return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "recall arguments are not JSON.", ex.Message)); }

            var query = Arguments.Text(arguments, "query")?.Trim() ?? "";
            if (query.Length > MaxQueryLength) query = query[..MaxQueryLength];

            var kindText = Arguments.Text(arguments, "kind")?.Trim().ToLowerInvariant() ?? "all";
            MemoryKind? only = kindText switch
            {
                "all" => null,
                "fragment" => MemoryKind.Fragment,
                "note" => MemoryKind.Note,
                _ => (MemoryKind?)null
            };
            if (kindText is not ("all" or "fragment" or "note"))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    $"recall.kind must be 'all', 'fragment' or 'note', not '{kindText}'."));

            var limit = Math.Clamp(Arguments.Number(arguments, "limit") ?? IMemoryStore.DefaultLimit, 1, 50);
            var searched = await owner._store.SearchAsync(owner.Scope, query, limit, owner._time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            if (!searched.Ok || searched.Value is null)
                return Result<SessionToolResult>.Failure(searched.Error ?? new(ErrorCode.Unavailable, "recall could not read."));

            var hits = only is { } wanted
                ? searched.Value.Where(hit => hit.Record.Kind == wanted).ToList()
                : searched.Value.ToList();

            var counts = await owner._store.CountEntriesAsync(owner.Scope, cancellationToken).ConfigureAwait(false);
            var builder = new StringBuilder();
            if (hits.Count == 0)
                builder.Append(string.IsNullOrEmpty(query) ? "这个会话还没有任何记忆。" : $"没有找到与「{query}」相关的记忆。");
            else
            {
                builder.Append(hits.Count).Append(" 条").Append(string.IsNullOrEmpty(query) ? "（最近的）" : $"（与「{query}」相关的）").Append("：");
                foreach (var hit in hits)
                {
                    if (builder.Length > MaxRecallChars)
                    {
                        builder.Append("\n…（还有条目没列出：用更窄的 query 或更小的 limit 再看一次）");
                        break;
                    }
                    builder.Append('\n').Append(Format(hit.Record));
                }
            }
            if (counts.Ok && counts.Value is not null)
            {
                builder.Append('\n').Append(CountsLine(counts.Value));
                if (GapHint(counts.Value) is { } hint) builder.Append(' ').Append(hint);
            }
            return Result<SessionToolResult>.Success(new SessionToolResult(builder.ToString()));
        }

        /// <summary>
        /// One row as the model reads it. The evidence is rendered inline and indented rather than summarized:
        /// the whole point of keeping it is that a wrong conclusion can be argued with, which needs the words it
        /// came from in the same glance.
        /// </summary>
        private static string Format(MemoryRecord record)
        {
            var builder = new StringBuilder();
            builder.Append('[').Append(record.Id).Append("] ")
                .Append(Label(record.Kind)).Append(" · ")
                .Append(record.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .Append(" · ")
                .Append(record.Text.Trim());
            if (!string.IsNullOrWhiteSpace(record.Evidence))
                builder.Append("\n    证据：").Append(record.Evidence.Trim().Replace("\n", "\n          "));
            return builder.ToString();
        }
    }

    private sealed class ForgetTool(SessionMemoryTools owner) : ISessionTool
    {
        public string Name => ForgetToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            JsonElement arguments;
            try { arguments = Arguments.Parse(call.ArgumentsJson); }
            catch (JsonException ex) { return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "forget arguments are not JSON.", ex.Message)); }

            var id = Arguments.Text(arguments, "id")?.Trim();
            if (string.IsNullOrEmpty(id))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "forget needs the id of the entry to drop."));

            var deleted = await owner._store.ForgetAsync(owner.Scope, id, cancellationToken).ConfigureAwait(false);
            if (!deleted.Ok)
                return Result<SessionToolResult>.Failure(deleted.Error ?? new(ErrorCode.Unavailable, "forget could not delete."));

            var counts = await owner._store.CountEntriesAsync(owner.Scope, cancellationToken).ConfigureAwait(false);
            var content = counts.Ok && counts.Value is not null
                ? $"忘了 {id}。{CountsLine(counts.Value)}"
                : $"忘了 {id}。";
            return Result<SessionToolResult>.Success(new SessionToolResult(content));
        }
    }

    private static string Label(MemoryKind kind) => kind == MemoryKind.Note ? "笔记" : "碎片";

    /// <summary>
    /// Reading the arguments, with the same rule as everywhere else: a name or a value the tool does not know is
    /// refused, not ignored. An ignored <c>kind</c> would turn a note into a fragment and nobody would see it.
    /// </summary>
    private static class Arguments
    {
        public static JsonElement Parse(string? argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return default;
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.Clone();
        }

        public static string? Text(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        public static int? Number(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out number)) return number;
            return null;
        }
    }
}
