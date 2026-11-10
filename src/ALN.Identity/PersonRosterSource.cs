using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Identity;

/// <summary>
/// Who the people in this conversation are, re-rendered every round from the ids the Session has already met.
///
/// <b>Why every round instead of once.</b> The rendered conversation already says what each speaker is called
/// (docs/31 §4), but "小林" is a label, and a label is worth nothing to a model that has forgotten who 小林 is. A fact
/// introduced once scrolls out of the window and cannot be looked up afterwards; the roster is small, so re-stating it
/// is cheaper than the mistake it prevents — an assistant that treats a regular as a stranger, or a stranger as
/// somebody it should obey.
///
/// <b>Why this does not contradict docs/11 §5.3.</b> That rule forbids writing the Session's own depth as a rank. This
/// section states a fact about <i>other</i> people: how deep what they ask for may reach (<see cref="Person.Label"/>).
/// It says nothing about who outranks the reader, and nothing here grants or withholds anything — the number is carried
/// so that a later instruction system has something to compare against, and so the model stops guessing (docs/31 §5.1).
///
/// <b>Why only registered people, and only ones in this conversation.</b> Listing everybody the table knows would put
/// twelve strangers in front of a conversation between two people, every single round. The table is the country; this
/// is the room.
/// </summary>
public sealed class PersonRosterSource(IPersonDirectory people) : IFloatingContextSource
{
    public const string SourceId = "identity.roster";

    /// <summary>The section name in the rendered block. Kept next to `time` and `memory`: a reader could find it.</summary>
    public const string SectionName = "people";

    /// <summary>
    /// How many people one roster holds. Twelve is a group chat, not a community: beyond that the line stops being
    /// something a model reads and becomes something it skips, and skipping is what makes the whole section useless.
    /// </summary>
    private const int MaxPeople = 12;

    /// <summary>How much of a note is repeated every round. The full text is one <c>whois</c> away.</summary>
    private const int MaxNoteChars = 120;

    /// <summary>
    /// Between the clock (-100) and memory (-90): a name is context for what is about to be read, not part of the
    /// message and not a status line about the Session itself (those sit at 190+).
    /// </summary>
    private const int Order = -85;

    private readonly IPersonDirectory _people = people;

    public string Id => SourceId;

    public ValueTask<Result<IReadOnlyList<RoundContextContribution>>> CollectAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.People is not { Count: > 0 } ids)
            return ValueTask.FromResult(Result<IReadOnlyList<RoundContextContribution>>.Success([]));

        // Sorted by id, not by appearance and not by depth: a roster that changes order between two rounds reads as
        // people having changed, which is the one thing it must never suggest (docs/31 §5).
        var persons = ids
            .Select(id => _people.FindById(id))
            .OfType<Person>()
            .OrderBy(person => person.Id, StringComparer.Ordinal)
            .ToArray();
        if (persons.Length == 0)
            return ValueTask.FromResult(Result<IReadOnlyList<RoundContextContribution>>.Success([]));

        var shown = persons.Take(MaxPeople).Select(Line).ToList();
        if (persons.Length > shown.Count)
            // Said out loud rather than silently cut: a model that is told the roster is incomplete can look one up;
            // a model handed a truncated list believes it has seen everybody.
            shown.Add($"（还有 {persons.Length - shown.Count} 个登记过的人也在这里说过话，没列出来。）");

        IReadOnlyList<RoundContextContribution> sections =
        [
            new RoundContextContribution("people", SectionName, string.Join('\n', shown), Order)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<RoundContextContribution>>.Success(sections));
    }

    /// <summary>One line per person: the label, then the note that was written about him.</summary>
    private static string Line(Person person)
    {
        var label = person.Self ? $"{person.Label}（这是我自己的账号）" : person.Label;
        if (person.Note is not { Length: > 0 } note) return label;
        var trimmed = note.Length > MaxNoteChars ? note[..MaxNoteChars] + "…" : note;
        return $"{label}：{trimmed}";
    }
}
