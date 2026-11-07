using System.Text.Json;
using System.Text.RegularExpressions;
using ALN.Contracts;
using ALN.Graph.Contracts;
using Jint;

namespace ALN.Scripting.Jint;

public sealed class JintScriptExecutor : IScriptExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ValueTask<Result> ValidateAsync(ScriptPackage package, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Regex.IsMatch(package.Manifest.EntryPoint, "^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant))
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, "Entry point must be a JavaScript identifier.")));
            var engine = BuildEngine(package, cancellationToken);
            engine.Execute(package.Source);
            var type = engine.Evaluate($"typeof {package.Manifest.EntryPoint}").AsString();
            if (type != "function") return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"Entry point '{package.Manifest.EntryPoint}' is not a function.")));
            return ValueTask.FromResult(Result.Success());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, "Script source is invalid.", ex.Message))); }
    }

    public async ValueTask<Result> ExecuteAsync(ScriptPackage package, Address target, RoutedEvent routedEvent,
        IScriptApi api, CancellationToken cancellationToken)
    {
        try
        {
            var engine = BuildEngine(package, cancellationToken);
            engine.SetValue("__eventJson", routedEvent.PayloadJson);
            engine.SetValue("__eventNameJson", JsonSerializer.Serialize(routedEvent.Name));
            engine.SetValue("__sessionRun", new Func<string, string, Task<string>>((specJson, input) => api.RunSessionAsync(specJson, input, cancellationToken)));
            engine.SetValue("__sessionActivate", new Func<string, Task<string>>(specJson => api.ActivateSessionAsync(specJson, cancellationToken)));
            engine.SetValue("__sessionOpen", new Func<string, Task<string>>(json => api.OpenSessionAsync(json, cancellationToken)));
            engine.SetValue("__sessionMessage", new Func<string, Task<string>>(json => api.MessageSessionAsync(json, cancellationToken)));
            engine.SetValue("__modelComplete", new Func<string, string, Task<string>>((profileId, json) => api.CompleteModelAsync(profileId, json, cancellationToken)));
            engine.SetValue("__sendSelf", new Func<string, Task<string>>(json => api.SendSelfAsync(json, cancellationToken)));
            engine.SetValue("__pollSelf", new Func<Task<string>>(() => api.PollSelfAsync(cancellationToken)));
            engine.SetValue("__channelAccept", new Func<Task<string>>(() => api.AcceptIntoChannelAsync(cancellationToken)));
            engine.SetValue("__channelReply", new Func<string, Task<string>>(json => api.ReplyThroughChannelAsync(json, cancellationToken)));
            engine.SetValue("__channelRoute", new Func<string, Task<string>>(json => api.RouteIntoChannelAsync(json, cancellationToken)));
            engine.SetValue("__log", new Action<string, string, string>((level, message, detailJson) => api.Log(level, message, detailJson)));
            engine.SetValue("__actionsList", new Func<Task<string>>(() => api.ListActionsAsync(cancellationToken)));
            engine.SetValue("__actionsCall", new Func<string, string, Task<string>>((action, json) => api.CallActionAsync(action, json, cancellationToken)));
            // State is handed over as JSON text in both directions: the script decides what a value means,
            // the host only knows a namespace and a key.
            engine.SetValue("__stateGet", new Func<string, Task<string>>(key => api.ReadStateAsync(key, cancellationToken)));
            engine.SetValue("__stateSet", new Func<string, string, Task<string>>(async (key, json) =>
            {
                await api.SetStateAsync(key, json, cancellationToken).ConfigureAwait(false);
                return "";
            }));
            engine.SetValue("__stateDelete", new Func<string, Task<string>>(async key =>
            {
                await api.DeleteStateAsync(key, cancellationToken).ConfigureAwait(false);
                return "";
            }));
            var source = $$"""
                (async function() {
                  const event = JSON.parse(__eventJson);
                  const eventName = JSON.parse(__eventNameJson);
                  const runSession = (spec, input) => __sessionRun(JSON.stringify(spec), input);
                  const activateSession = (spec) => __sessionActivate(JSON.stringify(spec));
                  const openSession = (options) => __sessionOpen(JSON.stringify(options ?? {}));
                  const messageSession = (options) => __sessionMessage(JSON.stringify(options ?? {}));
                  const completeModel = (profileId, request) => __modelComplete(profileId, JSON.stringify(request));
                  const sendSelf = __sendSelf;
                  const pollSelf = __pollSelf;
                  const acceptChannel = __channelAccept;
                  const replyChannel = __channelReply;
                  const routeChannel = __channelRoute;
                  const log = Object.freeze({
                    info: (message, detail) => __log("info", String(message ?? ""), detail === undefined ? "" : JSON.stringify(detail)),
                    warn: (message, detail) => __log("warn", String(message ?? ""), detail === undefined ? "" : JSON.stringify(detail)),
                    error: (message, detail) => __log("error", String(message ?? ""), detail === undefined ? "" : JSON.stringify(detail))
                  });
                  // The control plane, as this script: listing says what exists and what is out of reach; calling is
                  // authorized against the same table the model and a person are authorized against. A refusal comes
                  // back as data, so a script can decide what to do next instead of dying on it.
                  const actions = Object.freeze({
                    list: async () => JSON.parse(await __actionsList()),
                    call: async (id, args) => JSON.parse(await __actionsCall(String(id), args === undefined ? "{}" : JSON.stringify(args)))
                  });
                  // The script's own durable memory: survives restarts, scoped by the host to this mount.
                  const state = Object.freeze({
                    get: async (key) => { const raw = await __stateGet(String(key)); return raw === "" ? undefined : JSON.parse(raw); },
                    set: async (key, value) => { await __stateSet(String(key), JSON.stringify(value === undefined ? null : value)); },
                    delete: async (key) => { await __stateDelete(String(key)); }
                  });
                  const ctx = Object.freeze({
                    session: Object.freeze({ run: (spec, input) => runSession(spec, input), activate: (spec) => activateSession(spec), open: (options) => openSession(options), message: (options) => messageSession(options) }),
                    model: Object.freeze({ complete: (profileId, request) => completeModel(profileId, request) }),
                    self: Object.freeze({ adapter: Object.freeze({ send: (message) => sendSelf(JSON.stringify(message)), poll: () => pollSelf() }) }),
                    channel: Object.freeze({ accept: () => acceptChannel(), route: (options) => routeChannel(JSON.stringify(options ?? {})), reply: (message) => replyChannel(JSON.stringify(message)) }),
                    log,
                    state,
                    actions,
                    eventName
                  });
                  {{package.Source}}
                  const handler = {{package.Manifest.EntryPoint}};
                  if (typeof handler !== "function") throw new Error("Script entry point is not a function.");
                  await handler(event, ctx);
                })()
                """;
            await engine.EvaluateAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result.Failure(new(ErrorCode.Invalid, "Script execution failed.", ex.Message));
        }
    }

    private static Engine BuildEngine(ScriptPackage package, CancellationToken cancellationToken)
    {
        var options = new Options { ExperimentalFeatures = ExperimentalFeature.TaskInterop, AgentCanSuspend = false };
        options.Interop.Enabled = false;
        options.Interop.AllowGetType = false;
        options.Interop.AllowSystemReflection = false;
        options.Interop.AllowWrite = false;
        options.MaxStatements(package.Manifest.MaxStatements)
            .LimitMemory(package.Manifest.MemoryLimitBytes)
            .TimeoutInterval(TimeSpan.FromMilliseconds(package.Manifest.TimeoutMilliseconds))
            .CancellationToken(cancellationToken);
        // How long one await may sit on a host call. Jint's default here is ten seconds, which is not "no limit": a
        // script whose whole job is to wait for something slow — channel.chat waits for a whole Session turn — was cut
        // off at ten seconds while the work it started kept going, so every reply slower than that left a failed
        // dispatch behind it and no record of why. The manifest's timeout is the number already meant to bound this
        // handler (GraphManager arms the same value as the dispatch deadline), so it is the number used here too:
        // a script that needs longer says so in its manifest, where a person can read it, instead of discovering a
        // hidden ten seconds.
        if (package.Manifest.TimeoutMilliseconds > 0)
            options.Constraints.PromiseTimeout = TimeSpan.FromMilliseconds(package.Manifest.TimeoutMilliseconds);
        return new Engine(options);
    }
}
