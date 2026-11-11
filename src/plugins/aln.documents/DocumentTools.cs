using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace Aln.Documents;

/// <summary>
/// The tools this plugin offers a round. One source, one name, so that <c>Unregister</c> takes all of it back at once.
///
/// Every tool here answers a question about <b>a local file the model named itself</b> — a scope plus a relative
/// path, the same pair <c>fs_read</c> takes. Nothing here reaches a platform, downloads anything, or remembers what it
/// read last round: a document is opened when asked and forgotten when the call returns.
/// </summary>
public sealed class DocumentTools(IBinaryFileAccess files) : ISessionToolSource
{
    public const string SourceId = "aln.documents";
    public const string InfoToolName = "doc_info";

    private readonly IBinaryFileAccess _files = files;

    public string Id => SourceId;

    public static readonly ModelToolDefinition InfoDefinition = new(InfoToolName,
        "先说清一个办公文件是什么，再决定怎么读它。给 scope（通常是 workspace）和相对路径。\n"
        + "回答：多大、哪种格式（按字节判断，不看文件名）、能不能读出文字（现在只到判断格式这一步）。\n"
        + "这个工具读的是本机文件。别人在聊天里发来的文件不在盘上，要先用 files_get 把它取到 workspace。",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"宿主登记过的区名，例如 workspace。"},
          "path":{"type":"string","description":"相对那个区的路径，用 / 分段，例如 reports/q3.docx。"}
        },"required":["scope","path"]}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> tools =
        [
            // ActsExternally = false: reading or writing a file is not saying something to anybody, and the loop uses
            // that to decide whether this round's text was its reply (docs/08 §5.1).
            new SessionToolOffer(new InfoTool(this), InfoDefinition, ActsExternally: false)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(tools));
    }

    private static string Sizes(long bytes) => bytes < 1024 ? $"{bytes} 字节" : $"{bytes / 1024.0:0.#} KB";

    private sealed class InfoTool(DocumentTools owner) : ISessionTool
    {
        public string Name => InfoToolName;

        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (Arguments.Parse(call.ArgumentsJson) is not { } arguments)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "doc_info 需要一个 JSON 对象作为参数。"));

            var scope = Arguments.Text(arguments, "scope");
            if (string.IsNullOrWhiteSpace(scope))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    $"doc_info 需要 scope。这个宿主接受字节的区：{string.Join("、", owner._files.BinaryScopes)}。"));

            var path = Arguments.Text(arguments, "path");
            if (string.IsNullOrWhiteSpace(path))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "doc_info 需要 path（相对那个区的路径）。"));

            var opened = await owner._files.OpenReadAsync(scope!, path, cancellationToken).ConfigureAwait(false);
            if (!opened.Ok || opened.Value is not { } stream)
            {
                // The port's own words, not a paraphrase: it is the only thing that knows whether the scope was
                // refused, the path was bad, or the file is simply not there.
                var error = opened.Error ?? new Error(ErrorCode.Unavailable, "这个文件打不开，端口没有说原因。");
                return Result<SessionToolResult>.Failure(error);
            }

            await using (stream)
            {
                var length = stream.CanSeek ? stream.Length : -1;
                var parsed = DocumentReader.Read(stream);
                if (!parsed.Ok || parsed.Value is not { } document)
                {
                    // A refusal that already names what the bytes were is the useful answer; prefixing it with the path
                    // says which file it was about without hiding the reason.
                    var error = parsed.Error ?? new Error(ErrorCode.Unavailable, "这个文件读不出来。");
                    return Result<SessionToolResult>.Failure(
                        new Error(error.Code, $"{scope}/{path}：{error.Message}", error.Detail));
                }

                var text = new StringBuilder();
                text.Append($"{scope}/{path}：").Append(document.Format);
                if (length >= 0) text.Append("，").Append(Sizes(length));
                text.Append("。\n");

                var parts = new List<string>();
                if (document.Count(SectionKind.Paragraph) is var paragraphs and > 0) parts.Add($"段落 {paragraphs}");
                if (document.Count(SectionKind.Table) is var tables and > 0) parts.Add($"表格 {tables}");
                if (document.Count(SectionKind.Sheet) is var sheets and > 0) parts.Add($"工作表 {sheets}");
                if (document.Count(SectionKind.Slide) is var slides and > 0) parts.Add($"幻灯片 {slides}");
                if (document.Count(SectionKind.Page) is var pages and > 0) parts.Add($"页 {pages}");
                text.Append(parts.Count == 0 ? "没有读出任何内容。" : string.Join("，", parts));
                text.Append($"；共 {document.Characters} 字符。");
                if (document.Truncated)
                    text.Append("\n（读取到上限就停了，后面的内容还没读——用范围参数分次读。）");

                return Result<SessionToolResult>.Success(new SessionToolResult(text.ToString()));
            }
        }
    }

    /// <summary>
    /// The little bit of JSON reading a tool needs. A plugin cannot use the host's argument helpers — they live in
    /// the implementation assembly, which a plugin is not allowed to reference — so the rules it does share are
    /// spelled the same way: a missing or mistyped argument is a failure with a sentence, never a default.
    /// </summary>
    private static class Arguments
    {
        public static JsonElement? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.Clone()
                    : null;
            }
            catch (JsonException) { return null; }
        }

        public static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
