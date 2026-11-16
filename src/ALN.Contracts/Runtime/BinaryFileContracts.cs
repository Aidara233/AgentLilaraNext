namespace ALN.Contracts;

/// <summary>
/// The byte half of the file surface: streams instead of text, for a file that is not a document.
///
/// <see cref="IFileSystemAccess"/> is text on purpose — it is the surface the model uses to tidy things it can read
/// back, and a byte-level interface there would invite storing things it cannot. But "the model may only write text"
/// and "the model may only move text between machines" are different rules: moving a tarball, a certificate or a
/// binary is a normal thing to do, and it never requires the bytes to pass through the model's context. So the bytes
/// get their own surface, with their own scopes and their own ceiling.
///
/// Two rules are inherited rather than reinvented: the same scope + relative-path shape (no absolute paths, no <c>..</c>),
/// and the same atomicity — a file appears whole or not at all.
///
/// It is not only a pipe. <see cref="PatchAsync"/>, <see cref="TruncateAsync"/> and
/// <see cref="AppendFromFileAsync"/> edit bytes in place, which is what lets <c>fs_inspect</c>/<c>fs_patch</c> repair a
/// header or join two halves without the content ever being a document the model writes. The line is drawn at what the
/// caller has to <i>understand</i>: a position and a bounded literal, never a blob.
/// </summary>
public interface IBinaryFileAccess
{
    /// <summary>
    /// The ceiling a caller passes when it wants one but has no opinion: 64 MiB, enough for a build artifact or a
    /// certificate bundle. It is <b>not</b> applied by the file surface on its own — the scopes take their ceiling from
    /// the deployment (<c>LocalFileSystemAccess</c>), and the SSH module from its own configuration — because a
    /// transfer streams through one buffer and its size is therefore not a memory question. What is left is the typo
    /// this number guards: a path that turns out to name a disk image.
    /// </summary>
    const long MaxTransferBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The scopes that accept binary at all. A deliberate subset: a directory meant to hold text (the memory library,
    /// a script draft) should not quietly become a place to park binaries, and "which rooms may hold a tarball" belongs
    /// to whoever registered the scopes, not to the caller.
    /// </summary>
    IReadOnlyList<string> BinaryScopes { get; }

    /// <summary>
    /// Opens an existing file for reading. Absent is an error here, not an empty stream: the caller is moving a known
    /// file, so "there is nothing there" is the answer it needs. The caller disposes the stream.
    /// </summary>
    ValueTask<Result<Stream>> OpenReadAsync(string scope, string path, CancellationToken cancellationToken);

    /// <summary>
    /// The same file, opened for streaming, with a ceiling the caller names instead of the transfer cap.
    ///
    /// Everything else is identical on purpose — the same scopes, the same containment, the same "absent is an error" —
    /// because the one thing a second read path must never do is answer "may this caller touch that" differently
    /// (<c>docs/24</c>). What changes is only how large a file may be, and that is because
    /// <see cref="MaxTransferBytes"/> answers a different question than this one does: it caps a <i>copy that has to
    /// finish inside a bounded moment</i> (an SSH pull, a tool moving a tarball through the process), while handing a
    /// file to a socket that streams it out is not a copy at all — the bytes go from disk to a bridge at whatever pace
    /// the bridge reads, and a ceiling of a few gigabytes costs this process one buffer (<c>docs/28 §6.6</c>).
    ///
    /// The ceiling is a parameter rather than another constant because the right number belongs to whoever is doing the
    /// sending: the media door takes it from the deployment's configuration, a tool that copies passes
    /// <see cref="MaxTransferBytes"/>. A constant here would be one more number to disagree with the two others.
    /// </summary>
    ValueTask<Result<Stream>> OpenReadStreamAsync(string scope, string path, long ceiling,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="content"/> to a file, replacing what was there. Streamed to a temporary file beside the
    /// target and renamed into place, so a reader never sees half a file — the same rule the text writes follow.
    /// </summary>
    ValueTask<Result> SaveAsync(string scope, string path, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// The same write, with a ceiling the caller names instead of <see cref="MaxTransferBytes"/>.
    ///
    /// <b>Same reasoning as <see cref="OpenReadStreamAsync"/>, on the way in.</b> <see cref="MaxTransferBytes"/> answers
    /// "how much may one transfer move, when the caller is a tool and the file is one it chose" — 64 MiB, small enough
    /// that a mistyped path cannot start copying a disk image through this process. A caller that is instead receiving
    /// bytes it did not choose — what a platform is handing over, at whatever size that turns out to be — has a better
    /// number for the job, and it is not this constant.
    ///
    /// The ceiling is a parameter for the same reason it is one on the read side: the right number belongs to whoever is
    /// moving the bytes, and a second constant here would be one more thing to keep in agreement with the other two.
    /// </summary>
    ValueTask<Result> SaveAsync(string scope, string path, Stream content, long ceiling,
        CancellationToken cancellationToken);

    /// <summary>
    /// Overwrites bytes at <paramref name="offset"/>, or — with <paramref name="insert"/> — puts them there and pushes
    /// the rest along. Returns the file's new length.
    ///
    /// This is the difference between "the model may write files" and "the model may edit bytes": the argument is a
    /// bounded literal at a stated position, not a document. What that buys is repairable — a header that says the
    /// wrong length, a magic number to patch, four bytes of a counter — while what it does not buy is a way to author a
    /// megabyte of content the model cannot read back, which is the line <c>docs/24 §7.2</c> keeps.
    /// </summary>
    ValueTask<Result<long>> PatchAsync(string scope, string path, long offset, ReadOnlyMemory<byte> bytes,
        bool insert, CancellationToken cancellationToken);

    /// <summary>
    /// Cuts a file down to <paramref name="length"/> bytes and returns the result. Refuses to grow one: extending is
    /// <see cref="PatchAsync"/> at the end, where the bytes are named.
    /// </summary>
    ValueTask<Result<long>> TruncateAsync(string scope, string path, long length,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends the whole of one file to another and returns the combined length. Neither file passes through the
    /// caller's memory, so joining two halves of something is bounded by the disk and not by a context window.
    /// </summary>
    ValueTask<Result<long>> AppendFromFileAsync(string scope, string path, string sourceScope, string sourcePath,
        CancellationToken cancellationToken);
}
