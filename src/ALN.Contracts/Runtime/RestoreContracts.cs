namespace ALN.Contracts;

/// <summary>
/// One line of "what came back from disk". A restart that silently loses a mount, or quietly revives a
/// Channel somebody had stopped, is the failure this exists to make visible — a component says what it
/// restored instead of the host guessing from a log.
/// </summary>
public sealed record RestoreNote(string Component, string Summary);

/// <summary>
/// Written during startup by whoever restores something, read once afterwards by the host. Components
/// append; nothing reads it while startup is still running, so the order of notes is the order components
/// were revived in, which is itself useful.
/// </summary>
public interface IRestoreReporter
{
    void Note(string component, string summary);
    IReadOnlyList<RestoreNote> Snapshot();
}

/// <summary>A reporter that keeps nothing: components always have one, whether or not somebody listens.</summary>
public sealed class NullRestoreReporter : IRestoreReporter
{
    public static readonly NullRestoreReporter Instance = new();
    public void Note(string component, string summary) { }
    public IReadOnlyList<RestoreNote> Snapshot() => [];
}
