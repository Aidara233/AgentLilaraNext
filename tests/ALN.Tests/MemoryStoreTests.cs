using ALN.Contracts;
using ALN.Contracts.Memory;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// The memory table: what a Session writes down so the next Session does not start from nothing.
///
/// What these pin is the part that is easy to get wrong and hard to notice: a fragment and a note are different
/// things, a scope is not an address (it survives every Session that serves it), an unmatched query is empty
/// rather than "the newest rows, relabelled", and a conclusion keeps the text it was drawn from.
/// </summary>
public sealed class MemoryStoreTests : IDisposable
{
    private const string Scope = "adapter.qq/chat-42";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-memory-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SqliteStore OpenStore()
    {
        var store = new SqliteStore(Path.Combine(_root, "aln.db"));
        _stores.Add(store);
        return store;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_fragment_round_trips_with_its_evidence_and_its_scope()
    {
        var store = OpenStore();
        var written = await store.AddAsync(new MemoryDraft(
            Scope, MemoryKind.Fragment, "部署改到周五了",
            Evidence: "他说：那就周五吧，周四是客户冻结。", Source: "s-abc", At: Now), default);

        Assert.True(written.Ok);
        Assert.StartsWith("mem-", written.Value!.Id);

        var read = await store.ReadEntryAsync(Scope, written.Value.Id, default);
        Assert.Equal("部署改到周五了", read.Value!.Text);
        Assert.Equal("他说：那就周五吧，周四是客户冻结。", read.Value.Evidence);
        Assert.Equal("s-abc", read.Value.Source);
        Assert.Equal(MemoryKind.Fragment, read.Value.Kind);
        Assert.Equal(Now, read.Value.At);

        // A different conversation is a different memory even when the id is right.
        Assert.Null((await store.ReadEntryAsync("adapter.qq/chat-99", written.Value.Id, default)).Value);

        // Absent is not an error — the caller reads Error, not an exception.
        var missing = await store.ReadEntryAsync(Scope, "mem-nope", default);
        Assert.False(missing.Ok);
        Assert.Null(missing.Error);
        Assert.Null(missing.Value);
    }

    [Fact]
    public async Task Memory_survives_the_process_that_wrote_it()
    {
        var first = OpenStore();
        Assert.True((await first.AddAsync(new MemoryDraft(Scope, MemoryKind.Note, "周五部署", At: Now), default)).Ok);
        first.Dispose();

        var second = OpenStore();
        var counts = await second.CountEntriesAsync(Scope, default);
        Assert.Equal(1, counts.Value!.Notes);
        Assert.Equal(1, counts.Value.Total);
        Assert.Single((await second.RecentAsync(Scope, 20, default)).Value!);
    }

    [Fact]
    public async Task Search_returns_what_matches_and_nothing_that_does_not()
    {
        var store = OpenStore();
        await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, "部署改到周五了", At: Now.AddDays(-1)), default);
        await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, "他喜欢喝美式", At: Now), default);

        var hits = (await store.SearchAsync(Scope, "部署", 20, Now, default)).Value!;
        Assert.Single(hits);
        Assert.Equal("部署改到周五了", hits[0].Record.Text);

        // Chinese has no spaces for a tokenizer to cut on, so a two-character bigram is what makes "周五部署"
        // reach a row that says the two words in the other order.
        Assert.Single((await store.SearchAsync(Scope, "周五部署", 20, Now, default)).Value!);

        // Nothing matched: an empty answer, not the newest row wearing the query's label.
        Assert.Empty((await store.SearchAsync(Scope, "薪水", 20, Now, default)).Value!);
    }

    [Fact]
    public async Task An_empty_query_is_what_is_recent()
    {
        var store = OpenStore();
        await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, "旧的", At: Now.AddDays(-10)), default);
        await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, "新的", At: Now), default);

        var hits = (await store.SearchAsync(Scope, "", 20, Now, default)).Value!;
        Assert.Equal(2, hits.Count);
        Assert.Equal("新的", hits[0].Record.Text);
    }

    [Fact]
    public async Task A_note_beats_an_equally_relevant_fragment_and_an_older_row_loses_to_a_newer_one()
    {
        var store = OpenStore();
        await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, "客户周四冻结", At: Now.AddDays(-3)), default);
        await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Note, "客户周四冻结，所以部署挪到周五", At: Now.AddDays(-3)), default);
        await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, "客户周四冻结（旧消息）", At: Now.AddDays(-120)), default);

        var hits = (await store.SearchAsync(Scope, "客户冻结", 20, Now, default)).Value!;
        Assert.Equal(3, hits.Count);
        Assert.Equal(MemoryKind.Note, hits[0].Record.Kind);   // the conclusion first
        Assert.Equal(Now.AddDays(-120), hits[2].Record.At);    // the stale one last, but still findable

        Assert.True(hits[0].Score > hits[1].Score);
        Assert.True(hits[1].Score > hits[2].Score);
    }

    [Fact]
    public async Task Limit_caps_the_answer_without_changing_its_order()
    {
        var store = OpenStore();
        for (var i = 0; i < 5; i++)
            await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, $"部署第{i}次", At: Now.AddHours(i)), default);

        var two = (await store.SearchAsync(Scope, "部署", 2, Now.AddDays(1), default)).Value!;
        Assert.Equal(2, two.Count);
        Assert.Equal("部署第4次", two[0].Record.Text);
        Assert.Equal(20, IMemoryStore.DefaultLimit);   // the documented default a recall returns
    }

    [Fact]
    public async Task Forgetting_twice_is_fine_and_counting_separates_the_two_kinds()
    {
        var store = OpenStore();
        var fragment = (await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, "碎片", At: Now), default)).Value!;
        await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Note, "条目", At: Now), default);

        var counts = (await store.CountEntriesAsync(Scope, default)).Value!;
        Assert.Equal(1, counts.Fragments);
        Assert.Equal(1, counts.Notes);

        Assert.True((await store.ForgetAsync(Scope, fragment.Id, default)).Ok);
        Assert.True((await store.ForgetAsync(Scope, fragment.Id, default)).Ok);
        Assert.Equal(0, (await store.CountEntriesAsync(Scope, default)).Value!.Fragments);
    }

    [Fact]
    public async Task Nonsense_is_refused_rather_than_stored()
    {
        var store = OpenStore();
        Assert.Equal(ErrorCode.Invalid, (await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, "  "), default)).Error!.Code);
        Assert.Equal(ErrorCode.Invalid, (await store.AddAsync(new MemoryDraft("", MemoryKind.Fragment, "x"), default)).Error!.Code);
        Assert.Equal(ErrorCode.Invalid,
            (await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, new string('x', IMemoryStore.MaxTextLength + 1)), default)).Error!.Code);
        Assert.Equal(ErrorCode.Invalid,
            (await store.AddAsync(new MemoryDraft(Scope, (MemoryKind)7, "x"), default)).Error!.Code);

        // Blank evidence and blank source are "nobody claimed it", not empty strings in the table.
        var bare = (await store.AddAsync(new MemoryDraft(Scope, MemoryKind.Fragment, "有内容", Evidence: " ", Source: ""), default)).Value!;
        Assert.Null(bare.Evidence);
        Assert.Null(bare.Source);
    }
}

/// <summary>
/// The ranking rule itself, without a database. It lives in contracts so that every store agrees on the order;
/// these pin the two decisions that order rests on.
/// </summary>
public sealed class MemoryRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_cjk_run_becomes_a_term_and_its_bigrams()
    {
        var terms = MemoryRanking.Terms("部署时间 deploy!");
        Assert.Contains("部署时间", terms);
        Assert.Contains("部署", terms);
        Assert.Contains("时间", terms);
        Assert.Contains("deploy", terms);
        Assert.DoesNotContain("d", terms);           // a single latin letter matches everything
        Assert.Empty(MemoryRanking.Terms("   "));
    }

    [Fact]
    public void Coverage_is_the_share_of_the_query_that_matched()
    {
        var terms = MemoryRanking.Terms("部署 周五");
        Assert.Equal(1.0, MemoryRanking.Coverage("部署挪到周五", terms), 3);
        Assert.Equal(0.5, MemoryRanking.Coverage("部署挪到下周一", terms), 3);
        Assert.Equal(0, MemoryRanking.Coverage("他喜欢喝美式", terms));
    }

    [Fact]
    public void Decay_halves_over_one_half_life_and_never_reaches_zero()
    {
        Assert.Equal(1.0, MemoryRanking.Decay(Now, Now), 6);
        Assert.Equal(0.5, MemoryRanking.Decay(Now.AddDays(-MemoryRanking.HalfLifeDays), Now), 6);
        Assert.True(MemoryRanking.Decay(Now.AddDays(-3650), Now) > 0);
        Assert.Equal(1.0, MemoryRanking.Decay(Now.AddDays(3), Now), 6);   // a clock that ran backwards is not a penalty
    }

    [Fact]
    public void Evidence_counts_as_part_of_what_a_row_says()
    {
        // The conclusion mentions "冻结" but the evidence is where "周四" is; a query for it must still land.
        var note = new MemoryRecord("mem-1", "s", MemoryKind.Note, "部署挪到周五", "客户周四冻结", null, Now);
        var score = MemoryRanking.Score(note, MemoryRanking.Terms("周四"), Now);
        Assert.True(score > 0);
    }
}
