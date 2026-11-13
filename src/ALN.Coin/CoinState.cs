namespace ALN.Coin;

/// <summary>One stack of things in the bag. There is no "stack limit": a thousand of something is one entry.</summary>
public sealed record CoinStack(string Item, int Count);

/// <summary>
/// The whole of Lilara's purse, as one JSON document: <c>scope=coin</c>, <c>key=state</c>.
///
/// One key rather than three, because a purchase changes all of it at once — money down, goods up, stock down — and
/// three keys would mean three chances to be half-done. <see cref="Revision"/> is not a lock (the state store's
/// <c>AtomicallyAsync</c> already serializes writers); it exists so an audit line can name the exact version of the
/// purse a number was read from.
/// </summary>
/// <param name="Gifts">
/// Who gave Lilara a coin today, and on which local day. <c>personId → yyyy-MM-dd</c>, and only entries for the day being
/// written are kept, so the table is exactly "who gave today" rather than a history nobody reads.
///
/// It lives in the purse rather than in a key of its own because the coin and the day are one write: a balance that went
/// up while the day did not get recorded is a gift somebody can give twice — and one document is the only way two things
/// change together here (see the note on this record). A gift that is refused writes nothing, so the table only ever
/// grows by the people who actually gave.
///
/// <b>Keyed by person, not by account:</b> the identity table is what says two platform accounts are one person, and a
/// person gives once a day however many accounts he has.
/// </param>
public sealed record CoinState(
    long Balance,
    long Revision,
    IReadOnlyList<CoinStack> Bag,
    IReadOnlyDictionary<string, int> Sold,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Purchased,
    IReadOnlyList<string>? Settled = null,
    IReadOnlyDictionary<string, string>? Gifts = null)
{
    public static CoinState Empty(long balance = 0) => new(
        balance, 0, [],
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal),
        []);

    /// <summary>How many of one thing the purse currently holds. Zero when it holds none.</summary>
    public int CountOf(string item) =>
        Bag.FirstOrDefault(stack => string.Equals(stack.Item, item, StringComparison.Ordinal))?.Count ?? 0;

    /// <summary>How many of one thing were sold. Only meaningful for a good with a finite <c>stock</c>.</summary>
    public int SoldOf(string good) => Sold.TryGetValue(good, out var count) ? count : 0;

    /// <summary>
    /// Whether this person has already given a coin on <paramref name="day"/>. Never written and never given are the same
    /// answer, so a purse from before this existed behaves exactly like a purse nobody gave to today.
    /// </summary>
    public bool GaveOn(string person, string day) =>
        Gifts is { } gifts && gifts.TryGetValue(person, out var when)
        && string.Equals(when, day, StringComparison.Ordinal);

    /// <summary>How many people gave on <paramref name="day"/>. Only ever the day being written, by construction.</summary>
    public int GiversOn(string day) =>
        Gifts?.Count(entry => string.Equals(entry.Value, day, StringComparison.Ordinal)) ?? 0;
}

/// <summary>
/// A shelf entry. It knows a price, and the name of the script that decides what a purchase actually does — it does
/// <b>not</b> know what that is. Buying <c>test-box</c> may yield two <c>test-candy</c>, one of something else, or
/// nothing at all; only the script knows, and that is deliberate.
/// </summary>
/// <param name="Script">A callable script (<c>manifest.tool.callable</c>) the graph can run as a function.</param>
/// <param name="Stock">Remaining units across the whole deployment, or -1 for unlimited.</param>
public sealed record CoinGood(string Id, string Name, long Price, string Script, int Stock = -1,
    /// <summary>
    /// At most one per Session. Not a stock limit: a thing that only makes sense once for a given conversation (a
    /// hint, a reroll) can be unlimited across the deployment and still useless the second time to the same Session.
    /// </summary>
    bool OncePerSession = false);

/// <summary>
/// A thing that can be in the bag. Its <paramref name="Use"/> script decides what using it does, including how many
/// of it are consumed — zero, one, or all of them.
/// </summary>
public sealed record CoinItem(string Id, string Name, string Use);

/// <summary>
/// What the two catalogue files said. <see cref="Issues"/> is the part that matters: a good whose script does not
/// exist is <b>not sold</b> rather than sold-and-doing-nothing, because "I paid and nothing happened" is the one
/// lesson this system must not teach by accident.
/// </summary>
public sealed record CoinCatalogue(
    IReadOnlyList<CoinGood> Goods,
    IReadOnlyList<CoinItem> Items,
    IReadOnlyList<string> Issues)
{
    public static readonly CoinCatalogue Empty = new([], [], []);

    public CoinGood? Find(string? id) =>
        id is null ? null : Goods.FirstOrDefault(good => string.Equals(good.Id, id, StringComparison.Ordinal));

    public CoinItem? FindItem(string? id) =>
        id is null ? null : Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
}
