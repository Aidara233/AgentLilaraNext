using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// What a picture costs, and what a request may carry.
///
/// The second half is the important one. A picture is the only thing in a request that can grow without anybody
/// deciding to — nine screenshots, a loop that reads the same photo every round — and the provider's answer to an
/// oversized request is a refusal that does not go away, because the history that made it too big stays. So the gate
/// keeps the newest and leaves a sentence where the rest were.
/// </summary>
public sealed class ImageBudgetTests
{
    private static MediaOptions Policy(
        int maxImages = 6,
        long maxBytes = 24L * 1024 * 1024,
        long maxSingle = 8L * 1024 * 1024) => new()
    {
        MaxImagesPerRequest = maxImages,
        MaxImageBytesPerRequest = maxBytes,
        MaxImageBytes = maxSingle
    };

    private static ImagePart Picture(string mediaType = "image/png", int bytes = 1024, int? width = 512, int? height = 512) =>
        new(mediaType, new byte[bytes], width, height);

    private static IMediaPolicy AsPolicy(MediaOptions options) => new PolicyOverride(options);

    // ---- what a picture costs -------------------------------------------------------------------

    [Fact]
    public void A_picture_is_charged_by_area_not_by_the_size_of_its_base64()
    {
        // 1920×1080 is 4×3 tiles of 512: 85 + 12×170.
        Assert.Equal(85 + 12 * 170, TokenEstimator.EstimateImage(1920, 1080));
        // A sticker is one tile.
        Assert.Equal(85 + 170, TokenEstimator.EstimateImage(100, 100));
        // An undeclared size is charged the assumed maximum: "unknown" must never be the cheap case.
        Assert.Equal(1600, TokenEstimator.EstimateImage(null, null));
        Assert.Equal(1600, TokenEstimator.EstimateImage(0, 0));
    }

    [Fact]
    public void A_message_costs_its_text_and_its_picture()
    {
        var text = new ModelMessage("user", null, Parts: [new TextPart("看看这个")]);
        var withPicture = new ModelMessage("user", null, Parts: [new TextPart("看看这个"), Picture(width: 1920, height: 1080)]);

        Assert.Equal(TokenEstimator.Estimate("看看这个") + 4, TokenEstimator.Estimate(text));
        Assert.Equal(TokenEstimator.Estimate(text) + TokenEstimator.EstimateImage(1920, 1080), TokenEstimator.Estimate(withPicture));
    }

    // ---- what a request may carry ---------------------------------------------------------------

    [Fact]
    public void The_newest_pictures_win_and_the_rest_say_so_in_words()
    {
        var messages = new List<ModelMessage>();
        for (var index = 0; index < 8; index++)
            messages.Add(new ModelMessage("user", null, Parts: [new TextPart($"m{index}"), Picture()]));

        var dropped = ImageBudget.Apply(messages, AsPolicy(Policy(maxImages: 6)));

        Assert.Equal(2, dropped);
        // The first two are now text, the last six kept their picture — "what did they just send" is the question a turn
        // is usually answering.
        Assert.DoesNotContain(messages[0].Parts!, part => part is ImagePart);
        Assert.DoesNotContain(messages[1].Parts!, part => part is ImagePart);
        Assert.All(messages.Skip(2), message => Assert.Contains(message.Parts!, part => part is ImagePart));
        // The text of the message is untouched, and the note says there was a picture.
        Assert.Equal("m0", Assert.IsType<TextPart>(messages[0].Parts![0]).Text);
        var note = Assert.IsType<TextPart>(messages[0].Parts![1]).Text;
        Assert.Contains("一张图", note, StringComparison.Ordinal);
        Assert.Contains("fs_read", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_over_its_byte_ceiling_drops_the_oldest_until_it_fits()
    {
        var messages = new List<ModelMessage>
        {
            new("user", null, Parts: [Picture(bytes: 400)]),
            new("user", null, Parts: [Picture(bytes: 400)]),
            new("user", null, Parts: [Picture(bytes: 400)])
        };

        var dropped = ImageBudget.Apply(messages, AsPolicy(Policy(maxBytes: 900)));

        Assert.Equal(1, dropped);
        Assert.DoesNotContain(messages[0].Parts!, part => part is ImagePart);
        Assert.Contains(messages[2].Parts!, part => part is ImagePart);
    }

    [Fact]
    public void One_picture_over_the_single_image_ceiling_is_left_out_on_its_own()
    {
        var messages = new List<ModelMessage>
        {
            new("user", null, Parts: [Picture(bytes: 500)]),
            new("user", null, Parts: [Picture(bytes: 5000)])
        };

        var dropped = ImageBudget.Apply(messages, AsPolicy(Policy(maxSingle: 1000)));

        Assert.Equal(1, dropped);
        Assert.Contains(messages[0].Parts!, part => part is ImagePart);
        Assert.DoesNotContain(messages[1].Parts!, part => part is ImagePart);
    }

    [Fact]
    public void A_format_the_policy_does_not_allow_never_reaches_the_request()
    {
        var messages = new List<ModelMessage> { new("user", null, Parts: [Picture(mediaType: "image/tiff")]) };

        Assert.Equal(1, ImageBudget.Apply(messages, AsPolicy(Policy())));
        Assert.DoesNotContain(messages[0].Parts!, part => part is ImagePart);
    }

    [Fact]
    public void A_text_only_request_is_left_exactly_as_it_was()
    {
        var messages = new List<ModelMessage> { new("user", "只说话"), new("tool", "结果", ToolCallId: "c1", Name: "fs_read") };

        Assert.Equal(0, ImageBudget.Apply(messages, AsPolicy(Policy(maxImages: 0))));
        Assert.Null(messages[0].Parts);
        Assert.Equal("只说话", messages[0].Content);
        Assert.Null(messages[1].Parts);
    }

    [Fact]
    public void Order_inside_a_message_is_preserved_when_something_is_dropped()
    {
        var messages = new List<ModelMessage>
        {
            new("user", null, Parts: [Picture(), new TextPart("中间"), Picture()])
        };

        Assert.Equal(2, ImageBudget.Apply(messages, AsPolicy(Policy(maxImages: 0))));

        var parts = messages[0].Parts!;
        Assert.Equal(3, parts.Count);
        Assert.Equal("中间", Assert.IsType<TextPart>(parts[1]).Text);
    }

    /// <summary>A policy with one field changed, so each test says only what it is about.</summary>
    private sealed class PolicyOverride(MediaOptions options) : IMediaPolicy
    {
        public MediaViewPolicy AutoView => options.AutoView;
        public long MaxImageBytes => options.MaxImageBytes;
        public int MaxAutoViewsPerTurn => options.MaxAutoViewsPerTurn;
        public int MaxImagesPerRequest => options.MaxImagesPerRequest;
        public long MaxImageBytesPerRequest => options.MaxImageBytesPerRequest;
        public bool Allows(string mediaType) => MediaPolicy.Allows(options, mediaType);
        public MediaView Decide(int index, string mediaType, long bytes, int alreadyShown) =>
            MediaPolicy.Decide(options, index, mediaType, bytes, alreadyShown);
    }
}
