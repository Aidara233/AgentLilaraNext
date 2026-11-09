using ALN.Contracts;
using ALN.Contracts.Models;

namespace ALN.Runtime;

/// <summary>
/// The last gate before a request goes out: how many pictures it may carry, and how many bytes of them.
///
/// It exists because pictures are the one thing that can make a request enormous without anybody deciding to: nine
/// screenshots, a loop that reads the same photo every round, a conversation that has been going for a week. The
/// provider's answer to all of those is the same refusal, and once a Session's history contains enough pictures, every
/// later request is refused too — the failure does not decay, which is what makes it worth a gate.
///
/// The rule: <b>the newest pictures win, and what is dropped leaves a sentence behind.</b> Newest, because "what did they
/// just send me" is the question a turn is usually answering. A sentence, because a message that silently becomes text
/// reads like a message that never had a picture in it.
/// </summary>
public static class ImageBudget
{
    /// <summary>
    /// Applies <paramref name="policy"/> to every image in the request. Returns how many were replaced by a note.
    ///
    /// Nothing happens when the policy says there is no ceiling and no image is too big, which is the ordinary case and
    /// costs one walk over the message list.
    /// </summary>
    public static int Apply(IList<ModelMessage> messages, IMediaPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(policy);

        // Newest first: walking backwards is what makes "keep the ones that just arrived" a consequence of the order
        // rather than a second rule to keep in sync.
        var kept = 0;
        long keptBytes = 0;
        var dropped = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Parts is not { Count: > 0 } parts) continue;
            var rebuilt = new List<ModelContentPart>(parts.Count);
            var changed = false;
            for (var part = parts.Count - 1; part >= 0; part--)
            {
                if (parts[part] is not ImagePart image)
                {
                    rebuilt.Insert(0, parts[part]);
                    continue;
                }
                var bytes = image.Bytes.Length;
                var fits = kept < policy.MaxImagesPerRequest
                           && keptBytes + bytes <= policy.MaxImageBytesPerRequest
                           && bytes <= policy.MaxImageBytes
                           && policy.Allows(image.MediaType);
                if (fits)
                {
                    kept++;
                    keptBytes += bytes;
                    rebuilt.Insert(0, image);
                    continue;
                }
                dropped++;
                changed = true;
                rebuilt.Insert(0, new TextPart(Note(image)));
            }
            if (changed) messages[index] = message with { Parts = rebuilt };
        }
        return dropped;
    }

    /// <summary>
    /// What stands in for a picture that is not being sent. It says what it was and how big, so the model can tell the
    /// difference between "there was nothing here" and "there was something I am not seeing".
    /// </summary>
    private static string Note(ImagePart image)
    {
        var size = image.Width is > 0 && image.Height is > 0 ? $"，{image.Width}×{image.Height}" : "";
        return $"（一张图：{image.MediaType}{size}，{image.Bytes.Length} 字节；这次请求没有带上它。"
               + "要重新看，用 fs_read 打开那个文件。）";
    }
}
