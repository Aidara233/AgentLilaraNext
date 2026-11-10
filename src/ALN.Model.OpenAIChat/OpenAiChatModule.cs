using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Model.OpenAIChat;

public sealed class OpenAiChatModule : IModulePlugin
{
    public const string PackageId = "aln.model.openai-chat";
    private static readonly ModelProviderDescriptor ProviderDescriptor = new(
        "model.openai-chat", "chat", "OpenAI Chat Completions", ModelCapabilities.Streaming | ModelCapabilities.ToolCalls);
    private static readonly ModulePackageManifest PackageManifest = new(
        PackageId, "1.0.0", ModulePackageKind.Plugin,
        [new ModuleTypeProvision(ProviderDescriptor.TypeId, "model")], [], ModuleReloadPolicy.Replace);
    private IModelManager? _manager;

    public string ParticipantId => PackageId;
    public ModulePackageManifest Manifest => PackageManifest;

    public ValueTask<Result> StartAsync(IModulePluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manager = context.HostServices.GetService(typeof(IModelManager)) as IModelManager;
        if (manager is null)
            return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "Model manager is not configured.")));
        _manager = manager;
        return ValueTask.FromResult(manager.RegisterFactory(PackageId, new OpenAiChatProviderFactory(ProviderDescriptor)));
    }

    public ValueTask<Result<ModuleUnloadReadiness>> PrepareUnloadAsync(
        ModuleUnloadPreparation preparation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manager = _manager;
        if (manager is null) return ValueTask.FromResult(Result<ModuleUnloadReadiness>.Success(new(false, "Model manager is unavailable.")));
        // What blocks an unload is a call in flight, not an instance that happens to exist. An idle instance keeps the
        // old assembly alive (and therefore keeps working) until it is destroyed, which is exactly the state a hot
        // replacement wants: the new version registers a new factory, and the old instances finish their lives on the
        // old code. Requiring every profile to be destroyed first made this package impossible to replace without a
        // restart the moment anybody had ever configured one.
        var busy = manager.List()
            .Where(provider => provider.Descriptor.TypeId == ProviderDescriptor.TypeId && provider.InFlight > 0)
            .Select(provider => provider.Address.ToString())
            .ToArray();
        return ValueTask.FromResult(busy.Length > 0
            ? Result<ModuleUnloadReadiness>.Success(new(false,
                $"OpenAI Chat still has calls in flight: {string.Join(", ", busy)}. A switch waits for the turn boundary."))
            : Result<ModuleUnloadReadiness>.Success(new(true)));
    }

    public ValueTask<Result> DisconnectAsync(bool forced, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}

public sealed class OpenAiChatProviderFactory(ModelProviderDescriptor descriptor) : IModelProviderFactory
{
    public string TypeId => descriptor.TypeId;
    public ModelProviderDescriptor Describe => descriptor;

    public ValueTask<Result<IModelProvider>> CreateAsync(ModelProviderSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (spec.TypeId != TypeId || !spec.Address.IsInstance)
            return ValueTask.FromResult(Result<IModelProvider>.Failure(new(ErrorCode.Invalid, "OpenAI Chat provider requires a model.openai-chat instance address.")));
        var options = OpenAiChatProvider.ReadOptions(spec.ConfigurationJson);
        if (!options.Ok || options.Value is null)
            return ValueTask.FromResult(Result<IModelProvider>.Failure(options.Error!));
        return ValueTask.FromResult(Result<IModelProvider>.Success(
            new OpenAiChatProvider(spec.Address, descriptor, options.Value)));
    }
}

public sealed record OpenAiChatProviderOptions(string ApiKey, string? Endpoint = null);

/// <summary>
/// The settings one request is sent with. Held as a whole and replaced as a whole: a provider whose endpoint changed
/// and whose key did not is not a state anybody asked for.
/// </summary>
public sealed record OpenAiChatRuntimeOptions(Uri Endpoint, string ApiKey);

/// <summary>
/// The wire shape of one chat message.
///
/// <c>content</c> is always present, empty string when there is nothing to say. The OpenAI shape allows it to be
/// absent on an assistant message that only called tools; DeepSeek rejects that outright (HTTP 422, "missing field
/// `content`"), and that message is not an edge case — a turn that ends by asking to wait says nothing and calls a
/// tool, so replaying one such turn broke the next round of a real conversation. Sending the empty string costs a
/// few bytes and cannot be argued about.
///
/// Public because the shape is the thing worth testing without an HTTP server: a 422 is a slow way to learn it.
/// </summary>
public static class ChatPayload
{
    public static Dictionary<string, object?> Message(ModelMessage message)
    {
        var item = new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = Content(message) };
        if (message.Name is not null) item["name"] = message.Name;
        if (message.ToolCallId is not null) item["tool_call_id"] = message.ToolCallId;
        if (message.ToolCalls is { Count: > 0 }) item["tool_calls"] = message.ToolCalls.Select(call => new
        { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.ArgumentsJson } }).ToArray();
        return item;
    }

    /// <summary>
    /// A tool as this protocol wants it.
    ///
    /// <see cref="Parameters"/> is the reason this lives here instead of being spelled out at the call site: a tool whose
    /// source did not give a usable schema used to take the whole request down, because the schema was deserialized
    /// inside the builder and a bad one threw. A turn that never called that tool still failed to start — and the error
    /// said nothing about which tool was at fault. A tool nobody described is a tool with no arguments, and that is a
    /// thing this protocol can say.
    /// </summary>
    public static object Tool(ModelToolDefinition tool) => new
    {
        type = "function",
        function = new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = Parameters(tool.ParametersJson),
        }
    };

    /// <summary>
    /// A parameter schema, or the empty object schema when there is not a usable one.
    ///
    /// Malformed and absent are treated the same on purpose: the request needs an object schema, the schema is the
    /// provider's contract rather than ours, and refusing the whole turn over a field a script left out punishes every
    /// tool in the list. The tool is still offered, still callable, and a script that then complains about missing
    /// arguments is a script saying so — which is where that belongs.
    /// </summary>
    public static JsonElement Parameters(string? parametersJson)
    {
        if (!string.IsNullOrWhiteSpace(parametersJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(parametersJson);
                // An array or a bare string is not an object schema; neither will be accepted by a provider, and both
                // are worse than the empty one.
                if (parsed.ValueKind == JsonValueKind.Object) return parsed;
            }
            catch (JsonException)
            {
                // Fall through to the empty schema.
            }
        }
        return JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
    }

    /// <summary>
    /// The <c>content</c> field: a string when the message is text, an array of parts when it is not.
    ///
    /// The two shapes are not interchangeable — a provider accepts both, but only the array form can carry a picture —
    /// and a text-only message keeps sending a plain string, so nothing about an ordinary request changed shape when
    /// pictures arrived. That matters beyond aesthetics: the string form is what every provider in the wild has been
    /// accepting for years, and a proxy in front of one of them may not understand the array at all.
    /// </summary>
    private static object Content(ModelMessage message)
    {
        if (message.Parts is not { Count: > 0 } parts) return message.Content ?? "";
        return parts.Select(part => part switch
        {
            TextPart text => (object)new { type = "text", text = text.Text },
            ImagePart image => new { type = "image_url", image_url = new { url = image.ToDataUrl() } },
            // Unreachable for the two types that exist; kept so a future part cannot be dropped silently into an empty
            // message, which a provider reads as "the user said nothing".
            _ => new { type = "text", text = "" }
        }).ToArray();
    }
}

internal sealed class OpenAiChatProvider : IModelProvider, IModelProviderReconfigurable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new();
    /// <summary>Replaced whole, read whole. A request that already started keeps the settings it began with.</summary>
    private OpenAiChatRuntimeOptions _options;

    public OpenAiChatProvider(Address address, ModelProviderDescriptor descriptor, OpenAiChatRuntimeOptions options)
    { Address = address; Describe = descriptor; _options = options; }

    public Address Address { get; }
    public ModuleId Id => new(Address.ToString());
    public ModelProviderDescriptor Describe { get; }

    /// <summary>
    /// Reads one configuration into the settings a request uses. Public because the factory and the running provider
    /// have to agree on what a configuration means — two parsers would eventually disagree about which fields matter.
    /// </summary>
    public static Result<OpenAiChatRuntimeOptions> ReadOptions(string configurationJson)
    {
        OpenAiChatProviderOptions? options;
        try { options = JsonSerializer.Deserialize<OpenAiChatProviderOptions>(configurationJson, JsonOptions); }
        catch (JsonException ex) { return Result<OpenAiChatRuntimeOptions>.Failure(new(ErrorCode.Invalid, "OpenAI Chat configuration is invalid JSON.", ex.Message)); }
        if (options is null || string.IsNullOrWhiteSpace(options.ApiKey))
            return Result<OpenAiChatRuntimeOptions>.Failure(new(ErrorCode.Invalid, "OpenAI Chat configuration requires apiKey."));
        // The operator fills in the host (or pastes a full method URL); the protocol completes the path.
        var endpoint = ChatEndpoint.Normalize(options.Endpoint);
        if (!endpoint.Ok || endpoint.Value is null)
            return Result<OpenAiChatRuntimeOptions>.Failure(endpoint.Error!);
        return Result<OpenAiChatRuntimeOptions>.Success(new(endpoint.Value, options.ApiKey));
    }

    /// <inheritdoc />
    public Result ApplyConfiguration(string configurationJson)
    {
        var options = ReadOptions(configurationJson);
        if (!options.Ok || options.Value is null) return Result.Failure(options.Error!);
        // One write, one whole set of settings: a request in flight read the previous value and is unaffected, and the
        // next request cannot see half of this one.
        Volatile.Write(ref _options, options.Value);
        return Result.Success();
    }

    public async IAsyncEnumerable<ModelEvent> CompleteAsync(ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ModelId) || request.Messages.Count == 0)
        {
            yield return new ModelFailed(new(ErrorCode.Invalid, "Model id and at least one message are required."));
            yield break;
        }

        // One read for the whole request. Two reads could straddle a configuration change and send a request to the new
        // endpoint with the old key — which fails in a way that looks like the key is wrong.
        var settings = Volatile.Read(ref _options);
        HttpRequestMessage? httpRequest = null;
        Error? requestError = null;
        try { httpRequest = BuildRequest(request, settings); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // The type is part of the message on purpose: "could not be mapped" with a bare parser message is a bug
            // report nobody can act on, and this one used to arrive without ever naming the field or the tool.
            requestError = new(ErrorCode.Invalid, "Model request could not be mapped to OpenAI Chat.",
                $"{ex.GetType().Name}: {ex.Message}");
        }
        if (requestError is not null)
        {
            yield return new ModelFailed(requestError);
            yield break;
        }
        httpRequest!.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        var enumerator = StreamAsync(httpRequest, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var terminalCount = 0;
        try
        {
            while (true)
            {
                var hasNext = false;
                Exception? streamError = null;
                try { hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { streamError = ex; }

                if (streamError is not null)
                {
                    yield return new ModelFailed(MapTransportError(streamError));
                    yield break;
                }
                if (!hasNext) break;
                var current = enumerator.Current;
                if (current is ModelCompleted or ModelFailed) terminalCount++;
                if (terminalCount > 1)
                {
                    yield return new ModelFailed(new(ErrorCode.Invalid, "OpenAI Chat stream emitted multiple terminal events."));
                    yield break;
                }
                yield return current;
            }
        }
        finally { await enumerator.DisposeAsync().ConfigureAwait(false); }

        if (terminalCount != 1)
            yield return new ModelFailed(new(ErrorCode.Unavailable, "OpenAI Chat stream ended without exactly one terminal event.", Retryable: true));
    }

    private async IAsyncEnumerable<ModelEvent> StreamAsync(HttpRequestMessage request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (request)
        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Length > 4_000) body = body[..4_000];
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                // The status alone is not a diagnosis. The body names the field that was wrong — usually a tool schema
                // or a message shape — and the people reading this are looking at a chat window, not at a log. Keep
                // enough of it here to act on, and the whole thing in the detail.
                var why = body.Length > 240 ? body[..240] + "…" : body;
                yield return new ModelFailed(new(retryable ? ErrorCode.Unavailable : ErrorCode.Invalid,
                    $"OpenAI Chat returned HTTP {(int)response.StatusCode}: {why}", body, retryable));
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var usage = new ModelUsage(0, 0);
            string? finishReason = null;
            string? responseId = null;
            var sawDone = false;
            while (true)
            {
                string? line = null;
                Exception? readError = null;
                try { line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { readError = ex; }
                if (readError is not null) throw readError;
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") { sawDone = true; break; }

                var parsed = ParseChunk(data);
                if (!parsed.Ok || parsed.Value is null)
                {
                    yield return new ModelFailed(parsed.Error ?? new(ErrorCode.Invalid, "OpenAI Chat stream chunk is invalid."));
                    yield break;
                }
                if (parsed.Value.Usage is not null) usage = parsed.Value.Usage;
                if (parsed.Value.FinishReason is not null) finishReason = parsed.Value.FinishReason;
                if (parsed.Value.ResponseId is not null) responseId = parsed.Value.ResponseId;
                foreach (var modelEvent in parsed.Value.Events) yield return modelEvent;
            }

            if (!sawDone || finishReason is null)
            {
                yield return new ModelFailed(new(ErrorCode.Unavailable, "OpenAI Chat stream ended before its terminal chunk.", Retryable: true));
                yield break;
            }
            yield return new ModelCompleted(MapStopReason(finishReason), usage, responseId);
        }
    }

    private static Result<ParsedChunk> ParseChunk(string data)
    {
        try
        {
            using var chunk = JsonDocument.Parse(data);
            var root = chunk.RootElement;
            ModelUsage? usage = null;
            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                usage = new(ReadInt(usageElement, "prompt_tokens"), ReadInt(usageElement, "completion_tokens"),
                    ReadNullableInt(usageElement, "prompt_tokens_details", "cached_tokens"), null);
            var events = new List<ModelEvent>();
            var responseId = ReadString(root, "id");
            string? finishReason = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                    finishReason = finish.GetString();
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                        events.Add(new TextDelta(text));
                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        var index = toolCall.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0;
                        var id = ReadString(toolCall, "id");
                        string? name = null;
                        var arguments = "";
                        if (toolCall.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
                        { name = ReadString(function, "name"); arguments = ReadString(function, "arguments") ?? ""; }
                        events.Add(new ToolCallDelta(index, id, name, arguments));
                    }
                }
            }
            return Result<ParsedChunk>.Success(new(events, usage, finishReason, responseId));
        }
        catch (JsonException ex)
        { return Result<ParsedChunk>.Failure(new(ErrorCode.Invalid, "OpenAI Chat returned malformed streaming JSON.", ex.Message)); }
    }

    private HttpRequestMessage BuildRequest(ModelRequest request, OpenAiChatRuntimeOptions settings)
    {
        var messages = request.Messages.Select(ChatPayload.Message).ToArray();
        var body = new Dictionary<string, object?>
        { ["model"] = request.ModelId, ["messages"] = messages, ["stream"] = true, ["stream_options"] = new { include_usage = true } };
        if (request.Sampling?.Temperature is { } temperature) body["temperature"] = temperature;
        if (request.Sampling?.MaxOutputTokens is { } maxTokens) body["max_completion_tokens"] = maxTokens;
        if (request.Sampling?.TopP is { } topP) body["top_p"] = topP;
        if (request.Sampling?.Stop is { Count: > 0 } stop) body["stop"] = stop;
        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = request.Tools.Select(ChatPayload.Tool).ToArray();
            body["tool_choice"] = "auto";
        }
        return new HttpRequestMessage(HttpMethod.Post, settings.Endpoint) { Content = JsonContent.Create(body, options: JsonOptions) };
    }

    private static Error MapTransportError(Exception exception) => exception switch
    {
        HttpRequestException ex => new(ErrorCode.Unavailable, "OpenAI Chat request failed before receiving a response.", ex.Message, Retryable: true),
        TaskCanceledException ex => new(ErrorCode.Timeout, "OpenAI Chat request timed out.", ex.Message, Retryable: true),
        IOException ex => new(ErrorCode.Unavailable, "OpenAI Chat stream was interrupted.", ex.Message, Retryable: true),
        _ => new(ErrorCode.Internal, "OpenAI Chat request failed.", exception.Message)
    };

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;
    private static int? ReadNullableInt(JsonElement element, string parent, string property) =>
        element.TryGetProperty(parent, out var nested) && nested.ValueKind == JsonValueKind.Object && nested.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static StopReason MapStopReason(string value) => value switch
    {
        "stop" => StopReason.EndTurn,
        "tool_calls" or "function_call" => StopReason.ToolUse,
        "length" => StopReason.MaxTokens,
        "content_filter" => StopReason.ContentFilter,
        _ => StopReason.StopSequence
    };

    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
    private sealed record ParsedChunk(IReadOnlyList<ModelEvent> Events, ModelUsage? Usage, string? FinishReason, string? ResponseId);
}
