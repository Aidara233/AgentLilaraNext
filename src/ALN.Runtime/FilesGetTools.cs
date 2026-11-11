using System.Text;
using ALN.Contracts;
using ALN.Contracts.Adapters;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The one way a Session reaches a file that is sitting on the platform rather than in this process: <c>files_get</c>.
///
/// <b>Three shapes, decided by what the argument looks like — never by what failed.</b> No argument lists what is
/// available; something that is not a handle is a keyword search; a handle is a fetch. Trying one shape and falling back
/// to the next on failure would turn "the platform refused to hand over this file" into "so it must have been a search",
/// and the caller would get a listing where it asked for bytes (docs/28 §6.8).
///
/// <b>Where the bytes land is the caller's answer, not this tool's.</b> A fetch requires <c>scope</c> and
/// <c>path</c>, both named by the model, and the port decides whether that scope may hold bytes at all — this class does
/// not keep a list of scopes, because a list here would be a second place to disagree with the one that matters.
/// </summary>
public sealed class FilesGetTools(IChannelManager channels, IAdapterManager adapters, IBinaryFileAccess files,
    IJobRegistry? jobs = null) : ISessionToolSource
{
    private readonly IChannelManager _channels = channels;
    private readonly IAdapterManager _adapters = adapters;
    private readonly IBinaryFileAccess _files = files;
    /// <summary>
    /// Where a fetch that is too big to wait for gets written down. Optional: a host with no ledger still fetches files,
    /// it just does it inside the turn (docs/28 §9.4).
    /// </summary>
    private readonly IJobRegistry? _jobs = jobs;

    /// <summary>
    /// The largest file this tool will pull, when what is being handed over is whatever the platform happens to have.
    ///
    /// <b>Not the same question as <see cref="IBinaryFileAccess.MaxTransferBytes"/>.</b> That one answers "how much may
    /// one transfer move through this process for a caller that picked the file itself" — a tool moving a tarball, an SSH
    /// pull — and 64 MiB is a good answer there. This one answers "how big a thing may somebody have put on the platform
    /// and then asked for". A person's group file area is not a place to be cautious about; it is a place where a system
    /// image happens to live. 4 GiB is where that stops being a file and starts being a way to fill a disk, and it is
    /// also as far as QQ's own file area goes.
    ///
    /// It is a number about the destination disk, not about this host's memory: the adapter streams the bytes, so a file
    /// of this size costs one buffer on the way in (docs/28 §6.6.1).
    /// </summary>
    public const long MaxFetchBytes = 4L * 1024 * 1024 * 1024;

    public const string TypeId = "files.control";
    public const string SourceId = "system.files-tools";
    public const string ToolName = "files_get";

    /// <summary>How many entries a listing shows when nobody said. A group's file area can hold hundreds, and a model that
    /// is handed all of them spends its whole window on a directory index.</summary>
    public const int DefaultLimit = 20;

    /// <summary>How many rows a search looks through. Wider than a listing on purpose: a search is asking about the whole
    /// shelf, and stopping at twenty would answer "not there" about a file that is.</summary>
    public const int SearchDepth = 200;

    public string Id => SourceId;

    public static readonly ModelToolDefinition GetDefinition = new(ToolName,
        "Files the platform is holding for this conversation — a group's file area on QQ. Three ways to call it:\n"
        + "- no \"target\": list what is there, newest first.\n"
        + "- \"target\" that is not a handle: search names for that text.\n"
        + "- \"target\" that is a handle (starts with f:): fetch that file. A fetch REQUIRES \"scope\" and \"path\": "
        + "where to put it on this host. \"path\" is relative to the root of that scope (workspace is the usual one, "
        + "and its subdirectories are yours to name: \"downloads/report.zip\"); the scope has to be one this host "
        + "accepts bytes into, and being refused tells you which are.\n"
        + "Nothing is downloaded by itself: a file somebody sent stays on the platform until you ask for it here. "
        + "Large files take a while; the transfer stops rather than truncating if it exceeds this host's limit.",
        """
        {"type":"object","properties":{
          "target":{"type":"string","description":"A file handle (f:…) to fetch, or text to search names for. Omit to list."},
          "scope":{"type":"string","description":"With a handle: where to put it. e.g. workspace. Required for a fetch."},
          "path":{"type":"string","description":"With a handle: a path relative to that scope's root, e.g. downloads/report.zip. Required for a fetch."},
          "limit":{"type":"number","description":"How many entries a listing shows. Default 20, at most 200."}},
         "additionalProperties":false}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> offers =
        [
            // A fetch writes to this host's disk, so a turn that did it acted outside the conversation — which is what
            // ActsExternally means here ("was that text a reply, or was it working").
            new(new GetTool(this, request.Channel, request.SessionId), GetDefinition)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private sealed class GetTool(FilesGetTools owner, Address? channel, string sessionId) : ISessionTool
    {
        public string Name => ToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            var root = ToolArguments.Parse(call.ArgumentsJson);
            if (root is null) return ToolArguments.BadJson(ToolName);
            var located = owner.Locate(channel, out var adapter, out var conversation);
            if (located is not null) return Result<SessionToolResult>.Failure(located);

            var target = ToolArguments.Text(root.Value, "target")?.Trim();
            var limit = (int)Math.Clamp(ToolArguments.Number(root.Value, "limit") ?? DefaultLimit, 1, 200);

            if (string.IsNullOrEmpty(target))
                return await ListAsync(adapter!, conversation!, limit, cancellationToken).ConfigureAwait(false);

            // The shape decides, once. A handle is never searched for, and a keyword is never fetched.
            if (FileHandles.Looks(target))
                return await FetchAsync(adapter!, conversation!, target, root.Value, cancellationToken).ConfigureAwait(false);
            return await SearchAsync(adapter!, conversation!, target, limit, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<Result<SessionToolResult>> ListAsync(IFileCatalogAdapter adapter, string conversation,
            int limit, CancellationToken cancellationToken)
        {
            var listed = await adapter.ListAsync(conversation, limit, cancellationToken).ConfigureAwait(false);
            if (!listed.Ok || listed.Value is null)
                return Result<SessionToolResult>.Failure(listed.Error ??
                    new(ErrorCode.Unavailable, "平台的回答看不懂。"));

            var text = new StringBuilder();
            if (listed.Value.Files.Count == 0) text.Append("平台上现在没有文件。");
            else
            {
                text.Append(listed.Value.Files.Count).Append(" 个文件，最近的在最前：");
                foreach (var file in listed.Value.Files) text.Append('\n').Append(Line(file));
            }
            if (listed.Value.Note is { Length: > 0 } note) text.Append('\n').Append(note);
            if (listed.Value.Files.Count > 0)
                text.Append("\n取其中一个：files_get {\"target\":\"<handle>\",\"scope\":\"workspace\"," +
                            "\"path\":\"<放哪>\"}——文件名和放哪由你定，必须说清。");
            return ToolArguments.Ok(text.ToString());
        }

        private async ValueTask<Result<SessionToolResult>> SearchAsync(IFileCatalogAdapter adapter, string conversation,
            string keyword, int limit, CancellationToken cancellationToken)
        {
            var listed = await adapter.ListAsync(conversation, SearchDepth, cancellationToken).ConfigureAwait(false);
            if (!listed.Ok || listed.Value is null)
                return Result<SessionToolResult>.Failure(listed.Error ??
                    new(ErrorCode.Unavailable, "平台的回答看不懂。"));

            var matches = listed.Value.Files
                .Where(file => file.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToArray();
            if (matches.Length == 0)
            {
                var text = new StringBuilder();
                text.Append("没有名字里带 \"").Append(keyword).Append("\" 的文件");
                text.Append(listed.Value.Files.Count == 0
                    ? "——平台上现在一个文件都没有。"
                    : $"（看了 {listed.Value.Files.Count} 个）。不带 target 再问一次，可以看到全部。");
                if (listed.Value.Note is { Length: > 0 } note) text.Append('\n').Append(note);
                return ToolArguments.Ok(text.ToString());
            }

            var found = new StringBuilder();
            found.Append(matches.Length).Append(" 个匹配：");
            foreach (var file in matches) found.Append('\n').Append(Line(file));
            return ToolArguments.Ok(found.ToString());
        }

        private async ValueTask<Result<SessionToolResult>> FetchAsync(IFileCatalogAdapter adapter, string conversation,
            string handle, System.Text.Json.JsonElement arguments, CancellationToken cancellationToken)
        {
            var scope = ToolArguments.Text(arguments, "scope")?.Trim();
            var path = ToolArguments.Text(arguments, "path")?.Trim();
            // No invented destination, no "put it somewhere sensible": a file that lands where nobody said is a file the
            // next turn has to go looking for, and the model is the one that knows what it wants this for.
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(path))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    "取文件必须说清放到哪：\"scope\" 和 \"path\" 都是必填的。\"path\" 是从那个 scope 根开始的相对路径，" +
                    $"例如 {{\"scope\":\"workspace\",\"path\":\"downloads/report.zip\"}}。这个宿主收字节的 scope：" +
                    $"{string.Join("、", owner._files.BinaryScopes)}。"));

            // Always the ledger, never this turn. "Will this one be slow" is a question about a network nobody can answer
            // before the first byte moves, and a turn that blocks long enough for a person to notice reads as a hang. What
            // the job adds is only *when* the bytes move; the destination is decided here, because a background task that
            // chose its own path would write a file nobody can find a day later (docs/28 §6.8).
            if (owner._jobs is null)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Unavailable,
                    "这个宿主没有后台任务账本，而取文件一律走后台——没有账本就没人能告诉你会话什么时候拿到文件。"));

            // The one thing worth answering before anything is in flight: a scope that takes no bytes at all. A path that
            // escapes its scope stays the port's call at write time, and it costs a failed job instead of a failed call —
            // that is the price of not holding the turn open.
            if (!owner._files.BinaryScopes.Contains(scope))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                    $"\"{scope}\" 不收字节。这个宿主收字节的 scope：{string.Join("、", owner._files.BinaryScopes)}。"));

            return await owner.StartBackgroundAsync(adapter, conversation, sessionId, handle, scope!, path!,
                cancellationToken).ConfigureAwait(false);
        }

        private static string Line(PlatformFile file)
        {
            var text = new StringBuilder();
            text.Append("- ").Append(file.Name).Append("  ").Append(file.Handle);
            if (file.Bytes is { } bytes) text.Append("  ").Append(Size(bytes));
            if (file.At is { } at) text.Append("  ").Append(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            if (file.From is { Length: > 0 } from) text.Append("  ").Append(from);
            return text.ToString();
        }

        private static string Size(long bytes) => FilesGetTools.Size(bytes);
    }

    /// <summary>
    /// Starts the fetch as a job and returns at once.
    ///
    /// <b>The destination is fixed here, not when the job runs.</b> A background task that chose its own path would write
    /// a file nobody can find, and "which file was that" is exactly the question a person asks a day later (docs/28 §6.8).
    /// What the job adds is only *when* the bytes move — and the ledger's completion, which is what wakes the Session
    /// (docs/28 §9.4).
    /// </summary>
    private async ValueTask<Result<SessionToolResult>> StartBackgroundAsync(IFileCatalogAdapter adapter, string conversation,
        string sessionId, string handle, string scope, string path, CancellationToken cancellationToken)
    {
        var opened = await _jobs!.OpenAsync(new(JobKind, $"{scope}/{path}", sessionId), cancellationToken)
            .ConfigureAwait(false);
        if (!opened.Ok || opened.Value is null)
            return Result<SessionToolResult>.Failure(opened.Error ?? new(ErrorCode.Unavailable, "开不了后台任务。"));

        // Not awaited and not tied to the turn: the turn is about to end, and a cancelled turn is not a reason to abandon
        // a file somebody asked for.
        _ = Task.Run(() => RunFetchAsync(adapter, conversation, sessionId, opened.Value.Id, handle, scope, path),
            CancellationToken.None);

        return ToolArguments.Ok(
            $"放到后台取了：job {opened.Value.Id}，落点 {scope}/{path}。" +
            "取完会叫醒这个会话，到时候用 jobs 看结果；不用守着它。");
    }

    private async Task RunFetchAsync(IFileCatalogAdapter adapter, string conversation, string sessionId, string jobId,
        string handle, string scope, string path)
    {
        try
        {
            var fetched = await adapter.FetchAsync(conversation, handle, MaxFetchBytes,
                CancellationToken.None).ConfigureAwait(false);
            if (!fetched.Ok || fetched.Value is null)
            {
                await SettleAsync(sessionId, jobId, null, fetched.Error?.Message ?? "取不回来。").ConfigureAwait(false);
                return;
            }

            var content = fetched.Value;
            await using (content.Content)
            {
                var saved = await _files.SaveAsync(scope, path, content.Content, MaxFetchBytes,
                    CancellationToken.None).ConfigureAwait(false);
                if (!saved.Ok)
                {
                    await SettleAsync(sessionId, jobId, null, saved.Error?.Message ?? "存不下来。").ConfigureAwait(false);
                    return;
                }
            }
            var bytes = await SizeAtAsync(scope, path).ConfigureAwait(false);
            await SettleAsync(sessionId, jobId,
                (string.IsNullOrEmpty(content.Name) ? "已取回 " : $"已取回 {content.Name} ") + $"→ {scope}/{path}"
                + (bytes is { } known ? $"（{Size(known)}）" : string.Empty), null)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A thrown fetch is still an ending, and an ending nobody wrote down is a job the Session waits on forever.
            await SettleAsync(sessionId, jobId, null, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the ending, which is what wakes the Session. A failure to write it is not worth failing this task over: the
    /// row stays running and the next restart turns it into "interrupted", which is at least true.
    /// </summary>
    private async Task SettleAsync(string sessionId, string jobId, string? result, string? error)
    {
        if (_jobs is null) return;
        if (error is null) await _jobs.CompleteAsync(sessionId, jobId, result, CancellationToken.None).ConfigureAwait(false);
        else await _jobs.FailAsync(sessionId, jobId, error, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<long?> SizeAtAsync(string scope, string path)
    {
        var opened = await _files.OpenReadStreamAsync(scope, path, long.MaxValue, CancellationToken.None).ConfigureAwait(false);
        if (!opened.Ok || opened.Value is null) return null;
        await using var stream = opened.Value;
        return stream.CanSeek ? stream.Length : null;
    }

    /// <summary>What a background fetch is called in the ledger: one kind for every platform, so "what is still coming" is
    /// one question with one answer (docs/28 §9.1).</summary>
    private const string JobKind = "file.fetch";

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B"
    };

    /// <summary>
    /// Which adapter, and which of its conversations, this Session is talking through. Null means "found"; an error means
    /// why not — and the reasons are all different, so each one says its own.
    ///
    /// It goes through the Channel rather than through the Session's own state because the platform conversation id lives
    /// on the binding: a Session is a conversation with one Channel, and a Channel is aimed at one platform conversation.
    /// </summary>
    private Error? Locate(Address? channel, out IFileCatalogAdapter? adapter, out string conversation)
    {
        adapter = null;
        conversation = string.Empty;
        if (channel is null)
            return new(ErrorCode.Invalid,
                "这个会话没有连着任何平台会话，所以没有文件可取：文件在平台上，而这个会话没有平台。");

        var info = _channels.Get(channel.Value);
        if (!info.Ok || info.Value is null)
            return new(ErrorCode.Unavailable, $"读不到这个会话的频道（{channel}）。", info.Error?.Message);
        // A Channel serves one conversation. More than one would mean the question "which platform files" has no answer.
        if (info.Value.Bindings.Count != 1)
            return new(ErrorCode.Unavailable,
                $"这个频道绑定了 {info.Value.Bindings.Count} 个平台会话，文件不知道该从哪一个取。");

        var binding = info.Value.Bindings[0];
        var found = _adapters.Get(binding.Adapter);
        if (!found.Ok || found.Value is null)
            return new(ErrorCode.Unavailable, $"适配器 {binding.Adapter} 现在不可用。", found.Error?.Message);
        // Optional on purpose (I-37): an adapter that cannot do this simply does not implement it, and that is one
        // sentence rather than a capability to keep in sync.
        if (found.Value is not IFileCatalogAdapter catalog)
            return new(ErrorCode.Unavailable,
                $"这个平台（{binding.Adapter}）的适配器不提供文件目录，所以取不了平台上的文件。" +
                "现在只有 QQ 的群文件区会实现它。");
        adapter = catalog;
        conversation = binding.PlatformConversationId;
        return null;
    }
}

/// <summary>The module wrapper: a type id, a factory, and registration into the session's tool sources.</summary>
public sealed class FilesToolsModule(ModuleId id, FilesGetTools tools, ISessionExtensionRegistry extensions) : IKernelModule
{
    private bool _registered;

    public ModuleId Id { get; } = id;

    public static Result<IKernelModule> FromManifest(ModuleManifest manifest, IChannelManager channels,
        IAdapterManager adapters, IBinaryFileAccess files, ISessionExtensionRegistry extensions,
        IJobRegistry? jobs = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Type, FilesGetTools.TypeId, StringComparison.Ordinal))
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                $"This factory builds {FilesGetTools.TypeId}, not {manifest.Type}."));
        return Result<IKernelModule>.Success(new FilesToolsModule(manifest.Id,
            new FilesGetTools(channels, adapters, files, jobs), extensions));
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
