using System.Text.Json;
using ALN.Contracts;

namespace ALN.Identity;

/// <summary>
/// The operator's view of the identity table: who is on it, one person in full, and reading the folder again.
///
/// Three verbs, and the split is the point. <c>list</c> and <c>get</c> are agent-level — "who is this and who else do
/// you know" are questions an assistant should answer rather than guess from a nickname. <c>reload</c> is
/// operator-level: it decides which accounts this process believes belong to whom, which is a deployment decision
/// even though it changes no code and no capability (docs/31 §8).
///
/// There is deliberately no <c>set</c> or <c>remove</c>. The table is written by hand, and adding a write verb would
/// raise a question this module has no business answering: whether a model may add a person who can then say things
/// on somebody's behalf (docs/31 §10).
/// </summary>
public static class IdentityActionSet
{
    public static Result Register(IControlPlane plane, IPersonDirectory people, IPersonDirectoryReload reload)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        if (people is null) return Result.Failure(new(ErrorCode.Invalid, "An identity table is required."));
        if (reload is null) return Result.Failure(new(ErrorCode.Invalid, "A reloadable identity table is required."));

        var registrations = new (ActionDescriptor Descriptor, ActionHandler Handler)[]
        {
            (new(IdentityActions.List,
                "Everybody this process recognises: their id, the name the model will see, the note written about them, "
                + "how deep their words may reach, and the accounts that are theirs.",
                Capabilities.IdentityRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
             (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(
                 ActionArgs.JsonOf(new { people = people.List().Select(Describe) })))),

            (new(IdentityActions.Get,
                "One or more people, by id, by an adapter account, or by name. A name can belong to nobody or to more "
                + "than one person, which is why the answer is always a list.",
                Capabilities.IdentityRead, false,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The person's id — the directory name under identify/."},
                  "adapter":{"type":"string","description":"An adapter instance address, e.g. adapter.qq#main. Needs account."},
                  "account":{"type":"string","description":"The platform's id for the sender on that adapter."},
                  "name":{"type":"string","description":"The name as the model sees it. May match more than one person."}},
                 "additionalProperties":false}
                """, Subject: "id"),
             (arguments, _) =>
             {
                 var found = new List<Person>();
                 if (Text(arguments, "id") is { } id && people.FindById(id) is { } byId) found.Add(byId);
                 if (Text(arguments, "adapter") is { } adapterText && Text(arguments, "account") is { } account &&
                     Address.TryParse(adapterText, out var adapter) && people.Find(adapter, account) is { } byAccount)
                     found.Add(byAccount);
                 if (Text(arguments, "name") is { } name)
                     found.AddRange(people.List().Where(person =>
                         string.Equals(person.Name, name, StringComparison.Ordinal)));
                 // An empty answer is an answer: "nobody by that name" is a fact, not a failed call.
                 var distinct = found.DistinctBy(person => person.Id, StringComparer.Ordinal).ToArray();
                 return ValueTask.FromResult(Result<JsonElement>.Success(
                     ActionArgs.JsonOf(new { people = distinct.Select(Describe) })));
             }),

            (new(IdentityActions.Reload,
                "Read the identify/ folder again and put the result in place. A file that cannot be read costs that one "
                + "person and is reported; everybody else is still recognised. A folder that cannot be read at all "
                + "leaves the table that is already loaded exactly as it is.",
                Capabilities.IdentityControl, true,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
             async (_, cancellationToken) =>
             {
                 var report = await reload.ReloadAsync(cancellationToken).ConfigureAwait(false);
                 return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                 {
                     people = report.People.Select(Describe),
                     rejected = report.Rejected.Select(problem => new { path = problem.Path, reason = problem.Reason })
                 }));
             })
        };

        foreach (var (descriptor, handler) in registrations)
        {
            var registered = plane.Register(descriptor, handler);
            if (!registered.Ok) return registered;
        }
        return Result.Success();
    }

    /// <summary>
    /// One person as an answer. The accounts are spelled out in full because "which of my numbers is that" is the
    /// question somebody actually has when two adapters both know him.
    /// </summary>
    private static object Describe(Person person) => new
    {
        id = person.Id,
        name = person.Name,
        note = person.Note,
        level = person.Level.ToString().ToLowerInvariant(),
        self = person.Self,
        accounts = (person.Accounts ?? []).Select(account => new
        {
            adapter = account.Adapter.ToString(),
            account = account.Account
        })
    };

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
