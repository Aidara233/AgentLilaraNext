using System.Globalization;
using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The controlled file system, offered to a Session as tools: <c>fs_tree</c>, <c>fs_list</c>, <c>fs_read</c>,
/// <c>fs_write</c>, <c>fs_append</c>, <c>fs_move</c>, <c>fs_remove</c> for text, and — when the port has a byte half and
/// a scope that accepts bytes — <c>fs_inspect</c> and <c>fs_patch</c> for bytes (<c>docs/24 §7.2</c>).
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
    /// <summary>Reads bytes as bytes: size, type, hash, hex dump, strings. Never returns a file as content.</summary>
    public const string InspectToolName = "fs_inspect";
    /// <summary>Edits bytes in place, at a stated offset, with a bounded literal.</summary>
    public const string PatchToolName = "fs_patch";

    /// <summary>Most characters one read returns. A model that gets a truncated file is told so; silence here would
    /// look like the file simply ended.</summary>
    private const int MaxReadChars = 32_000;

    private readonly IFileSystemAccess _access = access;

    /// <summary>
    /// How a non-text file is identified. Null when the host has no media store, and then "not UTF-8" stays the answer —
    /// which is honest: without a store there is nothing that can say whether the bytes are a picture.
    /// </summary>
    private readonly IMediaStore? _media = media;

    /// <summary>
    /// The byte half, when the instance also implements it. Optional in the constructor and not a separate parameter
    /// because the usual case is one <c>LocalFileSystemAccess</c> being both halves: two parameters here would be two
    /// things to wire wrong, and the failure would be a tool that silently vanishes.
    /// </summary>
    private readonly IBinaryFileAccess? _binary = access as IBinaryFileAccess;

    /// <summary>Largest literal one byte edit may carry. Bounded on purpose: this is a scalpel, not a way to author a
    /// megabyte of content the model cannot read back. Anything bigger is a file to move (`ssh_*`, media) or an
    /// append <c>from</c> another file, which never passes through the context at all.</summary>
    private const int MaxEditBytes = 4 * 1024;

    /// <summary>Largest hex dump one inspection returns. 4 KiB is 256 lines of the usual shape — enough to read a
    /// header, a table, or where a file stopped making sense, and small enough that asking twice is cheap.</summary>
    private const int MaxDumpBytes = 4 * 1024;

    public string Id => SourceId;

    /// <summary>The scopes the model may see. A subset of what the process registered, and the reason a tool call
    /// can be refused before it reaches the disk.</summary>
    public IReadOnlyList<string> Scopes { get; } = scopes;

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
        "Read one text file. A file that does not exist is reported as such rather than as an empty file — guessing "
        + "which one you are looking at is how a summary ends up describing nothing. Long files are read in slices: the "
        + "answer says where it stopped, and passing that back as \"offset\" continues from there. \"maxChars\" may be as "
        + "large as you like (0 = the rest of the file), since the only real cost is how much of it you are reading at "
        + "once.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Which registered area to read from."},
          "path":{"type":"string","description":"File path relative to the scope."},
          "offset":{"type":"integer","description":"Character position to start at. Default 0. Use the offset the last answer ended with to continue."},
          "maxChars":{"type":"integer","description":"How many characters to return. Default 32000; 0 means the rest of the file."}},
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

    public static readonly ModelToolDefinition InspectDefinition = new(InspectToolName,
        "Look inside a file that is not text: how big it is, what it actually is, its sha256, and — the part that "
        + "answers questions — a hex dump with the text beside it, or the printable strings in it. fs_read refuses a "
        + "binary because it cannot hand it over as text; this is how you read one anyway.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Which registered area the file is in."},
          "path":{"type":"string","description":"File path relative to the scope."},
          "mode":{"type":"string","enum":["summary","hex","strings"],
                  "description":"summary (default): size, detected type, sha256 and the first bytes. hex: a hex+ASCII dump of a range. strings: the printable runs, with their offsets."},
          "offset":{"type":"integer","description":"Byte position to start at, for hex and strings. Default 0."},
          "length":{"type":"integer","description":"For hex: how many bytes to dump. Default 256, at most 4096."}},
         "required":["scope","path"]}
        """);

    public static readonly ModelToolDefinition PatchDefinition = new(PatchToolName,
        "Edit the bytes of a file that already exists, at a stated offset. Four operations: overwrite (replace bytes, "
        + "growing the file if the write runs past the end), append (bytes, or a whole other file), splice (insert bytes "
        + "at an offset — a literal, or a range copied out of another file), truncate (cut it down). A literal is at most "
        + "4096 bytes, written as hex or base64: this is for fixing a header, a magic number or a length field, not for "
        + "writing a document. To move real content, append from a file, so the bytes never pass through the conversation.",
        """
        {"type":"object","properties":{
          "scope":{"type":"string","description":"Which registered area the file is in."},
          "path":{"type":"string","description":"File path relative to the scope. It must already exist."},
          "op":{"type":"string","enum":["overwrite","append","splice","truncate"],
                 "description":"overwrite: replace bytes at offset. append: add at the end. splice: insert at offset, pushing the rest along. truncate: cut down to length."},
          "offset":{"type":"integer","description":"Where the edit starts. For overwrite it may equal the file's length (= append); it may not be past the end. For splice: where the new bytes go."},
          "hex":{"type":"string","description":"The bytes, as hex, e.g. \"deadbeef\" or \"de ad be ef\". Give this or base64, not both."},
          "base64":{"type":"string","description":"The bytes, as base64."},
          "length":{"type":"integer","description":"truncate: the size to cut down to (it can only get smaller)."},
          "sourceScope":{"type":"string","description":"append/splice: take the bytes from a file in this scope, instead of from a literal."},
          "sourcePath":{"type":"string","description":"append/splice: the file to copy from. append takes all of it; splice takes sourceLength bytes from sourceOffset."},
          "sourceOffset":{"type":"integer","description":"splice: byte position in the source to start copying at. Default 0."},
          "sourceLength":{"type":"integer","description":"splice: how many bytes to copy out of the source. At most 4096."}},
         "required":["scope","path","op"]}
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

        // The byte half, only when the port has one and one of the offered scopes accepts bytes: a tool whose every
        // answer is "denied" is worse than no tool, because the model tries it and then tries to route around it.
        if (_binary is { } binary &&
            Scopes.Any(scope => binary.BinaryScopes.Contains(scope, StringComparer.Ordinal)))
        {
            offers =
            [
                .. offers,
                new(new InspectTool(this, binary), InspectDefinition, ActsExternally: false),
                new(new PatchTool(this, binary), PatchDefinition, ActsExternally: false)
            ];
        }

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
            // Slices, not a wall. The cap exists because the answer goes into a context window, and a window is a real
            // budget — but a cap with no way past it is not a budget, it is a file the model can never read: it used to
            // end with "已截断" and no instruction, which is exactly the shape that makes a model guess at the rest.
            // So: read from anywhere, ask for as much as you want, and be told where you stopped.
            var offset = (int)(ToolArguments.Number(arguments, "offset") ?? 0);
            var maxChars = (int)(ToolArguments.Number(arguments, "maxChars") ?? MaxReadChars);
            if (offset < 0)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "\"offset\" is a character position; it cannot be negative."));
            if (offset > text.Length)
                return ToolArguments.Ok(
                    $"（{scope}/{path} 只有 {text.Length} 个字符，offset={offset} 已经在末尾之后。要看结尾就从更小的 offset 开始。）");
            // Zero or less means "the rest": the same spelling the transfer ceilings use.
            var take = maxChars <= 0 ? text.Length - offset : Math.Min(maxChars, text.Length - offset);
            var slice = text.AsSpan(offset, take);
            if (offset == 0 && take == text.Length) return ToolArguments.Ok(text);
            var end = offset + take;
            var footer = $"\n…（字符 {offset}–{end}，共 {text.Length} 个；" +
                         (end < text.Length ? $"继续读用 offset={end}。" : "已经读到结尾。") + "）";
            return ToolArguments.Ok(string.Concat(slice, footer));
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

    // ---- bytes ----------------------------------------------------------------------------------

    /// <summary>
    /// Bytes, read as bytes. The alternative — handing base64 back to the model — spends a context window and answers
    /// nothing; a hex dump with the text beside it is what a person reads a header with, and it is text a model can
    /// reason about. Everything here is bounded by the request, not by the file: a 2 GiB image is six lines of summary
    /// and a 256-byte window unless somebody asks for more.
    /// </summary>
    private sealed class InspectTool(FileSystemTools owner, IBinaryFileAccess binary) : ISessionTool
    {
        public string Name => InspectToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var scope = ToolArguments.Text(arguments, "scope");
            if (owner.CheckScope(scope).Error is { } denied) return Result<SessionToolResult>.Failure(denied);
            var path = ToolArguments.Text(arguments, "path");
            if (string.IsNullOrWhiteSpace(path))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "fs_inspect needs the path of a file."));

            // Ceiling 0: an inspection reads a window, and the file it belongs to may be any size at all.
            var opened = await binary.OpenReadStreamAsync(scope!, path, 0, cancellationToken).ConfigureAwait(false);
            if (!opened.Ok) return Result<SessionToolResult>.Failure(opened.Error!);
            await using var stream = opened.Value!;

            var mode = (ToolArguments.Text(arguments, "mode") ?? "summary").Trim().ToLowerInvariant();
            return mode switch
            {
                "hex" => ToolArguments.Ok(await DumpAsync().ConfigureAwait(false)),
                "strings" => ToolArguments.Ok(await StringsAsync().ConfigureAwait(false)),
                "summary" or "" => ToolArguments.Ok(await SummaryAsync().ConfigureAwait(false)),
                _ => Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    $"Unknown mode '{mode}'. Use summary (what is it), hex (a dump) or strings (readable runs)."))
            };

            async Task<string> SummaryAsync()
            {
                var head = new byte[16];
                var read = await stream.ReadAsync(head, cancellationToken).ConfigureAwait(false);
                var hash = await Sha256Async(stream, cancellationToken).ConfigureAwait(false);
                var what = "未知类型";
                if (owner._media is { } store &&
                    await store.DescribeAsync(scope!, path!, cancellationToken).ConfigureAwait(false) is { Ok: true, Value: { } media })
                    what = $"{media.MediaType}（{media.Kind}）";
                var firstBytes = read == 0 ? "（空文件）" : Convert.ToHexString(head, 0, read).ToLowerInvariant();
                return $"{Describe(scope!, path!)}：{stream.Length} 字节；{what}；sha256 {hash}\n头 {read} 字节：{firstBytes}";
            }

            async Task<string> DumpAsync()
            {
                var offset = Math.Max(0, ToolArguments.Number(arguments, "offset") ?? 0);
                var length = Math.Clamp(ToolArguments.Number(arguments, "length") ?? 256, 1, MaxDumpBytes);
                if (offset > stream.Length)
                    return $"（{Describe(scope!, path!)} 只有 {stream.Length} 字节，offset={offset} 已经在末尾之后。）";
                var count = (int)Math.Min(length, stream.Length - offset);
                stream.Position = offset;
                var buffer = new byte[count];
                var read = 0;
                while (read < count)
                {
                    var got = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
                    if (got == 0) break;
                    read += got;
                }

                var text = new StringBuilder();
                for (var line = 0; line < read; line += 16)
                {
                    var take = Math.Min(16, read - line);
                    text.Append((offset + line).ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
                    for (var i = 0; i < 16; i++)
                    {
                        text.Append(i < take ? buffer[line + i].ToString("x2", CultureInfo.InvariantCulture) : "  ");
                        text.Append(i == 7 ? "  " : " ");
                    }
                    text.Append(" |");
                    for (var i = 0; i < take; i++)
                    {
                        var b = buffer[line + i];
                        text.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
                    }
                    text.Append("|\n");
                }
                var shown = $"{Describe(scope!, path!)}：{stream.Length} 字节，第 {offset}–{offset + read} 字节";
                return read == 0 ? shown + "（空）" : $"{shown}\n{text}";
            }

            async Task<string> StringsAsync()
            {
                var offset = Math.Max(0, ToolArguments.Number(arguments, "offset") ?? 0);
                stream.Position = Math.Min(offset, stream.Length);
                var buffer = new byte[64 * 1024];
                var run = new StringBuilder();
                var lines = new List<string>();
                var total = 0;
                bool more = false;

                // `next` is where the buffer being scanned starts, so a run's offset is "buffer start + index in
                // buffer - length of the run": the one thing that has to be right, since an offset is the only way
                // this answer is useful.
                var next = offset;
                while (!more)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    for (var i = 0; i < read; i++)
                    {
                        var b = buffer[i];
                        if (b is >= 0x20 and < 0x7f) { run.Append((char)b); continue; }
                        if (run.Length >= 4) Add(next + i - run.Length);
                        run.Clear();
                    }
                    next += read;
                }
                if (run.Length >= 4) Add(next - run.Length);
                if (lines.Count == 0) return $"{Describe(scope!, path!)}：从第 {offset} 字节起没有 4 个及以上可打印字符的连续串。";
                return $"{Describe(scope!, path!)}：可打印串（偏移 + 内容）\n{string.Join("\n", lines)}"
                       + (more ? "\n…（到 200 条或 8 KiB 就停了；offset 往后挪一点继续看。）" : "");

                void Add(long at)
                {
                    if (total + run.Length > 8 * 1024 || lines.Count >= 200) { more = true; return; }
                    var one = run.ToString();
                    lines.Add($"{at:x8}  {(one.Length > 120 ? one[..120] + "…" : one)}");
                    total += run.Length + 12;
                }
            }
        }
    }

    /// <summary>
    /// Bytes edited in place, with a stated position and a bounded literal. Four operations, and each of them is
    /// something a person would describe out loud ("put these four bytes at 0x10", "cut it at two megabytes", "join
    /// these two parts"). Nothing here accepts a document: the value is in what the model can <i>fix</i>, and the limit
    /// is what keeps it from becoming a channel for content it cannot read back.
    /// </summary>
    private sealed class PatchTool(FileSystemTools owner, IBinaryFileAccess binary) : ISessionTool
    {
        public string Name => PatchToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(Name);
            var scope = ToolArguments.Text(arguments, "scope");
            if (owner.CheckScope(scope).Error is { } denied) return Result<SessionToolResult>.Failure(denied);
            var path = ToolArguments.Text(arguments, "path");
            if (string.IsNullOrWhiteSpace(path))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "fs_patch needs the path of a file."));
            var op = (ToolArguments.Text(arguments, "op") ?? "").Trim().ToLowerInvariant();
            var offset = ToolArguments.Number(arguments, "offset") ?? 0;
            var sourceScope = ToolArguments.Text(arguments, "sourceScope");
            var sourcePath = ToolArguments.Text(arguments, "sourcePath");
            var fromFile = !string.IsNullOrWhiteSpace(sourcePath);

            switch (op)
            {
                case "truncate":
                {
                    var length = ToolArguments.Number(arguments, "length");
                    if (length is not { } cut || cut < 0)
                        return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                            "truncate needs \"length\": the size to cut down to."));
                    var truncated = await binary.TruncateAsync(scope!, path, cut, cancellationToken).ConfigureAwait(false);
                    return truncated.Ok
                        ? ToolArguments.Ok($"{Describe(scope!, path!)}：已截到 {truncated.Value} 字节。")
                        : Result<SessionToolResult>.Failure(truncated.Error!);
                }
                case "append" when fromFile:
                {
                    var joined = await binary.AppendFromFileAsync(scope!, path, sourceScope ?? scope!, sourcePath!,
                        cancellationToken).ConfigureAwait(false);
                    return joined.Ok
                        ? ToolArguments.Ok($"{Describe(scope!, path!)}：已接上 {Describe(sourceScope ?? scope!, sourcePath!)}，现在 {joined.Value} 字节。")
                        : Result<SessionToolResult>.Failure(joined.Error!);
                }
                case "append":
                {
                    var literal = await LiteralAsync(arguments, cancellationToken).ConfigureAwait(false);
                    if (!literal.Ok) return Result<SessionToolResult>.Failure(literal.Error!);
                    // Appending is a patch at the end: one code path, and the "may not be past the end" rule holds.
                    var length = await LengthOfAsync().ConfigureAwait(false);
                    if (!length.Ok) return Result<SessionToolResult>.Failure(length.Error!);
                    var grown = await binary.PatchAsync(scope!, path, length.Value, literal.Value, insert: false,
                        cancellationToken).ConfigureAwait(false);
                    return grown.Ok
                        ? ToolArguments.Ok($"{Describe(scope!, path!)}：在末尾写入 {literal.Value!.Length} 字节，现在 {grown.Value} 字节。")
                        : Result<SessionToolResult>.Failure(grown.Error!);
                }
                case "overwrite" or "splice":
                {
                    var bytes = fromFile
                        ? await RangeFromFileAsync().ConfigureAwait(false)
                        : await LiteralAsync(arguments, cancellationToken).ConfigureAwait(false);
                    if (!bytes.Ok) return Result<SessionToolResult>.Failure(bytes.Error!);
                    var edited = await binary.PatchAsync(scope!, path, offset, bytes.Value!, insert: op == "splice",
                        cancellationToken).ConfigureAwait(false);
                    if (!edited.Ok) return Result<SessionToolResult>.Failure(edited.Error!);
                    var verb = op == "splice" ? "插入" : "覆盖";
                    return ToolArguments.Ok(
                        $"{Describe(scope!, path!)}：在第 {offset} 字节{verb} {bytes.Value!.Length} 字节，现在 {edited.Value} 字节。");
                }
                default:
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                        $"Unknown op '{op}'. Use overwrite (replace at offset), append (add at the end), splice (insert at offset) or truncate (cut down)."));
            }

            async Task<Result<long>> LengthOfAsync()
            {
                var stat = await owner._access.StatAsync(scope!, path!, cancellationToken).ConfigureAwait(false);
                if (stat.Error is { } statError) return Result<long>.Failure(statError);
                return stat.Value switch
                {
                    { IsDirectory: false } file => Result<long>.Success(file.Bytes),
                    { IsDirectory: true } => Result<long>.Failure(new(ErrorCode.Invalid,
                        $"{Describe(scope!, path!)} is a directory.")),
                    // Absence says so, in the same words the port uses: an edit does not create the file it edits.
                    _ => Result<long>.Failure(new(ErrorCode.Invalid,
                        $"{Describe(scope!, path!)} does not exist; there is nothing to edit."))
                };
            }

            async Task<Result<byte[]>> RangeFromFileAsync()
            {
                var readScope = sourceScope ?? scope!;
                var start = ToolArguments.Number(arguments, "sourceOffset") ?? 0;
                var count = ToolArguments.Number(arguments, "sourceLength") ?? 0;
                if (start < 0 || count <= 0)
                    return Result<byte[]>.Failure(new(ErrorCode.Invalid,
                        "splice from a file needs \"sourceLength\" (how many bytes) and optionally \"sourceOffset\"."));
                if (count > MaxEditBytes)
                    return Result<byte[]>.Failure(new(ErrorCode.Invalid,
                        $"At most {MaxEditBytes} bytes may be spliced from a file. To move more, append the whole file "
                        + "(op=append with sourcePath) — the bytes never pass through the conversation."));

                // Refused here as well as in the port, so the sentence names the tool's own limit rather than arriving
                // as "scope does not allow binary transfers" from three layers down.
                if (owner.CheckScope(readScope).Error is { } readDenied) return Result<byte[]>.Failure(readDenied);
                var opened = await binary.OpenReadStreamAsync(readScope, sourcePath!, 0, cancellationToken).ConfigureAwait(false);
                if (!opened.Ok) return Result<byte[]>.Failure(opened.Error!);
                await using var stream = opened.Value!;
                if (start > stream.Length)
                    return Result<byte[]>.Failure(new(ErrorCode.Invalid,
                        $"{Describe(readScope, sourcePath!)} is {stream.Length} bytes; sourceOffset {start} is past its end."));
                var buffer = new byte[Math.Min(count, (int)(stream.Length - start))];
                stream.Position = start;
                var read = 0;
                while (read < buffer.Length)
                {
                    var got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                    if (got == 0) break;
                    read += got;
                }
                return read == 0
                    ? Result<byte[]>.Failure(new(ErrorCode.Invalid, "The source range is empty; there is nothing to splice."))
                    : Result<byte[]>.Success(buffer[..read]);
            }
        }

        /// <summary>
        /// The bytes of one edit, from <c>hex</c> or <c>base64</c>. Exactly one, at most
        /// <see cref="MaxEditBytes"/> — a limit the model is told about here rather than discovering as an opaque
        /// refusal from the disk.
        /// </summary>
        private static async Task<Result<byte[]>> LiteralAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hex = ToolArguments.Text(arguments, "hex");
            var base64 = ToolArguments.Text(arguments, "base64");
            if (hex is not null && base64 is not null)
                return Result<byte[]>.Failure(new(ErrorCode.Invalid, "Give either \"hex\" or \"base64\", not both."));
            if (hex is null && base64 is null)
                return Result<byte[]>.Failure(new(ErrorCode.Invalid, "There are no bytes to write: give \"hex\" or \"base64\"."));

            byte[] bytes;
            if (hex is not null)
            {
                var cleaned = string.Concat(hex.Where(c => !char.IsWhiteSpace(c) && c is not ',' and not '-'));
                if (cleaned.Length % 2 != 0)
                    return Result<byte[]>.Failure(new(ErrorCode.Invalid,
                        "hex must name whole bytes: an even number of hex digits, e.g. \"deadbeef\"."));
                try { bytes = Convert.FromHexString(cleaned); }
                catch (FormatException)
                {
                    return Result<byte[]>.Failure(new(ErrorCode.Invalid,
                        "hex contains something that is not a hex digit: 0-9 and a-f only."));
                }
            }
            else
            {
                try { bytes = Convert.FromBase64String(base64!); }
                catch (FormatException)
                {
                    return Result<byte[]>.Failure(new(ErrorCode.Invalid, "base64 is not valid base64."));
                }
            }

            return bytes.Length > MaxEditBytes
                ? Result<byte[]>.Failure(new(ErrorCode.Invalid,
                    $"At most {MaxEditBytes} bytes may be written at once. To move more, put them in a file and use "
                    + "op=append with sourcePath — that path never passes the bytes through the conversation."))
                : Result<byte[]>.Success(bytes);
        }
    }

    /// <summary>sha256 of a whole stream, read through one buffer: a hash of a large file must not cost the file's size
    /// in memory.</summary>
    private static async Task<string> Sha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        stream.Position = 0;
        var buffer = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
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
