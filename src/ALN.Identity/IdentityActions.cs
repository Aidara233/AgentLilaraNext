using System.Text.Json;
using ALN.Contracts;

namespace ALN.Identity;

/// <summary>
/// The operator's and the model's view of the identity table: who is on it, one person in full, and keeping it up to
/// date.
///
/// The split between these verbs is the whole design (docs/31 §7.1):
/// <list type="bullet">
///   <item><b>Reading</b> is agent level — "who is this and who else do you know" are questions an assistant should
///   answer rather than guess from a nickname.</item>
///   <item><b>Writing a card</b> is agent level too, which is what the person asked for: names, notes, which accounts
///   are somebody's, whether a group of accounts is mine — all of it is bookkeeping an assistant does while talking to
///   somebody.</item>
///   <item><b>A level is not in this file at all.</b> No verb here takes one, because the number decides who the
///   instruction system has to obey, and a model that could write it could write its own chain of command.</item>
/// </list>
///
/// One rule is kept at runtime rather than in the table, exactly as <c>adapters.raw</c> keeps its own: <b>the accounts
/// of somebody who has a level can only be changed by somebody who has one</b>. The reason is narrow and real: writing
/// an account onto an existing person is how a caller inherits that person's level, so a model at L0 that could do it
/// would be a model that could promote itself by adding a number to a card. Changing his name or his note stays open at
/// every level — neither of those changes who his words will be attributed to.
/// </summary>
public static class IdentityActionSet
{
    public static Result Register(IControlPlane plane, IPersonDirectory people, IPersonDirectoryReload reload,
        IPersonDirectoryEditing editing)
    {
        if (plane is null) return Result.Failure(new(ErrorCode.Invalid, "A control plane is required."));
        if (people is null) return Result.Failure(new(ErrorCode.Invalid, "An identity table is required."));
        if (reload is null) return Result.Failure(new(ErrorCode.Invalid, "A reloadable identity table is required."));
        if (editing is null) return Result.Failure(new(ErrorCode.Invalid, "A writable identity table is required."));

        var listed = plane.Register(
            new(IdentityActions.List,
                "Everybody this process recognises: their id, the name the model will see, the note written about them, "
                + "how deep their words may reach, and the accounts that are theirs.",
                Capabilities.IdentityRead, false,
                ParamsJson: """{"type":"object","properties":{},"additionalProperties":false}"""),
            (_, _) => ValueTask.FromResult(Result<JsonElement>.Success(
                ActionArgs.JsonOf(new { people = people.List().Select(Describe) }))));
        if (!listed.Ok) return listed;

        var got = plane.Register(
            new(IdentityActions.Get,
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
            });
        if (!got.Ok) return got;

        // The one verb that is allowed to touch a level, and therefore the one that must sit behind the operator door.
        // AssignsLevel is what makes "a level may only be given away at or below your own" true here without a second
        // copy of that rule in this file.
        var levelled = plane.Register(
            new(IdentityActions.Level,
                "Set how deep an instruction from this person may be taken. This is the only verb that writes a level, "
                + "and it sits behind the operator door: a level may only be given away at or below your own, and no "
                + "model reaches this one.",
                Capabilities.IdentityControl, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The person's id."},
                  "level":{"type":"string","enum":["agent","operator","full"]}},
                 "required":["id","level"],"additionalProperties":false}
                """, Subject: "id", AssignsLevel: "level"),
            async (_, arguments, cancellationToken) =>
            {
                if (!arguments.TryReadText("id", out var id) || id.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "identity.people.level requires 'id'."));
                if (!arguments.TryReadText("level", out var text) ||
                    !Enum.TryParse<AccessLevel>(text.Trim(), ignoreCase: true, out var level))
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid,
                        "identity.people.level requires 'level': agent, operator or full."));
                var changed = await editing.SetLevelAsync(id.Trim(), level, cancellationToken).ConfigureAwait(false);
                if (!changed.Ok) return Result<JsonElement>.Failure(changed.Error!);
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    person = Describe(changed.Value!),
                    level = level.ToString().ToLowerInvariant()
                }));
            });
        if (!levelled.Ok) return levelled;

        // Writing a card. Authorized rather than plain because of the one runtime rule above: whether this caller may
        // touch *these accounts* depends on the person already on the table, which arguments alone cannot say.
        var set = plane.Register(
            new(IdentityActions.Set,
                "Write somebody's card: the name the model will see, the note about him, and which adapter accounts are "
                + "his. Creates the person when there is none. This is a whole-card write — bring every account he has "
                + "and the note he should have, because anything left out is removed. It cannot set a level: that is "
                + "identity.people.level, and only a person reaches it. If the person already has a level above agent, "
                + "changing his accounts needs the caller to have a level too, because an account is who his words "
                + "belong to.",
                Capabilities.IdentityWrite, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The person's id: the directory name under identify/. Creates or overwrites."},
                  "name":{"type":"string","description":"The name the model will see. Required — it is what replaces the platform's nickname."},
                  "note":{"type":"string","description":"A sentence about this person, in the words of whoever wrote it. Leave it out to remove it."},
                  "self":{"type":"boolean","description":"True when these accounts are the bot's own. Only one person can be self."},
                  "accounts":{"type":"array","description":"Every account that is his. At least one; an account belongs to one person.",
                    "items":{"type":"object","properties":{
                      "adapter":{"type":"string","description":"An adapter instance address, e.g. adapter.qq#main."},
                      "account":{"type":"string","description":"The platform's id for him there."}},
                     "required":["adapter","account"],"additionalProperties":false}}},
                 "required":["id","name","accounts"],"additionalProperties":false}
                """, Subject: "id"),
            async (actor, arguments, cancellationToken) =>
            {
                if (!arguments.TryReadText("id", out var rawId) || rawId.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "identity.people.set requires 'id'."));
                // Refused out loud rather than ignored: a caller that sent a level and was told "done" would believe a
                // promotion happened, and the next thing it does would be based on that belief.
                if (arguments.TryGetProperty("level", out _))
                    return Result<JsonElement>.Failure(new(ErrorCode.Denied,
                        "identity.people.set cannot write a level. Use identity.people.level, which needs an operator."));

                if (!arguments.TryReadText("name", out var name) || name.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "identity.people.set requires 'name'."));

                var note = Text(arguments, "note");
                var self = arguments.TryGetProperty("self", out var selfJson) && selfJson.ValueKind == JsonValueKind.True;
                if (!TryReadAccounts(arguments, out var accounts, out var accountError))
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, accountError!));

                var id = rawId.Trim();
                var existing = people.FindById(id);
                // Who counts as "me" is nobody's bookkeeping but a person's: setting self, clearing it, or bringing a
                // new card in already marked self — all of them decide which accounts the process treats as its own,
                // and all of them need somebody who is trusted with that. Without this line an L0 caller could clear
                // the flag on one card and set it on another, and the rule about levels below would never be reached.
                if (self != (existing?.Self ?? false) && actor.Level < AccessLevel.Operator)
                {
                    return Result<JsonElement>.Failure(new(ErrorCode.Denied,
                        self
                            ? "'self' says which accounts are mine, and that is a person's decision: marking accounts as "
                              + $"mine needs {AccessLevels.Describe(AccessLevel.Operator)} or higher."
                            : $"clearing 'self' on '{id}' needs {AccessLevels.Describe(AccessLevel.Operator)} or higher: "
                              + "it decides which accounts the process treats as its own."));
                }

                if (existing is { } person && person.Level > AccessLevel.Agent && actor.Level < AccessLevel.Operator &&
                    ChangesAccounts(person, accounts))
                {
                    return Result<JsonElement>.Failure(new(ErrorCode.Denied,
                        $"'{person.Id}' is {AccessLevels.Describe(person.Level)}, so which accounts are his is a "
                        + $"person's decision: changing them needs {AccessLevels.Describe(AccessLevel.Operator)} or higher. "
                        + "His name and note you may change at any level; a level is not yours to write at all."));
                }

                var written = await editing.SetAsync(new PersonInput(id, name, note, accounts, self), cancellationToken)
                    .ConfigureAwait(false);
                if (!written.Ok || written.Value is null) return Result<JsonElement>.Failure(written.Error!);
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new
                {
                    person = Describe(written.Value.Person),
                    rejected = written.Value.Rejected.Select(problem => new { path = problem.Path, reason = problem.Reason })
                }));
            });
        if (!set.Ok) return set;

        var remove = plane.Register(
            new(IdentityActions.Remove,
                "Take a person off the table by removing his card. The folder stays, with anything else in it. Removing "
                + "somebody who has a level needs the caller to have one too — otherwise his messages would quietly stop "
                + "being his, and a stranger would be answering in his place.",
                Capabilities.IdentityWrite, true,
                ParamsJson: """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The person's id."}},
                 "required":["id"],"additionalProperties":false}
                """, Subject: "id"),
            async (actor, arguments, cancellationToken) =>
            {
                if (!arguments.TryReadText("id", out var rawId) || rawId.Trim().Length == 0)
                    return Result<JsonElement>.Failure(new(ErrorCode.Invalid, "identity.people.remove requires 'id'."));
                var id = rawId.Trim();
                if (people.FindById(id) is { } existing && actor.Level < AccessLevel.Operator)
                {
                    // Two doors, and either one is enough to stop it. Deleting the self card is the same act as
                    // clearing the flag — it decides which accounts the process treats as its own — so it gets the
                    // same answer, not a loophole beside it.
                    if (existing.Self)
                        return Result<JsonElement>.Failure(new(ErrorCode.Denied,
                            $"'{existing.Id}' is the set of accounts that are mine: removing it needs "
                            + $"{AccessLevels.Describe(AccessLevel.Operator)} or higher."));
                    if (existing.Level > AccessLevel.Agent)
                        return Result<JsonElement>.Failure(new(ErrorCode.Denied,
                            $"'{existing.Id}' is {AccessLevels.Describe(existing.Level)}: removing him needs "
                            + $"{AccessLevels.Describe(AccessLevel.Operator)} or higher."));
                }
                var removed = await editing.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
                if (!removed.Ok) return Result<JsonElement>.Failure(removed.Error!);
                return Result<JsonElement>.Success(ActionArgs.JsonOf(new { removed = id }));
            });
        if (!remove.Ok) return remove;

        return plane.Register(
            new(IdentityActions.Reload,
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
            });
    }

    /// <summary>
    /// Whether this write would change <b>which accounts are recognised as this person</b>. The name and the note are
    /// not in this question: those change how he is described, not whose words are his. Neither is <c>self</c> — that
    /// one has a door of its own, independent of any level, because it decides which accounts are the bot's.
    /// </summary>
    private static bool ChangesAccounts(Person person, IReadOnlyList<PersonAccount> accounts)
    {
        var current = (person.Accounts ?? []).Select(account => account.Key).OrderBy(key => key, StringComparer.Ordinal);
        var wanted = accounts.Select(account => account.Key).OrderBy(key => key, StringComparer.Ordinal);
        return !current.SequenceEqual(wanted, StringComparer.Ordinal);
    }

    /// <summary>Reads the accounts array, or says which entry is wrong. An absent array is no accounts at all.</summary>
    private static bool TryReadAccounts(JsonElement arguments, out IReadOnlyList<PersonAccount> accounts, out string? error)
    {
        accounts = [];
        error = null;
        if (!arguments.TryGetProperty("accounts", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            error = "identity.people.set requires 'accounts': at least one, because a person nobody can arrive as cannot be recognised.";
            return false;
        }

        var read = new List<PersonAccount>();
        var index = 0;
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("adapter", out var adapterJson) || adapterJson.ValueKind != JsonValueKind.String ||
                !Address.TryParse(adapterJson.GetString(), out var adapter) || !adapter.IsInstance)
            {
                error = $"accounts[{index}].adapter must be an adapter instance address like \"adapter.qq#main\".";
                return false;
            }
            if (!entry.TryGetProperty("account", out var accountJson) || accountJson.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(accountJson.GetString()))
            {
                error = $"accounts[{index}].account is empty.";
                return false;
            }
            read.Add(new PersonAccount(adapter, accountJson.GetString()!.Trim()));
            index++;
        }

        if (read.Count == 0)
        {
            error = "identity.people.set requires 'accounts': at least one, because a person nobody can arrive as cannot be recognised.";
            return false;
        }
        accounts = read;
        return true;
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
