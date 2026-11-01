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
/// three that make a connection debuggable at 2am.
/// </summary>
internal sealed class OneBotConnection(Uri endpoint, string? accessToken, IModuleLogger log)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)
    ];

    private readonly ConcurrentDictionary<string, TaskCompletionSource<OneBotResponse?>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ClientWebSocket? _socket;

    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public string? LastError { get; private set; }
    public long? SelfId { get; private set; }

    /// <summary>
    /// Connect, receive, reconnect — until cancelled. Returns only when the token is cancelled; every
    /// failure in between is a log line and a delay, because an adapter that gives up on the first
    /// network blip is an adapter you have to babysit.
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
                // Nothing is sent before this loop runs: an action awaited before the reader exists can
                // never be answered, because the answer would arrive on the socket nobody is reading yet.
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
    public async Task<Result<OneBotResponse>> CallAsync(Func<string, string> build, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return Result<OneBotResponse>.Failure(new(ErrorCode.Unavailable,
                "OneBot connection is not open.", endpoint.ToString(), true));

        var echo = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<OneBotResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[echo] = completion;
        try
        {
            await SendAsync(socket, build(echo), cancellationToken).ConfigureAwait(false);
            var wait = timeout ?? RequestTimeout;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(wait);
            using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
            OneBotResponse? response;
            try
            {
                response = await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Result<OneBotResponse>.Failure(new(ErrorCode.Timeout,
                    $"OneBot did not answer within {wait.TotalSeconds:0.#}s.", echo, true));
            }

            if (response is null)
                return Result<OneBotResponse>.Failure(new(ErrorCode.Unavailable, "OneBot closed the connection.", echo, true));
            return Result<OneBotResponse>.Success(response);
        }
        finally
        {
            _pending.TryRemove(echo, out _);
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
        completion.TrySetResult(OneBotProtocol.ReadResponse(root));
    }

    private void FailPending()
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var completion))
                completion.TrySetResult(null);
    }
}
