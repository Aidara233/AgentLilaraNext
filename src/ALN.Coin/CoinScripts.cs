using ALN.Contracts;
using ALN.Graph.Contracts;

namespace ALN.Coin;

/// <summary>
/// The narrow thing the market needs from the script host: "can this script be called", and "call it".
///
/// Narrow on purpose. The purse does not want to know what a graph is, and a test of the purse should not have to
/// stand up a graph engine to answer one question.
/// </summary>
public interface ICoinScripts
{
    bool CanRun(string script);

    /// <summary>Runs the script as itself, with its own depth, and returns whatever it said.</summary>
    ValueTask<Result<string>> InvokeAsync(string script, string argumentsJson, CancellationToken cancellationToken);
}

/// <summary>The production answer: the graph's tool scripts, which are the only ones with "one entry, one answer".</summary>
public sealed class GraphCoinScripts(IGraphRuntime graph) : ICoinScripts
{
    public bool CanRun(string script) =>
        graph.ToolScripts.Any(package => string.Equals(package.Manifest.Id, script, StringComparison.Ordinal)) &&
        graph.Scripts.Any(entry =>
            string.Equals(entry.Package.Manifest.Id, script, StringComparison.Ordinal) && entry.Enabled);

    public ValueTask<Result<string>> InvokeAsync(string script, string argumentsJson,
        CancellationToken cancellationToken) =>
        graph.InvokeToolAsync(script, argumentsJson, cancellationToken);
}
