using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Identity;

/// <summary>
/// The two tools that decide who this conversation keeps in mind: <c>remember_person</c> and <c>forget_person</c>.
///
/// <b>Why these exist at all.</b> The roster in the round context used to be assembled from traffic — everybody who
/// spoke got a line, and the cap of twelve then decided who survived. That is a list of whoever was loud, not a list of
/// who matters: a group chat leaves an assistant carrying a dozen names it has no use for and no way to tell the two
/// that count. The decision belongs to the one having the conversation, so it is a tool rather than a side effect
/// (docs/31 §5).
///
/// They are cheap and local: neither reaches the network, neither ends the turn, and both answer with the roster as it
/// stands afterwards, so the model never has to guess whether it worked.
/// </summary>
public sealed class PersonMemoryTools(IPersonDirectory people, ISessionPeopleRoster roster) : ISessionToolSource
{
    public const string SourceId = "system.identity-memory";
    public const string RememberToolName = "remember_person";
    public const string ForgetToolName = "forget_person";

    private readonly IPersonDirectory _people = people;
    private readonly ISessionPeopleRoster _roster = roster;

    public string Id => SourceId;

    public static readonly ModelToolDefinition RememberDefinition = new(RememberToolName,
        "Keep somebody in mind for the rest of this conversation: after this, every round carries who he is, what was "
        + "written about him, and how deep what he asks for may reach. Use it for the people this conversation is "
        + "actually about — somebody who spoke once in a group is not that. The person has to be on the identity table "
        + "already; if he is not, the answer says so.",
        """
        {"type":"object","properties":{
          "id":{"type":"string","description":"The person's id — the value of the person attribute on his message."},
          "name":{"type":"string","description":"His name, when you do not have the id. Exact match; a name that fits two people is refused."}},
         "additionalProperties":false}
        """);

    public static readonly ModelToolDefinition ForgetDefinition = new(ForgetToolName,
        "Stop keeping somebody in mind for this conversation. The identity table is untouched — he is still recognised "
        + "on his messages, he just stops taking up room in every round.",
        """
        {"type":"object","properties":{
          "id":{"type":"string","description":"The person's id."},
          "name":{"type":"string","description":"His name, when you do not have the id."}},
         "additionalProperties":false}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> offers =
        [
            // Neither is a reply and neither reaches anybody: the model decides, is told what happened, and goes on.
            new(new RosterTool(this, request.SessionId, remember: true), RememberDefinition, ActsExternally: false),
            new(new RosterTool(this, request.SessionId, remember: false), ForgetDefinition, ActsExternally: false)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    /// <summary>
    /// One tool, two directions: the arguments, the refusal messages and the answer are the same shape, and only the
    /// verb differs. Two classes would have been two copies of "resolve a person, then tell the model the roster".
    /// </summary>
    private sealed class RosterTool(PersonMemoryTools owner, string sessionId, bool remember) : ISessionTool
    {
        public string Name => remember ? RememberToolName : ForgetToolName;

        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call,
            CancellationToken cancellationToken)
        {
            JsonElement arguments;
            try
            {
                arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson)
                    .RootElement.Clone();
            }
            catch (JsonException)
            {
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "The arguments were not valid JSON."));
            }

            var resolved = Resolve(owner._people, arguments);
            if (!resolved.Ok) return Result<SessionToolResult>.Failure(resolved.Error!);
            var person = resolved.Value!;

            var edited = remember
                ? await owner._roster.RememberAsync(sessionId, person.Id, cancellationToken).ConfigureAwait(false)
                : await owner._roster.ForgetAsync(sessionId, person.Id, cancellationToken).ConfigureAwait(false);
            if (!edited.Ok) return Result<SessionToolResult>.Failure(edited.Error!);

            return Result<SessionToolResult>.Success(new SessionToolResult(
                Render(person, remember, edited.Value ?? [])));
        }
    }

    /// <summary>
    /// The person the arguments name, by id first and by exact name second. A name that fits two people is refused
    /// rather than guessed at: remembering the wrong one is a mistake that survives in every later round.
    /// </summary>
    private static Result<Person> Resolve(IPersonDirectory people, JsonElement arguments)
    {
        if (Text(arguments, "id") is { } id)
        {
            var byId = people.FindById(id);
            return byId is null
                ? Result<Person>.Failure(new(ErrorCode.Invalid,
                    $"Nobody on the identity table is called '{id}'. whois lists the ones who are."))
                : Result<Person>.Success(byId);
        }

        if (Text(arguments, "name") is { } name)
        {
            var matches = people.List().Where(person => string.Equals(person.Name, name, StringComparison.Ordinal)).ToArray();
            return matches.Length switch
            {
                0 => Result<Person>.Failure(new(ErrorCode.Invalid,
                    $"Nobody on the identity table is called '{name}'. whois lists the ones who are.")),
                1 => Result<Person>.Success(matches[0]),
                _ => Result<Person>.Failure(new(ErrorCode.Conflict,
                    $"{matches.Length} people are called '{name}': {string.Join(", ", matches.Select(person => person.Id))}. "
                    + "Use the id."))
            };
        }

        return Result<Person>.Failure(new(ErrorCode.Invalid, "Give either 'id' or 'name'."));
    }

    /// <summary>
    /// What the model is told afterwards: whether it worked, and who is on the roster now. The list is the point —
    /// without it the model has to remember what it had already decided, which is exactly what a roster is for.
    /// </summary>
    private static string Render(Person person, bool remember, IReadOnlyList<PersonSeen> roster)
    {
        var text = new StringBuilder();
        text.AppendLine(remember
            ? $"记住了 {person.Label}：之后每一轮都会带着他。"
            : $"不再记着 {person.Label}：身份表没动，他说话仍然认得出，只是不占每一轮的位置了。");
        text.Append(roster.Count == 0
            ? "这段会话现在一个人都没记。"
            : $"这段会话现在记着 {roster.Count} 个人：{string.Join("、", roster.Select(seen => seen.Id))}。");
        return text.ToString();
    }

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
