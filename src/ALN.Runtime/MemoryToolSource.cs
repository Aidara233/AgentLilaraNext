using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// Memory, offered to a Session as tools: <c>remember</c>, <c>recall</c>, <c>forget</c>.
///
/// A source rather than a session built-in, because the session should not know that memory exists. The scope is
/// decided here (<see cref="ConversationScope"/>) for the same reason: which conversation a turn may read is a
/// fact about memory, and a session that computed it would be holding half of another module's rule.
/// </summary>
public sealed class MemoryToolSource(IMemoryStore store, TimeProvider? timeProvider = null) : ISessionToolSource
{
    public const string SourceId = "system.memory-tools";

    public string Id => SourceId;

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tools = new SessionMemoryTools(store, ConversationScope.Of(request), request.SessionId, timeProvider);
        var offers = new List<SessionToolOffer>
        {
            // Writing to, or reading from, this conversation's own notes is not saying anything to anyone: a turn
            // that only called these has not spoken, and the loop has to know that.
            new(tools.Find(SessionMemoryTools.RememberToolName).Value!, SessionMemoryTools.RememberDefinition, ActsExternally: false),
            new(tools.Find(SessionMemoryTools.RecallToolName).Value!, SessionMemoryTools.RecallDefinition, ActsExternally: false),
            new(tools.Find(SessionMemoryTools.ForgetToolName).Value!, SessionMemoryTools.ForgetDefinition, ActsExternally: false)
        };
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }
}
