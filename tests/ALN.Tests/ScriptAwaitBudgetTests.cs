using System.Diagnostics;
using ALN.Contracts;
using ALN.Graph.Contracts;
using ALN.Scripting.Jint;

namespace ALN.Tests;

/// <summary>
/// How long a script may sit on a host call.
///
/// A script's whole job is sometimes to wait: channel.chat waits for a Session turn to finish, which takes as long as
/// the model takes. Jint has its own answer to "how long may one await wait" and it is ten seconds — not the manifest's
/// timeout, and not something the manifest could see. The deployment showed what that costs: every reply slower than
/// ten seconds left a failed dispatch behind it ("Script execution failed.") while the turn it started finished anyway.
/// </summary>
public sealed class ScriptAwaitBudgetTests
{
    [Fact]
    public async Task A_wait_longer_than_ten_seconds_is_allowed_when_the_manifest_allows_it()
    {
        // The shape that failed in the deployment: the budget is a minute, the host call takes eleven seconds.
        var result = await Run("async function onEvent(event, ctx) { await ctx.session.activate({}); }",
            timeoutMilliseconds: 60_000, hostDelay: TimeSpan.FromSeconds(11));
        Assert.True(result.Ok, $"{result.Error?.Message} / {result.Error?.Detail}");
    }

    [Fact]
    public async Task The_manifest_timeout_is_what_bounds_the_wait_not_a_hidden_ten_seconds()
    {
        // Half a second written in the manifest has to mean half a second: a script that declares a short budget must
        // not be given ten seconds of grace, and one that declares a long one must not be cut off at ten.
        var started = Stopwatch.GetTimestamp();
        var result = await Run("async function onEvent(event, ctx) { await ctx.session.activate({}); }",
            timeoutMilliseconds: 800, hostDelay: TimeSpan.FromSeconds(30));
        var took = Stopwatch.GetElapsedTime(started);
        Assert.False(result.Ok);
        Assert.True(took < TimeSpan.FromSeconds(5), $"waited {took.TotalMilliseconds:F0}ms, which is the old ten-second floor");
    }

    private static async Task<Result> Run(string source, int timeoutMilliseconds, TimeSpan hostDelay)
    {
        var manifest = new ScriptManifest("probe.await", 1, "channel", MountMode.Manual,
            ["channel.message.accepted"], "onEvent", ["channel.bound-send"], TrustDomain.Trusted,
            TimeoutMilliseconds: timeoutMilliseconds);
        var target = new Address("channel.conversation", "holder", "c-1");
        return await new JintScriptExecutor().ExecuteAsync(new ScriptPackage(manifest, source), target,
            new RoutedEvent("channel.message.accepted", target, Guid.NewGuid(), "{}"),
            new SlowApi(hostDelay), CancellationToken.None);
    }

    /// <summary>A host that answers as slowly as a turn does, and says nothing else.</summary>
    private sealed class SlowApi(TimeSpan delay) : IScriptApi
    {
        public Task<string> RunSessionAsync(string sessionSpecJson, string userInput, CancellationToken cancellationToken) =>
            Task.FromResult("{}");

        public async Task<string> ActivateSessionAsync(string sessionSpecJson, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return "{\"ok\":true}";
        }

        public Task<string> CompleteModelAsync(string profileId, string requestJson, CancellationToken cancellationToken) =>
            Task.FromResult("{}");
        public Task<string> OpenSessionAsync(string optionsJson, CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> MessageSessionAsync(string optionsJson, CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> SendSelfAsync(string outboundMessageJson, CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> AcceptIntoChannelAsync(CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> RouteIntoChannelAsync(string optionsJson, CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> ReplyThroughChannelAsync(string outboundMessageJson, CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> PollSelfAsync(CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> ReadStateAsync(string key, CancellationToken cancellationToken) => Task.FromResult("");
        public Task SetStateAsync(string key, string json, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteStateAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> ListActionsAsync(CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> CallActionAsync(string action, string argumentsJson, CancellationToken cancellationToken) => Task.FromResult("{}");
        public void Log(string level, string message, string? detail) { }
    }
}
