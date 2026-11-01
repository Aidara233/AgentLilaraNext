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
            engine.SetValue("__modelComplete", new Func<string, string, Task<string>>((profileId, json) => api.CompleteModelAsync(profileId, json, cancellationToken)));
            engine.SetValue("__sendSelf", new Func<string, Task<string>>(json => api.SendSelfAsync(json, cancellationToken)));
            engine.SetValue("__pollSelf", new Func<Task<string>>(() => api.PollSelfAsync(cancellationToken)));
            engine.SetValue("__channelAccept", new Func<Task<string>>(() => api.AcceptIntoChannelAsync(cancellationToken)));
            engine.SetValue("__channelReply", new Func<string, Task<string>>(json => api.ReplyThroughChannelAsync(json, cancellationToken)));
            engine.SetValue("__channelRoute", new Func<string, Task<string>>(json => api.RouteIntoChannelAsync(json, cancellationToken)));
            engine.SetValue("__log", new Action<string, string, string>((level, message, detailJson) => api.Log(level, message, detailJson)));
            var source = $$"""
                (async function() {
                  const event = JSON.parse(__eventJson);
                  const eventName = JSON.parse(__eventNameJson);
                  const runSession = (spec, input) => __sessionRun(JSON.stringify(spec), input);
                  const activateSession = (spec) => __sessionActivate(JSON.stringify(spec));
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
                  const ctx = Object.freeze({
                    session: Object.freeze({ run: (spec, input) => runSession(spec, input), activate: (spec) => activateSession(spec) }),
                    model: Object.freeze({ complete: (profileId, request) => completeModel(profileId, request) }),
                    self: Object.freeze({ adapter: Object.freeze({ send: (message) => sendSelf(JSON.stringify(message)), poll: () => pollSelf() }) }),
                    channel: Object.freeze({ accept: () => acceptChannel(), route: (options) => routeChannel(JSON.stringify(options ?? {})), reply: (message) => replyChannel(JSON.stringify(message)) }),
                    log,
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
        return new Engine(options);
    }
}
