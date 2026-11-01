using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Graph.Contracts;

namespace ALN.Graph;

public sealed class GraphManager : IGraphRuntime
{
    private static readonly HashSet<string> AllowedCapabilities = new(StringComparer.Ordinal)
    { ScriptCapabilities.SelfAdapterSend, ScriptCapabilities.SelfAdapterPoll, ScriptCapabilities.ChannelAccept, ScriptCapabilities.ChannelReply, ScriptCapabilities.ModelComplete, ScriptCapabilities.SessionRun };
    private readonly IAdapterManager _adapters;
    private readonly IChannelManager _channels;
    private readonly IClockManager _clock;
    private readonly IModuleTypeCatalog _types;
    private readonly IScriptExecutor _executor;
    private readonly IAuditSink _audit;
    private readonly IModelManager? _models;
    private readonly ISessionManager? _sessions;
    private readonly Dictionary<string, ScriptPackage> _packages = new(StringComparer.Ordinal);
    private readonly List<MountedScript> _mounted = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Address> _drainingTargets = new();
    private readonly Dictionary<Address, int> _activeDispatches = new();
    private readonly Dictionary<Address, TaskCompletionSource> _dispatchDrained = new();
    private bool _loaded;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GraphManager(IAdapterManager adapters, IChannelManager channels, IClockManager clock, IModuleTypeCatalog types,
        IScriptExecutor executor, IAuditSink audit, IModelManager? models = null, ISessionManager? sessions = null)
    { _adapters = adapters; _channels = channels; _clock = clock; _types = types; _executor = executor; _audit = audit; _models = models; _sessions = sessions; }

    public IReadOnlyList<MountedScript> Mounted
    { get { _gate.Wait(); try { return _mounted.ToArray(); } finally { _gate.Release(); } } }

    public async ValueTask<Result> LoadAsync(IEnumerable<ScriptPackage> packages, CancellationToken cancellationToken)
    {
        var incoming = packages.ToArray();
        var candidate = new Dictionary<string, ScriptPackage>(StringComparer.Ordinal);
        foreach (var package in incoming)
        {
            var m = package.Manifest;
            if (string.IsNullOrWhiteSpace(m.Id) || !candidate.TryAdd(m.Id, package) || m.Version < 1 ||
                string.IsNullOrWhiteSpace(m.TargetType) || !_types.IsKnownAbstract(m.TargetType) || m.Events is null || m.Events.Length == 0 ||
                m.Events.Any(e => e is not (EventNames.AdapterEnvelope or EventNames.ChannelMessageAccepted or EventNames.ClockPulse)) || string.IsNullOrWhiteSpace(m.EntryPoint) ||
                m.MaxStatements is < 1 or > 1_000_000 || m.TimeoutMilliseconds is < 1 or > 60_000 ||
                m.MemoryLimitBytes is < 1_048_576 or > 268_435_456)
                return Result.Failure(new(ErrorCode.Invalid, $"Invalid script manifest: {m.Id}"));
            if (m.TrustDomain == TrustDomain.Restricted)
                return Result.Failure(new(ErrorCode.Denied, "Restricted scripts require the process-isolated runner, which is not part of this initial host."));
            if (m.Capabilities is null || m.Capabilities.Any(c => !AllowedCapabilities.Contains(c)))
                return Result.Failure(new(ErrorCode.Denied, $"Script {m.Id} requests an unsupported capability."));
            if (m.Mount is not (MountMode.Default or MountMode.Manual))
                return Result.Failure(new(ErrorCode.Invalid, $"Invalid mount mode: {m.Id}"));
            if (string.IsNullOrWhiteSpace(package.Source) || package.Source.Length > 256_000)
                return Result.Failure(new(ErrorCode.Invalid, $"Empty or oversized source: {m.Id}"));
            var validated = await _executor.ValidateAsync(package, cancellationToken).ConfigureAwait(false);
            if (!validated.Ok) return validated;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var targets = _adapters.List().Select(a => a.Address).Concat(_channels.List().Select(c => c.Address)).ToArray();
            var mounts = new List<MountedScript>();
            foreach (var package in incoming.Where(p => p.Manifest.Mount == MountMode.Default))
            foreach (var target in targets)
                if (IsCompatible(package.Manifest.TargetType, target)) mounts.Add(new(package, target));
            _packages.Clear();
            foreach (var pair in candidate) _packages.Add(pair.Key, pair.Value);
            _mounted.Clear(); _mounted.AddRange(mounts); _loaded = true;
            return Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> AttachAsync(Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded) return Result.Failure(new(ErrorCode.Unavailable, "Graph scripts are not loaded."));
            if (!_types.IsConcrete(target.TypeId)) return Result.Failure(new(ErrorCode.Invalid, "Mount target must be a concrete module instance."));
            if (!TargetExists(target)) return Result.Failure(new(ErrorCode.Invalid, $"Mount target not found: {target}"));
            _drainingTargets.Remove(target);
            foreach (var package in _packages.Values.Where(p => p.Manifest.Mount == MountMode.Default && IsCompatible(p.Manifest.TargetType, target)))
                if (!_mounted.Any(m => m.Package.Manifest.Id == package.Manifest.Id && m.Target == target)) _mounted.Add(new(package, target));
            return Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> DetachAsync(Address target, CancellationToken cancellationToken)
    {
        Task? drained = null;
        List<MountedScript> removed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _drainingTargets.Add(target);
            removed = _mounted.Where(m => m.Target == target).ToList();
            _mounted.RemoveAll(m => m.Target == target);
            if (_activeDispatches.TryGetValue(target, out var active) && active > 0)
            {
                if (!_dispatchDrained.TryGetValue(target, out var completion))
                    _dispatchDrained.Add(target, completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                drained = completion.Task;
            }
        }
        finally { _gate.Release(); }

        if (drained is not null)
        {
            try { await drained.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    _drainingTargets.Remove(target);
                    foreach (var mount in removed)
                        if (!_mounted.Any(existing => existing.Package.Manifest.Id == mount.Package.Manifest.Id && existing.Target == target))
                            _mounted.Add(mount);
                }
                finally { _gate.Release(); }
                throw;
            }
        }
        return Result.Success();
    }

    public async ValueTask<Result> ForceDetachAsync(Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _drainingTargets.Add(target);
            _mounted.RemoveAll(m => m.Target == target);
            return Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> MountAsync(string scriptId, Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_packages.TryGetValue(scriptId, out var package)) return Result.Failure(new(ErrorCode.Invalid, $"Script not found: {scriptId}"));
            if (package.Manifest.Mount != MountMode.Manual) return Result.Failure(new(ErrorCode.Denied, "Default scripts are mounted by instance lifecycle."));
            if (_drainingTargets.Contains(target)) return Result.Failure(new(ErrorCode.Unavailable, "Manual mount target is draining.", Retryable: true));
            if (!IsCompatible(package.Manifest.TargetType, target) || !TargetExists(target)) return Result.Failure(new(ErrorCode.Invalid, "Manual mount target is absent or incompatible."));
            if (_mounted.Any(m => m.Package.Manifest.Id == scriptId && m.Target == target)) return Result.Failure(new(ErrorCode.Conflict, "Script is already mounted."));
            _mounted.Add(new(package, target)); return Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> UnmountAsync(string scriptId, Address target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = _mounted.RemoveAll(m => m.Package.Manifest.Id == scriptId && m.Target == target);
            return removed == 0 ? Result.Failure(new(ErrorCode.Invalid, "Mount not found.")) : Result.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<Result> DispatchAsync(RoutedEvent routedEvent, CancellationToken cancellationToken)
    {
        var target = routedEvent.Target ?? routedEvent.Source;
        ClockPulse? clockPulse = null;
        if (routedEvent.Name == EventNames.ClockPulse && routedEvent.Source == _clock.Address && routedEvent.Target is not null)
        {
            try { clockPulse = JsonSerializer.Deserialize<ClockPulse>(routedEvent.PayloadJson, JsonOptions); }
            catch (JsonException) { }
        }
        var dispatchEntered = false;
        try
        {
            MountedScript[] handlers;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_drainingTargets.Contains(target))
                    return Result.Failure(new(ErrorCode.Unavailable, $"Graph target is draining: {target}", Retryable: true));
                _activeDispatches[target] = _activeDispatches.GetValueOrDefault(target) + 1;
                dispatchEntered = true;
                handlers = _mounted.Where(m => m.Target == target && m.Package.Manifest.Events.Contains(routedEvent.Name, StringComparer.Ordinal)).OrderBy(m => m.Package.Manifest.Id, StringComparer.Ordinal).ToArray();
            }
            finally { _gate.Release(); }
            Error? firstFailure = null;
            foreach (var mount in handlers)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(mount.Package.Manifest.TimeoutMilliseconds));
                var api = new ScriptApi(_adapters, _channels, _clock, _audit, _models, _sessions, mount, routedEvent, deadline.Token);
                var result = await _executor.ExecuteAsync(mount.Package, mount.Target, routedEvent, api, deadline.Token).ConfigureAwait(false);
                if (!result.Ok) firstFailure ??= result.Error;
            }
            return firstFailure is null ? Result.Success() : Result.Failure(firstFailure);
        }
        finally
        {
            if (dispatchEntered) await EndDispatchAsync(target).ConfigureAwait(false);
            if (clockPulse is not null && routedEvent.Target is { } clockTarget)
                await _clock.AcknowledgeAsync(clockPulse.SubscriptionId, clockTarget, routedEvent.EventId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask EndDispatchAsync(Address target)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_activeDispatches.TryGetValue(target, out var active)) return;
            if (active > 1)
            {
                _activeDispatches[target] = active - 1;
                return;
            }
            _activeDispatches.Remove(target);
            if (_dispatchDrained.Remove(target, out var completion)) completion.TrySetResult();
        }
        finally { _gate.Release(); }
    }

    private bool IsCompatible(string targetType, Address target) => _types.IsAssignableTo(target.TypeId, targetType);
    private bool TargetExists(Address target) => target.Module switch
    { "adapter" => _adapters.Get(target).Ok, "channel" => _channels.Get(target).Ok, _ => false };

    private sealed class ScriptApi(IAdapterManager adapters, IChannelManager channels, IClockManager clock, IAuditSink audit,
        IModelManager? models, ISessionManager? sessions, MountedScript mount, RoutedEvent current, CancellationToken ct) : IScriptApi
    {
        public async Task<string> RunSessionAsync(string sessionSpecJson, string userInput, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.SessionRun, cancellationToken).ConfigureAwait(false);
            if (sessions is null) throw new InvalidOperationException("Session manager is unavailable.");
            if (current.Name != EventNames.ChannelMessageAccepted || current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.SessionRun, "Not the source Channel message event.", cancellationToken).ConfigureAwait(false);
            var channelMessage = Parse<ChannelMessage>(current.PayloadJson);
            if (channelMessage.Channel != current.Source || channelMessage.MessageId != current.EventId ||
                !channels.IsBound(current.Source, channelMessage.Binding.Adapter, channelMessage.Binding.PlatformConversationId) ||
                !StringComparer.Ordinal.Equals(channelMessage.Text, userInput))
                await DenyAsync(ScriptCapabilities.SessionRun, "Channel message does not match routed event.", cancellationToken).ConfigureAwait(false);
            var spec = Parse<SessionSpec>(sessionSpecJson);
            if (spec.Tools?.Any(tool => tool.Name != "send_message") == true)
                return JsonSerializer.Serialize(new { ok = false, error = new { message = "Unknown Session tool binding." } }, JsonOptions);
            if (spec.Tools?.Any(tool => tool.Name == "send_message") == true &&
                !mount.Package.Manifest.Capabilities.Contains(ScriptCapabilities.ChannelReply, StringComparer.Ordinal))
                await DenyAsync(ScriptCapabilities.ChannelReply, "send_message requires channel.bound-send.", cancellationToken).ConfigureAwait(false);
            var identity = $"{current.Source}\n{channelMessage.Channel}";
            var sessionId = "s-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionRun, "allowed", sessionId), cancellationToken).ConfigureAwait(false);
            var input = new SessionTurnInput([new SessionChannelMessage(channelMessage.MessageId.ToString("N"), "user",
                channelMessage.Sender, channelMessage.Text, channelMessage.At)]);
            var result = await sessions.RunTurnAsync(sessionId, spec, current.EventId, input, this, cancellationToken).ConfigureAwait(false);
            if (!result.Ok || result.Value is null)
            {
                await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                    mount.Target, ScriptCapabilities.SessionRun, "failed", result.Error?.Message), cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = false, error = result.Error }, JsonOptions);
            }
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SessionRun, "completed", sessionId), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = true, result = result.Value }, JsonOptions);
        }

        public async ValueTask<Result<string>> ExecuteAsync(string sessionId, Guid activationId, ModelToolCall call,
            CancellationToken cancellationToken)
        {
            if (call.Name != "send_message")
                return Result<string>.Failure(new(ErrorCode.Denied, $"Session tool binding is not available: {call.Name}"));
            try { await RequireCapabilityAsync(ScriptCapabilities.ChannelReply, cancellationToken).ConfigureAwait(false); }
            catch (UnauthorizedAccessException ex)
            { return Result<string>.Failure(new(ErrorCode.Denied, ex.Message)); }
            if (current.Name != EventNames.ChannelMessageAccepted || current.Source != mount.Target)
                return Result<string>.Failure(new(ErrorCode.Denied, "Session send_message requires the source Channel message event."));

            ChannelMessage channelMessage;
            SessionSendArguments arguments;
            try
            {
                channelMessage = Parse<ChannelMessage>(current.PayloadJson);
                arguments = Parse<SessionSendArguments>(call.ArgumentsJson);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            { return Result<string>.Failure(new(ErrorCode.Invalid, "Session tool arguments are invalid.", ex.Message)); }
            var binding = channelMessage.Binding;
            if (channelMessage.Channel != current.Source || channelMessage.MessageId != current.EventId ||
                !channels.IsBound(current.Source, binding.Adapter, binding.PlatformConversationId) || string.IsNullOrWhiteSpace(arguments.Text))
                return Result<string>.Failure(new(ErrorCode.Invalid, "Session send_message requires non-empty text for the bound Channel."));

            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, "session.tool.send_message", "allowed", call.Id), cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await SendAsync(current.EventId, binding.Adapter,
                    new OutboundMessage(binding.PlatformConversationId, arguments.Text, channelMessage.PlatformMessageId), cancellationToken)
                    .ConfigureAwait(false);
                return Result<string>.Success(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            { return Result<string>.Failure(new(ErrorCode.Unavailable, "Session send_message failed.", ex.Message)); }
        }

        public async Task<string> CompleteModelAsync(string profileId, string requestJson, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.ModelComplete, cancellationToken).ConfigureAwait(false);
            if (models is null) throw new InvalidOperationException("Model manager is unavailable.");
            var request = Parse<ModelRequest>(requestJson);
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.ModelComplete, "allowed", profileId), cancellationToken).ConfigureAwait(false);
            var started = await models.CompleteAsync(profileId, request, cancellationToken).ConfigureAwait(false);
            if (!started.Ok || started.Value is null)
            {
                await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                    mount.Target, ScriptCapabilities.ModelComplete, "failed", started.Error?.Message), cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = false, error = started.Error, events = Array.Empty<object>() }, JsonOptions);
            }

            var events = new List<object>();
            var terminalCount = 0;
            var successful = true;
            await foreach (var modelEvent in started.Value.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                switch (modelEvent)
                {
                    case ThinkingDelta thinking: events.Add(new { type = "thinkingDelta", text = thinking.Text }); break;
                    case TextDelta text: events.Add(new { type = "textDelta", text = text.Text }); break;
                    case ToolCallDelta tool: events.Add(new { type = "toolCallDelta", tool.Index, tool.CallId, tool.Name, tool.ArgumentsJsonFragment }); break;
                    case ModelCompleted completed:
                        terminalCount++;
                        events.Add(new { type = "completed", completed.Reason, completed.Usage, completed.ProviderResponseId });
                        break;
                    case ModelFailed failed:
                        terminalCount++;
                        successful = false;
                        events.Add(new { type = "failed", error = failed.Error });
                        break;
                    default: throw new InvalidOperationException("Model provider emitted an unknown event type.");
                }
                if (terminalCount > 1) throw new InvalidOperationException("Model provider emitted more than one terminal event.");
            }
            if (terminalCount != 1) throw new InvalidOperationException("Model provider stream ended without exactly one terminal event.");
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.ModelComplete, successful ? "completed" : "failed", profileId), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = successful, events }, JsonOptions);
        }

        public async Task<string> SendSelfAsync(string json, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.SelfAdapterSend, cancellationToken).ConfigureAwait(false);
            if (current.Name != EventNames.AdapterEnvelope || current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.SelfAdapterSend, "Not the source Adapter event.", cancellationToken).ConfigureAwait(false);
            var envelope = Parse<AdapterEnvelope>(current.PayloadJson);
            var message = Parse<OutboundMessage>(json);
            if (envelope.Adapter != current.Source || message.PlatformConversationId != envelope.PlatformConversationId)
                await DenyAsync(ScriptCapabilities.SelfAdapterSend, "Reply destination differs from source conversation.", cancellationToken).ConfigureAwait(false);
            return await SendAsync(envelope.EventId, current.Source, message, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> AcceptIntoChannelAsync(CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.ChannelAccept, cancellationToken).ConfigureAwait(false);
            if (current.Name != EventNames.AdapterEnvelope || current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.ChannelAccept, "Not the source Adapter event.", cancellationToken).ConfigureAwait(false);
            var envelope = Parse<AdapterEnvelope>(current.PayloadJson);
            if (envelope.Adapter != current.Source || envelope.EventId != current.EventId)
                await DenyAsync(ScriptCapabilities.ChannelAccept, "Adapter payload does not match routed event.", cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id, mount.Target,
                ScriptCapabilities.ChannelAccept, "allowed"), cancellationToken).ConfigureAwait(false);
            var accepted = await channels.AcceptAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (!accepted.Ok) throw new InvalidOperationException(accepted.Error?.Message ?? "Channel intake failed.");
            return JsonSerializer.Serialize(new { acceptedChannels = accepted.Value }, JsonOptions);
        }

        public async Task<string> ReplyThroughChannelAsync(string json, CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.ChannelReply, cancellationToken).ConfigureAwait(false);
            if (current.Name != EventNames.ChannelMessageAccepted || current.Source != mount.Target)
                await DenyAsync(ScriptCapabilities.ChannelReply, "Not the mounted Channel event.", cancellationToken).ConfigureAwait(false);
            var routed = Parse<ChannelMessage>(current.PayloadJson);
            var message = Parse<OutboundMessage>(json);
            var binding = routed.Binding;
            if (routed.Channel != current.Source || !channels.IsBound(current.Source, binding.Adapter, binding.PlatformConversationId) ||
                message.PlatformConversationId != binding.PlatformConversationId)
                await DenyAsync(ScriptCapabilities.ChannelReply, "Reply destination differs from Channel binding.", cancellationToken).ConfigureAwait(false);
            return await SendAsync(current.EventId, binding.Adapter, message, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> PollSelfAsync(CancellationToken cancellationToken)
        {
            await RequireCapabilityAsync(ScriptCapabilities.SelfAdapterPoll, cancellationToken).ConfigureAwait(false);
            if (current.Name != EventNames.ClockPulse || current.Source != clock.Address || current.Target != mount.Target)
                await DenyAsync(ScriptCapabilities.SelfAdapterPoll, "Clock pulse is not addressed to this Adapter.", cancellationToken).ConfigureAwait(false);
            var pulse = Parse<ClockPulse>(current.PayloadJson);
            if (!clock.IsRegistered(pulse.SubscriptionId, mount.Target))
                return JsonSerializer.Serialize(new { polled = false, stale = true }, JsonOptions);
            var found = adapters.Get(mount.Target);
            if (!found.Ok || found.Value is not IPollingAdapter poller)
                throw new InvalidOperationException("Mounted Adapter does not expose a polling endpoint.");
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, ScriptCapabilities.SelfAdapterPoll, "allowed"), cancellationToken).ConfigureAwait(false);
            var polled = await poller.PollOnceAsync(cancellationToken).ConfigureAwait(false);
            if (!polled.Ok) throw new InvalidOperationException(polled.Error?.Message ?? "Adapter poll failed.");
            return JsonSerializer.Serialize(new { polled = true, accepted = polled.Value }, JsonOptions);
        }

        private async ValueTask RequireCapabilityAsync(string capability, CancellationToken cancellationToken)
        {
            if (mount.Package.Manifest.Capabilities.Contains(capability, StringComparer.Ordinal)) return;
            await DenyAsync(capability, "Capability was not declared.", cancellationToken).ConfigureAwait(false);
        }

        private async Task DenyAsync(string action, string detail, CancellationToken cancellationToken)
        {
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, current.EventId, mount.Package.Manifest.Id,
                mount.Target, action, "denied", detail), cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedAccessException(detail);
        }

        private async Task<string> SendAsync(Guid eventId, Address adapter, OutboundMessage message, CancellationToken cancellationToken)
        {
            await audit.AppendAsync(new(DateTimeOffset.UtcNow, eventId, mount.Package.Manifest.Id, adapter, "adapter.send", "allowed", message.PlatformConversationId), cancellationToken).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var found = adapters.Get(adapter);
            if (!found.Ok || found.Value is null) throw new InvalidOperationException("Bound Adapter is unavailable.");
            var result = await found.Value.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) throw new InvalidOperationException(result.Error?.Message ?? "Adapter send failed.");
            return JsonSerializer.Serialize(result.Value, JsonOptions);
        }

        private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new ArgumentException("Script API received invalid JSON.");
        private sealed record SessionSendArguments(string Text);
    }
}

public sealed class JsonScriptCatalog(string programRoot, string dataRoot) : IGraphScriptCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public async ValueTask<Result<IReadOnlyList<ScriptPackage>>> LoadAsync(CancellationToken cancellationToken)
    {
        var packages = new List<ScriptPackage>();
        try
        {
            var roots = new[]
            {
                (Path.Combine(programRoot, "resources", "scripts", "builtin"), TrustDomain.Builtin),
                (Path.Combine(dataRoot, "scripts", "trusted"), TrustDomain.Trusted),
                (Path.Combine(dataRoot, "scripts", "restricted"), TrustDomain.Restricted)
            };
            foreach (var (root, expectedDomain) in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                    if (bytes.Length > 64_000) return Result<IReadOnlyList<ScriptPackage>>.Failure(new(ErrorCode.Invalid, "Script manifest exceeds 64 KB."));
                    var manifest = JsonSerializer.Deserialize<ScriptFileManifest>(bytes, Options);
                    if (manifest is null || manifest.TrustDomain != expectedDomain) return Result<IReadOnlyList<ScriptPackage>>.Failure(new(ErrorCode.Denied, $"Invalid manifest or trust domain mismatch: {manifestPath}"));
                    var scriptPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "main.js");
                    var source = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
                    if (source.Length > 256_000) return Result<IReadOnlyList<ScriptPackage>>.Failure(new(ErrorCode.Invalid, $"Script source exceeds 256 KB: {manifest.Id}"));
                    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
                    packages.Add(new(new(manifest.Id, manifest.Version, manifest.TargetType, manifest.Mount, manifest.Events,
                        manifest.EntryPoint, manifest.Capabilities, manifest.TrustDomain, manifest.MaxStatements, manifest.TimeoutMilliseconds, manifest.MemoryLimitBytes), source, fingerprint));
                }
            }
            return Result<IReadOnlyList<ScriptPackage>>.Success(packages);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<IReadOnlyList<ScriptPackage>>.Failure(new(ErrorCode.Invalid, "Could not load script catalog.", ex.Message)); }
    }
}
