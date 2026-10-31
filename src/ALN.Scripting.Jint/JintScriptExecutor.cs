using System.Text.Json;
using System.Text.RegularExpressions;
using ALN.Contracts;
using ALN.Graph.Contracts;
using Jint;

namespace ALN.Scripting.Jint;

public sealed class JintScriptExecutor(IExecutionJournal journal) : IScriptExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IExecutionJournal _journal = journal;

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
        var started = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        try
        {
            var engine = BuildEngine(package, cancellationToken);
            engine.SetValue("__eventJson", routedEvent.PayloadJson);
            engine.SetValue("__sendSelf", new Func<string, Task<string>>(json => api.SendSelfAsync(json, cancellationToken)));
            engine.SetValue("__channelAccept", new Func<Task<string>>(() => api.AcceptIntoChannelAsync(cancellationToken)));
            engine.SetValue("__channelReply", new Func<string, Task<string>>(json => api.ReplyThroughChannelAsync(json, cancellationToken)));
            var source = $$"""
                (async function() {
                  const event = JSON.parse(__eventJson);
                  const sendSelf = __sendSelf;
                  const acceptChannel = __channelAccept;
                  const replyChannel = __channelReply;
                  const ctx = Object.freeze({
                    self: Object.freeze({ adapter: Object.freeze({ send: (message) => sendSelf(JSON.stringify(message)) }) }),
                    channel: Object.freeze({ accept: () => acceptChannel(), reply: (message) => replyChannel(JSON.stringify(message)) })
                  });
                  {{package.Source}}
                  const handler = {{package.Manifest.EntryPoint}};
                  if (typeof handler !== "function") throw new Error("Script entry point is not a function.");
                  await handler(event, ctx);
                })()
                """;
            await engine.EvaluateAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
            _journal.Record(new(id, package.Manifest.Id, target, routedEvent.Name, true, null, started, DateTimeOffset.UtcNow)
            { EventId = routedEvent.EventId, Fingerprint = package.Fingerprint });
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            _journal.Record(new(id, package.Manifest.Id, target, routedEvent.Name, false, "Cancelled or timed out", started, DateTimeOffset.UtcNow)
            { EventId = routedEvent.EventId, Fingerprint = package.Fingerprint });
            throw;
        }
        catch (Exception ex)
        {
            _journal.Record(new(id, package.Manifest.Id, target, routedEvent.Name, false, ex.Message, started, DateTimeOffset.UtcNow)
            { EventId = routedEvent.EventId, Fingerprint = package.Fingerprint });
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
