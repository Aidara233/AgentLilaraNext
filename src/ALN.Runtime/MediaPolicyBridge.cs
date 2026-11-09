using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The one place the rest of the process asks "may this be shown", when the media module is the thing that decides it.
///
/// It exists because of an ordering fact, not because indirection is nice: the <see cref="SessionManager"/> is built by
/// the host before any module is loaded, and the policy lives in <c>media.local/module.json</c>, which the kernel reads
/// later. Handing the Session manager the media module itself is impossible at construction time; handing it a copy of
/// the numbers would freeze them, and then <c>media.local/autoView</c> would say one thing and every request would do
/// another. So the host builds this, the module fills it in when it starts, and everyone else reads through it.
///
/// Until it is filled, it answers with <see cref="DefaultMediaPolicy"/>: a host that never configured a media module
/// still has a ceiling rather than no ceiling (docs/28 §4).
/// </summary>
public sealed class MediaPolicyBridge : IMediaPolicy
{
    private IMediaPolicy _current = DefaultMediaPolicy.Instance;

    /// <summary>The policy in force right now. Volatile because a module start (or a parameter change) happens on another thread than a turn.</summary>
    public IMediaPolicy Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public MediaViewPolicy AutoView => Current.AutoView;
    public long MaxImageBytes => Current.MaxImageBytes;
    public int MaxAutoViewsPerTurn => Current.MaxAutoViewsPerTurn;
    public int MaxImagesPerRequest => Current.MaxImagesPerRequest;
    public long MaxImageBytesPerRequest => Current.MaxImageBytesPerRequest;
    public bool Allows(string mediaType) => Current.Allows(mediaType);

    public MediaView Decide(int index, string mediaType, long bytes, int alreadyShown) =>
        Current.Decide(index, mediaType, bytes, alreadyShown);
}
