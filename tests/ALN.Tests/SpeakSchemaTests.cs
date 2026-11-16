using System.Text.Json;
using ALN.Contracts;
using ALN.Graph.Contracts;
using ALN.Scripting.Jint;

namespace ALN.Tests;

/// <summary>
/// The <c>speak</c> schema a script declares is the whole of what a provider tells the model that tool takes, so a
/// block missing from that JSON is a block no model can emit — however complete the runtime's handling of it is.
///
/// That is not hypothetical. The runtime has understood <c>{"type":"file"}</c> since files were added, with tests on both
/// sides of the adapter; but the scripts that mint the chat sessions declared <c>speak</c> with a required <c>text</c> and
/// <c>additionalProperties: false</c>, and the one shipped schema that had blocks enumerated only
/// <c>text/at/image/face</c>. So "put this file in the group" had no conventional route at all: the model reached for the
/// bridge's own <c>upload_group_file</c> through the raw door instead, which a group-level Session is refused
/// (docs/28 §6.6). The failure was invisible in the tests because they hand the tool its arguments directly and never
/// read the contract the model was given.
///
/// Reading the script source with a string match would pass on a comment. This runs the script for real and reads the
/// schema out of the spec it builds, which is the only version that answers "what does the model actually see".
/// </summary>
public sealed class SpeakSchemaTests
{
    /// <summary>Every script in this tree that hands a Session a <c>speak</c> tool.</summary>
    public static TheoryData<string, string> SpeakScripts() => new()
    {
        // The private/single-conversation policy, and the group policy: these two are what a real deployment runs.
        { "samples/scripts/trusted/channel.chat/main.js", "有话说" },
        { "samples/scripts/trusted/group.mention-only/main.js", "@429615832 有话说" },
        // The builtin probe's /session command: the only speak tool nobody can edit, so it drifts silently.
        { "src/ALN.Host/resources/scripts/builtin/model.smoke/main.js", "/session 有话说" }
    };

    [Theory]
    [MemberData(nameof(SpeakScripts))]
    public async Task A_script_that_offers_speak_offers_every_block_the_runtime_understands(string script, string text)
    {
        var schema = await SpeakSchemaOfAsync(script, text);

        Assert.True(schema.TryGetProperty("properties", out var properties), "the speak schema has no properties");
        Assert.True(properties.TryGetProperty("text", out _));
        // Blocks are the only way to lay a message out — a mention, a picture, a face, a file.
        Assert.True(properties.TryGetProperty("blocks", out var blocks),
            $"{script} declares speak without blocks, so no model can mention anybody or send a picture or a file");
        Assert.Equal("array", blocks.GetProperty("type").GetString());

        var items = blocks.GetProperty("items");
        var kinds = items.GetProperty("properties").GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(kind => kind.GetString()).ToHashSet(StringComparer.Ordinal);
        // What GraphManager.BuildBlocksAsync answers for. A kind missing here is a kind the model is never offered.
        foreach (var kind in new[] { "text", "at", "image", "face", "file" })
            Assert.Contains(kind, kinds);

        // A block names what and where; a file additionally names what the other side should see it as. These are the
        // fields BuildBlocksAsync reads, and a field the schema does not declare is refused by a strict provider.
        var fields = items.GetProperty("properties");
        foreach (var field in new[] { "text", "who", "path", "scope", "name", "id" })
            Assert.True(fields.TryGetProperty(field, out _), $"{script} declares a blocks item without '{field}'");

        // The name of a file is what a receiver reads, so the ceiling on it is stated where the model can see it.
        Assert.Contains("120", fields.GetProperty("name").GetProperty("description").GetString());

        // One message, not an unbounded layout: the runtime refuses past 32 blocks, and a schema that stayed silent
        // would turn that refusal into a surprise.
        Assert.Equal(32, blocks.GetProperty("maxItems").GetInt32());
    }

    [Theory]
    [MemberData(nameof(SpeakScripts))]
    public async Task Speaking_with_blocks_alone_is_allowed_by_the_schema(string script, string text)
    {
        var schema = await SpeakSchemaOfAsync(script, text);

        // "required: [text]" is the shape that made a file-only message impossible: a caller that wanted to send a
        // picture or a file and nothing else had no legal call. The two are alternatives, and the schema has to say so.
        var required = schema.TryGetProperty("required", out var listed)
            ? listed.EnumerateArray().Select(name => name.GetString()).ToArray()
            : [];
        Assert.DoesNotContain("text", required);

        Assert.True(schema.TryGetProperty("anyOf", out var alternatives), "text and blocks are alternatives and the schema must say so");
        Assert.Contains(alternatives.EnumerateArray(),
            alternative => alternative.TryGetProperty("required", out var one) &&
                one.EnumerateArray().Any(name => name.GetString() == "blocks"));
    }

    /// <summary>
    /// Runs one script as a Channel message would, and returns the <c>speak</c> schema out of the spec it handed the
    /// Session. The script's own outcome is irrelevant here — this is a question about the contract, not the turn.
    /// </summary>
    private static async Task<JsonElement> SpeakSchemaOfAsync(string script, string text)
    {
        var api = new CapturingApi();
        var package = new ScriptPackage(
            new ScriptManifest(Path.GetFileName(Path.GetDirectoryName(script)!), 1, "channel", MountMode.Manual,
                [EventNames.ChannelMessageAccepted], "onEvent", [], TrustDomain.Builtin),
            await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), script)));

        var binding = new ChannelBinding(new Address("adapter", "qq", "qq-1"), "group-616565017");
        var messageId = Guid.NewGuid();
        var message = new ChannelMessage(messageId, new Address("channel", "conversation", "chat-1"), binding,
            "m-1", text, DateTimeOffset.UtcNow, Sender: "20000", MentionsSelf: true, SenderRemark: null, SenderName: "小林");
        var routed = new RoutedEvent(EventNames.ChannelMessageAccepted, new Address("channel", "conversation", "chat-1"),
            messageId, JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var run = await new JintScriptExecutor().ExecuteAsync(package, routed.Source, routed, api, default);
        Assert.True(run.Ok, run.Error?.Detail ?? run.Error?.Message);

        var spec = api.SpecJson;
        Assert.False(string.IsNullOrWhiteSpace(spec), $"{script} never handed a Session a spec");

        using var document = JsonDocument.Parse(spec!);
        var tools = document.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        var speak = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == "speak");
        // A schema declared as a string is the shape every provider is given: it is parsed once for validity and then
        // handed over verbatim (SessionManager.RunTurnCoreAsync), so this string is the contract.
        using var parameters = JsonDocument.Parse(speak.GetProperty("parametersJson").GetString()!);
        return parameters.RootElement.Clone();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ALN.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>Keeps the spec and answers as a Session would; nothing else about the world interests this question.</summary>
    private sealed class CapturingApi : IScriptApi
    {
        public string? SpecJson { get; private set; }

        public Task<string> ActivateSessionAsync(string specJson, CancellationToken cancellationToken)
        {
            SpecJson ??= specJson;
            return Task.FromResult("{\"ok\":true}");
        }

        public Task<string> RunSessionAsync(string specJson, string input, CancellationToken cancellationToken)
        {
            SpecJson ??= specJson;
            return Task.FromResult("{\"ok\":true,\"result\":{}}");
        }

        public Task<string> OpenSessionAsync(string options, CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> MessageSessionAsync(string options, CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> CompleteModelAsync(string profileId, string request, CancellationToken cancellationToken) =>
            Task.FromResult("{\"ok\":true,\"events\":[]}");
        public Task<string> SendSelfAsync(string message, CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> AcceptIntoChannelAsync(CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> RouteIntoChannelAsync(string options, CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> ReplyThroughChannelAsync(string message, CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> PollSelfAsync(CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> ReadStateAsync(string key, CancellationToken cancellationToken) => Task.FromResult("");
        public Task SetStateAsync(string key, string json, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteStateAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Log(string level, string message, string? detailJson) { }
        public Task<string> ListActionsAsync(CancellationToken cancellationToken) => Task.FromResult("{}");
        public Task<string> CallActionAsync(string action, string json, CancellationToken cancellationToken) => Task.FromResult("{}");
    }
}
