using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Identity;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// Who is who (docs/31): one directory per person, read without a lock, and the three places the answer shows up —
/// the name on a message, the roster in the round context, and the lookup a model can make.
///
/// The tests that matter most are the refusals. An identity table that quietly accepts a contradictory card is worse
/// than one that has no card at all: it does not fail, it just starts believing the wrong thing about who spoke, and
/// nothing downstream can tell. So a broken file, an account claimed twice and a second "self" are each pinned here.
/// </summary>
public sealed class IdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-identity-" + Guid.NewGuid().ToString("N"));
    private static readonly Address Channel = new("channel", "conversation", "my-group");
    private static readonly Address Adapter = new("adapter", "qq", "main");
    private static readonly Address Backup = new("adapter", "qq", "backup");

    private readonly List<SqliteStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- reading one directory -------------------------------------------------------------------

    [Fact]
    public async Task A_person_is_one_directory_and_answers_by_every_account_he_declared()
    {
        Write("xiaolin", """
        {"name":"小林","note":"我妹妹。","level":"operator",
         "accounts":[{"adapter":"adapter.qq#main","account":"1234567"},
                     {"adapter":"adapter.qq#main","account":"7654321"}]}
        """);
        var people = await Load();

        var found = people.Find(Adapter, "1234567");
        Assert.NotNull(found);
        Assert.Equal("xiaolin", found!.Id);
        Assert.Equal("小林", found.Name);
        Assert.Equal(AccessLevel.Operator, found.Level);
        // A second number on the same adapter is the same person — that is what a 小号 is (docs/31 §2).
        Assert.Equal("xiaolin", people.Find(Adapter, "7654321")!.Id);
        Assert.Null(people.Find(Backup, "1234567"));
    }

    [Fact]
    public async Task One_person_can_arrive_through_two_adapters()
    {
        Write("xiaolin", """
        {"name":"小林","accounts":[{"adapter":"adapter.qq#main","account":"1234567"},
                                   {"adapter":"adapter.qq#backup","account":"1234567"}]}
        """);
        var people = await Load();

        Assert.Equal("xiaolin", people.Find(Adapter, "1234567")!.Id);
        Assert.Equal("xiaolin", people.Find(Backup, "1234567")!.Id);
    }

    [Fact]
    public async Task A_broken_card_costs_that_person_and_nobody_else()
    {
        Write("good", """{"name":"好的人","accounts":[{"adapter":"adapter.qq#main","account":"1"}]}""");
        Write("broken", """{"name":"坏了","accounts":[{"adapter":"adapter.qq#main","account":"2"}""");
        var (people, report) = await LoadWithReport();

        Assert.Single(report.Rejected);
        Assert.Contains("broken", report.Rejected[0].Path, StringComparison.Ordinal);
        Assert.NotNull(people.Find(Adapter, "1"));
        Assert.Null(people.Find(Adapter, "2"));
    }

    [Fact]
    public async Task An_account_belongs_to_one_person_and_a_clash_takes_the_whole_card()
    {
        // Sorted by directory name, so "a" is read first and "b" is the one refused.
        Write("a", """
        {"name":"甲","accounts":[{"adapter":"adapter.qq#main","account":"111"},
                                 {"adapter":"adapter.qq#main","account":"222"}]}
        """);
        Write("b", """
        {"name":"乙","accounts":[{"adapter":"adapter.qq#main","account":"333"},
                                 {"adapter":"adapter.qq#main","account":"111"}]}
        """);
        var (people, report) = await LoadWithReport();

        Assert.Single(report.Rejected);
        Assert.Contains("already a's", report.Rejected[0].Reason, StringComparison.Ordinal);
        // The clash was on the second account; the first must not be claimed either, because a person is one unit.
        Assert.Null(people.Find(Adapter, "333"));
        Assert.Equal("a", people.Find(Adapter, "111")!.Id);
        Assert.Equal("a", people.Find(Adapter, "222")!.Id);
    }

    [Fact]
    public async Task Only_one_group_of_accounts_can_be_mine()
    {
        Write("a", """{"name":"我","self":true,"accounts":[{"adapter":"adapter.qq#main","account":"111"}]}""");
        Write("b", """{"name":"也是我","self":true,"accounts":[{"adapter":"adapter.qq#main","account":"222"}]}""");
        var (people, report) = await LoadWithReport();

        Assert.Single(report.Rejected);
        Assert.Equal("a", people.Self!.Id);
        // And an echo from the backup number is still mine: both accounts sit under the one self-person.
        Assert.True(people.IsSelf(Adapter, "111"));
        Assert.False(people.IsSelf(Adapter, "222"));
    }

    [Fact]
    public async Task An_adapter_has_to_be_an_instance_not_a_type()
    {
        Write("xiaolin", """{"name":"小林","accounts":[{"adapter":"adapter.qq","account":"1"}]}""");
        var (people, report) = await LoadWithReport();

        Assert.Single(report.Rejected);
        Assert.Contains("instance address", report.Rejected[0].Reason, StringComparison.Ordinal);
        Assert.Empty(people.List());
    }

    [Fact]
    public async Task A_card_with_no_account_cannot_be_recognised()
    {
        Write("nobody", """{"name":"没有账号"}""");
        var (people, report) = await LoadWithReport();

        Assert.Single(report.Rejected);
        Assert.Contains("at least one account", report.Rejected[0].Reason, StringComparison.Ordinal);
        Assert.Empty(people.List());
    }

    [Fact]
    public async Task A_stranger_is_an_answer_not_a_failure()
    {
        Write("xiaolin", """{"name":"小林","accounts":[{"adapter":"adapter.qq#main","account":"1"}]}""");
        var people = await Load();

        Assert.Null(people.Find(Adapter, "999"));
        Assert.Null(people.Find(Adapter, null));
        // Not vacuous: the table is there, it simply does not know that number.
        Assert.Single(people.List());
    }

    [Fact]
    public async Task No_folder_means_nobody_is_recognised_rather_than_an_error()
    {
        var people = new PersonDirectory(_root);
        var report = await people.ReloadAsync(default);

        Assert.Empty(report.People);
        Assert.Empty(report.Rejected);
        Assert.Null(people.Self);
    }

    [Fact]
    public async Task Renaming_somebody_changes_the_label_and_not_the_id()
    {
        Write("xiaolin", """{"name":"小林","accounts":[{"adapter":"adapter.qq#main","account":"1"}]}""");
        var people = await Load();
        Assert.Equal("小林", people.Find(Adapter, "1")!.Name);

        Write("xiaolin", """{"name":"林妹妹","accounts":[{"adapter":"adapter.qq#main","account":"1"}]}""");
        await people.ReloadAsync(default);

        var renamed = people.Find(Adapter, "1")!;
        Assert.Equal("xiaolin", renamed.Id);
        Assert.Equal("林妹妹", renamed.Name);
        // A person nobody declared any more is gone, not remembered: the folder is the whole truth.
        Assert.Single(people.List());
    }

    // ---- what the conversation shows -------------------------------------------------------------

    [Fact]
    public async Task The_conversation_names_a_registered_person_by_the_name_that_does_not_change()
    {
        Write("xiaolin", """{"name":"小林","accounts":[{"adapter":"adapter.qq#main","account":"1234567"}]}""");
        var people = await Load();

        var store = OpenStore();
        var stored = new StoredMessage("m1", Channel, "group-123456", "m1", "1234567",
            "帮我看看这个", new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),
            // What the platform called him that day. The table says what he is called here.
            SenderName: "🔥龙傲天🔥", SenderRemark: "群名片", Person: "xiaolin");
        Assert.True((await store.AppendAsync(stored, default)).Ok);

        var source = new ChannelDeltaSource(new FileChannelDeltaSource(store, store), new StubChannels(), people);
        var collected = await source.CollectAsync(Request(), default);

        Assert.True(collected.Ok, collected.Error?.Detail);
        var entry = Assert.Single(collected.Value!.Entries);
        Assert.Equal("小林(群名片)", entry.Sender);
        // The person travels with the entry: the turn writes it into the timeline and remembers having met him.
        Assert.Equal("xiaolin", entry.Person);
    }

    [Fact]
    public async Task A_stranger_keeps_the_naming_the_platform_gave_him()
    {
        Write("xiaolin", """{"name":"小林","accounts":[{"adapter":"adapter.qq#main","account":"1234567"}]}""");
        var people = await Load();

        var store = OpenStore();
        Assert.True((await store.AppendAsync(new StoredMessage("m1", Channel, "group-123456", "m1", "999",
                "路过", new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero), SenderName: "路人丙",
                Person: null), default)).Ok);

        var source = new ChannelDeltaSource(new FileChannelDeltaSource(store, store), new StubChannels(), people);
        var collected = await source.CollectAsync(Request(), default);

        var entry = Assert.Single(collected.Value!.Entries);
        Assert.Equal("路人丙", entry.Sender);
        Assert.Null(entry.Person);
    }

    // ---- the roster ------------------------------------------------------------------------------

    [Fact]
    public async Task The_roster_lists_only_the_people_this_conversation_has_met_in_a_stable_order()
    {
        Write("b", """{"name":"阿伟","accounts":[{"adapter":"adapter.qq#main","account":"2"}]}""");
        Write("a", """{"name":"小林","note":"我妹妹。","level":"operator","accounts":[{"adapter":"adapter.qq#main","account":"1"}]}""");
        Write("c", """{"name":"没来过的人","accounts":[{"adapter":"adapter.qq#main","account":"3"}]}""");
        var people = await Load();
        var roster = new PersonRosterSource(people);

        // Met in the order b, a — the roster sorts by id, so two rounds cannot disagree about who is in the room.
        var collected = await roster.CollectAsync(Request(people: ["b", "a"]), default);
        var text = Assert.Single(collected.Value!).Content;

        Assert.Equal("小林（L1 operator）：我妹妹。\n阿伟", text);
        Assert.DoesNotContain("没来过的人", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_roster_says_so_when_it_left_people_out()
    {
        var ids = new List<string>();
        for (var index = 0; index < 14; index++)
        {
            var id = $"p{index:00}";
            Write(id, $$"""{"name":"人{{index}}","accounts":[{"adapter":"adapter.qq#main","account":"{{index}}"}]}""");
            ids.Add(id);
        }
        var people = await Load();
        var roster = new PersonRosterSource(people);

        var text = Assert.Single((await roster.CollectAsync(Request(people: ids), default)).Value!).Content;

        // Twelve lines and a fourteenth: the count is what a model needs in order to know it may ask about more.
        Assert.Contains("还有 2 个", text, StringComparison.Ordinal);
        Assert.DoesNotContain("人13", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_that_has_met_nobody_gets_no_roster_at_all()
    {
        var roster = new PersonRosterSource(await Load());
        var collected = await roster.CollectAsync(Request(people: []), default);

        Assert.True(collected.Ok);
        Assert.Empty(collected.Value!);
    }

    // ---- the lookup a model can make -------------------------------------------------------------

    [Fact]
    public async Task Whois_finds_a_person_by_the_name_the_model_was_shown()
    {
        Write("xiaolin", """{"name":"小林","note":"我妹妹。可以帮她查资料。","level":"operator","accounts":[{"adapter":"adapter.qq#main","account":"1234567"}]}""");
        var people = await Load();
        var tools = new IdentityTools(people);

        var offers = await tools.ToolsAsync(Request(), default);
        var tool = Assert.Single(offers.Value!);
        var answer = await tool.Tool.ExecuteAsync(new ModelToolCall("call-1", "whois", """{"name":"小林"}"""), default);

        Assert.True(answer.Ok, answer.Error?.Detail);
        var text = answer.Value!.Content;
        Assert.Contains("小林", text, StringComparison.Ordinal);
        Assert.Contains("L1 operator", text, StringComparison.Ordinal);
        Assert.Contains("我妹妹", text, StringComparison.Ordinal);
        Assert.Contains("adapter.qq#main", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whois_says_who_it_does_know_when_it_does_not_know_this_one()
    {
        Write("xiaolin", """{"name":"小林","accounts":[{"adapter":"adapter.qq#main","account":"1234567"}]}""");
        var people = await Load();
        var tools = new IdentityTools(people);
        var tool = Assert.Single((await tools.ToolsAsync(Request(), default)).Value!).Tool;

        var answer = await tool.ExecuteAsync(new ModelToolCall("call-1", "whois", """{"name":"陌生人"}"""), default);

        Assert.True(answer.Ok, answer.Error?.Detail);
        // A model that is told "nobody by that name" and nothing else cannot tell a nickname from a stranger.
        Assert.Contains("没有这个人的记录", answer.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("小林", answer.Value!.Content, StringComparison.Ordinal);
    }

    // ---- the action table ------------------------------------------------------------------------

    [Fact]
    public async Task The_action_lists_people_without_their_credentials_or_their_absence()
    {
        Write("xiaolin", """{"name":"小林","level":"operator","accounts":[{"adapter":"adapter.qq#main","account":"1234567"}]}""");
        var people = await Load();
        var plane = new ControlPlane(new NullAudit(), NullSignalLog.Instance);
        Assert.True(IdentityActionSet.Register(plane, people, people).Ok);

        var answer = await plane.InvokeAsync(Actor.System, IdentityActions.List, JsonDocument.Parse("{}").RootElement, default);

        Assert.True(answer.Ok, answer.Error?.Detail);
        var row = Assert.Single(answer.Value.GetProperty("people").EnumerateArray());
        Assert.Equal("xiaolin", row.GetProperty("id").GetString());
        Assert.Equal("operator", row.GetProperty("level").GetString());
        Assert.Equal("1234567", row.GetProperty("accounts")[0].GetProperty("account").GetString());
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private void Write(string id, string json)
    {
        var directory = Path.Combine(_root, PersonDirectory.DirectoryName, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PersonDirectory.InfoFileName), json);
    }

    private async Task<PersonDirectory> Load()
    {
        var people = new PersonDirectory(_root);
        await people.ReloadAsync(default);
        return people;
    }

    private async Task<(PersonDirectory People, PersonDirectoryReport Report)> LoadWithReport()
    {
        var people = new PersonDirectory(_root);
        var report = await people.ReloadAsync(default);
        return (people, report);
    }

    /// <summary>A round request with nothing but what the roster needs: a Session id and who it has met.</summary>
    private static RoundContextRequest Request(IReadOnlyList<string>? people = null) =>
        new("s-1", Guid.NewGuid(), 2, Channel, new ChannelBinding(Adapter, "group-123456"), AccessLevel.Agent, people);

    private SqliteStore OpenStore()
    {
        Directory.CreateDirectory(_root);
        var store = new SqliteStore(Path.Combine(_root, "aln.db"));
        _stores.Add(store);
        return store;
    }

    /// <summary>Just enough of the Channel manager to answer the window question the delta source asks.</summary>
    private sealed class StubChannels : IChannelManager
    {
        public Result<ChannelInfo> Get(Address address) => Result<ChannelInfo>.Success(
            new ChannelInfo(address, "test", [new ChannelBinding(Adapter, "group-123456")], true,
                ChannelTypes.Conversation));

        public IReadOnlyList<ChannelInfo> List() => [];
        public bool IsBound(Address channel, Address adapter, string platformConversationId) => true;
        public Result<ChannelInfo> FindBound(Address adapter, string platformConversationId) =>
            Result<ChannelInfo>.Failure(new(ErrorCode.Invalid, "none"));
        public ValueTask<Result<ChannelInfo>> CreateAsync(ChannelSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result> StartAsync(Address address, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> StopAsync(Address address, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> DestroyAsync(Address channel, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<int>> AcceptAsync(AdapterEnvelope envelope, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Result> CompleteAcceptAsync(Address channel, Guid messageId, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result> RecordReplyAsync(Address channel, Guid answeredMessageId, string platformConversationId,
            string text, string? platformMessageId, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
        public ValueTask<Result<int>> ReplayPendingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result<int>.Success(0));
    }

    private sealed class NullAudit : IAuditSink
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
