using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// Retention for derived artifacts: stored model calls, which are the largest thing on disk and the only thing whose
/// deletion costs nothing. The rules being defended: only day folders go, only in the category named, and a folder whose
/// name is not a date is left exactly where it is.
/// </summary>
public sealed class ArtifactRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-artifacts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Old_day_folders_go_and_recent_ones_stay()
    {
        var store = new FileArtifactStore(_root);
        await WriteDays("model-calls", "20261001", "20261030", "20261104", "20261105");
        await WriteDays("snapshots", "20260101");

        var pruned = await store.PruneAsync("model-calls", keepDays: 7, today: new DateOnly(2026, 11, 6), default);

        Assert.True(pruned.Error is null, pruned.Error?.Message);
        // 10-30 is seven days back, so it is outside the window: "keep 7 days" means today plus six.
        Assert.Equal(2, pruned.Value!.Removed);
        Assert.Equal(2, pruned.Value.Kept);
        Assert.True(pruned.Value.Bytes > 0);
        Assert.False(Directory.Exists(Path.Combine(_root, "model-calls", "20261001")));
        Assert.False(Directory.Exists(Path.Combine(_root, "model-calls", "20261030")));
        Assert.True(Directory.Exists(Path.Combine(_root, "model-calls", "20261104")));
        Assert.True(Directory.Exists(Path.Combine(_root, "model-calls", "20261105")));
        // Another category is somebody else's data.
        Assert.True(Directory.Exists(Path.Combine(_root, "snapshots", "20260101")));
    }

    [Fact]
    public async Task A_folder_whose_name_is_not_a_date_is_left_alone_and_counted()
    {
        var store = new FileArtifactStore(_root);
        await WriteDays("model-calls", "20260101");
        var odd = Path.Combine(_root, "model-calls", "manual-keep");
        Directory.CreateDirectory(odd);
        await File.WriteAllTextAsync(Path.Combine(odd, "x.json"), "{}");

        var pruned = await store.PruneAsync("model-calls", keepDays: 7, today: new DateOnly(2026, 11, 6), default);

        Assert.Equal(1, pruned.Value!.Removed);
        Assert.Equal(1, pruned.Value.Unknown);
        Assert.True(Directory.Exists(odd));
    }

    [Fact]
    public async Task Refusals_and_an_empty_category_are_answered_rather_than_guessed()
    {
        var store = new FileArtifactStore(_root);

        Assert.Equal(ErrorCode.Invalid, (await store.PruneAsync("../etc", 7, new DateOnly(2026, 11, 6), default)).Error!.Code);
        // Keeping zero days is not "keep nothing", it is a mistake: a retention window has to be a window.
        Assert.Equal(ErrorCode.Invalid, (await store.PruneAsync("model-calls", 0, new DateOnly(2026, 11, 6), default)).Error!.Code);
        var empty = await store.PruneAsync("model-calls", 7, new DateOnly(2026, 11, 6), default);
        Assert.True(empty.Ok);
        Assert.Equal(0, empty.Value!.Removed);
    }

    private async Task WriteDays(string category, params string[] days)
    {
        foreach (var day in days)
        {
            var directory = Path.Combine(_root, category, day);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "call.json"), new string('x', 128));
        }
    }
}
