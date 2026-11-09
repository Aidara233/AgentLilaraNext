namespace ALN.Contracts.Adapters;

/// <summary>
/// The <c>f:</c> marker: how a name that means "something the platform is holding" cannot be mistaken for a path.
///
/// It exists because exactly one decision is made on it — <c>files.get</c> has three shapes (list, search, fetch) and
/// dispatching them by trying one and falling back to the next would turn "the platform refused" into "so it must have
/// been a search". A prefix makes the shape readable, so a failure stays a failure (docs/28 §6.7).
///
/// The prefix is <b>written by the adapter and read by the tool</b>, and nothing else in between builds one by hand.
/// </summary>
public static class FileHandles
{
    public const string Prefix = "f:";

    /// <summary>Wraps an adapter's own name. Idempotent, so a handle that came back from a caller still works.</summary>
    public static string Wrap(string inner) =>
        inner.StartsWith(Prefix, StringComparison.Ordinal) ? inner : Prefix + inner;

    /// <summary>The adapter's own name, or false when this is not a handle at all.</summary>
    public static bool TryUnwrap(string? text, out string inner)
    {
        inner = string.Empty;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        inner = text[Prefix.Length..];
        return inner.Length > 0;
    }

    /// <summary>Whether this looks like a handle — the one question the dispatch asks.</summary>
    public static bool Looks(string? text) => TryUnwrap(text, out _);
}

/// <summary>
/// One file the platform is holding, as a model may point at it.
/// </summary>
/// <param name="Handle">
/// The adapter's own name for it, <b>already wrapped with <see cref="FileHandles.Prefix"/></b>. Opaque to everybody but
/// the adapter that made it: it may carry a file id, a folder, an account — none of that is anybody else's business, and
/// a handle that leaked its shape would be one more thing a caller could construct by hand.
/// </param>
/// <param name="Name">What to call it — the first thing a reader recognises. Never invented: a platform that gives no
/// name gets a blank one rather than a plausible lie.</param>
/// <param name="Bytes">Size when the platform said. Null is "not known", which is not zero and is not a reason to
/// refuse: it is a reason to fetch without deciding anything from it.</param>
/// <param name="At">When it appeared, if the platform says. Used for ordering and for "is this the one from today".</param>
/// <param name="From">Who put it there, if the platform says.</param>
public sealed record PlatformFile(string Handle, string Name, long? Bytes = null, DateTimeOffset? At = null,
    string? From = null);

/// <summary>
/// What a listing turned out to be. <b>A listing is not a success</b>: an adapter that cannot list says why with a
/// failure, and one that can list but could not show everything says so in <paramref name="Note"/> — "the root folder
/// has three subfolders this build does not open" is information the reader can act on, and an empty array would have
/// told it the opposite.
/// </summary>
/// <param name="Folders">Names of containers the listing did not descend into. Empty for platforms without them.</param>
/// <param name="Note">One sentence about what is missing from this listing, in the platform's own terms.</param>
public sealed record PlatformFileListing(IReadOnlyList<PlatformFile> Files,
    IReadOnlyList<string>? Folders = null, string? Note = null);

/// <summary>
/// The bytes behind a handle, plus what to call them. <b>The caller disposes <paramref name="Content"/></b> — it is
/// handed over open so that a 60 MB file can go from the platform to a scope without ever being one array in this
/// process.
/// </summary>
/// <param name="MediaType">The platform's own content type when it sent one. A claim, not a probe: whoever stores the
/// bytes decides what they are from the bytes (docs/28 §5).</param>
public sealed record PlatformFileContent(Stream Content, string Name, long? Bytes = null, string? MediaType = null);

/// <summary>
/// Optional: an adapter whose platform keeps files the model can ask for later — a group's file list, an attachment
/// somebody uploaded yesterday, a document in a channel.
///
/// <b>Why this is optional and why it is not a capability table.</b> An adapter that does not implement it simply does
/// not have the feature, and the answer to the caller is one sentence saying so — there is no separate declaration to
/// keep in sync (I-33). What this interface describes is behaviour the adapter itself performs on the same call the
/// caller made, which is the opposite of a claim about what somebody else can do.
///
/// <b>It is deliberately not a downloader.</b> The two verbs are "what is there" and "give me the bytes"; where they
/// land on this disk, with what name, and whether they may land at all are the host's questions — the adapter has no
/// scope, no path and no opinion about either (docs/28 §6.7).
/// </summary>
public interface IFileCatalogAdapter
{
    /// <summary>
    /// What this conversation's platform is holding, newest first, at most <paramref name="limit"/> entries.
    ///
    /// Fails with a reason — not with an empty list — when this conversation cannot be listed at all: a private chat has
    /// no shared folder, and "nothing is there" is a different sentence from "this is the wrong kind of conversation".
    /// </summary>
    ValueTask<Result<PlatformFileListing>> ListAsync(string conversationId, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// The bytes behind one handle from <see cref="ListAsync"/> (or from an inbound notice).
    ///
    /// Refused rather than truncated when the file is larger than <paramref name="maxBytes"/>: a short file that looks
    /// complete is how a "backup" turns out not to be one. The ceiling is the caller's because it knows what it is
    /// asking for — a copy that has to finish now, against a file it is about to stream somewhere else.
    /// </summary>
    ValueTask<Result<PlatformFileContent>> FetchAsync(string conversationId, string handle, long maxBytes,
        CancellationToken cancellationToken);
}
