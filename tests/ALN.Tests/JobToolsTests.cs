using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The job table as the model meets it: list and get, and nothing that changes anything.
///
/// The property worth pinning is the one that is easy to add later and wrong to add: <b>no kill</b>. The table remembers
/// work; it does not own it. A verb here that wrote "killed" into a row while the command kept running would be the exact
/// shape this project already deleted once — a component answering for what somebody else can do (I-33, `docs/28 §9.3`).
/// </summary>
public sealed class JobToolsTests
{
    private static async Task<Result<SessionToolResult>> CallAsync(JobTools tools, string arguments, string session = "s-1")
    {
        var offers = (await tools.ToolsAsync(new RoundContextRequest(session, Guid.NewGuid(), 1), default)).Value!;
        var offer = Assert.Single(offers);
        return await offer.Tool.ExecuteAsync(new ModelToolCall("call-1", "jobs", arguments), default);
    }

    [Fact]
    public async Task An_empty_table_says_nothing_is_running_instead_of_showing_a_blank()
    {
        var answered = await CallAsync(new JobTools(new FileJobRegistry()), "{}");

        Assert.True(answered.Ok, answered.Error?.Message);
        Assert.Contains("没有后台任务", answered.Value!.Content, StringComparison.Ordinal);
        // And how something would end up there: an empty list that does not say how to fill it invites "nothing here, so
        // nothing can go wrong".
        Assert.Contains("wait\":false", answered.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_keeps_the_five_endings_apart()
    {
        var registry = new FileJobRegistry();
        var tools = new JobTools(registry);
        var done = await registry.OpenAsync(new("ssh.run", "build.sh", "s-1"), default);
        await registry.CompleteAsync("s-1", done.Value!.Id, "ssh 上那条命令 exit=0（成功）。", default);
        var failed = await registry.OpenAsync(new("ssh.run", "deploy.sh", "s-1"), default);
        await registry.FailAsync("s-1", failed.Value!.Id, "连接断了", default);
        var running = await registry.OpenAsync(new("file.fetch", "群文件 周报.zip", "s-1"), default);
        await registry.ProgressAsync("s-1", running.Value!.Id, 3 * 1024 * 1024, 12 * 1024 * 1024, "bytes", default);

        var listed = await CallAsync(tools, """{"action":"list"}""");

        var text = listed.Value!.Content;
        Assert.Contains("完成", text, StringComparison.Ordinal);
        Assert.Contains("失败：连接断了", text, StringComparison.Ordinal);
        Assert.Contains("跑着", text, StringComparison.Ordinal);
        // Progress is bytes when there is a total: "3 MB / 12 MB" answers "is it worth waiting for", a percentage does not.
        Assert.Contains("3 MB / 12 MB", text, StringComparison.Ordinal);
        // Oldest first, like the table: the order work was asked for is the order it happened in.
        Assert.True(text.IndexOf("build.sh", StringComparison.Ordinal) < text.IndexOf("群文件", StringComparison.Ordinal));
        // And the line that stops a model from asking this tool to stop something.
        Assert.Contains("ssh_job", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_conversations_jobs_are_not_this_conversations_business()
    {
        var registry = new FileJobRegistry();
        await registry.OpenAsync(new("ssh.run", "theirs.sh", "s-2"), default);
        var mine = await registry.OpenAsync(new("ssh.run", "mine.sh", "s-1"), default);

        var listed = await CallAsync(new JobTools(registry), """{"action":"list"}""");
        Assert.Contains("mine.sh", listed.Value!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("theirs.sh", listed.Value.Content, StringComparison.Ordinal);
        // Not even by id: the table is keyed by conversation, and a row id from another one is simply not there.
        var refused = await CallAsync(new JobTools(registry), $$"""{"action":"get","id":"{{mine.Value!.Id}}"}""", "s-2");
        Assert.False(refused.Ok);
    }

    [Fact]
    public async Task Getting_one_row_says_how_it_ended_and_why()
    {
        var registry = new FileJobRegistry();
        var opened = await registry.OpenAsync(new("file.fetch", "群文件 周报.zip", "s-1"), default);
        await registry.ProgressAsync("s-1", opened.Value!.Id, 4096, 8192, "bytes", default);
        await registry.FailAsync("s-1", opened.Value.Id, "平台没给下载地址", default);

        var got = await CallAsync(new JobTools(registry), $$"""{"action":"get","id":"{{opened.Value.Id}}"}""");

        Assert.True(got.Ok, got.Error?.Message);
        Assert.Contains("群文件 周报.zip", got.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("平台没给下载地址", got.Value.Content, StringComparison.Ordinal);
        Assert.Contains("4 KB / 8 KB", got.Value.Content, StringComparison.Ordinal);
        Assert.Contains("结束", got.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_job_says_it_will_not_survive_a_restart()
    {
        // The model's most expensive mistake here is waiting for something that a restart already took away. The row
        // cannot know that yet — the reconcile happens on the next start — so the answer has to say it while it can.
        var registry = new FileJobRegistry();
        var opened = await registry.OpenAsync(new("ssh.run", "make -j8", "s-1"), default);

        var got = await CallAsync(new JobTools(registry),
            $$"""{"action":"get","id":"{{opened.Value!.Id}}"}""");

        Assert.Contains("还在跑", got.Value!.Content, StringComparison.Ordinal);
        Assert.Contains("重启", got.Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_to_stop_something_is_refused_and_points_at_the_tool_that_can()
    {
        var registry = new FileJobRegistry();
        var opened = await registry.OpenAsync(new("ssh.run", "sleep 900", "s-1"), default);

        var refused = await CallAsync(new JobTools(registry),
            $$"""{"action":"kill","id":"{{opened.Value!.Id}}"}""");

        Assert.False(refused.Ok);
        Assert.Contains("只读", refused.Error!.Message, StringComparison.Ordinal);
        // And nothing was written: a row that says "killed" while the command runs is worse than no row at all.
        var row = (await registry.GetAsync("s-1", opened.Value.Id, default)).Value!;
        Assert.Equal(JobState.Running, row.State);
    }

    [Fact]
    public async Task Get_without_an_id_says_what_is_missing()
    {
        var refused = await CallAsync(new JobTools(new FileJobRegistry()), """{"action":"get"}""");

        Assert.False(refused.Ok);
        Assert.Contains("id", refused.Error!.Message, StringComparison.Ordinal);
    }
}
