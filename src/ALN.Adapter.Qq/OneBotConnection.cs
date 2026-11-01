using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using ALN.Contracts;

namespace ALN.Adapter.Qq;

/// <summary>
/// One forward WebSocket to a OneBot implementation: the bot connects out, receives events as JSON and
/// sends actions on the same socket, matching each answer to its request by <c>echo</c>.
///
/// Deliberately dumb about meaning: it does not know what a group is. It owns exactly three pieces of
/// state worth having — the socket, the pending echoes, and the last error — because those are the
/// three that make a connection debuggable at 2am. It also learns who we are (<c>get_login_info</c>),
/// because an account id is something the platform knows and a config file should not have to.
/// </summary>
internal sealed class OneBotConnection(Uri endpoint, string? accessToken, IModuleLogger log)
{
    /// <summary>How long one action waits for its echo. Settable: a slow bridge is a fact about the
    /// deployment, not about the protocol.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)
    ];

    private readonly ConcurrentDictionary<string, TaskCompletionSource<OneBotReply?>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ClientWebSocket? _socket;

    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public string? LastError { get; private set; }
    public long? SelfId { get; private set; }
    public string? Nickname { get; private set; }

    /// <summary>
    /// Connect, identify, receive, reconnect — until cancelled. Returns only when the token is
    /// cancelled; every failure in between is a log line and a delay, because an adapter that gives up
    /// on the first network blip is an adapter you have to babysit.
    /// </summary>
    public async Task RunAsync(Func<JsonElement, Task> onEvent, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                if (!string.IsNullOrEmpty(accessToken))
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

                _socket = socket;
                LastError = null;
                attempt = 0;
                log.Info("OneBot connected", new Dictionary<string, string> { ["endpoint"] = endpoint.ToString() });

                // The probe must not be awaited before the reader exists: the answer arrives on the same
                // socket, and an action awaited with no reader is an action that can never complete.
                _ = ProbeLoginAsync(cancellationToken);
                await ReceiveAsync(socket, onEvent, cancellationToken).ConfigureAwait(false);
                log.Warn("OneBot closed the connection", new Dictionary<string, string> { ["endpoint"] = endpoint.ToString() });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or InvalidOperationException or IOException)
            {
                LastError = ex.Message;
                log.Warn("OneBot connection failed", new Dictionary<string, string>
                {
                    ["endpoint"] = endpoint.ToString(),
                    ["error"] = ex.Message
                });
            }
            finally
            {
                _socket = null;
                FailPending();
            }

            if (cancellationToken.IsCancellationRequested) break;
            var delay = Backoff[Math.Min(attempt, Backoff.Length - 1)];
            attempt++;
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Sends one action and waits for its echo. <paramref name="build"/> receives the echo id so the
    /// protocol layer stays a pure function of its inputs.
    /// </summary>
    public async Task<Result<OneBotReply>> CallAsync(Func<string, string> build, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return Result<OneBotReply>.Failure(new(ErrorCode.Unavailable,
                "OneBot connection is not open.", endpoint.ToString(), true));

        var echo = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<OneBotReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[echo] = completion;
        try
        {
            await SendAsync(socket, build(echo), cancellationToken).ConfigureAwait(false);
            var wait = timeout ?? RequestTimeout;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(wait);
            using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
            OneBotReply? reply;
            try
            {
                reply = await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Result<OneBotReply>.Failure(new(ErrorCode.Timeout,
                    $"OneBot did not answer within {wait.TotalSeconds:0.#}s.", echo, true));
            }

            if (reply is null)
                return Result<OneBotReply>.Failure(new(ErrorCode.Unavailable, "OneBot closed the connection.", echo, true));
            return Result<OneBotReply>.Success(reply.Value);
        }
        finally
        {
            _pending.TryRemove(echo, out _);
        }
    }

    /// <summary>
    /// Asks the platform who we are. Best effort: a failed probe costs a log line, not the connection —
    /// and self-echoes are still filtered by <c>message_sent</c> and the event's own <c>self_id</c>.
    /// </summary>
    private async Task ProbeLoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reply = await CallAsync(OneBotProtocol.BuildLoginInfo, cancellationToken, TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            if (!reply.Ok || !reply.Value.Response.Ok)
            {
                log.Warn("get_login_info did not succeed", new Dictionary<string, string>
                {
                    ["endpoint"] = endpoint.ToString(),
                    ["detail"] = reply.Ok ? $"retcode {reply.Value.Response.RetCode}" : reply.Error?.Message ?? ""
                });
                return;
            }

            if (OneBotProtocol.ReadLoginInfo(reply.Value.Data) is not { } login) return;
            SelfId = login.UserId;
            Nickname = login.Nickname;
            log.Info("OneBot login info received", new Dictionary<string, string>
            {
                ["account"] = login.UserId.ToString(),
                ["nickname"] = login.Nickname ?? "",
                ["endpoint"] = endpoint.ToString()
            });
        }
        catch (OperationCanceledException)
        {
            // Shutting down; nothing to report.
        }
        catch (Exception ex)
        {
            log.Warn("get_login_info failed", new Dictionary<string, string> { ["error"] = ex.Message });
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, Func<JsonElement, Task> onEvent, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var frame = new MemoryStream();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); }
                    catch (WebSocketException) { }
                    return;
                }
                frame.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            JsonDocument document;
            try { document = JsonDocument.Parse(frame.ToArray()); }
            catch (JsonException ex)
            {
                log.Warn("OneBot sent something that is not JSON", new Dictionary<string, string> { ["error"] = ex.Message });
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                if (root.TryGetProperty("echo", out var echo) && echo.ValueKind == JsonValueKind.String)
                {
                    Complete(echo.GetString()!, root);
                    continue;
                }

                if (!root.TryGetProperty("post_type", out _)) continue;
                if (OneBotProtocol.ReadSelfId(root) is { } selfId) SelfId = selfId;
                try
                {
                    await onEvent(root).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // One bad event must not kill the socket: the next message is probably fine.
                    log.Error("OneBot event handling failed", new Dictionary<string, string> { ["error"] = ex.Message });
                }
            }
        }
    }

    private async Task SendAsync(ClientWebSocket socket, string payload, CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Complete(string echo, JsonElement root)
    {
        if (!_pending.TryRemove(echo, out var completion)) return;
        if (OneBotProtocol.ReadResponse(root) is not { } response)
        {
            completion.TrySetResult(null);
            return;
        }
        // The document is disposed right after this call, so whatever a caller wants out of `data` has
        // to be cloned now. Cloning a small answer is cheaper than keeping documents alive.
        var data = root.TryGetProperty("data", out var payload) ? payload.Clone() : default;
        completion.TrySetResult(new OneBotReply(response, data));
    }

    private void FailPending()
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var completion))
                completion.TrySetResult(null);
    }
}

/// <summary>An answered action: the parsed status plus a private copy of its <c>data</c>.</summary>
internal readonly record struct OneBotReply(OneBotResponse Response, JsonElement Data);
