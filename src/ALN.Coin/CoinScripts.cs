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

    /// <summary>Every script the graph actually loaded. When a good cannot be sold, this is what the issue prints:
    /// the difference between "no such script" and "loaded but not callable" is one line of log away from the answer.</summary>
    IReadOnlyList<string> Loaded { get; }

    /// <summary>Runs the script as itself, with its own depth, and returns whatever it said.</summary>
    ValueTask<Result<string>> InvokeAsync(string script, string argumentsJson, CancellationToken cancellationToken);
}

/// <summary>The production answer: the graph's tool scripts, which are the only ones with "one entry, one answer".</summary>
public sealed class GraphCoinScripts(IGraphRuntime graph) : ICoinScripts
{
    /// <summary>
    /// Callable means "the manifest names an entry point", not "the tool definition was resolved". Resolving a
    /// definition is what the loader does for the model's tool list, and it prefers the script's own <c>describe()</c>
    /// over the manifest's <c>tool</c> block — so a settlement script that has a <c>call</c> but no <c>describe()</c>
    /// has no definition and is still perfectly callable. Gating the shelf on that would hide every such script.
    /// </summary>
    public IReadOnlyList<string> Loaded =>
        graph.Scripts.Select(entry => entry.Package.Manifest.Id).Order(StringComparer.Ordinal).ToArray();

    public bool CanRun(string script) =>
        graph.Scripts.Any(entry =>
            entry.Enabled &&
            string.Equals(entry.Package.Manifest.Id, script, StringComparison.Ordinal) &&
            entry.Package.Manifest.Call is not null);

    public ValueTask<Result<string>> InvokeAsync(string script, string argumentsJson,
        CancellationToken cancellationToken) =>
        graph.InvokeToolAsync(script, argumentsJson, cancellationToken);
}
