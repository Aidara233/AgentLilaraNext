using System.Text.Json;
using ALN.Contracts;

namespace ALN.Adapter.Qq;

/// <summary>
/// A QQ account reached through an OneBot v11 implementation over a forward WebSocket.
///
/// What it is responsible for: the connection (and its reconnects), turning pushed events into
/// <see cref="AdapterEnvelope"/>, and turning <see cref="OutboundMessage"/> into a platform call. What
/// it deliberately does not do: decide whether a message is interesting, deduplicate across adapters,
/// or create anything. It is one account and one socket.
///
/// v1 scope: group text in, group text out. Private chats, images, mentions and rate-limit pacing are
/// not implemented — see docs/16.
/// </summary>
public sealed class QqAdapter : AdapterBase, IAdapterHealth, IAdapterIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int SeenCapacity = 512;

    private readonly IEventSink _sink;
    private readonly IModuleLogger _log;
    private readonly Uri _endpoint;
    private readonly OneBotConnection _connection;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly object _seenGate = new();
    private CancellationTokenSource? _run;
    private Task? _loop;
    private volatile string? _detail;

    public QqAdapter(AdapterSpec spec, IEventSink sink, IModuleLogger log) : base(spec.Address, spec.Descriptor)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        var settings = spec.Settings ?? new Dictionary<string, string>();
        if (!settings.TryGetValue("endpoint", out var endpoint) || string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Adapter settings must contain 'endpoint' (ws://host:port).", nameof(spec));
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _endpoint!) ||
            (_endpoint.Scheme != "ws" && _endpoint.Scheme != "wss"))
            throw new ArgumentException("Adapter setting 'endpoint' must be an absolute ws:// or wss:// URL.", nameof(spec));

        settings.TryGetValue("accessToken", out var token);
        _connection = new OneBotConnection(_endpoint, string.IsNullOrWhiteSpace(token) ? null : token, log);
        _detail = $"未连接（{_endpoint}）";
    }

    public bool Connected => _connection.IsConnected;

    // Learned from the platform on connect, not typed into a config file. Null until the platform says.
    public string? AccountId => _connection.SelfId?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? DisplayName => _connection.Nickname;

    public string? HealthDetail => _connection.IsConnected
        ? $"已连接 {_endpoint}" + (DisplayName is { Length: > 0 } name ? $"（{name} / {AccountId}）" : string.Empty)
        : _detail ?? _connection.LastError;

    public override async ValueTask<Result> StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning && _loop is not null) return Result.Success();
        var started = await base.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Ok) return started;

        _run = new CancellationTokenSource();
        var token = _run.Token;
        _loop = Task.Run(() => _connection.RunAsync(PublishAsync, token), CancellationToken.None);
        _log.Info("QQ adapter started", new Dictionary<string, string>
        {
            ["account"] = Descriptor.AccountId,
            ["endpoint"] = _endpoint.ToString()
        });
        return Result.Success();
    }

    public override async ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_run is { } run)
        {
            await run.CancelAsync().ConfigureAwait(false);
            if (_loop is { } loop)
            {
                try { await loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException) { _log.Warn("QQ adapter reconnect loop did not stop in time", null); }
                catch (OperationCanceledException) { }
            }
            run.Dispose();
            _run = null;
            _loop = null;
            _detail = "已停止";
        }
        _log.Info("QQ adapter stopped", new Dictionary<string, string> { ["account"] = Descriptor.AccountId });
        return await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        if (!IsRunning)
            return Result<SendResult>.Failure(new(ErrorCode.Unavailable, "QQ adapter is not running."));
        if (message is null || string.IsNullOrWhiteSpace(message.Text))
            return Result<SendResult>.Failure(new(ErrorCode.Invalid, "Outbound text must be non-empty."));
        if (!OneBotProtocol.TryReadConversation(message.PlatformConversationId, out var target, out var platform))
            return Result<SendResult>.Failure(new(ErrorCode.Invalid,
                $"无法识别的会话 id（应为 {OneBotProtocol.GroupPrefix}<群号> 或 {OneBotProtocol.PrivatePrefix}<QQ号>）：{message.PlatformConversationId}"));

        var sent = await _connection.CallAsync(
            echo => OneBotProtocol.BuildSend(echo, target, platform, message.Text, message.ReplyTo),
            cancellationToken).ConfigureAwait(false);
        if (!sent.Ok)
            return Result<SendResult>.Failure(sent.Error ?? new(ErrorCode.Unavailable, "OneBot send failed."));

        var response = sent.Value.Response;
        if (!response.Ok)
        {
            // retcode from OneBot carries the platform's own reason (muted, not a member, rate limited).
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

        return Result<SendResult>.Success(new SendResult(true,
            response.MessageId ?? message.PlatformConversationId));
    }

    /// <summary>
    /// Pushed event → envelope → event sink. Returning without publishing is the normal path for
    /// anything that is not a group message from somebody else.
    /// </summary>
    private async Task PublishAsync(JsonElement root)
    {
        // The event's own self_id is authoritative: the connection may not have seen a heartbeat yet,
        // and a message that came from our own account must never look like user input.
        var inbound = OneBotProtocol.ReadMessage(root, OneBotProtocol.ReadSelfId(root) ?? _connection.SelfId);
        if (inbound is null) return;
        if (inbound.MessageId is { Length: > 0 } id && !MarkSeen(id)) return;

        var envelope = new AdapterEnvelope(Address, Descriptor.PlatformId, inbound.ConversationId,
            inbound.MessageId, inbound.Text, inbound.At, inbound.SenderId, inbound.MentionsSelf);
        var published = await _sink.PublishAsync(new RoutedEvent(EventNames.AdapterEnvelope, Address,
            envelope.EventId, JsonSerializer.Serialize(envelope, JsonOptions)), CancellationToken.None).ConfigureAwait(false);
        if (!published.Ok)
            _log.Warn("inbound envelope was not queued", new Dictionary<string, string>
            {
                ["conversation"] = envelope.PlatformConversationId,
                ["error"] = published.Error?.Message ?? string.Empty
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
