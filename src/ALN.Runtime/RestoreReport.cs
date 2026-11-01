using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The startup receipt. Appended to from whatever thread a component restores on (they do not strictly
/// happen in one place: lifecycle revival runs a whole signal round), read once after startup.
/// </summary>
public sealed class RestoreReport : IRestoreReporter
{
    private readonly List<RestoreNote> _notes = [];
    private readonly object _gate = new();

    public void Note(string component, string summary)
    {
        if (string.IsNullOrWhiteSpace(component) || string.IsNullOrWhiteSpace(summary)) return;
        lock (_gate) _notes.Add(new(component, summary));
    }

    public IReadOnlyList<RestoreNote> Snapshot()
    {
        lock (_gate) return _notes.ToArray();
    }
}
