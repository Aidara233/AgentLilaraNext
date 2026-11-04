namespace ALN.Contracts.Sessions;

/// <summary>
/// Work that becomes true only when the turn that produced it succeeds.
///
/// The reason it is a thing rather than a callback: reading a cursor forward is a promise that the reader is done
/// with what it read, and a turn that never completed did not do anything with it. Making the advance a separate
/// step means "the model failed" and "the message was handled" stop being the same event — which is exactly the
/// bug that swallowed a person's message during the first real run.
///
/// The Session runs these after the turn has been written down, and runs none of them for a turn that failed.
/// </summary>
public interface ITurnCommit
{
    ValueTask<Result> CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Where a round-context contributor puts the promises its contribution depends on. A contributor that took
/// something from a queue must register the advance here instead of spending it on the spot.
/// </summary>
public interface ITurnCommitSink
{
    void Add(ITurnCommit commit);
}
