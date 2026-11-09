namespace ALN.Contracts;

/// <summary>
/// What a stored piece of media is, in the crudest possible terms.
///
/// A kind is decided by the bytes, never by a file name or by who sent it (<see cref="MediaProbe"/>), because the
/// interesting question — "may this be put in front of a model as a picture?" — is answered by the format and nothing
/// else. <see cref="File"/> is the honest answer for everything we do not recognise: it is still stored, still
/// referenced, still transferable; it simply never becomes anything but a name.
/// </summary>
public enum MediaKind
{
    /// <summary>A picture: PNG, JPEG, GIF or WebP. The only kind this build can show a model.</summary>
    Image,

    /// <summary>Voice. Stored and referenced; nothing in this build listens to it.</summary>
    Voice,

    /// <summary>Video. Stored and referenced; nothing in this build plays it.</summary>
    Video,

    /// <summary>A document or archive somebody sent. Bytes with a name, and that is all it is.</summary>
    File,

    /// <summary>A card, a share, a forwarded bundle: the platform's own rich object, kept as its text.</summary>
    Card
}

/// <summary>
/// A piece of media, as a record points at it: <b>where it is, what it is, how big, how wide</b>. Never the bytes.
///
/// This is the shape history stores and a message carries, and the reason it holds a location instead of content is
/// that everything below it would break at once otherwise: <c>history.jsonl</c> is append-only and read whole into
/// memory with a 64 MiB ceiling, a model call record is written to an artifact file on every step, and a request is
/// rebuilt from these records on every round. A picture inlined into any of those is a picture paid for repeatedly.
///
/// The location is a <b>scope + relative path</b>, the same pair every other file operation takes, so there is one
/// path rule in the process and not two. <c>media</c> is where things other people sent live and is read-only to a
/// model; <c>workspace</c> is where a model's own pictures live and may be rewritten by it — a difference the record
/// tolerates because a workspace file is the model's own note, not evidence.
/// </summary>
/// <param name="Scope">The registered scope the file sits in: <c>media</c> or <c>workspace</c>.</param>
/// <param name="Path">Path inside that scope, <c>/</c>-separated — the same spelling <c>fs_read</c> takes.</param>
/// <param name="MediaType">What the bytes are, as detected: <c>image/png</c>, <c>application/octet-stream</c>, ….</param>
/// <param name="Bytes">Size on disk, so a decision about "is this too big to send" needs no stat.</param>
/// <param name="Width">Pixels, when the format told us. Null means "not known", which is not the same as zero.</param>
/// <param name="Height">Pixels, when the format told us.</param>
/// <param name="Hash">SHA-256 of the bytes, lower-case hex. The store's name for the file, and cheap to compare.</param>
public sealed record MediaRef(
    string Scope,
    string Path,
    MediaKind Kind,
    string MediaType,
    long Bytes,
    int? Width = null,
    int? Height = null,
    string? Hash = null)
{
    /// <summary>How to name this in text: <c>media/image/ab/ab12…png</c>. One spelling, everywhere.</summary>
    public string Locator => $"{Scope}/{Path}";

    /// <summary>"1920×1080" when the size is known, else null — the bit a receipt wants to say.</summary>
    public string? Size => Width is > 0 && Height is > 0 ? $"{Width}×{Height}" : null;

    /// <summary>Whether this is a picture a vision model could be shown at all.</summary>
    public bool IsViewable => Kind == MediaKind.Image;
}

/// <summary>
/// Where media comes from: bytes in, a reference out.
///
/// It is deliberately <b>not</b> a read path. Reading goes through <see cref="IBinaryFileAccess"/>, which already
/// knows about scopes, sizes and the read-only rule; a second way to open the same files would be a second answer to
/// "may this caller touch that".
///
/// Ingesting does not decide whether a model may look at the result. That decision belongs to whoever is assembling a
/// request — a session that knows which model it is talking to, how much room is left, and what the operator asked
/// for (docs/28 §4). A store that refused "unviewable" media would throw away the file as well as the question.
/// </summary>
public interface IMediaStore
{
    /// <summary>
    /// Largest single piece of media this build will take in: the inbound path has to hold a whole picture before it
    /// can be named, and a bridge that hands over a disk image should be told no rather than obeyed.
    /// </summary>
    const long MaxIngestBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Takes the bytes, names them by their content and returns where they went.
    ///
    /// The same bytes twice is the same file: naming by hash is what makes "he sent that picture again" cost nothing,
    /// and what lets a record be checked against what it points at. The kind is what the caller expected; the format is
    /// what the bytes say, and when the two disagree the bytes win (<see cref="MediaProbe"/>).
    /// </summary>
    ValueTask<Result<MediaRef>> IngestAsync(MediaKind kind, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// What an existing file is, without copying it anywhere: size, format and dimensions, read off its header.
    ///
    /// This is the question a picture the model produced itself raises — a screenshot in <c>workspace</c> is not
    /// something the host ingested, but a request still has to know whether it can be shown and how much it costs. The
    /// answer is a <see cref="MediaRef"/> pointing at where the file already is, so nothing is duplicated to describe it.
    /// </summary>
    /// <param name="Scope">A scope the caller may read; <c>workspace</c> and <c>media</c> are the usual two.</param>
    ValueTask<Result<MediaRef>> DescribeAsync(string scope, string path, CancellationToken cancellationToken);
}

/// <summary>
/// Where a file on this disk becomes a URL somebody else can fetch — the half of sending a picture that base64 cannot do
/// well.
///
/// Opt-in by construction: nothing serves anything unless the host was told an address, because listening on a socket is
/// a deployment decision and the address has to be one the bridge can actually reach. Null means "no answer", and every
/// caller then falls back to what it did before (docs/16 §12.4).
/// </summary>
public interface IMediaUrlSource
{
    /// <summary>
    /// A URL a bridge can GET for this file, or null when this host does not serve it: an unknown scope, a file that is
    /// not a picture, one bigger than is worth putting on the wire, or no endpoint configured at all.
    /// </summary>
    Uri? UrlFor(string scope, string path);
}

/// <summary>
/// What a run of bytes is, decided by looking at the first few dozen of them.
///
/// Three refusals or absences worth keeping apart:
/// <list type="bullet">
///   <item>An unknown format is not an error: it is <see cref="MediaKind.File"/>, stored and passed along as bytes.</item>
///   <item>A known format whose header is truncated to the point that no size can be read is still that format, with
///   <c>Width</c>/<c>Height</c> null. Guessing "0×0" would make an estimate wrong in the confident direction.</item>
///   <item>Nothing here decodes pixels. That is what keeps the process free of an image decoder, and an image decoder
///   is a parser fed by strangers.</item>
/// </list>
/// </summary>
public sealed record MediaInfo(MediaKind Kind, string MediaType, string Extension, int? Width = null, int? Height = null)
{
    /// <summary>What an unrecognised run of bytes is: a file with no opinion attached.</summary>
    public static readonly MediaInfo Unknown = new(MediaKind.File, "application/octet-stream", "bin");
}

/// <summary>
/// When an arriving picture is put in front of the model by itself.
///
/// The default is <see cref="First"/> and the reason is not politeness: a picture costs a thousand tokens or more, the
/// model usually needs to know <b>that</b> nine arrived rather than what all nine look like, and one-to-one chat is the
/// case where "he sent a picture and asked about it" is the whole conversation. <see cref="Off"/> is for a deployment
/// that would rather pay a tool call; <see cref="All"/> is for a deployment whose pictures are all worth it.
/// </summary>
public enum MediaViewPolicy
{
    /// <summary>Never show one unasked. Every picture is a receipt and a file name.</summary>
    Off,

    /// <summary>Show the first picture of an arrival, say so about the rest.</summary>
    First,

    /// <summary>Show every picture that passes the other gates, up to the per-turn ceiling.</summary>
    All
}

/// <summary>
/// One picture's verdict, and — when the answer is no — the sentence the receipt will repeat.
///
/// A reason is required on a refusal because the model has to be able to tell "there was no picture" from "there was
/// one and you are not seeing it". The second one has to be said out loud, or a model answers about an image it never
/// saw with all the confidence it would have had about one it did.
/// </summary>
public sealed record MediaView(bool Shown, string? Reason)
{
    public static readonly MediaView Yes = new(true, null);

    public static MediaView No(string reason) => new(false, reason);
}

/// <summary>
/// The policy a request assembly asks before putting a picture into a request: which formats, how big, how many.
///
/// It is read per round rather than captured, because an operator changing it must not need a restart — and it is an
/// interface rather than a static because the answer belongs to the deployment's configuration, which the media module
/// owns (docs/28 §4).
/// </summary>
public interface IMediaPolicy
{
    MediaViewPolicy AutoView { get; }

    /// <summary>Largest single picture that may be shown at all. Bigger ones are a receipt and a file name.</summary>
    long MaxImageBytes { get; }

    /// <summary>How many pictures one turn may show without being asked. Stops "nine screenshots" from eating a window.</summary>
    int MaxAutoViewsPerTurn { get; }

    /// <summary>How many pictures one request may carry, asked or not.</summary>
    int MaxImagesPerRequest { get; }

    /// <summary>How many bytes of picture one request may carry.</summary>
    long MaxImageBytesPerRequest { get; }

    /// <summary>Whether a detected media type may be shown to a model at all.</summary>
    bool Allows(string mediaType);

    /// <summary>
    /// Whether this picture — the <paramref name="index"/>-th of an arrival — is shown unasked, given how many have
    /// already been shown this turn.
    /// </summary>
    MediaView Decide(int index, string mediaType, long bytes, int alreadyShown);
}
