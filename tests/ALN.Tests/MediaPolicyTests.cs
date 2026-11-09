using ALN.Contracts;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// The media policy: what may be shown to a model, and when it is shown unasked.
///
/// The interesting part is not the numbers, it is that every "no" carries a sentence. A model that is not told a
/// picture exists answers as if the message were empty; a model that is told one exists but is not shown it has to be
/// told <b>why</b>, or it will assume it saw it.
/// </summary>
public sealed class MediaPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-mediapolicy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static MediaModule Module(string? config = null, IParameterRegistry? parameters = null)
    {
        var built = MediaModule.FromManifest(
            new ModuleManifest(new ModuleId(MediaModule.TypeId), MediaModule.TypeId, Enabled: true, ConfigJson: config),
            parameters);
        Assert.True(built.Ok, built.Error?.Message);
        return (MediaModule)built.Value!;
    }

    // ---- the file ------------------------------------------------------------------------------

    [Fact]
    public void The_defaults_show_the_first_picture_and_say_so_about_the_rest()
    {
        var parsed = MediaModule.Parse(MediaModule.DefaultConfig, MediaModule.TypeId);
        Assert.True(parsed.Ok, parsed.Error?.Message);
        var options = parsed.Value!;
        Assert.Equal(MediaViewPolicy.First, options.AutoView);
        Assert.Equal(8L * 1024 * 1024, options.MaxImageBytes);
        Assert.Equal(6, options.MaxImagesPerRequest);
        Assert.Equal(3, options.MaxAutoViewsPerTurn);
        Assert.Equal(["image/png", "image/jpeg", "image/gif", "image/webp"], options.Allowed);
    }

    [Theory]
    [InlineData("""{"autoView":"sometimes"}""")]
    [InlineData("""{"allowed":[]}""")]
    [InlineData("""{"maxImageBytes":-1}""")]
    [InlineData("""{"maxImagesPerRequest":"six"}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void A_configuration_that_cannot_be_understood_is_refused_rather_than_defaulted(string config)
    {
        var parsed = MediaModule.Parse(config, MediaModule.TypeId);
        Assert.False(parsed.Ok);
        Assert.Equal(ErrorCode.Invalid, parsed.Error!.Code);
    }

    // ---- the three gates -----------------------------------------------------------------------

    [Fact]
    public void The_first_picture_of_an_arrival_is_shown_and_the_next_is_explained()
    {
        var module = Module("""{"autoView":"first"}""");

        Assert.True(module.Decide(0, "image/png", 1000, alreadyShown: 0).Shown);

        var second = module.Decide(1, "image/png", 1000, alreadyShown: 1);
        Assert.False(second.Shown);
        Assert.Contains("第一张", second.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Off_shows_nothing_and_says_that_it_is_a_choice()
    {
        var view = Module("""{"autoView":"off"}""").Decide(0, "image/png", 100, 0);
        Assert.False(view.Shown);
        Assert.Contains("off", view.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void All_shows_every_picture_until_the_turn_ceiling()
    {
        var module = Module("""{"autoView":"all","maxAutoViewsPerTurn":2}""");

        Assert.True(module.Decide(0, "image/png", 100, 0).Shown);
        Assert.True(module.Decide(1, "image/png", 100, 1).Shown);
        var third = module.Decide(2, "image/png", 100, 2);
        Assert.False(third.Shown);
        Assert.Contains("上限 2", third.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_too_big_or_in_the_wrong_format_says_which_and_how_big()
    {
        var module = Module("""{"maxImageBytes":1048576}""");

        var tooBig = module.Decide(0, "image/png", 5 * 1024 * 1024, 0);
        Assert.False(tooBig.Shown);
        // The numbers are in the sentence: "I cannot see this" without a size reads as a glitch, with one it reads as a limit.
        Assert.Contains("5 MB", tooBig.Reason!, StringComparison.Ordinal);
        Assert.Contains("1 MB", tooBig.Reason!, StringComparison.Ordinal);

        var wrongFormat = module.Decide(0, "image/tiff", 100, 0);
        Assert.False(wrongFormat.Shown);
        Assert.Contains("image/tiff", wrongFormat.Reason!, StringComparison.Ordinal);
    }

    // ---- tuning it while it runs ---------------------------------------------------------------

    [Fact]
    public async Task The_policy_can_be_changed_while_the_process_runs()
    {
        var state = new SqliteStore(Path.Combine(_root, "aln.db"));
        try
        {
            var registry = new ParameterRegistry(state);
            var module = Module(MediaModule.DefaultConfig, registry);
            Assert.True((await module.InitializeAsync(default)).Ok);

            Assert.True(module.Decide(1, "image/png", 100, 1).Shown == false);

            var applied = await registry.SetAsync(MediaModule.TypeId, "autoView", "all", default);
            Assert.True(applied.Ok, applied.Error?.Message);
            // Read straight off the module: the next request sees the new answer without anything being restarted.
            Assert.Equal(MediaViewPolicy.All, module.AutoView);
            Assert.True(module.Decide(1, "image/png", 100, 1).Shown);

            var ceiling = await registry.SetAsync(MediaModule.TypeId, "maxAutoViewsPerTurn", "0", default);
            Assert.True(ceiling.Ok, ceiling.Error?.Message);
            Assert.False(module.Decide(0, "image/png", 100, 0).Shown);
        }
        finally { state.Dispose(); }
    }

    [Theory]
    [InlineData("autoView", "sometimes")]
    [InlineData("maxImageBytes", "-5")]
    [InlineData("allowed", "")]
    public async Task A_value_the_module_cannot_use_is_refused_and_the_old_one_stays(string name, string value)
    {
        var module = Module(MediaModule.DefaultConfig);
        var before = module.ReadParameter(name);
        var applied = await module.ApplyParameterAsync(name, value, default);
        Assert.False(applied.Ok);
        Assert.Equal(ErrorCode.Invalid, applied.Error!.Code);
        Assert.Equal(before, module.ReadParameter(name));
    }

    [Fact]
    public async Task An_unknown_parameter_is_an_error_rather_than_a_silent_no_op()
    {
        var module = Module(MediaModule.DefaultConfig);
        var applied = await module.ApplyParameterAsync("autoViewEverything", "yes", default);
        Assert.False(applied.Ok);
        Assert.Equal(ErrorCode.Invalid, applied.Error!.Code);
    }

    [Fact]
    public async Task Stopping_the_module_gives_the_parameter_scope_back()
    {
        var state = new SqliteStore(Path.Combine(_root, "aln.db"));
        try
        {
            var registry = new ParameterRegistry(state);
            var module = Module(MediaModule.DefaultConfig, registry);
            Assert.True((await module.InitializeAsync(default)).Ok);
            Assert.Contains(registry.List(), view => view.Scope == MediaModule.TypeId);

            Assert.True((await module.StopAsync(default)).Ok);
            Assert.DoesNotContain(registry.List(), view => view.Scope == MediaModule.TypeId);
        }
        finally { state.Dispose(); }
    }
}
