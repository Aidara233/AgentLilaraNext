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
            // Parse/execute only the package top level in a capability-free realm. Budgets still apply.
            engine.Execute(package.Source);
            var type = engine.Evaluate($"typeof {package.Manifest.EntryPoint}").AsString();
            if (type != "function")
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"Entry point '{package.Manifest.EntryPoint}' is not a function.")));
            return ValueTask.FromResult(Result.Success());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, "Script source is invalid.", ex.Message))); }
    }

    public async ValueTask<Result> ExecuteAsync(ScriptPackage package, Address target, string eventName,
        AdapterEnvelope envelope, IAdapterScriptApi adapter, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        try
        {
            var engine = BuildEngine(package, cancellationToken);
            engine.SetValue("__eventJson", JsonSerializer.Serialize(envelope, JsonOptions));
            engine.SetValue("__send", new Func<string, Task<string>>(
                json => adapter.SendAsync(json, cancellationToken)));
            var source = $$"""
                (async function() {
                  const event = JSON.parse(__eventJson);
                  const send = __send;
                  const ctx = Object.freeze({ self: Object.freeze({ adapter: Object.freeze({
                    send: (message) => send(JSON.stringify(message))
                  }) }) });
                  {{package.Source}}
                  const handler = {{package.Manifest.EntryPoint}};
                  if (typeof handler !== "function") throw new Error("Script entry point is not a function.");
                  await handler(event, ctx);
                })()
                """;
            await engine.EvaluateAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
            _journal.Record(new(id, package.Manifest.Id, target, eventName, true, null, started, DateTimeOffset.UtcNow)
            { EventId = envelope.EventId, Fingerprint = package.Fingerprint });
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            _journal.Record(new(id, package.Manifest.Id, target, eventName, false, "Cancelled or timed out", started, DateTimeOffset.UtcNow)
            { EventId = envelope.EventId, Fingerprint = package.Fingerprint });
            throw;
        }
        catch (Exception ex)
        {
            _journal.Record(new(id, package.Manifest.Id, target, eventName, false, ex.Message, started, DateTimeOffset.UtcNow)
            { EventId = envelope.EventId, Fingerprint = package.Fingerprint });
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
