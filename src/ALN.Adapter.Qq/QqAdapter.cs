using System.Threading.Channels;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Adapters;

namespace ALN.Adapter.Qq;

/// <summary>
/// A QQ account reached through an OneBot v11 implementation over a forward WebSocket.
///
/// What it is responsible for: the connection (and its reconnects), turning pushed events into
/// <see cref="AdapterEnvelope"/>, and turning <see cref="OutboundMessage"/> into a platform call. What
/// it deliberately does not do: decide whether a message is interesting, deduplicate across adapters,
/// or create anything. It is one account and one socket.
///
/// v1 scope: group text in, group text out, plus pictures in (bytes fetched from the bridge's url and handed to the
/// Channel as an attachment). Private chats, voice, and rate-limit pacing are not implemented — see docs/16.
/// </summary>
public sealed class QqAdapter : AdapterBase, IAdapterHealth, IAdapterIdentity, IParameterTarget, ITargetNaming, IRawAdapter,
    IFileCatalogAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int SeenCapacity = 512;
    /// <summary>How long one picture may take to arrive before the message goes on without it.</summary>
    private static readonly TimeSpan MediaFetchTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How big a picture may be to be <b>sent</b>: 8 MiB of bytes, about 11 MiB of base64 inside one WebSocket frame.
    ///
    /// Not a taste—a limit somebody else's process pays for. NapCat is a Node process holding the whole segment, and
    /// reports of it dying on a ~40 MB base64 segment (and of memory spikes well before that) are why this is smaller
    /// than what the store will take in. Over the limit the picture travels as its one-line description, so the message
    /// still arrives (docs/16 §12.3).
    /// </summary>
    public const int MaxOutboundImageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// How big a file may be to go as <b>base64</b>: the same 8 MiB, and for the same reason — it is the one route that
    /// puts the whole thing in somebody else's memory. Everything bigger has to travel as a URL or a path, which is the
    /// point of having those routes at all (docs/28 §6.6). It is not a limit on what may be sent, only on this route.
    /// </summary>
    public const int MaxOutboundFileBase64Bytes = 8 * 1024 * 1024;
    /// <summary>
    /// One client for every picture this account ever fetches: a new one per message would leak sockets on a busy
    /// group. It is never asked for anything but the bridge's own media urls.
    /// </summary>
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly IEventSink _sink;
    private readonly IModuleLogger _log;
    private readonly Uri _endpoint;
    private readonly OneBotConnection _connection;
    private readonly QqNames _names;
    private readonly InboundJournal _journal;
    private readonly IInboundAcks? _acks;
    private readonly IBinaryFileAccess? _files;
    private readonly IMediaUrlSource? _urls;
    /// <summary>
    /// "<c>their/prefix=scope/our/prefix</c>" pairs from the instance's <c>pathMap</c> setting, longest first.
    ///
    /// It exists because <c>get_image</c> answers with a path inside the bridge's own container: with a shared volume the
    /// operator can say which of their directories is which of our scopes, and then a picture is reachable without a
    /// url at all. Without it, the honest answer is "a picture we cannot reach" (docs/16 §12.1).
    /// </summary>
    private readonly IReadOnlyList<(string From, string Scope, string To)> _pathMap;
    /// <summary>How a picture leaves: a URL the bridge fetches (default), base64 in the frame, or the fallback chain.</summary>
    private readonly ImageDelivery _imageDelivery;
    /// <summary>How a file leaves: a URL (default), a path the bridge opens, base64 for small ones, or two-step.</summary>
    private readonly FileDelivery _fileDelivery;

    /// <summary>
    /// The three ways a picture can leave, and why all three exist. <see cref="Auto"/> is the default because it is the
    /// one that needs no decision: the host usually serves a URL (fast, and it does not care how big the picture is),
    /// and when it does not, base64 still works for a small one. <see cref="Base64"/> is the escape hatch for a bridge
    /// that cannot reach this machine at all; <see cref="Url"/> is for proving which of the two a deployment is actually
    /// using (docs/16 §12.5).
    /// </summary>
    private enum ImageDelivery { Auto, Url, Base64 }

    /// <summary>
    /// The five ways a file can leave, because implementations disagree about what <c>file</c> accepts and because the
    /// size that makes base64 a bad idea for a picture makes it an impossible idea for a file (docs/28 §6.6).
    /// <see cref="Auto"/> prefers a URL — one call, no bytes through this process — then a path the bridge can open
    /// itself, then base64 for something small. <see cref="TwoStep"/> is for an implementation whose upload takes paths
    /// only: the bridge fetches our URL onto its own disk first, and the upload names that path.
    ///
    /// There is deliberately no trial and error between them. An upload that fails may have half arrived, so the route is
    /// decided before the call, and which one was taken is reported rather than guessed at afterwards.
    /// </summary>
    private enum FileDelivery { Auto, Url, Path, Base64, TwoStep }

    /// <summary>What was put in the <c>file</c> parameter, and which of the routes it came from — the second half is
    /// what makes a log line or a receipt answer "how did it go" rather than only "it went".</summary>
    private sealed record FileTransport(string Argument, string Kind);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly object _seenGate = new();
    private CancellationTokenSource? _run;
    private Task? _loop;
    private Task? _pump;
    /// <summary>Bridge events waiting to be handled, so handling one can talk back without blocking the reader.</summary>
    private Channel<JsonElement>? _inbound;
    private Task? _replayLoop;
    private volatile string? _detail;

    public QqAdapter(AdapterSpec spec, IEventSink sink, IModuleLogger log, IInboundAcks? acks = null,
        IQueuedMessageStore? queue = null,
        /// <summary>How a picture named by a reference becomes bytes. Optional; see <see cref="ReadPictureAsync"/>.</summary>
        IBinaryFileAccess? files = null,
        /// <summary>How it becomes a URL a bridge can fetch instead. Optional; without it, base64.</summary>
        IMediaUrlSource? urls = null)
        : base(spec.Address, spec.Descriptor)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _acks = acks;
        _files = files;
        _urls = urls;
        // One journal per instance: two QQ accounts on one host must not share one.
        _journal = new InboundJournal(queue, _log, Address);

        var settings = spec.Settings ?? new Dictionary<string, string>();
        if (!settings.TryGetValue("endpoint", out var endpoint) || string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Adapter settings must contain 'endpoint' (ws://host:port).", nameof(spec));
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _endpoint!) ||
            (_endpoint.Scheme != "ws" && _endpoint.Scheme != "wss"))
            throw new ArgumentException("Adapter setting 'endpoint' must be an absolute ws:// or wss:// URL.", nameof(spec));

        settings.TryGetValue("accessToken", out var token);
        _connection = new OneBotConnection(_endpoint, string.IsNullOrWhiteSpace(token) ? null : token, log);
        _pathMap = ParsePathMap(settings, log);
        var delivery = settings.TryGetValue("imageDelivery", out var declared) ? declared.Trim().ToLowerInvariant() : "auto";
        _imageDelivery = delivery switch
        {
            "" or "auto" => ImageDelivery.Auto,
            "url" => ImageDelivery.Url,
            "base64" => ImageDelivery.Base64,
            _ => throw new ArgumentException(
                "Adapter setting 'imageDelivery' must be auto (url, else base64), url (url only) or base64 (never a url).",
                nameof(spec))
        };
        var fileDelivery = settings.TryGetValue("fileDelivery", out var declaredFile) ? declaredFile.Trim().ToLowerInvariant() : "auto";
        _fileDelivery = fileDelivery switch
        {
            "" or "auto" => FileDelivery.Auto,
            "url" => FileDelivery.Url,
            "path" => FileDelivery.Path,
            "base64" => FileDelivery.Base64,
            "two-step" or "twostep" or "download" => FileDelivery.TwoStep,
            _ => throw new ArgumentException(
                "Adapter setting 'fileDelivery' must be auto (url, else path, else base64), url, path (a path on the "
                + "bridge's own disk, mapped by pathMap), base64 (small files only) or two-step (the bridge downloads "
                + "our url itself before uploading the local path).",
                nameof(spec))
        };
        _names = new QqNames(_connection, log);
        _detail = $"未连接（{_endpoint}）";
    }

    public bool Connected => _connection.IsConnected;

    // Learned from the platform on connect, not typed into a config file. Null until the platform says.
    public string? AccountId => _connection.SelfId?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? DisplayName => _connection.Nickname;

    public string? HealthDetail => _connection.IsConnected
        ? $"已连接 {_endpoint}" + (DisplayName is { Length: > 0 } name ? $"（{name} / {AccountId}）" : string.Empty)
        : _detail ?? _connection.LastError;

    /// <summary>
    /// How this platform names somebody: the group card when the question is about a group, the nickname otherwise, and
    /// the remark this account gave them. See <see cref="QqNames"/> for why it can answer from what it already knows
    /// and when it has to go and ask.
    /// </summary>
    public ValueTask<TargetName?> ResolveAsync(string targetId, string? scope, CancellationToken cancellationToken) =>
        _names.ResolveAsync(targetId, scope, cancellationToken);

    public override async ValueTask<Result> StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning && _loop is not null) return Result.Success();
        var started = await base.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Ok) return started;

        _run = new CancellationTokenSource();
        var token = _run.Token;
        // Events are handled off the receive loop, through one queue: handling a message can now talk back to the bridge
        // (get_image), and an answer is read by that same loop — awaiting it from inside would be waiting for oneself
        // until the request timeout. One reader keeps the order the bridge sent them in.
        _inbound = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true });
        _loop = Task.Run(() => _connection.RunAsync(root =>
        {
            // Cloned: the connection's document is disposed the moment the frame is handled, and the queue outlives it.
            _inbound?.Writer.TryWrite(root.Clone());
            return Task.CompletedTask;
        }, token), CancellationToken.None);
        _pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var root in _inbound.Reader.ReadAllAsync(token).ConfigureAwait(false))
                    await PublishAsync(root).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* stopping */ }
        }, CancellationToken.None);
        // Anything the previous process received but never handed over is replayed once the socket is up.
        // Earlier would be worse than late: the reply those messages provoke needs a socket to leave through,
        // and a failed reply is exactly what the mailbox will not bring back.
        _replayLoop = Task.Run(() => ReplayWhenConnectedAsync(token), CancellationToken.None);
        _log.Info("QQ adapter started", new Dictionary<string, string>
        {
            ["account"] = Descriptor.AccountId,
            ["endpoint"] = _endpoint.ToString()
        });
        return Result.Success();
    }

    /// <summary>
    /// One of OneBot's own actions, passed through: the ones this host never wrapped.
    ///
    /// The answer is the platform's own JSON — including its <c>retcode</c>, which is where a bridge says "muted", "not a
    /// member", "no such group". Translating that into our error codes would throw away the only part a model could act
    /// on, so it is handed back whole, and a refusal by the platform is a successful call with a refusal inside it.
    /// </summary>
    public async ValueTask<Result<JsonElement>> RawAsync(string action, JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!IsRunning)
            return Result<JsonElement>.Failure(new(ErrorCode.Unavailable, "QQ adapter is not running."));
        if (string.IsNullOrWhiteSpace(action))
            return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "An OneBot action name is required."));

        var called = await _connection.CallAsync(echo => OneBotProtocol.BuildRaw(echo, action, parameters),
            cancellationToken).ConfigureAwait(false);
        if (!called.Ok)
            return Result<JsonElement>.Failure(called.Error ?? new(ErrorCode.Unavailable, "OneBot call failed."));

        var reply = called.Value;
        return Result<JsonElement>.Success(OneBotProtocol.BuildRawAnswer(reply.Response, reply.Data));
    }

    public override async ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_run is { } run)
        {
            _inbound?.Writer.TryComplete();
            await run.CancelAsync().ConfigureAwait(false);
            if (_loop is { } loop)
            {
                try { await loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException) { _log.Warn("QQ adapter reconnect loop did not stop in time", null); }
                catch (OperationCanceledException) { }
            }
            if (_pump is { } pump)
            {
                try { await pump.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException) { _log.Warn("QQ adapter event pump did not stop in time", null); }
                catch (OperationCanceledException) { }
            }
            run.Dispose();
            _run = null;
            _loop = null;
            _pump = null;
            _inbound = null;
            _detail = "已停止";
        }
        _log.Info("QQ adapter stopped", new Dictionary<string, string> { ["account"] = Descriptor.AccountId });
        return await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one thing worth turning at runtime: how long an action waits for the bridge's echo. Everything else
    /// about the connection (endpoint, token) is a spec value, because changing it means reconnecting.
    /// </summary>
    public string ParameterScope => Address.ToString();

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new(TimeoutParameter, ParameterKind.Integer, null,
            "一次 OneBot 动作等应答最多多久（秒）。下一次动作生效，不必重连。",
            ParameterApply.Immediate, Secret: false, Minimum: 1, Maximum: 300),
        new(UploadTimeoutParameter, ParameterKind.Integer, null,
            "上传文件时等应答最多多久（秒）：桥接要先把整个文件收到自己那边，这不是一次普通的请求。",
            ParameterApply.Immediate, Secret: false, Minimum: 10, Maximum: 3600)
    ];

    public const string TimeoutParameter = "requestTimeoutSeconds";
    public const string UploadTimeoutParameter = "uploadTimeoutSeconds";

    /// <summary>
    /// How long a file upload or a bridge-side download may take. Separate from the request timeout on purpose: sending a
    /// hundred megabytes is not the same question as asking for a login info, and one number that has to serve both is
    /// either too short for the file or too long for everything else (docs/28 §6.6).
    /// </summary>
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How many pieces the bridge is asked to fetch in. A hint — it may ignore it — and the door answers ranges.</summary>
    private const int UploadThreads = 4;

    public string? ReadParameter(string name) =>
        string.Equals(name, TimeoutParameter, StringComparison.Ordinal)
            ? ((int)_connection.RequestTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Equals(name, UploadTimeoutParameter, StringComparison.Ordinal)
                ? ((int)UploadTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;

    public ValueTask<Result> ApplyParameterAsync(string name, string value, CancellationToken cancellationToken)
    {
        if (string.Equals(name, UploadTimeoutParameter, StringComparison.Ordinal))
        {
            if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var uploadSeconds) ||
                uploadSeconds is < 10 or > 3600)
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid,
                    $"uploadTimeoutSeconds must be 10..3600: {value}")));
            UploadTimeout = TimeSpan.FromSeconds(uploadSeconds);
            return ValueTask.FromResult(Result.Success());
        }
        if (!string.Equals(name, TimeoutParameter, StringComparison.Ordinal))
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"Unknown parameter: {name}")));
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 300)
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"requestTimeoutSeconds must be 1..300: {value}")));
        _connection.RequestTimeout = TimeSpan.FromSeconds(seconds);
        return ValueTask.FromResult(Result.Success());
    }

    public override async ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        if (!IsRunning)
            return Result<SendResult>.Failure(new(ErrorCode.Unavailable, "QQ adapter is not running."));
        if (message is null)
            return Result<SendResult>.Failure(new(ErrorCode.Invalid, "Outbound message must not be null."));
        // A file is not part of a message on this platform. `upload_group_file` is an action of its own and the chat never
        // mentions what it delivered, so the layout is split here: the words go as one message, each file as its own call.
        // That means the order the caller wrote them in cannot be honoured — which is reported, not hidden, because the
        // alternative is a model believing it said "look at this [file] what do you think" as one bubble (docs/28 §6.6).
        var blocks = message.Blocks ?? [];
        var files = blocks.OfType<FileBlock>().ToArray();
        var parts = blocks.Where(block => block is not FileBlock).ToArray();
        // A layout is a message even when its text view is only a placeholder, so emptiness is asked of the layout. No
        // layout at all is the other, older shape: the message is its text, and it still goes.
        if (blocks.Count == 0 && string.IsNullOrWhiteSpace(message.Text))
            return Result<SendResult>.Failure(new(ErrorCode.Invalid, "Outbound text must be non-empty."));
        if (!OneBotProtocol.TryReadConversation(message.PlatformConversationId, out var target, out var platform))
            return Result<SendResult>.Failure(new(ErrorCode.Invalid,
                $"无法识别的会话 id（应为 {OneBotProtocol.GroupPrefix}<群号> 或 {OneBotProtocol.PrivatePrefix}<QQ号>）：{message.PlatformConversationId}"));

        var notes = new List<string>();
        var delivered = 0;
        string? platformMessageId = null;

        if (blocks.Count == 0)
        {
            // Words only: the path every sender took before layouts existed, and it stays byte-for-byte what it was.
            var said = await _connection.CallAsync(
                echo => OneBotProtocol.BuildSend(echo, target, platform, message.Text, message.ReplyTo),
                cancellationToken).ConfigureAwait(false);
            if (!said.Ok)
                return Result<SendResult>.Failure(said.Error ?? new(ErrorCode.Unavailable, "OneBot send failed."));
            var answer = said.Value.Response;
            if (!answer.Ok)
                return Rejected(answer, platform, target);
            platformMessageId = answer.MessageId;
            delivered++;
        }
        else if (parts.Length > 0)
        {
            var layout = message with { Blocks = parts };
            // Pictures are read once, before the call: a segment list is built whole, and a file that cannot be read must
            // still leave that part as the sentence its text view already had.
            var pictures = await ResolvePicturesAsync(layout, cancellationToken).ConfigureAwait(false);
            // Which parts did not travel as themselves. They still travel — as the sentence their text view already had —
            // but the caller is told, because a model that believes it sent a picture is worse off than one that reads
            // that it did not (IOutboundAdapter.SendAsync: degrade, and say so).
            notes.AddRange(pictures.Where(pair => pair.Value is null).Select(pair => pair.Key)
                .Select(locator => $"{locator} 没有作为图片发出，以文字形态留在了消息里"));
            var sent = await _connection.CallAsync(
                echo => OneBotProtocol.BuildSend(echo, target, platform, layout, pictures),
                cancellationToken).ConfigureAwait(false);
            if (!sent.Ok)
                return Result<SendResult>.Failure(sent.Error ?? new(ErrorCode.Unavailable, "OneBot send failed."));

            var response = sent.Value.Response;
            if (!response.Ok)
                return Rejected(response, platform, target);
            platformMessageId = response.MessageId;
            delivered++;
            if (files.Length > 0)
                notes.Add("文件在消息之后单独上传：群文件不进聊天流，对方要在文件列表里看");
        }

        foreach (var file in files)
        {
            var uploaded = await UploadFileAsync(target, platform, file, cancellationToken).ConfigureAwait(false);
            if (!uploaded.Ok)
            {
                notes.Add($"{file.SuggestedName} 没有发出：{uploaded.Error?.Message ?? "上传失败"}");
                continue;
            }
            delivered++;
        }

        // Nothing at all arrived: that is a failure, not a message with a note. Everything arriving with a note is the
        // other case, and the note is the point of it.
        if (delivered == 0)
            return Result<SendResult>.Failure(new(ErrorCode.Unavailable, string.Join("；", notes)));

        return Result<SendResult>.Success(new SendResult(true,
            platformMessageId ?? message.PlatformConversationId,
            notes.Count == 0 ? null : string.Join("；", notes),
            PlatformMessageId: platformMessageId));
    }

    /// <summary>
    /// The platform's own reason, kept as it wrote it: a <c>retcode</c> is how OneBot says "muted", "not a member", "rate
    /// limited", and that is a sentence a model can act on where "send failed" is not.
    /// </summary>
    private Result<SendResult> Rejected(OneBotResponse response, string platform, long target)
    {
        _log.Warn("OneBot rejected a send", new Dictionary<string, string>
        {
            ["platform"] = platform,
            ["target"] = target.ToString(),
            ["retcode"] = response.RetCode.ToString(),
            ["message"] = response.Message ?? string.Empty
        });
        return Result<SendResult>.Failure(new(ErrorCode.Unavailable,
            $"OneBot retcode {response.RetCode}: {response.Message ?? "no message"}", $"{platform}:{target}", true));
    }

    /// <summary>
    /// One file as one action: pick what goes in the <c>file</c> parameter, call the bridge, and say why if it refused.
    ///
    /// The failure of one file does not fail the others or the message that already went — a group file that was too big
    /// or that the bridge would not take is reported in <see cref="SendResult.Detail"/> next to whatever did arrive.
    /// </summary>
    private async ValueTask<Result> UploadFileAsync(long targetId, string platform, FileBlock block,
        CancellationToken cancellationToken)
    {
        var transport = await FileTransportAsync(block, cancellationToken).ConfigureAwait(false);
        if (!transport.Ok || transport.Value is null)
            return Result.Failure(transport.Error ?? new(ErrorCode.Unavailable, "no way to send this file"));
        var name = block.SuggestedName;
        if (string.IsNullOrWhiteSpace(name)) name = "file";

        var sent = await _connection.CallAsync(
            echo => OneBotProtocol.BuildUploadFile(echo, targetId, platform, transport.Value.Argument, name),
            cancellationToken, UploadTimeout).ConfigureAwait(false);
        if (!sent.Ok)
            return Result.Failure(sent.Error ?? new(ErrorCode.Unavailable, "OneBot upload failed."));

        var response = sent.Value.Response;
        if (!response.Ok)
        {
            _log.Warn("OneBot rejected a file upload", new Dictionary<string, string>
            {
                ["platform"] = platform,
                ["target"] = targetId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["file"] = block.File.Locator,
                ["transport"] = transport.Value.Kind,
                ["retcode"] = response.RetCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["message"] = response.Message ?? string.Empty
            });
            return Result.Failure(new(ErrorCode.Unavailable,
                $"OneBot retcode {response.RetCode}: {response.Message ?? "no message"}", $"{platform}:{targetId}", true));
        }

        _log.Info("outbound file", new Dictionary<string, string>
        {
            ["file"] = block.File.Locator,
            ["name"] = name,
            ["bytes"] = block.File.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["transport"] = transport.Value.Kind
        });
        return Result.Success();
    }

    /// <summary>
    /// What this adapter puts in <c>file</c>, decided by <c>fileDelivery</c> and by what the host can offer.
    ///
    /// The order in <see cref="FileDelivery.Auto"/> is about who pays: a URL costs this process nothing and does not care
    /// how big the file is; a path costs nothing at all but needs a shared filesystem; base64 costs a copy in this
    /// process and the whole thing in the bridge's memory, so it is only for something small. Nothing is retried after a
    /// refusal, because a failed upload may have half arrived — the route is chosen before the call.
    /// </summary>
    private async ValueTask<Result<FileTransport>> FileTransportAsync(FileBlock block, CancellationToken cancellationToken)
    {
        var file = block.File;
        var url = _urls?.UrlFor(file.Scope, file.Path)?.ToString();
        var path = TryMapPathOut(file.Scope, file.Path, out var mapped) ? mapped : null;

        switch (_fileDelivery)
        {
            case FileDelivery.Url:
                return url is null
                    ? NoUrl()
                    : Result<FileTransport>.Success(new(url, "url"));
            case FileDelivery.Path:
                return path is null
                    ? NoPath()
                    : Result<FileTransport>.Success(new(path, "path"));
            case FileDelivery.Base64:
                return await Base64TransportAsync(file, cancellationToken).ConfigureAwait(false);
            case FileDelivery.TwoStep:
                return url is null ? NoUrl() : await TwoStepAsync(url, cancellationToken).ConfigureAwait(false);
            default:
                if (url is not null) return Result<FileTransport>.Success(new(url, "url"));
                if (path is not null) return Result<FileTransport>.Success(new(path, "path"));
                if (file.Bytes <= MaxOutboundFileBase64Bytes)
                    return await Base64TransportAsync(file, cancellationToken).ConfigureAwait(false);
                return Result<FileTransport>.Failure(new(ErrorCode.Unavailable,
                    $"{file.Locator} 是 {file.Bytes} 字节，没有可用的传输路：宿主没开媒体门（→ url）、pathMap 没覆盖它"
                    + $"（→ path），而 base64 只到 {MaxOutboundFileBase64Bytes} 字节。把门开上，或者给适配器配 pathMap。"));
        }

        Result<FileTransport> NoUrl() => Result<FileTransport>.Failure(new(ErrorCode.Unavailable,
            $"fileDelivery={_fileDelivery.ToString().ToLowerInvariant()} 需要宿主开媒体门，但它没有开（或 {file.Scope} 不在门上）。"));
        Result<FileTransport> NoPath() => Result<FileTransport>.Failure(new(ErrorCode.Unavailable,
            "fileDelivery=path 需要 pathMap 把宿主的目录映射到桥接那台机器上的目录，但它没有覆盖这个文件。"));
    }

    /// <summary>
    /// The bridge fetches our URL onto its own disk, and the upload names that path. Two calls instead of one, and the
    /// only route that works on an implementation whose <c>upload_*</c> takes paths only — the go-cqhttp API says exactly
    /// that, while NapCat accepts http(s) directly (docs/16 §12.5).
    ///
    /// The thread count is a request to fetch in pieces, which is why the door answers byte ranges (docs/28 §6.6).
    /// </summary>
    private async ValueTask<Result<FileTransport>> TwoStepAsync(string url, CancellationToken cancellationToken)
    {
        var downloaded = await _connection.CallAsync(
            echo => OneBotProtocol.BuildDownloadFile(echo, url, UploadThreads),
            cancellationToken, UploadTimeout).ConfigureAwait(false);
        if (!downloaded.Ok)
            return Result<FileTransport>.Failure(downloaded.Error ?? new(ErrorCode.Unavailable, "OneBot download failed."));

        var response = downloaded.Value.Response;
        if (!response.Ok)
            return Result<FileTransport>.Failure(new(ErrorCode.Unavailable,
                $"download_file retcode {response.RetCode}: {response.Message ?? "no message"}", url, true));
        var local = OneBotProtocol.ReadDownloadedPath(downloaded.Value.Data);
        return local is null
            ? Result<FileTransport>.Failure(new(ErrorCode.Invalid,
                "download_file 回答了成功，但没有给出本地路径；这条降级路走不通。"))
            : Result<FileTransport>.Success(new(local, "two-step"));
    }

    private async ValueTask<Result<FileTransport>> Base64TransportAsync(MediaRef file, CancellationToken cancellationToken)
    {
        if (file.Bytes > MaxOutboundFileBase64Bytes)
            return Result<FileTransport>.Failure(new(ErrorCode.Invalid,
                $"{file.Locator} 是 {file.Bytes} 字节，超过 base64 这条路的 {MaxOutboundFileBase64Bytes} 字节上限；"
                + "它只能作为 URL 或 path 出去。"));
        var encoded = await ReadAsBase64Async(file, MaxOutboundFileBase64Bytes, cancellationToken).ConfigureAwait(false);
        return encoded is null
            ? Result<FileTransport>.Failure(new(ErrorCode.Unavailable, $"{file.Locator} 读不出来，没有东西可发。"))
            : Result<FileTransport>.Success(new(encoded, "base64"));
    }

    /// <summary>
    /// Pushed event → journal → envelope → event sink. The journal comes first because the socket cannot
    /// replay: a message that arrived exists only in this process until it is written down.
    /// </summary>
    private async Task PublishAsync(JsonElement root)
    {
        // The event's own self_id is authoritative: the connection may not have seen a heartbeat yet,
        // and a message that came from our own account must never look like user input.
        var inbound = OneBotProtocol.ReadMessage(root, OneBotProtocol.ReadSelfId(root) ?? _connection.SelfId);
        if (inbound is null) return;
        if (inbound.MessageId is { Length: > 0 } id && !MarkSeen(id)) return;

        // The name comes with the event; the remark does not. So the remark is read out of what the friend list already
        // said, and the list is asked for in the background if it was never fetched: publishing never waits for either.
        _names.Nudge();
        // Pictures: the segment says where one is, and fetching it is this Adapter's job. Bounded on purpose — a bridge
        // that hands over a disk image must be told no by size, not by the host running out of memory (docs/28 §5).
        var attachments = await DownloadAsync(root, CancellationToken.None).ConfigureAwait(false);
        var envelope = new AdapterEnvelope(Address, Descriptor.PlatformId, inbound.ConversationId,
            inbound.MessageId, inbound.Text, inbound.At, inbound.SenderId, inbound.MentionsSelf,
            inbound.SenderName, _names.Remembered(inbound.SenderId), attachments);
        // A failed journal must not swallow the message: it is still worth publishing, just without the
        // promise that a crash would bring it back.
        //
        // The journal is deliberately text-only: it promises "this message can be delivered after a crash", and keeping
        // a picture in a queue row would mean a second durable copy of it, written on every message (docs/28 §8).
        var journaled = await _journal.RememberAsync(envelope with { Attachments = null }, CancellationToken.None).ConfigureAwait(false);
        if (!journaled.Ok)
            _log.Warn("inbound message could not be journaled", new Dictionary<string, string>
            {
                ["conversation"] = envelope.PlatformConversationId,
                ["error"] = journaled.Error?.Message ?? string.Empty
            });
        _acks?.Expect(envelope.EventId, settle => AcknowledgeJournalAsync(envelope.EventId, settle));
        var published = await _sink.PublishAsync(new RoutedEvent(EventNames.AdapterEnvelope, Address,
            envelope.EventId, JsonSerializer.Serialize(envelope, JsonOptions)), CancellationToken.None).ConfigureAwait(false);
        if (!published.Ok)
        {
            // It stays journaled: the next start tries again rather than pretending it was delivered.
            _acks?.Forget(envelope.EventId);
            _log.Warn("inbound envelope was not queued", new Dictionary<string, string>
            {
                ["conversation"] = envelope.PlatformConversationId,
                ["error"] = published.Error?.Message ?? string.Empty
            });
        }
    }

    /// <summary>
    /// Every picture in the message as something the bridge can accept, keyed by what it points at.
    ///
    /// <c>base64://</c> and not a file path: a bridge may be on another machine, and even on this one its file system is
    /// not ours to write into. A picture that cannot be read (no file port, gone, too big) maps to null, and its block then
    /// travels as the one-line description it always had in the text view — the message still goes out, and nothing
    /// silently disappears (docs/28 §6). The nulls are also what <see cref="SendAsync"/> reports in
    /// <see cref="SendResult.Detail"/>: arriving as a sentence is not the same as arriving as a picture.
    /// </summary>
    private async ValueTask<Dictionary<string, string?>> ResolvePicturesAsync(OutboundMessage message,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var block in message.Blocks ?? [])
        {
            if (block is not ImageBlock picture) continue;
            if (map.ContainsKey(picture.Picture.Locator)) continue;
            map[picture.Picture.Locator] = await ReadPictureAsync(picture, cancellationToken).ConfigureAwait(false);
        }
        return map;
    }

    private async ValueTask<string?> ReadPictureAsync(ImageBlock block, CancellationToken cancellationToken)
    {
        // A URL first, when this host serves one: the bridge fetches it itself, which costs this process nothing and does
        // not care how big the picture is or which machine it lives on. That is the normal path; base64 is the fallback
        // for a host that serves no URL, or for a bridge that cannot reach this machine at all (imageDelivery).
        if (_imageDelivery != ImageDelivery.Base64 &&
            _urls?.UrlFor(block.Picture.Scope, block.Picture.Path) is { } url)
            return url.ToString();
        // Asked for "url only": a message with no URL is not silently turned into base64, because the point of the
        // setting is to see which path a deployment really uses.
        if (_imageDelivery == ImageDelivery.Url) return null;

        return await ReadAsBase64Async(block.Picture, MaxOutboundImageBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A file from the media area as <c>base64://</c>, or null when it cannot be read or is over <paramref name="limit"/>.
    ///
    /// The limit is a parameter because the two callers pay different prices: a picture is capped at what a bridge can
    /// hold, a file at what this process is willing to copy for a route that has no better option. Nothing here throws —
    /// a part that cannot be read travels as its text view, and the message arrives (docs/28 §6.5).
    /// </summary>
    private async ValueTask<string?> ReadAsBase64Async(MediaRef media, long limit, CancellationToken cancellationToken)
    {
        if (_files is null)
        {
            _log.Warn("a part could not be sent: this host gave the adapter no file port",
                new Dictionary<string, string> { ["part"] = media.Locator });
            return null;
        }
        var opened = await _files.OpenReadAsync(media.Scope, media.Path, cancellationToken).ConfigureAwait(false);
        if (!opened.Ok || opened.Value is null)
        {
            _log.Warn("a part could not be read", new Dictionary<string, string>
            {
                ["part"] = media.Locator,
                ["error"] = opened.Error?.Message ?? "not found"
            });
            return null;
        }
        try
        {
            await using var stream = opened.Value;
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                // Eight megabytes of picture is about eleven of base64 in one WebSocket frame, and a bridge that is asked
                // to hold more than that has been seen to fall over (docs/16 §12.3). Too big is not an error: the block
                // travels as the sentence it always had, and the message arrives.
                if (buffer.Length + read > limit)
                {
                    _log.Warn("a part is larger than this route sends", new Dictionary<string, string>
                    {
                        ["part"] = media.Locator,
                        ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    });
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.Length == 0 ? null : "base64://" + Convert.ToBase64String(buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("a part could not be read", new Dictionary<string, string>
            {
                ["part"] = media.Locator,
                ["error"] = ex.Message
            });
            return null;
        }
    }

    /// <summary>
    /// The bytes of the pictures an inbound event carried, as attachments. Never throws, never fails the message.
    ///
    /// Three rules, all of them about a picture being an extra rather than the message. <b>A picture that cannot be
    /// fetched is not a message that cannot be delivered:</b> the text already says <c>[图片]</c>, so the model gets a
    /// receipt-shaped sentence and the conversation goes on. <b>Size is checked before reading and while reading</b>,
    /// because a Content-Length is a claim. <b>The fetch is bounded in time</b> — a bridge whose media endpoint hangs must
    /// not hold up every later message in the socket.
    /// </summary>
    private async ValueTask<IReadOnlyList<InboundAttachment>?> DownloadAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = OneBotProtocol.ReadImages(root);
        if (images.Count == 0) return null;
        var attachments = new List<InboundAttachment>(images.Count);
        foreach (var image in images)
        {
            var bytes = await LoadImageAsync(image, cancellationToken).ConfigureAwait(false);
            if (bytes is null) continue;
            // The type is a claim that the store checks against the bytes; a picture that is not one never becomes
            // something a model is shown.
            attachments.Add(new InboundAttachment(MediaKind.Image, GuessImageType(bytes, image.File), bytes));
        }
        return attachments.Count == 0 ? null : attachments;
    }

    /// <summary>
    /// One picture's bytes, by whichever of the three doors the bridge offers.
    ///
    /// The order is not arbitrary. <b>A url</b> is what a bridge sends when it already made the picture available, and it
    /// is the cheapest and the only one that works across machines. <b><c>get_image</c></b> is what resolves a segment
    /// that carried only a file name — the answer is sometimes a url, sometimes base64, and sometimes a path *inside the
    /// bridge's own container*. <b>A mapped path</b> is the last resort: the operator said which container directory
    /// corresponds to which scope here, and the file arrives through the same port everything else reads with.
    /// </summary>
    private async ValueTask<byte[]?> LoadImageAsync(OneBotImage image, CancellationToken cancellationToken)
    {
        // A url on the segment is the cheap path — and a real one expires: a QQ CDN address answered 400 "invalid rkey"
        // to a plain client minutes after the message arrived, so a failed fetch here is not the end of the picture, only
        // the end of that address. It falls through to get_image, which mints a fresh one.
        var direct = TryHttpUrl(image.Url) is { } url
            ? await FetchAsync(url, image.File, cancellationToken).ConfigureAwait(false)
            : null;
        if (direct is not null) return direct;

        if (string.IsNullOrWhiteSpace(image.File))
        {
            _log.Warn("an inbound picture had no url and no file name, so it was left as text", null);
            return null;
        }

        // The segment only has a name: ask the bridge to resolve it. That is the documented way, and it is also the way
        // that tells us whether the picture is a url, base64, or a file on the bridge's disk.
        var parameters = JsonSerializer.Serialize(new Dictionary<string, string> { ["file"] = image.File });
        using var request = JsonDocument.Parse(parameters);
        var resolved = await _connection.CallAsync(echo => OneBotProtocol.BuildRaw(echo, "get_image", request.RootElement),
            cancellationToken).ConfigureAwait(false);
        if (!resolved.Ok)
        {
            _log.Warn("get_image did not answer, so the picture was left as text", new Dictionary<string, string>
            {
                ["file"] = image.File,
                ["error"] = resolved.Error?.Message ?? string.Empty
            });
            return null;
        }

        var data = resolved.Value.Data;
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("url", out var reported) && reported.GetString() is { } reportedUrl &&
            TryHttpUrl(reportedUrl) is { } fetched)
            return await FetchAsync(fetched, image.File, cancellationToken).ConfigureAwait(false);

        // Some implementations answer with the bytes themselves: `base64` as its own field, or a `file` that is a data
        // url. Both are cheap to accept and neither needs a second round trip.
        foreach (var candidate in new[] { "base64", "file" })
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(candidate, out var value)) continue;
            if (value.GetString() is not { Length: > 0 } text) continue;
            if (text.StartsWith("base64://", StringComparison.Ordinal)) return Decode(text["base64://".Length..]);
            if (text.StartsWith("data:", StringComparison.Ordinal) && text.Contains(";base64,", StringComparison.Ordinal))
                return Decode(text[(text.IndexOf(";base64,", StringComparison.Ordinal) + ";base64,".Length)..]);
        }

        // A path. It is the bridge's own file system, which is this one only when the two share a volume — so the operator
        // says what maps to what, and without that mapping this is a picture we cannot reach (docs/16 §12.1).
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("file", out var path) &&
            path.GetString() is { Length: > 0 } local)
        {
            if (TryMapPath(local, out var scope, out var relative))
                return await ReadMappedAsync(scope, relative, local, cancellationToken).ConfigureAwait(false);
            _log.Warn("the bridge answered with a path on its own disk and no pathMap is configured for it", new Dictionary<string, string>
            {
                ["file"] = local
            });
            return null;
        }

        _log.Warn("get_image answered without a url, base64 or path", new Dictionary<string, string> { ["file"] = image.File });
        return null;

        static byte[]? Decode(string payload)
        {
            try { return Convert.FromBase64String(payload); }
            catch (FormatException) { return null; }
        }
    }

    /// <summary>A URL only if it is one this process may fetch: an absolute http(s) address, not a file or a scheme we do not speak.</summary>
    private static Uri? TryHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>
    /// The bytes behind a url, with a ceiling and a deadline: a bridge whose media endpoint hangs must not hold up every
    /// later message on the socket, and a 500 MB "picture" must be refused before it is in memory.
    /// </summary>
    private async ValueTask<byte[]?> FetchAsync(Uri url, string? file, CancellationToken cancellationToken)    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MediaFetchTimeout);
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A QQ CDN url answered 400 for a plain client in real life ("invalid rkey"): the browser that was meant
                // to fetch it is the bridge's, not ours. Saying which status keeps that diagnosable.
                _log.Warn("an inbound picture could not be fetched", new Dictionary<string, string>
                {
                    ["file"] = file ?? string.Empty,
                    ["status"] = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
                return null;
            }
            if (response.Content.Headers.ContentLength is { } declared && declared > IMediaStore.MaxIngestBytes)
            {
                _log.Warn("an inbound picture is larger than this build takes in", new Dictionary<string, string>
                {
                    ["bytes"] = declared.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await ReadCappedAsync(stream, file, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Warn("an inbound picture timed out and was left as text", new Dictionary<string, string>
            {
                ["url"] = url.GetLeftPart(UriPartial.Path)
            });
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _log.Warn("an inbound picture could not be fetched", new Dictionary<string, string>
            {
                ["error"] = ex.Message
            });
            return null;
        }
    }

    /// <summary>
    /// A url a caller asked for by handle, with the caller's own ceiling and a reason when it fails — unlike the picture
    /// path above, where a failure is deliberately quiet and the message travels on without the picture.
    ///
    /// Buffered, not streamed. That is a real cost and a deliberate one: this is the route a bridge answers with when it
    /// holds the file on an HTTP side, and buffering it once (≤ what the caller asked for) keeps one code path for
    /// "bytes to hand over" instead of three. The route that must never buffer — <c>pathMap</c> — streams instead.
    /// </summary>
    private async ValueTask<Result<byte[]>> FetchBytesAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(UploadTimeout);
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<byte[]>.Failure(new(ErrorCode.Unavailable,
                    $"桥接给的地址回答了 {(int)response.StatusCode}。",
                    url.GetLeftPart(UriPartial.Path), true));
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
                return Result<byte[]>.Failure(new(ErrorCode.Invalid,
                    $"这个文件是 {declared} 字节，超过这次取的 {maxBytes} 字节上限。"));

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                // Checked as it grows, not only from Content-Length: a chunked response declares nothing, and "it fit in
                // memory" is not a limit.
                if (buffer.Length + read > maxBytes)
                    return Result<byte[]>.Failure(new(ErrorCode.Invalid,
                        $"这个文件超过这次取的 {maxBytes} 字节上限，取到一半停下了。"));
                buffer.Write(chunk, 0, read);
            }
            return Result<byte[]>.Success(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<byte[]>.Failure(new(ErrorCode.Timeout, "桥接的文件地址超时了。", url.GetLeftPart(UriPartial.Path), true));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Result<byte[]>.Failure(new(ErrorCode.Unavailable, "桥接的文件地址取不回来。", ex.Message, true));
        }
    }

    /// <summary>Reads at most what the store will take in, and says so when it stops early rather than truncating silently.</summary>
    private async ValueTask<byte[]?> ReadCappedAsync(Stream stream, string? file, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > IMediaStore.MaxIngestBytes)
            {
                _log.Warn("an inbound picture is larger than this build takes in", new Dictionary<string, string>
                {
                    ["file"] = file ?? string.Empty
                });
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>
    /// The group file area, as a list of things a model may point at.
    ///
    /// <b>Private chats have no list, and that is a failure with a reason rather than an empty answer.</b> On QQ the
    /// group's file area is a place of its own; a private conversation has no such place, and the file somebody sent in
    /// one is an attachment that already arrived with the message. Saying "nothing is there" would be answering a
    /// question nobody asked (docs/28 §6.7).
    /// </summary>
    public async ValueTask<Result<PlatformFileListing>> ListAsync(string conversationId, int limit,
        CancellationToken cancellationToken)
    {
        if (!IsRunning)
            return Result<PlatformFileListing>.Failure(new(ErrorCode.Unavailable, "QQ adapter is not running."));
        if (!OneBotProtocol.TryReadConversation(conversationId, out var target, out var platform))
            return Result<PlatformFileListing>.Failure(new(ErrorCode.Invalid,
                $"无法识别的会话 id（应为 {OneBotProtocol.GroupPrefix}<群号> 或 {OneBotProtocol.PrivatePrefix}<QQ号>）：{conversationId}"));
        if (platform == OneBotProtocol.PrivatePlatform)
            return Result<PlatformFileListing>.Failure(new(ErrorCode.Unavailable,
                "私聊没有文件目录：QQ 的群才有文件区。私聊里对方发的文件是随消息一起到的附件，不需要再来取一次。"));

        var called = await _connection.CallAsync(
            echo => OneBotProtocol.BuildListGroupFiles(echo, target), cancellationToken).ConfigureAwait(false);
        if (!called.Ok)
            return Result<PlatformFileListing>.Failure(called.Error ?? new(ErrorCode.Unavailable, "OneBot list failed."));
        var answer = called.Value.Response;
        if (!answer.Ok)
        {
            _log.Warn("OneBot rejected a group file listing", new Dictionary<string, string>
            {
                ["target"] = target.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["retcode"] = answer.RetCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["message"] = answer.Message ?? string.Empty
            });
            return Result<PlatformFileListing>.Failure(new(ErrorCode.Unavailable,
                $"OneBot retcode {answer.RetCode}: {answer.Message ?? "no message"}", $"group:{target}", true));
        }

        var (files, folders) = OneBotProtocol.ReadGroupFiles(called.Value.Data);
        var entries = files
            // Newest first: "the thing that just appeared" is what somebody asks for by default, and an unsorted list of
            // forty files is a list nobody reads to the end.
            .OrderByDescending(file => file.At ?? DateTimeOffset.MinValue)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(file => new PlatformFile(FileHandles.Wrap($"{file.BusId}|{file.Id}"), file.Name, file.Bytes,
                file.At, file.From))
            .ToArray();
        // A partial listing says it is partial. The root of a group's file area often has folders in it, and this build
        // does not open them — which is a fact worth one sentence, not a silence (docs/28 §6.7).
        var note = folders.Count == 0
            ? null
            : $"根目录还有 {folders.Count} 个文件夹（{string.Join("、", folders.Take(5))}"
              + (folders.Count > 5 ? "…" : "") + "），本构建不进去看，只列根目录里的文件。";
        if (entries.Length == 0 && note is null)
            note = "这个群的文件区是空的。";
        return Result<PlatformFileListing>.Success(new PlatformFileListing(entries, folders, note));
    }

    /// <summary>
    /// The bytes behind one handle.
    ///
    /// Four shapes come back from <c>get_file</c> depending on the bridge, and this is the place that knows all four:
    /// <c>base64://</c>, a data url, an http(s) url, or a path on the bridge's own disk. The last one is only reachable
    /// when the two machines share a volume and the operator said so with <c>pathMap</c> — the same mapping inbound
    /// pictures already use, read the same direction (docs/28 §6.6). Everything is capped at
    /// <paramref name="maxBytes"/> and refused rather than truncated: a short file that looks complete is how a backup
    /// turns out not to be one.
    /// </summary>
    public async ValueTask<Result<PlatformFileContent>> FetchAsync(string conversationId, string handle, long maxBytes,
        CancellationToken cancellationToken)
    {
        if (!IsRunning)
            return Result<PlatformFileContent>.Failure(new(ErrorCode.Unavailable, "QQ adapter is not running."));
        if (!OneBotProtocol.TryReadConversation(conversationId, out var target, out var platform))
            return Result<PlatformFileContent>.Failure(new(ErrorCode.Invalid,
                $"无法识别的会话 id：{conversationId}"));
        if (!FileHandles.TryUnwrap(handle, out var inner))
            return Result<PlatformFileContent>.Failure(new(ErrorCode.Invalid,
                $"这不是一个文件 handle（应以 {FileHandles.Prefix} 开头）：{handle}"));

        // The handle is this adapter's own shape: "busid|file_id" for a group file, a bare id for a private one. It is
        // split at the *first* bar so a file id that contains one still survives.
        var bar = inner.IndexOf('|', StringComparison.Ordinal);
        var busid = platform == OneBotProtocol.PrivatePlatform ? string.Empty : bar <= 0 ? "0" : inner[..bar];
        var fileId = platform == OneBotProtocol.PrivatePlatform ? inner : bar >= 0 ? inner[(bar + 1)..] : inner;
        if (string.IsNullOrWhiteSpace(fileId))
            return Result<PlatformFileContent>.Failure(new(ErrorCode.Invalid, $"handle 里没有 file_id：{handle}"));

        var called = await _connection.CallAsync(
            echo => OneBotProtocol.BuildGetFile(echo, busid, fileId), cancellationToken, UploadTimeout)
            .ConfigureAwait(false);
        if (!called.Ok)
            return Result<PlatformFileContent>.Failure(called.Error ?? new(ErrorCode.Unavailable, "OneBot get_file failed."));
        var answer = called.Value.Response;
        if (!answer.Ok)
        {
            _log.Warn("OneBot rejected get_file", new Dictionary<string, string>
            {
                ["platform"] = platform,
                ["target"] = target.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["retcode"] = answer.RetCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["message"] = answer.Message ?? string.Empty
            });
            return Result<PlatformFileContent>.Failure(new(ErrorCode.Unavailable,
                $"retcode {answer.RetCode}：{answer.Message ?? "桥接没说原因"}——换一个 file_id，或者用 list 看看它还在不在。",
                $"{platform}:{target}", true));
        }

        var body = OneBotProtocol.ReadFileBody(called.Value.Data);
        if (string.IsNullOrWhiteSpace(body))
            return Result<PlatformFileContent>.Failure(new(ErrorCode.Unavailable,
                "get_file 的回答里没有 file / url / base64，桥接的实现和这个构建的假设不一致。"));

        var name = OneBotProtocol.ReadFileName(called.Value.Data) ?? FallbackName(body);
        if (body.StartsWith("base64://", StringComparison.Ordinal))
            return Decode(body["base64://".Length..]);
        if (body.StartsWith("data:", StringComparison.Ordinal) && body.Contains(";base64,", StringComparison.Ordinal))
            return Decode(body[(body.IndexOf(";base64,", StringComparison.Ordinal) + ";base64,".Length)..]);
        if (TryHttpUrl(body) is { } url)
        {
            var fetched = await FetchBytesAsync(url, maxBytes, cancellationToken).ConfigureAwait(false);
            return fetched.Ok && fetched.Value is not null
                ? Result<PlatformFileContent>.Success(new(new MemoryStream(fetched.Value, writable: false), name,
                    fetched.Value.Length))
                : Result<PlatformFileContent>.Failure(fetched.Error ?? new(ErrorCode.Unavailable, "文件取不回来。"));
        }

        // A path. It is on the bridge's own disk, which is this one only when the two share a volume and the operator
        // said so with pathMap — and that mapping is the whole safety story: it can only ever reach a scope the host
        // registered, never an arbitrary path a bridge printed.
        if (TryMapPath(body, out var scope, out var relative))
        {
            if (_files is null)
                return Result<PlatformFileContent>.Failure(new(ErrorCode.Unavailable,
                    "桥接给的是一个路径，而这台宿主没有给适配器文件端口，所以读不到。"));
            var opened = await _files.OpenReadAsync(scope, relative, cancellationToken).ConfigureAwait(false);
            if (!opened.Ok || opened.Value is null)
                return Result<PlatformFileContent>.Failure(opened.Error ?? new(ErrorCode.Unavailable,
                    $"映射到的 {scope}/{relative} 读不出来。"));
            return Result<PlatformFileContent>.Success(new(opened.Value, name, null));
        }

        return Result<PlatformFileContent>.Failure(new(ErrorCode.Unavailable,
            $"桥接说文件在它自己的磁盘上：{body}。这台宿主读不到那个路径——" +
            "要么让桥接和宿主共享卷并配上 pathMap（把那个目录映射到一个 scope），要么让桥接用 base64 回答。"));

        Result<PlatformFileContent> Decode(string payload)
        {
            try
            {
                var bytes = Convert.FromBase64String(payload);
                if (bytes.Length > maxBytes)
                    return Result<PlatformFileContent>.Failure(new(ErrorCode.Invalid,
                        $"这个文件解出来是 {bytes.Length} 字节，超过这次取的 {maxBytes} 字节上限。", null, false));
                return Result<PlatformFileContent>.Success(new(new MemoryStream(bytes, writable: false), name, bytes.Length,
                    OneBotProtocol.ReadFileMediaType(called.Value.Data)));
            }
            catch (FormatException ex)
            {
                return Result<PlatformFileContent>.Failure(new(ErrorCode.Unavailable,
                    "桥接给的是 base64，但解不开。", ex.Message));
            }
        }

        // The bridge's own idea of a name, or the end of whatever it gave us: a model needs something to call this file,
        // and "the last segment" is at least true.
        static string FallbackName(string body)
        {
            var tail = body.Split('|').Last();
            var cut = tail.LastIndexOfAny(['/', '\\']);
            var candidate = cut >= 0 ? tail[(cut + 1)..] : tail;
            return string.IsNullOrWhiteSpace(candidate) ? "file" : candidate;
        }
    }

    /// <summary>
    /// A file the bridge named by path, read through the host's own port once the operator's mapping says where it is here.
    ///
    /// The mapping is the whole safety story: <c>pathMap</c> is a list of "their directory = our scope/directory", so this
    /// can only ever read inside a scope the host registered — never an arbitrary path somebody else's process printed.
    /// </summary>
    private async ValueTask<byte[]?> ReadMappedAsync(string scope, string relative, string original,
        CancellationToken cancellationToken)
    {
        if (_files is null)
        {
            _log.Warn("a mapped picture needs the host's file port and this host gave the adapter none", new Dictionary<string, string>
            {
                ["file"] = original
            });
            return null;
        }
        var opened = await _files.OpenReadAsync(scope, relative, cancellationToken).ConfigureAwait(false);
        if (!opened.Ok || opened.Value is null)
        {
            _log.Warn("a mapped picture could not be read", new Dictionary<string, string>
            {
                ["scope"] = scope,
                ["path"] = relative,
                ["error"] = opened.Error?.Message ?? "not found"
            });
            return null;
        }
        try
        {
            await using var stream = opened.Value;
            return await ReadCappedAsync(stream, original, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("a mapped picture could not be read", new Dictionary<string, string> { ["error"] = ex.Message });
            return null;
        }
    }

    /// <summary>
    /// "<c>their/prefix=scope/our/prefix</c>", several separated by <c>;</c> — where a path from the bridge lives on this
    /// disk. Longest prefix wins, because a container usually has a parent directory mapped as well.
    /// </summary>
    private bool TryMapPath(string path, out string scope, out string relative)
    {
        scope = string.Empty;
        relative = string.Empty;
        var best = -1;
        foreach (var mapping in _pathMap)
        {
            if (mapping.From.Length <= best) continue;
            if (!path.StartsWith(mapping.From, StringComparison.Ordinal)) continue;
            var tail = path[mapping.From.Length..].TrimStart('/', '\\');
            scope = mapping.Scope;
            relative = mapping.To.Length == 0 ? tail : $"{mapping.To}/{tail}";
            best = mapping.From.Length;
        }
        return best >= 0;
    }

    /// <summary>
    /// The same table, read the other way: a file this host holds as <c>scope/path</c> → the path the bridge can open.
    ///
    /// It only exists for sending, and only for a deployment where the two machines share a filesystem — which is the one
    /// case where handing over a path costs nothing at all (docs/28 §6.6). Longest <c>To</c> wins, so a mapping of a
    /// subdirectory beats a mapping of its parent, exactly like the inbound direction.
    /// </summary>
    private bool TryMapPathOut(string scope, string path, out string bridgePath)
    {
        bridgePath = string.Empty;
        var best = -1;
        foreach (var mapping in _pathMap)
        {
            if (!string.Equals(mapping.Scope, scope, StringComparison.Ordinal)) continue;
            if (mapping.To.Length <= best) continue;
            if (mapping.To.Length > 0 &&
                !path.StartsWith(mapping.To + "/", StringComparison.Ordinal) &&
                !string.Equals(path, mapping.To, StringComparison.Ordinal)) continue;
            var tail = mapping.To.Length == 0 ? path : path[(mapping.To.Length + 1)..];
            bridgePath = tail.Length == 0 ? mapping.From : $"{mapping.From.TrimEnd('/', '\\')}/{tail}";
            best = mapping.To.Length;
        }
        return best >= 0 && bridgePath.Length > 0;
    }

    /// <summary>
    /// Reads the <c>pathMap</c> setting: "<c>their/prefix=scope/our/prefix</c>", several separated by <c>;</c>.
    ///
    /// Refused rather than guessed, one pair at a time: a mapping whose target is not a scope the file port knows would
    /// turn "map this directory" into "read anywhere", and a mapping nobody can satisfy is worse than no mapping at all —
    /// it looks configured.
    /// </summary>
    private static IReadOnlyList<(string From, string Scope, string To)> ParsePathMap(
        IReadOnlyDictionary<string, string> settings, IModuleLogger log)
    {
        if (!settings.TryGetValue("pathMap", out var declared) || string.IsNullOrWhiteSpace(declared)) return [];
        var mappings = new List<(string From, string Scope, string To)>();
        foreach (var pair in declared.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = pair.IndexOf('=');
            if (split <= 0 || split == pair.Length - 1)
            {
                log.Warn("a pathMap entry is not 'their/prefix=scope/prefix' and was ignored",
                    new Dictionary<string, string> { ["entry"] = pair });
                continue;
            }
            var from = pair[..split].TrimEnd('/', '\\');
            var target = pair[(split + 1)..].Trim('/');
            var slash = target.IndexOf('/');
            var scope = slash < 0 ? target : target[..slash];
            var to = slash < 0 ? string.Empty : target[(slash + 1)..];
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(scope) || FilePaths.Canonical(to) is null)
            {
                log.Warn("a pathMap entry does not name a directory and a scope prefix, and was ignored",
                    new Dictionary<string, string> { ["entry"] = pair });
                continue;
            }
            mappings.Add((from, scope, to));
        }
        // Longest first: a container has a parent directory mapped as well as the one the pictures land in.
        return mappings.OrderByDescending(mapping => mapping.From.Length).ToArray();
    }

    /// <summary>What a bridge says a picture is: its file name first, then the bytes, and "an image" when neither says.</summary>
    private static string GuessImageType(byte[] bytes, string? file)    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F') return "image/gif";
        if (bytes.Length >= 12 && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        var extension = Path.GetExtension(file ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            _ => "image/*"
        };
    }

    /// <summary>
    /// Waits for the socket, then replays. Bounded: if the platform never answers, the journal simply stays
    /// for the next start — the alternative is replaying into a socket that cannot send.
    /// </summary>
    private async Task ReplayWhenConnectedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (!_connection.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                _log.Warn("inbound journal was not replayed: the platform did not connect", null);
                return;
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        if (cancellationToken.IsCancellationRequested) return;
        var replayed = await ReplayJournalAsync(cancellationToken).ConfigureAwait(false);
        if (!replayed.Ok)
            _log.Warn("undelivered inbound messages could not be replayed", new Dictionary<string, string>
            {
                ["error"] = replayed.Error?.Message ?? string.Empty
            });
    }

    /// <summary>
    /// Re-publishes whatever was received but never acknowledged, keeping the original event id so the
    /// acknowledgement that follows is the same one the first delivery would have produced.
    /// </summary>
    private async ValueTask<Result<int>> ReplayJournalAsync(CancellationToken cancellationToken)
    {
        var entries = await _journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (entries.Error is { } readError) return Result<int>.Failure(readError);
        var replayed = 0;
        foreach (var envelope in entries.Value!)
        {
            _acks?.Expect(envelope.EventId, settle => AcknowledgeJournalAsync(envelope.EventId, settle));
            var published = await _sink.PublishAsync(new RoutedEvent(EventNames.AdapterEnvelope, Address,
                envelope.EventId, JsonSerializer.Serialize(envelope, JsonOptions)), cancellationToken).ConfigureAwait(false);
            if (!published.Ok)
            {
                _acks?.Forget(envelope.EventId);
                // Queue full or closed: stop here. These stay journaled for the next attempt.
                return Result<int>.Failure(published.Error!);
            }
            replayed++;
        }
        if (replayed > 0)
            _log.Info("replayed inbound messages that were never delivered", new Dictionary<string, string>
            {
                ["count"] = replayed.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        return Result<int>.Success(replayed);
    }

    private async ValueTask AcknowledgeJournalAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var forgotten = await _journal.ForgetAsync(eventId, cancellationToken).ConfigureAwait(false);
        if (!forgotten.Ok)
            _log.Warn("a delivered inbound message could not be removed from the journal", new Dictionary<string, string>
            {
                ["event"] = eventId.ToString("D"),
                ["error"] = forgotten.Error?.Message ?? string.Empty
            });
    }

    /// <summary>
    /// OneBot does not replay on reconnect, but a reconnect can race with a message already in flight;
    /// a small window is cheaper than a duplicate reply in a real group.
    /// </summary>
    private bool MarkSeen(string messageId)
    {
        lock (_seenGate)
        {
            if (!_seen.Add(messageId)) return false;
            _seenOrder.Enqueue(messageId);
            while (_seenOrder.Count > SeenCapacity) _seen.Remove(_seenOrder.Dequeue());
            return true;
        }
    }
}
