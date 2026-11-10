using System.Text.Json.Serialization;

namespace ALN.Contracts;

/// <summary>
/// One account somewhere, said the way the identity table has to say it: <b>an instance address plus the platform's
/// own id for the sender</b>.
///
/// Not <c>platformId</c> (docs/31 §2): one platform can have two accounts online at once — the primary and the backup
/// (docs/09 §2.3) — and their sender spaces are their own. A table keyed by <c>qq</c> would merge two spaces into one.
/// </summary>
/// <param name="Adapter">The adapter instance whose messages carry this id, e.g. <c>adapter.qq#main</c>.</param>
/// <param name="Account">What the platform calls this person: a QQ number, a user id, a mailbox folder name.</param>
public sealed record PersonAccount(Address Adapter, string Account)
{
    /// <summary>The key this account is looked up by. One spelling, so two lookups cannot disagree.</summary>
    public string Key => KeyOf(Adapter, Account);

    /// <summary>
    /// The one place an account becomes a key. Case-sensitive because platform ids are: folding them would make two
    /// different people in one platform the same person, and that is the one mistake this table exists to prevent.
    /// </summary>
    public static string KeyOf(Address adapter, string account) => $"{adapter}\u001f{account}";
}

/// <summary>
/// A person: a name that holds no matter what the platform calls him today, the accounts that are his, and what he is
/// allowed to ask for.
///
/// <see cref="Id"/> is the directory name and never changes (<c>C-01</c>: a name that can be rewritten is not an
/// identity). <see cref="Name"/> is a label and may change — that is why it is not the id.
/// </summary>
public sealed record Person(
    string Id,
    string Name,
    /// <summary>
    /// What to do with this person, in his own words — a sentence a person wrote, not a verdict this system reached.
    /// It is the only honest carrier for "how much should I trust him" (docs/31 §7): a system that scored people
    /// would be making a judgement that is somebody else's to make.
    /// </summary>
    string? Note = null,
    /// <summary>
    /// How deep an instruction from this person may be taken. <b>Data today</b>: nothing in the tool gate reads it —
    /// the depth of a round is the Session's own level (docs/31 §7). It is here because the instruction system that
    /// will read it is coming, and the number has to be written down somewhere a person can see.
    /// </summary>
    [property: JsonConverter(typeof(AccessLevelJson))] AccessLevel Level = AccessLevel.Agent,
    /// <summary>
    /// These accounts are mine: the <c>botIdentity[]</c> docs/09 §2.3 described and the code never had (docs/31 §6).
    /// Two accounts under one self-person is what makes "an echo from the backup number is still an echo" true
    /// without a "which one is primary" field.
    /// </summary>
    bool Self = false,
    IReadOnlyList<PersonAccount>? Accounts = null)
{
    /// <summary>How this person is written where a person is written: the name, then how deep his words reach.</summary>
    /// <remarks>
    /// The level is spelled as a fact about <b>him</b> ("his instructions may go this deep"), never as a rank compared
    /// to the reader's own (docs/11 §5.3: depth says what you can touch, never who outranks whom). One method, so the
    /// roster and the refusal messages cannot drift apart.
    /// </remarks>
    public string Label => Level == AccessLevel.Agent
        ? Name
        : $"{Name}（L{(int)Level} {Level.ToString().ToLowerInvariant()}）";

    public bool Owns(Address adapter, string? account) =>
        !string.IsNullOrWhiteSpace(account) && Accounts is { Count: > 0 } accounts &&
        accounts.Any(one => one.Adapter == adapter && string.Equals(one.Account, account, StringComparison.Ordinal));
}

/// <summary>
/// Who is who, by account. The one question the message path asks, answered synchronously and without failing:
/// a round must not wait on, or be refused by, a lookup about a name.
///
/// <b>Absence is an answer</b> (<c>null</c>): the message came from somebody this table does not know. It is not an
/// error and it does not stop the message — a stranger is not an intruder, and refusing messages here would be
/// answering a question ("should I listen to this person?") that belongs to the channel's own scripts (docs/31 §9.3).
/// </summary>
public interface IPersonDirectory
{
    /// <summary>This account's person, or null when the table does not know him.</summary>
    Person? Find(Address adapter, string? account);

    /// <summary>A person by his id — the id a stored message row carries, so a transcript can still name who spoke.</summary>
    Person? FindById(string? id);

    /// <summary>Everybody on the table, ordered by id so that a roster cannot change order between two rounds.</summary>
    IReadOnlyList<Person> List();

    /// <summary>The person whose accounts are mine, or null when nobody said.</summary>
    Person? Self { get; }

    /// <summary>Whether this account is mine — the <c>self</c> test docs/09 §2.3 asks for.</summary>
    bool IsSelf(Address adapter, string? account) => Find(adapter, account) is { Self: true };
}

/// <summary>
/// Reading the table again. Separate from <see cref="IPersonDirectory"/> for the same reason
/// <c>INetTargetEditing</c> is separate from <c>INetAccess</c>: the hot path holds the reading half, and nothing that
/// identifies a speaker can rebuild the table underneath itself.
/// </summary>
public interface IPersonDirectoryReload
{
    /// <summary>
    /// Rescans the directory and swaps the table in one move.
    ///
    /// All or nothing (docs/07 §5.1): a scan with a hard problem leaves the old table in place and says why. Half a
    /// table is worse than a stale one — a stale table is wrong in ways somebody already lived with, half a table is
    /// wrong in ways nobody has seen.
    /// </summary>
    ValueTask<PersonDirectoryReport> ReloadAsync(CancellationToken cancellationToken);
}

/// <summary>What one scan produced: who is on the table, and every file that was refused with the reason.</summary>
/// <param name="People">Accepted people, ordered by id.</param>
/// <param name="Rejected">Files that did not become a person. Never fatal: one broken file is one person missing.</param>
public sealed record PersonDirectoryReport(IReadOnlyList<Person> People, IReadOnlyList<PersonProblem> Rejected)
{
    public static PersonDirectoryReport Empty { get; } = new([], []);
}

/// <summary>One refused file: which one, and the sentence a person needs to fix it.</summary>
public sealed record PersonProblem(string Path, string Reason);

/// <summary>The action ids the identity module answers to. Here for the same reason as the other modules' lists: an
/// id is part of the interface, and the module that implements it must not be the only place it is written down.</summary>
public static class IdentityActions
{
    /// <summary>Everybody on the table. A read: "who do you recognise" is a question an assistant should answer.</summary>
    public const string List = "identity.people.list";

    /// <summary>One person, by id or by account — the action behind the model's <c>whois</c>.</summary>
    public const string Get = "identity.people.get";

    /// <summary>
    /// Read the directory again. Operator level: it decides who the process believes about who said what, which is a
    /// deployment decision even though it changes no code and no capability.
    /// </summary>
    public const string Reload = "identity.reload";
}
