using System.Text.Json;
using ALN.Contracts;

namespace ALN.Identity;

/// <summary>
/// Who is who, read from <c>&lt;dataRoot&gt;/identify/&lt;id&gt;/info.json</c> — one directory per person (docs/31 §1).
///
/// <b>Reading is lock-free and synchronous.</b> The table is an immutable snapshot and a reload swaps the reference:
/// a lookup in the middle of a reload sees either the whole old table or the whole new one, never a half-built one.
/// That is what lets the message path call it without a semaphore and without a cancellation token.
///
/// <b>A bad file costs that one person.</b> Every other directory is still read and still answers. A table that went
/// empty because one JSON was missing a comma would be a table that stops recognising everybody over a typo
/// (docs/31 §9.1).
/// </summary>
public sealed class PersonDirectory : IPersonDirectory, IPersonDirectoryReload
{
    /// <summary>The data-root folder this reads. Named for what happens in it — recognising people — and not for the
    /// module that does it; a directory name is not an id (<c>C-02</c> is about <c>modules/&lt;id&gt;/</c>).</summary>
    public const string DirectoryName = "identify";

    /// <summary>What one person's directory holds. Everything else in that directory is nobody's business here.</summary>
    public const string InfoFileName = "info.json";

    /// <summary>The id is a directory name, so the ceiling is a filesystem one and the real limit is readability.</summary>
    private const int MaxIdLength = 96;

    private const int MaxNameLength = 120;
    private const int MaxNoteLength = 2000;
    private const int MaxAccounts = 64;
    private const int MaxAccountLength = 256;

    private static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web)
    { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly string _dataRoot;

    /// <summary>
    /// The current table. Swapped whole by <see cref="ReloadAsync"/>; read once per lookup. <c>volatile</c> because a
    /// reference write is atomic and this is exactly the case where the memory model's word is what makes it safe.
    /// </summary>
    private volatile Table _table = Table.Empty;

    public PersonDirectory(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("A data root is required.", nameof(dataRoot));
        _dataRoot = dataRoot;
    }

    /// <summary>Where the people live. Shown in the startup receipt so nobody has to guess the folder name.</summary>
    public string Root => Path.Combine(_dataRoot, DirectoryName);

    public Person? Find(Address adapter, string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        return _table.ByAccount.GetValueOrDefault(PersonAccount.KeyOf(adapter, account));
    }

    public Person? FindById(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _table.ById.GetValueOrDefault(id.Trim());

    public IReadOnlyList<Person> List() => _table.Ordered;

    public Person? Self => _table.Self;

    /// <summary>
    /// Whether this account is mine. Spelled out here rather than left to the interface's default, because the class
    /// is what tests and the host hold: a default implementation reachable only through the interface is a method
    /// that exists for some callers and not others, and that is the kind of difference nobody remembers.
    /// </summary>
    public bool IsSelf(Address adapter, string? account) => Find(adapter, account) is { Self: true };

    /// <summary>
    /// Reads the whole folder and puts the result in place.
    ///
    /// It does not refuse to run when the folder is missing: no table is the honest answer to "nobody has written
    /// down who anybody is", and every message then arrives from a stranger — which is what a fresh data root should
    /// look like, not a failure (docs/31 §9.3).
    /// </summary>
    public ValueTask<PersonDirectoryReport> ReloadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var problems = new List<PersonProblem>();
        var byId = new Dictionary<string, Person>(StringComparer.Ordinal);
        // The account key points at the person's id, not at the Person: the whole record is then one lookup away, and
        // the two maps are built in one pass and frozen together.
        var byAccountId = new Dictionary<string, string>(StringComparer.Ordinal);
        var self = (Person?)null;

        string[] directories;
        try
        {
            directories = Directory.Exists(Root)
                ? Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder exists and cannot be read. Keeping the table that is already loaded is the conservative
            // answer: a stale table still recognises people, an emptied one recognises nobody and says nothing.
            problems.Add(new PersonProblem(Root, $"cannot be read ({ex.Message}); the table already loaded is kept"));
            return ValueTask.FromResult(new PersonDirectoryReport(_table.Ordered, problems));
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            var path = Path.Combine(directory, InfoFileName);
            // A directory with no card in it is skipped without an error: somebody may still be filling it in, and a
            // person is defined by their info.json, not by a folder existing.
            if (!File.Exists(path)) continue;

            if (id.Length > MaxIdLength)
            {
                problems.Add(new PersonProblem(path, $"the directory name is longer than {MaxIdLength} characters"));
                continue;
            }

            var read = ReadOne(id, path, problems);
            if (read is null) continue;
            var person = read.Value.Person;

            if (byId.ContainsKey(person.Id))
            {
                problems.Add(new PersonProblem(path, $"'{person.Id}' is already taken by another directory"));
                continue;
            }

            // Every account is checked before any of them is claimed: a person is one unit, so a collision on his
            // third account must leave all three unclaimed rather than claiming the first two.
            string? clashKey = null;
            foreach (var key in read.Value.Keys)
                if (byAccountId.ContainsKey(key)) { clashKey = key; break; }

            if (clashKey is not null)
            {
                var (adapter, account) = read.Value.Spelling[clashKey];
                problems.Add(new PersonProblem(path,
                    $"{adapter} / {account} is already {byAccountId[clashKey]}'s; a platform account belongs to one person"));
                continue;
            }

            if (person.Self)
            {
                if (self is not null)
                {
                    problems.Add(new PersonProblem(path,
                        $"already marked as self by '{self.Id}'; only one group of accounts can be mine"));
                    continue;
                }
                self = person;
            }

            byId[person.Id] = person;
            foreach (var key in read.Value.Keys) byAccountId[key] = person.Id;
        }

        var ordered = byId.Values.OrderBy(person => person.Id, StringComparer.Ordinal).ToArray();
        _table = Table.Build(byId, byAccountId, ordered, self);
        return ValueTask.FromResult(new PersonDirectoryReport(ordered, problems));
    }

    /// <summary>One person's card, or null when the file does not describe one.</summary>
    private static ParsedPerson? ReadOne(string id, string path, List<PersonProblem> problems)
    {
        PersonDocument? document;
        try
        {
            using var stream = File.OpenRead(path);
            document = JsonSerializer.Deserialize<PersonDocument>(stream, Read);
        }
        catch (JsonException ex)
        {
            problems.Add(new PersonProblem(path,
                $"is not valid JSON ({ex.Message})"));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new PersonProblem(path, $"cannot be read ({ex.Message})"));
            return null;
        }

        if (document is null)
        {
            problems.Add(new PersonProblem(path, "is empty"));
            return null;
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            problems.Add(new PersonProblem(path, "needs a name: it is what the model will call this person"));
            return null;
        }
        if (document.Name.Trim().Length > MaxNameLength)
        {
            problems.Add(new PersonProblem(path, $"name is longer than {MaxNameLength} characters"));
            return null;
        }
        if (document.Note is { } note && note.Length > MaxNoteLength)
        {
            problems.Add(new PersonProblem(path, $"note is longer than {MaxNoteLength} characters"));
            return null;
        }

        if (!TryReadLevel(document.Level, out var level))
        {
            problems.Add(new PersonProblem(path,
                $"level must be agent, operator or full (got '{document.Level?.GetRawText()}')"));
            return null;
        }

        var accounts = new List<PersonAccount>();
        var keys = new List<string>();
        var spelling = new Dictionary<string, (Address Adapter, string Account)>(StringComparer.Ordinal);
        foreach (var entry in document.Accounts ?? [])
        {
            if (accounts.Count >= MaxAccounts)
            {
                problems.Add(new PersonProblem(path, $"has more than {MaxAccounts} accounts"));
                return null;
            }
            if (string.IsNullOrWhiteSpace(entry.Account))
            {
                problems.Add(new PersonProblem(path, $"accounts[{accounts.Count}].account is empty"));
                return null;
            }
            if (entry.Account.Trim().Length > MaxAccountLength)
            {
                problems.Add(new PersonProblem(path, $"accounts[{accounts.Count}].account is not a usable id"));
                return null;
            }
            // An instance address, not a type: the same platform can be online twice, and their sender ids are their
            // own spaces (docs/31 §2). "qq" would silently merge the primary's ids with the backup's.
            if (!Address.TryParse(entry.Adapter, out var adapter) || !adapter.IsInstance)
            {
                problems.Add(new PersonProblem(path,
                    $"accounts[{accounts.Count}].adapter must be an instance address like \"adapter.qq#main\" "
                    + $"(got '{entry.Adapter}')"));
                return null;
            }
            var account = new PersonAccount(adapter, entry.Account.Trim());
            if (!spelling.TryAdd(account.Key, (adapter, account.Account)))
            {
                // The same account twice in one file: harmless in effect (the lookup answers the same thing) but it is
                // a card contradicting itself, and saying so is cheaper than guessing which line was meant.
                problems.Add(new PersonProblem(path, $"lists {adapter} / {account.Account} twice"));
                return null;
            }
            accounts.Add(account);
            keys.Add(account.Key);
        }

        if (accounts.Count == 0)
        {
            problems.Add(new PersonProblem(path,
                "needs at least one account: a person nobody can arrive as cannot be recognised"));
            return null;
        }

        return new ParsedPerson(
            new Person(id, document.Name.Trim(),
                string.IsNullOrWhiteSpace(document.Note) ? null : document.Note.Trim(),
                level, document.Self ?? false, accounts),
            keys, spelling);
    }

    /// <summary>
    /// "operator", "Operator", or the number — and nothing else. A bare integer in a file a person opens is a setting
    /// somebody will get wrong, which is why <c>AccessLevelJson</c> exists; here it is read by hand because the failure
    /// has to say which file and which field, and a converter's exception does not.
    /// </summary>
    private static bool TryReadLevel(JsonElement? element, out AccessLevel level)
    {
        level = AccessLevel.Agent;
        if (element is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is >= 0 and <= 2)
        {
            level = (AccessLevel)number;
            return true;
        }
        if (value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<AccessLevel>(value.GetString(), ignoreCase: true, out var named))
        {
            level = named;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The whole table, immutable once built. One object so a swap can never leave the two maps disagreeing — and an
    /// explicit constructor rather than record property initialisers, because those run in an order nobody reading the
    /// declaration can see.
    /// </summary>
    private sealed class Table
    {
        private Table(Dictionary<string, Person> byId, Dictionary<string, Person> byAccount,
            IReadOnlyList<Person> ordered, Person? self)
        {
            ById = byId;
            ByAccount = byAccount;
            Ordered = ordered;
            Self = self;
        }

        public static Table Empty { get; } = Build(new(StringComparer.Ordinal), new(StringComparer.Ordinal), [], null);

        public Dictionary<string, Person> ById { get; }
        public Dictionary<string, Person> ByAccount { get; }
        public IReadOnlyList<Person> Ordered { get; }
        public Person? Self { get; }

        /// <summary>
        /// Builds the frozen table from what a scan produced. A method rather than a constructor overload because the
        /// two dictionary parameters differ only by their value type, and a call with two empty comparer-only
        /// dictionaries picks the wrong one.
        /// </summary>
        public static Table Build(Dictionary<string, Person> byId, Dictionary<string, string> byAccountId,
            IReadOnlyList<Person> ordered, Person? self) =>
            new(byId, Built(byId, byAccountId), ordered, self);

        private static Dictionary<string, Person> Built(Dictionary<string, Person> byId,
            Dictionary<string, string> byAccountId)
        {
            var built = new Dictionary<string, Person>(byAccountId.Count, StringComparer.Ordinal);
            foreach (var (key, owner) in byAccountId)
                if (byId.TryGetValue(owner, out var person)) built[key] = person;
            return built;
        }
    }

    private readonly record struct ParsedPerson(Person Person, List<string> Keys,
        Dictionary<string, (Address Adapter, string Account)> Spelling);

    /// <summary>What a person's card looks like on disk. Nullable everywhere, because saying <i>why</i> a card is wrong
    /// needs to see the card that was actually written, not a binding failure.</summary>
    private sealed record PersonDocument(
        string? Name,
        string? Note,
        JsonElement? Level,
        bool? Self,
        AccountDocument[]? Accounts);

    private sealed record AccountDocument(string? Adapter, string? Account);
}
