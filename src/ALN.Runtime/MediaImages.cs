using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Runtime;

/// <summary>
/// Turning a stored picture into something a request can carry — or into a sentence saying why not.
///
/// This is the only place that reads picture bytes, and it is deliberately the only place that decides three things at
/// once: whether the file is still there, whether it is within the limits, and whether this model may be shown it. Split
/// across three call sites, one of them would eventually forget one — the one that matters being "this model cannot see
/// images", whose failure mode is a provider refusing the whole request.
///
/// The bytes are read here and nowhere else: a <see cref="MediaRef"/> in the timeline is a pointer, and this is the step
/// that turns a pointer into a payload for exactly one request (docs/28 §3).
/// </summary>
public static class MediaImages
{
    /// <summary>
    /// What to put in a request for one stored picture: a part to send, or the reason it is not being sent.
    ///
    /// <paramref name="modelSeesImages"/> is asked first because it is the cheapest answer and the one with the worst
    /// failure mode: sending a picture to a model that cannot read them gets the entire request rejected.
    /// </summary>
    public static async ValueTask<(ImagePart? Part, string Note)> LoadAsync(MediaRef media, IBinaryFileAccess? files,
        IMediaPolicy policy, bool modelSeesImages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        var label = Describe(media);
        if (!media.IsViewable) return (null, $"（{label}：不是能看的图片格式）");
        if (!policy.Allows(media.MediaType)) return (null, $"（{label}：{media.MediaType} 不在可看的格式里）");
        if (!modelSeesImages) return (null, $"（{label}：本模型看不到图像，只能把文件位置告诉你）");
        if (media.Bytes > policy.MaxImageBytes)
            return (null, $"（{label}：{MediaPolicy.Describe(media.Bytes)}，超过单图上限 {MediaPolicy.Describe(policy.MaxImageBytes)}）");
        if (files is null) return (null, $"（{label}：这个宿主没有接上字节面，看不到它）");

        var opened = await files.OpenReadAsync(media.Scope, media.Path, cancellationToken).ConfigureAwait(false);
        if (!opened.Ok || opened.Value is null) return (null, $"（{label}：文件已经不在那儿了）");

        try
        {
            await using var stream = opened.Value;
            // Read exactly the size the reference declared, and no more: a file that grew since it was described must
            // not become a request nobody budgeted for.
            if (media.Bytes > policy.MaxImageBytes) return (null, $"（{label}：超过单图上限）");
            var buffer = new byte[media.Bytes];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            return (new ImagePart(media.MediaType, buffer.AsMemory(0, read), media.Width, media.Height, label), $"（{label}）");
        }
        catch (IOException) { return (null, $"（{label}：读的时候出错了）"); }
        catch (UnauthorizedAccessException) { return (null, $"（{label}：没有权限读它）"); }
    }

    /// <summary>How a picture is named in text: where it is, what it is, how big. The same spelling everywhere.</summary>
    public static string Describe(MediaRef media)
    {
        var size = media.Size is { } dimensions ? $"，{dimensions}" : "";
        return $"{media.Locator}（{media.MediaType}{size}，{MediaPolicy.Describe(media.Bytes)}）";
    }

    /// <summary>
    /// The sentence that stands in for a picture that is not being sent. Says what it was, and how to see it — a model
    /// that is told only "no" retries; one that is told where the file is has something to do instead.
    /// </summary>
    public static string Receipt(MediaRef media, string? why = null) =>
        why ?? $"（{Describe(media)}；要看就用 fs_read 打开它）";
}
