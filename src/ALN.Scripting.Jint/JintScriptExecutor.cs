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

    /// <summary>
    /// The definition a script gives for itself: <c>describe()</c>, called once at load.
    ///
    /// No <c>ctx</c> here on purpose. A definition is a fact about the tool (its name, what it takes), and one that had
    /// to call the host to say so would be a definition that could change between restarts. A script with no
    /// <c>describe()</c> answers <c>null</c>: the loader decides whether that is a mistake.
    /// </summary>
    public async ValueTask<Result<string>> DescribeAsync(ScriptPackage package, CancellationToken cancellationToken)
    {
        try
        {
            var engine = BuildEngine(package, cancellationToken);
            var described = await engine.EvaluateAsync($$"""
                (async function() {
                  {{package.Source}}
                  if (typeof describe !== "function") return null;
                  const answer = await describe();
                  return JSON.stringify(answer === undefined ? null : answer);
                })()
                """, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result<string>.Success(described?.ToString() ?? "null");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var code = IsConstraint(ex) ? ErrorCode.Timeout : ErrorCode.Invalid;
            return Result<string>.Failure(new(code, "Script describe() failed.", ex.Message));
        }
    }

    /// <summary>
    /// The script as a tool: one entry point, one argument object, one answer.
    ///
    /// The same host surface as an event handler — the code runs with the script's own capabilities and level, because a
    /// tool is the script acting, not the caller acting. A throw becomes a readable failure; a constraint (timeout,
    /// statement budget, memory) becomes <see cref="ErrorCode.Timeout"/>, which is the code that says "this may already
    /// have happened" rather than "nothing happened" — the distinction an async tool will need and this one already owes.
    /// </summary>
    public async ValueTask<Result<string>> InvokeAsync(ScriptPackage package, Address target, string argumentsJson,
        IScriptApi api, CancellationToken cancellationToken)
    {
        try
        {
            var engine = BuildEngine(package, cancellationToken);
            engine.SetValue("__argsJson", argumentsJson);
            // The same bindings an event handler gets. Kept as a block rather than shared with ExecuteAsync because the
            // two wrappers differ only in how they call the script, and splitting the JS in half to save twenty lines
            // would make both harder to read than the duplication does.
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

            var answer = await engine.EvaluateAsync($$"""
                (async function() {
                  const args = JSON.parse(__argsJson);
                  const log = Object.freeze({
                    info: (message, detail) => __log("info", String(message ?? ""), detail === undefined ? "" : JSON.stringify(detail)),
                    warn: (message, detail) => __log("warn", String(message ?? ""), detail === undefined ? "" : JSON.stringify(detail)),
                    error: (message, detail) => __log("error", String(message ?? ""), detail === undefined ? "" : JSON.stringify(detail))
                  });
                  const actions = Object.freeze({
                    list: async () => JSON.parse(await __actionsList()),
                    call: async (id, args) => JSON.parse(await __actionsCall(String(id), args === undefined ? "{}" : JSON.stringify(args)))
                  });
                  const state = Object.freeze({
                    get: async (key) => { const raw = await __stateGet(String(key)); return raw === "" ? undefined : JSON.parse(raw); },
                    set: async (key, value) => { await __stateSet(String(key), JSON.stringify(value === undefined ? null : value)); },
                    delete: async (key) => { await __stateDelete(String(key)); }
                  });
                  // No `event` and no `eventName`: a tool call is not an event, and pretending one arrived is how a
                  // script ends up guessing which of its two lives it is living.
                  const ctx = Object.freeze({
                    session: Object.freeze({
                      run: (spec, input) => __sessionRun(JSON.stringify(spec), input),
                      activate: (spec) => __sessionActivate(JSON.stringify(spec)),
                      open: (options) => __sessionOpen(JSON.stringify(options ?? {})),
                      message: (options) => __sessionMessage(JSON.stringify(options ?? {}))
                    }),
                    model: Object.freeze({ complete: (profileId, request) => __modelComplete(profileId, JSON.stringify(request)) }),
                    self: Object.freeze({ adapter: Object.freeze({ send: (message) => __sendSelf(JSON.stringify(message)), poll: () => __pollSelf() }) }),
                    channel: Object.freeze({ accept: () => __channelAccept(), route: (options) => __channelRoute(JSON.stringify(options ?? {})), reply: (message) => __channelReply(JSON.stringify(message)) }),
                    log,
                    state,
                    actions
                  });
                  {{package.Source}}
                  const call = {{package.Manifest.Call}};
                  if (typeof call !== "function") throw new Error("Script call entry point is not a function.");
                  const out = await call(args, ctx);
                  return JSON.stringify(out === undefined ? null : out);
                })()
                """, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result<string>.Success(answer?.ToString() ?? "null");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // "It ran out of time" and "it threw" are different answers: the first may have reached the host and taken
            // effect, the second did not get that far. Collapsing them would teach the model to retry a call that
            // already happened.
            var code = IsConstraint(ex) ? ErrorCode.Timeout : ErrorCode.Invalid;
            return Result<string>.Failure(new(code, "Script tool call failed.", ex.Message));
        }
    }

    /// <summary>Whether this is the interpreter stopping the script rather than the script stopping itself.</summary>
    private static bool IsConstraint(Exception exception) =>
        // Jint's own types, spelled with an alias: this assembly's namespace is ALN.Scripting.Jint, so a bare
        // `Jint.Runtime.…` would resolve against it and find nothing.
        exception is global::Jint.Runtime.JavaScriptException ? false
            : exception is TimeoutException
                or OperationCanceledException
                or global::Jint.Runtime.StatementsCountOverflowException
                or global::Jint.Runtime.MemoryLimitExceededException;

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
