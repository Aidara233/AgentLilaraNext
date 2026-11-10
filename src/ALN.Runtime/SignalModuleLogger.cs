using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Module-facing point log. Records land on the same tree as dispatch edges: they inherit the
/// ambient parent, so a note written while handling an event sits under that edge instead of in
/// a separate stream. Three levels only; nothing here can throw into the caller.
/// </summary>
public sealed class SignalModuleLogger(ISignalLog signalLog, string moduleId) : IModuleLogger
{
    public void Info(string message, IReadOnlyDictionary<string, string>? detail = null) =>
        Write(SignalLevel.Info, "info", message, detail);

    public void Warn(string message, IReadOnlyDictionary<string, string>? detail = null) =>
        Write(SignalLevel.Warn, "warn", message, detail);

    public void Error(string message, IReadOnlyDictionary<string, string>? detail = null) =>
        Write(SignalLevel.Error, "error", message, detail);

    private void Write(SignalLevel level, string outcome, string? message, IReadOnlyDictionary<string, string>? detail)
    {
        try
        {
            if (!signalLog.IsEnabled(level)) return;
            var text = message ?? "";
            if (text.Length > 2_000) text = text[..2_000];
            // The sentence always travels, even when there is structure beside it. Dropping it whenever a caller had
            // detail to give meant a record could say {"file":"x.jpg","status":"400"} and never say what happened —
            // so a grep for the thing you were looking for found nothing, and the log looked like it had not been
            // written at all. A detail that already carries "message" keeps its own.
            var payload = detail is { Count: > 0 }
                ? new Dictionary<string, string>(detail, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            payload.TryAdd("message", text);
            signalLog.Write(new()
            {
                Group = "module",
                Name = moduleId,
                ParentId = SignalScope.CurrentId,
                StepId = SignalScope.StepId ?? Guid.Empty,
                From = moduleId,
                Mechanism = "log",
                Outcome = outcome,
                Level = level,
                Detail = payload
            });
        }
        catch
        {
            // Logging is never allowed to change the outcome of the work it describes.
        }
    }
}

public sealed class SignalModuleLoggerFactory(ISignalLog signalLog) : IModuleLoggerFactory
{
    public IModuleLogger For(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
            throw new ArgumentException("Module id is required for a module logger.", nameof(moduleId));
        return new SignalModuleLogger(signalLog, moduleId);
    }
}
