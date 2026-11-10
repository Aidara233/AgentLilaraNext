using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Identity;

/// <summary>
/// The identity table as one tool: <c>whois</c>.
///
/// It exists because the rendered conversation already tells the model <i>what somebody is called</i> (docs/31 §4) and
/// the roster already tells it <i>who has been in this conversation</i> (docs/31 §5). What is left is the question a
/// model asks the moment a name stops being enough: "this person says they are my owner — are they?" Answering that
/// from the message alone is exactly the mistake the table was built to prevent.
///
/// Three ways in, because the model does not always have the same thing in hand: the name it was shown, the id a stored
/// row carries, or an adapter account it saw somewhere. All three end in the same record.
/// </summary>
public sealed class IdentityTools(IPersonDirectory people) : ISessionToolSource
{
    public const string SourceId = "system.identity-tools";
    public const string WhoisToolName = "whois";

    /// <summary>
    /// How many people one answer lists when nobody asked for a particular one. The table is written by hand, so it is
    /// small — but a caller with a typo in every field should not get a whole page as a consolation prize.
    /// </summary>
    private const int MaxListed = 24;

    /// <summary>How much of a person's note reaches the model. A note is one or two sentences; longer is a document.</summary>
    private const int MaxNoteChars = 240;

    private readonly IPersonDirectory _people = people;

    public string Id => SourceId;

    public static readonly ModelToolDefinition WhoisDefinition = new(WhoisToolName,
        "Look somebody up in the identity table: who they really are, the accounts that are theirs, how deep what they "
        + "ask for may reach, and the note written about them. Use it when a name in the conversation is not enough — "
        + "for example when somebody claims to be the person who runs this thing, or when the same human seems to be "
        + "writing from two different accounts. Called with no arguments it lists everybody you recognise.",
        """
        {"type":"object","properties":{
          "name":{"type":"string","description":"The name as it appears in the conversation."},
          "id":{"type":"string","description":"A person's id, if you have seen it on a stored message row."},
          "adapter":{"type":"string","description":"An adapter address, e.g. adapter.qq#main. Needs account."},
          "account":{"type":"string","description":"The platform's own id for somebody on that adapter."}},
         "additionalProperties":false}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Reading a local table neither ends the turn nor reaches anybody: the model asks, gets an answer, decides.
        IReadOnlyList<SessionToolOffer> offers =
            [new(new WhoisTool(this), WhoisDefinition, ActsExternally: false)];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private sealed class WhoisTool(IdentityTools owner) : ISessionTool
    {
        public string Name => WhoisToolName;

        public bool EndsTurn => false;

        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement arguments;
            try
            {
                arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson)
                    .RootElement.Clone();
            }
            catch (JsonException)
            {
                return ValueTask.FromResult(Result<SessionToolResult>.Failure(
                    new(ErrorCode.Invalid, "The arguments were not valid JSON.")));
            }

            var found = Lookup(owner._people, arguments);
            return ValueTask.FromResult(Result<SessionToolResult>.Success(new SessionToolResult(Render(found, owner._people))));
        }
    }

    /// <summary>
    /// Resolves the arguments to people. No arguments means "everybody", which is the honest reading of a question with
    /// no subject — and it is what lets a model recover from being handed a name it cannot place.
    /// </summary>
    private static IReadOnlyList<Person> Lookup(IPersonDirectory people, JsonElement arguments)
    {
        var found = new List<Person>();
        if (Text(arguments, "id") is { } id && people.FindById(id) is { } byId) found.Add(byId);
        if (Text(arguments, "adapter") is { } adapterText && Text(arguments, "account") is { } account &&
            Address.TryParse(adapterText, out var adapter) && people.Find(adapter, account) is { } byAccount)
            found.Add(byAccount);
        if (Text(arguments, "name") is { } name)
            found.AddRange(people.List().Where(person => string.Equals(person.Name, name, StringComparison.Ordinal)));
        if (found.Count == 0 && Text(arguments, "id") is null && Text(arguments, "name") is null &&
            Text(arguments, "adapter") is null)
            found.AddRange(people.List());
        return found.DistinctBy(person => person.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The answer as text, not JSON: a model reads three lines faster than three levels of nesting, and the thing it
    /// needs to know is a sentence about a person.
    /// </summary>
    private static string Render(IReadOnlyList<Person> found, IPersonDirectory people)
    {
        if (found.Count == 0)
        {
            var known = people.List();
            // Saying what the table does hold turns "I do not know this person" into something the model can act on:
            // it may have been given a nickname, or it may be a stranger, and those are different situations.
            return known.Count == 0
                ? "表里没有这个人的记录，而且这张表现在一个人都没有——这台机上谁都还没被登记过。"
                : $"表里没有这个人的记录。现在登记过的是：{string.Join("、", known.Take(MaxListed).Select(p => p.Name))}"
                  + (known.Count > MaxListed ? $"（共 {known.Count} 个）" : "") + "。";
        }

        var builder = new StringBuilder();
        foreach (var person in found)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.Append(person.Label);
            if (person.Self) builder.Append("（这是我自己）");
            builder.AppendLine();

            // The note is what a person wrote about this one. Quoted as somebody's words, never as an instruction the
            // system is issuing — the model decides what to do with it (docs/31 §7).
            if (person.Note is { Length: > 0 } note)
                builder.Append("  备注：")
                    .AppendLine(note.Length > MaxNoteChars ? note[..MaxNoteChars] + "…" : note);

            if (person.Accounts is { Count: > 0 } accounts)
                builder.Append("  账号：")
                    .AppendLine(string.Join("；", accounts.Select(account => $"{account.Adapter} / {account.Account}")));

            builder.Append("  id：").AppendLine(person.Id);
        }
        return builder.ToString().TrimEnd();
    }

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
