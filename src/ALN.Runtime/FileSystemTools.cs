using System.Globalization;
using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The controlled file system, offered to a Session as tools: <c>fs_tree</c>, <c>fs_list</c>, <c>fs_read</c>,
/// <c>fs_write</c>, <c>fs_append</c>, <c>fs_move</c>, <c>fs_remove</c>.
///
/// Why these exist at all: everything a model is allowed to keep on disk (a memory block, a workspace note, a
/// script's data) has to be reachable by the same hand, or every module ends up growing its own slightly different
/// "and also let the model edit it" tool. One surface, one path rule, one place where "which directories are
/// reachable" is answered.
///
/// What these are <b>not</b>: a semantic layer. Nothing here knows what a memory group is; that knowledge lives in
/// the memory module, which is why tidying memory is "move the file" and writing a fragment is "append a line".
/// </summary>
public sealed class FileSystemTools(IFileSystemAccess access, IReadOnlyList<string> scopes, IMediaStore? media = null)
    : ISessionToolSource
{
    public const string SourceId = "system.filesystem-tools";

    public const string TreeToolName = "fs_tree";
    public const string ListToolName = "fs_list";
    public const string ReadToolName = "fs_read";
    public const string WriteToolName = "fs_write";
    public const string AppendToolName = "fs_append";
    public const string MoveToolName = "fs_move";
    public const string RemoveToolName = "fs_remove";

    /// <summary>Most characters one read returns. A model that gets a truncated file is told so; silence here would
    /// look like the file simply ended.</summary>
    private const int MaxReadChars = 32_000;

    private readonly IFileSystemAccess _access = access;

    /// <summary>
    /// How a non-text file is identified. Null when the host has no media store, and then "not UTF-8" stays the answer —
    /// which is honest: without a store there is nothing that can say whether the bytes are a picture.
    /// </summary>
    private readonly IMediaStore? _media = media;

    /// <summary>The scopes the model may see. A subset of what the process registered, and the reason a tool call
    /// can be refused before it reaches the disk.</summary>
    public IReadOnlyList<string> Scopes { get; } = scopes;

    public string Id => SourceId;

    public static readonly ModelToolDefinition TreeDefinition = new(TreeToolName,
        "List everything under one directory, recursively: relative paths, sizes and times, directories included. "
        + "Use this to see the shape of a scope before deciding what to read (a memory group's block and archive "
        + "files show up here). An empty path walks the scope root.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Which registered area to look in."},
          "path":{"type":"string","description":"Directory relative to the scope; empty for the scope root."}},
         "required":["scope"]}
        """);

    public static readonly ModelToolDefinition ListDefinition = new(ListToolName,
        "List one directory's immediate children. Cheaper than a walk when you only need the names.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Which registered area to look in."},
          "path":{"type":"string","description":"Directory relative to the scope; empty for the scope root."}},
         "required":["scope"]}
        """);

    public static readonly ModelToolDefinition ReadDefinition = new(ReadToolName,
        "Read one text file whole. A file that does not exist is reported as such rather than as an empty file — "
        + "guessing which one you are looking at is how a summary ends up describing nothing.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Which registered area to read from."},
          "path":{"type":"string","description":"File path relative to the scope."}},
         "required":["scope","path"]}
        """);

    public static readonly ModelToolDefinition WriteDefinition = new(WriteToolName,
        "Write one text file, replacing whatever was there. Parent directories are created. This is how a "
        + "consolidated block is written: the whole text, once.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Which registered area to write into."},
          "path":{"type":"string","description":"File path relative to the scope."},
          "text":{"type":"string","description":"The whole new content."}},
         "required":["scope","path","text"]}
        """);

    public static readonly ModelToolDefinition AppendDefinition = new(AppendToolName,
        "Add text to the end of a file, creating it if needed. One call's text is written as one contiguous run, so "
        + "two appends never splice into each other.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Which registered area to write into."},
          "path":{"type":"string","description":"File path relative to the scope."},
          "text":{"type":"string","description":"Text to add at the end."}},
         "required":["scope","path","text"]}
        """);

    public static readonly ModelToolDefinition MoveDefinition = new(MoveToolName,
        "Rename or move one file or directory inside one scope. The destination's parent must exist; an existing "
        + "destination is refused rather than overwritten.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Which registered area both paths are in."},
          "from":{"type":"string","description":"Existing path, relative to the scope."},
          "to":{"type":"string","description":"New path, relative to the scope."}},
         "required":["scope","from","to"]}
        """);

    public static readonly ModelToolDefinition RemoveDefinition = new(RemoveToolName,
        "Delete a file, or a directory with recursive=true. A non-empty directory is refused unless recursive is "
        + "set, because a subtree deleted by accident cannot be asked for back.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Which registered area to delete from."},
          "path":{"type":"string","description":"File or directory path, relative to the scope."},
          "recursive":{"type":"boolean","description":"Required to delete a directory that still has something in it."}},
         "required":["scope","path"]}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Scopes.Count == 0)
            // Nothing reachable: offering the tools would be offering names that can only fail.
            return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success([]));

        IReadOnlyList<SessionToolOffer> offers =
        [
            // None of these speaks to anyone: a step that only tidied a file has not replied, and the loop has to
            // be able to tell that apart (docs/08 §5.1).
            new(new TreeTool(this), TreeDefinition, ActsExternally: false),
            new(new ListTool(this), ListDefinition, ActsExternally: false),
            new(new ReadTool(this), ReadDefinition, ActsExternally: false),
            new(new WriteTool(this), WriteDefinition, ActsExternally: false),
            new(new AppendTool(this), AppendDefinition, ActsExternally: false),
            new(new MoveTool(this), MoveDefinition, ActsExternally: false),
            new(new RemoveTool(this), RemoveDefinition, ActsExternally: false)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    /// <summary>
    /// The scope check the port would also make, done here as well so the refusal reads like advice: a model that is
    /// told which scopes exist can pick one, while one that is told "denied" will try the same thing again.
    /// </summary>
    private Result CheckScope(string? scope) =>
        scope is not null && Scopes.Contains(scope, StringComparer.Ordinal)
            ? Result.Success()
            : Result.Failure(new(ErrorCode.Denied,
                $"'{scope}' is not one of the areas you may use. Available: {string.Join("、", Scopes)}."));

    private sealed class TreeTool(FileSystemTools owner) : ISessionTool
    {
        public string Name => TreeToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var scope = ToolArguments.Text(arguments, "scope");
            if (owner.CheckScope(scope).Error is { } denied) return Result<SessionToolResult>.Failure(denied);
            var path = ToolArguments.Text(arguments, "path") ?? "";

            var tree = await owner._access.TreeAsync(scope!, path, cancellationToken).ConfigureAwait(false);
            if (!tree.Ok || tree.Value is null) return Result<SessionToolResult>.Failure(tree.Error!);
            if (tree.Value.Count == 0) return ToolArguments.Ok($"{Describe(scope!, path)} 是空的。");

            var builder = new StringBuilder().Append(Describe(scope!, path)).Append("：\n");
            foreach (var node in tree.Value)
                builder.Append(Line(node.Path, node.IsDirectory, node.Bytes, node.ModifiedAt)).Append('\n');
            return ToolArguments.Ok(builder.ToString().TrimEnd());
        }
    }

    private sealed class ListTool(FileSystemTools owner) : ISessionTool
    {
        public string Name => ListToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var scope = ToolArguments.Text(arguments, "scope");
            if (owner.CheckScope(scope).Error is { } denied) return Result<SessionToolResult>.Failure(denied);
            var path = ToolArguments.Text(arguments, "path") ?? "";

            var listing = await owner._access.ListAsync(scope!, path, cancellationToken).ConfigureAwait(false);
            if (!listing.Ok || listing.Value is null) return Result<SessionToolResult>.Failure(listing.Error!);
            if (listing.Value.Count == 0) return ToolArguments.Ok($"{Describe(scope!, path)} 是空的。");

            var builder = new StringBuilder().Append(Describe(scope!, path)).Append("：\n");
            foreach (var entry in listing.Value)
                builder.Append(Line(entry.Name, entry.IsDirectory, entry.Bytes, entry.ModifiedAt)).Append('\n');
            return ToolArguments.Ok(builder.ToString().TrimEnd());
        }
    }

    private sealed class ReadTool(FileSystemTools owner) : ISessionTool
    {
        public string Name => ReadToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var scope = ToolArguments.Text(arguments, "scope");
            if (owner.CheckScope(scope).Error is { } denied) return Result<SessionToolResult>.Failure(denied);
            var path = ToolArguments.Text(arguments, "path");
            if (string.IsNullOrWhiteSpace(path))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "fs_read needs the path of a file."));

            var read = await owner._access.ReadAsync(scope!, path, cancellationToken).ConfigureAwait(false);
            // Absence is not a failure: Result<T>.Ok is false for a null value, so the two are told apart by Error.
            if (read.Error is { } error)
            {
                // A picture is not a mistake. Before saying "not UTF-8 text", ask what the bytes actually are: refusing
                // to open the one kind of file this build can show a model would be the wrong answer to the wrong
                // question, and the model would have no way to tell it apart from a broken file.
                if (owner._media is { } mediaStore)
                {
                    var described = await mediaStore.DescribeAsync(scope!, path, cancellationToken).ConfigureAwait(false);
                    if (described.Ok && described.Value is { IsViewable: true } picture)
                        return ToolArguments.Ok(
                            $"这是图片：{MediaImages.Describe(picture)}。",
                            attachments: [picture]);
                    if (described.Ok && described.Value is { } other && other.Kind != MediaKind.File)
                        return ToolArguments.Ok(
                            $"这个文件不是文本，是 {other.MediaType}（{MediaPolicy.Describe(other.Bytes)}）；本框架读不出它的内容。");
                }
                return Result<SessionToolResult>.Failure(error);
            }
            if (read.Value is null)
                return ToolArguments.Ok($"没有 {scope}/{path} 这个文件。（不存在，不是空文件。）");

            var text = read.Value.Text;
            if (text.Length > MaxReadChars)
                // Saying where the cut is lets the model ask for a different file instead of reasoning about a
                // sentence that stops mid-way for no visible reason.
                text = string.Concat(text.AsSpan(0, MaxReadChars), $"\n…（已截断：完整 {read.Value.Bytes} 字节）");
            return ToolArguments.Ok(text);
        }
    }

    private sealed class WriteTool(FileSystemTools owner) : ISessionTool
    {
        public string Name => WriteToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var scope = ToolArguments.Text(arguments, "scope");
            if (owner.CheckScope(scope).Error is { } denied) return Result<SessionToolResult>.Failure(denied);
            var path = ToolArguments.Text(arguments, "path");
            if (string.IsNullOrWhiteSpace(path))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "fs_write needs the path of a file."));
            var text = ToolArguments.Text(arguments, "text")
                       ?? throw new InvalidOperationException("fs_write needs text.");

            var written = await owner._access.WriteAsync(scope!, path, text, cancellationToken).ConfigureAwait(false);
            return written.Ok && written.Value is not null
                ? ToolArguments.Ok($"已写入 {scope}/{written.Value.Path}（{written.Value.Bytes} 字节，整篇替换）。")
                : Result<SessionToolResult>.Failure(written.Error!);
        }
    }

    private sealed class AppendTool(FileSystemTools owner) : ISessionTool
    {
        public string Name => AppendToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var scope = ToolArguments.Text(arguments, "scope");
            if (owner.CheckScope(scope).Error is { } denied) return Result<SessionToolResult>.Failure(denied);
            var path = ToolArguments.Text(arguments, "path");
            if (string.IsNullOrWhiteSpace(path))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "fs_append needs the path of a file."));
            var text = ToolArguments.Text(arguments, "text")
                       ?? throw new InvalidOperationException("fs_append needs text.");

            var appended = await owner._access.AppendAsync(scope!, path, text, cancellationToken).ConfigureAwait(false);
            return appended.Ok
                ? ToolArguments.Ok($"已追加到 {scope}/{path}，现在 {appended.Value} 字节。")
                : Result<SessionToolResult>.Failure(appended.Error!);
        }
    }

    private sealed class MoveTool(FileSystemTools owner) : ISessionTool
    {
        public string Name => MoveToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var scope = ToolArguments.Text(arguments, "scope");
            if (owner.CheckScope(scope).Error is { } denied) return Result<SessionToolResult>.Failure(denied);
            var from = ToolArguments.Text(arguments, "from");
            var to = ToolArguments.Text(arguments, "to");
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "fs_move needs both from and to."));

            var moved = await owner._access.MoveAsync(scope!, from, to, cancellationToken).ConfigureAwait(false);
            return moved.Ok
                ? ToolArguments.Ok($"已移动 {scope}/{from} → {scope}/{to}。")
                : Result<SessionToolResult>.Failure(moved.Error!);
        }
    }

    private sealed class RemoveTool(FileSystemTools owner) : ISessionTool
    {
        public string Name => RemoveToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var scope = ToolArguments.Text(arguments, "scope");
            if (owner.CheckScope(scope).Error is { } denied) return Result<SessionToolResult>.Failure(denied);
            var path = ToolArguments.Text(arguments, "path");
            if (string.IsNullOrWhiteSpace(path))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "fs_remove needs a path."));
            var recursive = ToolArguments.Flag(arguments, "recursive");

            var removed = await owner._access.DeleteAsync(scope!, path, recursive, cancellationToken).ConfigureAwait(false);
            return removed.Ok
                ? ToolArguments.Ok($"已删除 {scope}/{path}。" + (recursive ? "（含子目录）" : ""))
                : Result<SessionToolResult>.Failure(removed.Error!);
        }
    }

    // ---- rendering ------------------------------------------------------------------------------

    private static string Describe(string scope, string path) =>
        string.IsNullOrEmpty(path) ? scope : $"{scope}/{FilePaths.Canonical(path) ?? path}";

    private static string Line(string name, bool isDirectory, long bytes, DateTimeOffset modifiedAt)
    {
        var when = modifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var size = isDirectory ? "      <dir>" : $"{bytes,11} B";
        return $"{size}  {when}  {(isDirectory ? name + "/" : name)}";
    }
}

/// <summary>
/// Reading tool arguments, with the same policy as everywhere else: a missing required value or a value of the wrong
/// shape is refused with a sentence, not defaulted. A silently defaulted <c>scope</c> would write into the wrong
/// area, which is the one mistake nobody can undo.
/// </summary>
internal static class ToolArguments
{
    public static JsonElement? Parse(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    public static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A number, or a number written as a string — providers differ, and both mean the same thing here.</summary>
    public static int? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    public static Result<SessionToolResult> Ok(string content, IReadOnlyList<MediaRef>? attachments = null) =>
        Result<SessionToolResult>.Success(new SessionToolResult(content, Attachments: attachments));

    public static Result<SessionToolResult> BadJson(string tool) =>
        Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, $"{tool} arguments must be a JSON object."));
}
