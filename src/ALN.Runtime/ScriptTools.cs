using System.Text;
using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;
using ALN.Graph.Contracts;

namespace ALN.Runtime;

/// <summary>One script as the host sees it: what it is called, where it came from, and whether it may run.</summary>
public sealed record ScriptStatus(string Id, string Library, bool Enabled);

/// <summary>What a reload produced: the set that is now running, and everything that was refused on the way.</summary>
public sealed record ScriptReloadResult(IReadOnlyList<ScriptStatus> Scripts, IReadOnlyList<ScriptIssue> Rejected);

/// <summary>
/// Reloading, in one place.
///
/// The action (<c>scripts.reload</c>) and the tool (<c>scripts_reload</c>) are two ways to ask the same question, and the
/// answer must not depend on which door was used. The semantics live here — read every folder, hand the whole set to the
/// graph, report both kinds of refusal — and both entries only forward.
/// </summary>
public static class ScriptReload
{
    public static async ValueTask<Result<ScriptReloadResult>> RunAsync(IGraphRuntime graph, IGraphScriptCatalog catalog,
        CancellationToken cancellationToken)
    {
        var read = await catalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.Ok || read.Value is null)
            return Result<ScriptReloadResult>.Failure(read.Error ?? new(ErrorCode.Internal, "Script loading failed."));

        var applied = await graph.LoadAsync(read.Value, cancellationToken).ConfigureAwait(false);
        if (!applied.Ok)
            return Result<ScriptReloadResult>.Failure(applied.Error ?? new(ErrorCode.Internal, "The graph refused the catalogue."));

        var scripts = graph.Scripts
            .Select(entry => new ScriptStatus(entry.Package.Manifest.Id, LibraryOf(entry.Package), entry.Enabled))
            .OrderBy(status => status.Id, StringComparer.Ordinal)
            .ToArray();
        // Both kinds of refusal in one list: the file the catalogue could not use, and the script the graph would not
        // take. A caller asking "did my edit take" that saw only the first would read "loaded" on an empty set.
        var rejected = catalog.LastIssues.Concat(graph.RejectedScripts).ToArray();
        return Result<ScriptReloadResult>.Success(new(scripts, rejected));
    }

    /// <summary>The folder a package came from, in the words the model has been taught: bundled, user, draft, plugin:…</summary>
    public static string LibraryOf(ScriptPackage package) =>
        string.IsNullOrWhiteSpace(package.Origin) ? "user" : package.Origin;
}

/// <summary>
/// Script control as tools: what is loaded, and reload it.
///
/// Two tools, and deliberately not a third. Writing a script is a file operation (<c>fs_write</c> in the draft scope) and
/// mounting one is a person's decision (<c>modules.edge.mount</c>); what was missing from the model's side was only the
/// ability to see the result and to ask for the folders to be read again. A tool that mounted would undo the reason the
/// draft folder exists.
///
/// Neither tool speaks to anyone: they change what runs, not what the conversation hears.
/// </summary>
public sealed class ScriptTools(IGraphRuntime graph, IGraphScriptCatalog catalog, ScriptLibrary? library = null,
    ICatalog? modules = null, IControlPlane? plane = null)
    : ISessionToolSource
{
    public const string SourceId = "system.script-tools";
    public const string ListToolName = "scripts_list";
    public const string ReloadToolName = "scripts_reload";
    public const string MountToolName = "scripts_mount";
    public const string UnmountToolName = "scripts_unmount";
    public const string InstancesToolName = "instances_list";
    public const string InstanceToolName = "instances_show";

    private readonly IGraphRuntime _graph = graph;
    private readonly IGraphScriptCatalog _catalog = catalog;
    private readonly ScriptLibrary? _library = library;
    private readonly ICatalog? _modules = modules;
    private readonly IControlPlane? _plane = plane;

    public string Id => SourceId;

    public static readonly ModelToolDefinition ListDefinition = new(ListToolName,
        "Every script the host knows about: its id, which folder it came from (bundled, user, draft, plugin:…), whether "
        + "it is enabled, what it answers to, and what was refused and why. Use this after writing a draft in "
        + "scripts/draft: a script that is not in this list was refused, and the reason is in the same answer.",
        """{"type":"object","properties":{},"additionalProperties":false}""");

    public static readonly ModelToolDefinition ReloadDefinition = new(ReloadToolName,
        "Read the script folders again and switch to what they now say. Use it after writing or changing a script in "
        + "scripts/draft. Loading replaces the running set: a script whose new body is refused keeps the body that "
        + "already works, and the answer names what was refused. It does not mount anything: a draft still has to be "
        + "mounted by hand before it runs.",
        """{"type":"object","properties":{},"additionalProperties":false}""");

    public static readonly ModelToolDefinition MountDefinition = new(MountToolName,
        "Mount one script on one instance, so it starts answering that instance's events. Take the id from scripts_list "
        + "and the address from the graph. A script kept in scripts/draft moves to scripts/live when the mount works, and "
        + "moves back when the last mount goes away — the folder is the record of whether it is in effect. This changes "
        + "what that instance does from now on; it puts nothing into the conversation.",
        """
        {"type":"object","properties":{
          "script":{"type":"string","description":"The script id, exactly as scripts_list shows it."},
          "target":{"type":"string","description":"The instance address to mount it on, e.g. channel.conversation#chat-auto."}},
         "required":["script","target"]}
        """);

    public static readonly ModelToolDefinition UnmountDefinition = new(UnmountToolName,
        "Stop one script on one instance. If that was its last mount and it lives in scripts/live, it moves back to "
        + "scripts/draft: the folder keeps saying whether it is in effect.",
        """
        {"type":"object","properties":{
          "script":{"type":"string","description":"The script id."},
          "target":{"type":"string","description":"The instance it was mounted on."}},
         "required":["script","target"]}
        """);

    public static readonly ModelToolDefinition InstancesDefinition = new(InstancesToolName,
        "Every instance the graph knows, with its address — the value scripts_mount takes as 'target'. Addresses are not "
        + "guessable, so look them up instead of inventing them. Each line says which scripts are already mounted on it. "
        + "Filter with 'query' (any part of an address or a type) or 'type' (one type, e.g. 'channel', which also keeps "
        + "'channel.conversation').",
        """
        {"type":"object","properties":{
          "query":{"type":"string","description":"Case-insensitive substring of the address or the type. Optional."},
          "type":{"type":"string","description":"One type, e.g. 'channel' or 'channel.conversation'. Optional."},
          "limit":{"type":"integer","minimum":1,"maximum":200,"description":"How many to show at most. Default 50."}},
         "additionalProperties":false}
        """);

    public static readonly ModelToolDefinition InstanceDefinition = new(InstanceToolName,
        "One instance in full: its type and category, the scripts mounted on it, and — when the instance can describe "
        + "itself — its current state (for a channel: what it is aimed at, whether it is running, its window). Use this "
        + "after changing something to see what actually happened, instead of assuming it worked.",
        """
        {"type":"object","properties":{
          "address":{"type":"string","description":"The instance address, exactly as instances_list shows it."}},
         "required":["address"],"additionalProperties":false}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> offers =
        [
            new(new ListTool(this), ListDefinition, ActsExternally: false),
            new(new ReloadTool(this, request.Level), ReloadDefinition, ActsExternally: false),
            // Mounting does change what a channel answers to from now on, but it says nothing to the conversation: the
            // next message does. Keeping it non-external is what lets a turn mount and then speak in the same step
            // without the reply being taken for the effect. The level travels with it: a Session mounts what it could be,
            // not what somebody else is.
            new(new MountTool(this, request.Level), MountDefinition, ActsExternally: false),
            new(new UnmountTool(this), UnmountDefinition, ActsExternally: false),
            // Reading addresses, for the same reason the mount tool exists: an address cannot be guessed, and the
            // moment a model can mount it needs to see what there is to mount onto.
            new(new InstancesTool(this), InstancesDefinition, ActsExternally: false),
            // And one instance in full: the list says what is there, this says what it is doing — which is the question
            // after a change, and the difference between editing and guessing.
            new(new InstanceTool(this), InstanceDefinition, ActsExternally: false)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private sealed class ListTool(ScriptTools owner) : ISessionTool
    {
        public string Name => ListToolName;
        public bool EndsTurn => false;

        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            var scripts = owner._graph.Scripts;
            if (scripts.Count == 0)
                text.Append("No script is loaded: nothing would answer a message.");
            else
                text.Append(scripts.Count).Append(scripts.Count == 1 ? " script is loaded:" : " scripts are loaded:");

            foreach (var (package, enabled) in scripts.OrderBy(entry => entry.Package.Manifest.Id, StringComparer.Ordinal))
            {
                var manifest = package.Manifest;
                text.Append("\n- ").Append(manifest.Id)
                    .Append(" [").Append(ScriptReload.LibraryOf(package)).Append("] ")
                    .Append(enabled ? "enabled" : "disabled")
                    .Append(", mount ").Append(manifest.Mount == MountMode.Default ? "default" : "manual")
                    .Append(", on ").Append(string.Join(" + ", manifest.Events));
                // The one thing a draft's author needs to hear: being loaded is not being in charge.
                if (string.Equals(ScriptReload.LibraryOf(package), "draft", StringComparison.Ordinal))
                    text.Append(" (draft: it runs only once somebody mounts it)");
            }

            AppendIssues(text, "refused by the graph", owner._graph.RejectedScripts);
            AppendIssues(text, "not usable as a file", owner._catalog.LastIssues);
            return ValueTask.FromResult(Result<SessionToolResult>.Success(new(text.ToString())));
        }

        private static void AppendIssues(StringBuilder text, string heading, IReadOnlyList<ScriptIssue> issues)
        {
            if (issues.Count == 0) return;
            text.Append("\n\n").Append(issues.Count).Append(" ").Append(heading).Append(':');
            foreach (var issue in issues) text.Append("\n- ").Append(issue.Path).Append(": ").Append(issue.Reason);
        }
    }

    private sealed class ReloadTool(ScriptTools owner, AccessLevel level) : ISessionTool
    {
        public string Name => ReloadToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            // Through the plane, not around it. The action and this tool are the same verb, and a verb with two doors at
            // two depths — one audited, one not — is how a table stops being the answer to "who may do this". A host
            // without a plane says so instead of quietly doing the work anyway.
            if (owner._plane is null)
                return Result<SessionToolResult>.Failure(new(ErrorCode.Unavailable, "This host has no control plane."));
            var actor = new Actor(ActionTools.ActorId, ActorKind.Model, ActionTools.ModelCapabilities, level);
            var acted = await owner._plane.InvokeAsync(actor, ScriptActions.Reload,
                JsonSerializer.SerializeToElement(new { }), cancellationToken).ConfigureAwait(false);
            if (!acted.Ok)
            {
                var refused = new StringBuilder();
                refused.Append("error: ").Append(ScriptActions.Reload)
                    .Append("\ncode: ").Append((acted.Error?.Code.ToString() ?? "failed").ToLowerInvariant())
                    .Append("\n").Append(acted.Error?.Message ?? "Reload failed.");
                if (!string.IsNullOrWhiteSpace(acted.Error?.Detail)) refused.Append("\n").Append(acted.Error!.Detail);
                return Result<SessionToolResult>.Success(new(refused.ToString()));
            }

            var data = acted.Value;
            var scripts = data.TryGetProperty("scripts", out var scriptRows) && scriptRows.ValueKind == JsonValueKind.Array
                ? scriptRows.EnumerateArray().Select(row => new ScriptStatus(
                    row.GetProperty("id").GetString() ?? "",
                    row.TryGetProperty("library", out var lib) ? lib.GetString() ?? "user" : "user",
                    !row.TryGetProperty("enabled", out var on) || on.ValueKind != JsonValueKind.False)).ToArray()
                : [];
            var rejected = data.TryGetProperty("rejected", out var issues) && issues.ValueKind == JsonValueKind.Array
                ? issues.EnumerateArray().Select(row => new ScriptIssue(
                    row.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "",
                    row.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "")).ToArray()
                : [];

            var text = new StringBuilder();
            text.Append(scripts.Length == 0
                ? "Reloaded. No script is loaded: nothing would answer a message."
                : $"Reloaded. {scripts.Length} script(s) now loaded:");
            foreach (var status in scripts)
                text.Append("\n- ").Append(status.Id).Append(" [").Append(status.Library).Append("] ")
                    .Append(status.Enabled ? "enabled" : "disabled")
                    .Append(string.Equals(status.Library, "draft", StringComparison.Ordinal)
                        ? " (draft: runs only after somebody mounts it)"
                        : "");
            if (rejected.Length > 0)
            {
                text.Append("\n\n").Append(rejected.Length).Append(" refused:");
                foreach (var issue in rejected)
                    text.Append("\n- ").Append(issue.Path).Append(": ").Append(issue.Reason);
            }
            return Result<SessionToolResult>.Success(new(text.ToString()));
        }
    }

    /// <summary>
    /// Mounting, with the folder following the fact rather than standing in for it.
    ///
    /// The mount happens first, so a refusal never leaves a script looking live. Only after it works does the folder
    /// move — and if the body then fails to load from its new place, the move is undone rather than leaving a rule
    /// pointing at something the catalogue no longer holds.
    /// </summary>
    private sealed class MountTool(ScriptTools owner, AccessLevel callerLevel) : ISessionTool
    {
        public string Name => MountToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(MountToolName);
            var script = ToolArguments.Text(arguments, "script")?.Trim();
            if (string.IsNullOrEmpty(script))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "scripts_mount needs a 'script' id."));
            var targetText = ToolArguments.Text(arguments, "target")?.Trim();
            if (string.IsNullOrEmpty(targetText) || !Address.TryParse(targetText, out var target))
                return Result<SessionToolResult>.Failure(
                    new(ErrorCode.Invalid, "scripts_mount needs a 'target' instance address, e.g. channel.conversation#chat-auto."));

            var mounted = await owner._graph.MountAsync(script, target, cancellationToken, callerLevel).ConfigureAwait(false);
            if (!mounted.Ok) return Result<SessionToolResult>.Failure(mounted.Error!);

            var text = new StringBuilder($"Mounted {script} on {target}.");
            if (owner._library?.Promote(script) is { } promoted)
            {
                if (!promoted.Ok)
                {
                    text.Append($" It stays in scripts/draft: {promoted.Error!.Message}");
                }
                else
                {
                    var reloaded = await ScriptReload.RunAsync(owner._graph, owner._catalog, cancellationToken).ConfigureAwait(false);
                    if (!reloaded.Ok || reloaded.Value is null || !reloaded.Value.Scripts.Any(status => status.Id == script))
                    {
                        // It loaded from the draft folder and not from the live one. Put it back where it worked: a
                        // folder that lies about being in effect is worse than a mount that points at a draft.
                        owner._library.Demote(script);
                        await ScriptReload.RunAsync(owner._graph, owner._catalog, cancellationToken).ConfigureAwait(false);
                        return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                            $"{script} could not be loaded from scripts/live, so it was moved back to scripts/draft. "
                            + "The mount is in place and takes effect once the script loads again."));
                    }
                    text.Append(" It moved from scripts/draft to scripts/live: the folder now says it is in effect.");
                }
            }

            text.Append(" It answers the next event on that instance.");
            return Result<SessionToolResult>.Success(new(text.ToString()));
        }
    }

    /// <summary>
    /// Unmounting, and the folder following it back. Only when nothing else mounts the script: one that still answers
    /// somewhere else is still in effect, and moving it to the draft folder would then be a lie in the other direction.
    /// </summary>
    private sealed class UnmountTool(ScriptTools owner) : ISessionTool
    {
        public string Name => UnmountToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            if (ToolArguments.Parse(call.ArgumentsJson) is not { } arguments) return ToolArguments.BadJson(UnmountToolName);
            var script = ToolArguments.Text(arguments, "script")?.Trim();
            if (string.IsNullOrEmpty(script))
                return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid, "scripts_unmount needs a 'script' id."));
            var targetText = ToolArguments.Text(arguments, "target")?.Trim();
            if (string.IsNullOrEmpty(targetText) || !Address.TryParse(targetText, out var target))
                return Result<SessionToolResult>.Failure(
                    new(ErrorCode.Invalid, "scripts_unmount needs the 'target' it was mounted on."));

            var unmounted = await owner._graph.UnmountAsync(script, target, cancellationToken).ConfigureAwait(false);
            if (!unmounted.Ok) return Result<SessionToolResult>.Failure(unmounted.Error!);

            var text = new StringBuilder($"Unmounted {script} from {target}.");
            var stillMounted = owner._graph.Rules.Any(rule => string.Equals(rule.ScriptId, script, StringComparison.Ordinal));
            if (!stillMounted && owner._library?.Demote(script) is { Ok: true })
            {
                await ScriptReload.RunAsync(owner._graph, owner._catalog, cancellationToken).ConfigureAwait(false);
                text.Append(" It moved back to scripts/draft: no mount names it any more.");
            }
            return Result<SessionToolResult>.Success(new(text.ToString()));
        }
    }

    /// <summary>
    /// Listing addresses. This exists because a mount names an address and an address cannot be derived: the model has to
    /// be able to look at what there is. It reports what is already mounted on each one, so "what answers here" is one
    /// question with one answer rather than two lists the caller has to join.
    /// </summary>
    private sealed class InstancesTool(ScriptTools owner) : ISessionTool
    {
        public string Name => InstancesToolName;
        public bool EndsTurn => false;

        private const int DefaultLimit = 50;

        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = ToolArguments.Parse(call.ArgumentsJson);
            var query = arguments is { } root ? ToolArguments.Text(root, "query")?.Trim() : null;
            var type = arguments is { } typed ? ToolArguments.Text(typed, "type")?.Trim() : null;
            var limit = arguments is { } limited
                ? Math.Clamp(ToolArguments.Number(limited, "limit") ?? DefaultLimit, 1, 200)
                : DefaultLimit;

            var mounted = owner._graph.Mounted
                .GroupBy(entry => entry.Target.ToString(), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(entry => entry.Package.Manifest.Id).Order(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);

            var matching = owner._graph.Instances
                .Where(address => address.IsInstance)
                .Where(address => string.IsNullOrWhiteSpace(type)
                                  || address.TypeId == type
                                  || address.TypeId.StartsWith(type + ".", StringComparison.Ordinal))
                .Where(address => string.IsNullOrWhiteSpace(query)
                                  || address.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
                                  || address.TypeId.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(address => address.ToString(), StringComparer.Ordinal)
                .ToArray();

            var text = new StringBuilder();
            if (matching.Length == 0)
            {
                text.Append(string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(type)
                    ? "No instance exists yet: there is nothing to mount a script on."
                    : $"No instance matches{(string.IsNullOrWhiteSpace(type) ? "" : $" type {type}")}"
                      + $"{(string.IsNullOrWhiteSpace(query) ? "" : $" query \"{query}\"")}.");
                return ValueTask.FromResult(Result<SessionToolResult>.Success(new(text.ToString())));
            }

            text.Append(matching.Length <= limit
                ? $"{matching.Length} instance(s):"
                : $"{matching.Length} instance(s), showing the first {limit} — narrow it with query or type:");
            foreach (var address in matching.Take(limit))
            {
                text.Append("\n- ").Append(address).Append(" [").Append(address.TypeId).Append(']');
                if (mounted.TryGetValue(address.ToString(), out var scripts))
                    text.Append(" ← ").Append(string.Join(", ", scripts));
            }
            return ValueTask.FromResult(Result<SessionToolResult>.Success(new(text.ToString())));
        }
    }

    /// <summary>
    /// One instance in full: what it is, what it is doing, what is mounted on it, and — when it can say — its own state.
    ///
    /// The list answers "what is there"; this answers "what is this one doing now", which is the question somebody has
    /// after changing something. The state block is the instance's own: the host does not know what a Channel is aimed
    /// at, and an instance that cannot describe itself says nothing rather than being guessed at.
    /// </summary>
    private sealed class InstanceTool(ScriptTools owner) : ISessionTool
    {
        public string Name => InstanceToolName;
        public bool EndsTurn => false;

        public ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = ToolArguments.Parse(call.ArgumentsJson);
            var text = arguments is { } root ? ToolArguments.Text(root, "address")?.Trim() : null;
            if (string.IsNullOrWhiteSpace(text) || !Address.TryParse(text, out var address) || !address.IsInstance)
                return ValueTask.FromResult(Result<SessionToolResult>.Failure(
                    new(ErrorCode.Invalid, "instances_show requires 'address' as an instance address.")));

            var id = new ModuleId(address.ToString());
            var row = owner._modules?.Describe(id);
            var module = owner._modules?.Find(id);
            if (row is null && module is null)
                return ValueTask.FromResult(Result<SessionToolResult>.Success(new(
                    $"{address}: no such instance. instances_list shows what exists; an address cannot be invented.")));

            var body = new StringBuilder();
            body.Append(address).Append(" [").Append(row?.TypeId ?? address.TypeId).Append(']');
            if (row is not null) body.Append(' ').Append(row.Category).Append(' ').Append(row.Role.ToString().ToLowerInvariant());

            var mounted = owner._graph.Mounted.Where(entry => entry.Target == address).ToArray();
            body.Append("\nscripts: ").Append(mounted.Length == 0
                ? "none — nothing answers this instance's events"
                : string.Join(", ", mounted.Select(entry => entry.Package.Manifest.Id).Order(StringComparer.Ordinal)));
            foreach (var mount in mounted.Where(entry => entry.RuleId is { Length: > 0 }))
                body.Append("\n  rule: ").Append(mount.RuleId);

            if (module is IInstanceSnapshot snapshot)
            {
                body.Append("\nstate: ");
                try { body.Append(snapshot.Snapshot().GetRawText()); }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                { body.Append("(the instance could not describe itself: ").Append(ex.Message).Append(')'); }
            }
            else body.Append("\nstate: this instance does not describe itself.");

            return ValueTask.FromResult(Result<SessionToolResult>.Success(new(body.ToString())));
        }
    }
}

/// <summary>
/// Scripts that offer themselves as tools, offered to Sessions.
///
/// Visible everywhere, deliberately: a tool is part of what a Session can do, and the model's own tool should not need a
/// per-session opt-in to be usable. What keeps that honest is on every line of the description — the script it comes from,
/// the folder, the level — because a tool definition has no "（转交）" position to carry a sender, unlike a message.
///
/// The offer's level is the script's own, so a Session cannot borrow a tool deeper than itself: same rule as mounting, and
/// the reason is the same — a tool runs with the code's reach, not the caller's.
/// </summary>
public sealed class ScriptCallTools(IGraphRuntime graph) : ISessionToolSource
{
    public const string SourceId = "system.script-call-tools";

    public string Id => SourceId;

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Every callable script, offered to every Session it was opened to — the author decides that, not the caller's
        // level. What matters is the level the tool <b>runs</b> at, and that is the maker's (its folder's): a person can
        // use interfaces the model cannot reach to build a tool the model may call, and the model can build a basic one a
        // deeper Session may call. Calling a tool never hands the caller the tool's authority, so there is nothing to be
        // gained by hiding a deep tool from a shallow Session — and everything to lose, because that is the whole way
        // work gets done for someone who cannot do it themselves.
        var offers = graph.ToolScripts
            .Where(package => package.Tool is not null && package.Tool.Access <= request.Level)
            .Select(package => new SessionToolOffer(
                new ScriptCallTool(graph, package),
                new ModelToolDefinition(package.Tool!.Name, Describe(package), package.Tool.ParametersJson),
                // A script tool can act: it has the same ctx an event handler has, so a turn that only called one has
                // not spoken to anybody just because it produced text.
                ActsExternally: true,
                MinLevel: package.Tool!.Access))
            .ToArray();
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    /// <summary>
    /// The definition the model reads, with the provenance no other part of the tool surface gets to carry.
    ///
    /// The level in it is the <b>worker's</b>, not the reader's, and that is the sentence to get right: "this tool reaches
    /// L2 full" tells a shallow caller it just borrowed a person's hands; "this tool is L0" tells a deep Session the
    /// answer it gets came from a shallow author. Either way the reader knows who is acting, which is the only thing the
    /// description is here to establish.
    /// </summary>
    private static string Describe(ScriptPackage package) =>
        $"{package.Tool!.Description}\n\n"
        + $"（这是一个脚本，不是内建工具：id `{package.Manifest.Id}`，来自 {ScriptReload.LibraryOf(package)}，"
        + $"制作者与运行时的等级都是 {AccessLevels.Describe(package.Level)}。它按自己所在目录的权限做事，不按调用它的会话——"
        + "调用它不会给你任何权限，只是请它做件事。"
        + $"可调用者：{AccessLevels.Describe(package.Tool.Access)} 及以上。"
        + "改它要 scripts_reload；改名相当于换一个工具。）";

    private sealed class ScriptCallTool(IGraphRuntime graph, ScriptPackage package) : ISessionTool
    {
        public string Name => package.Tool!.Name;

        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call,
            CancellationToken cancellationToken)
        {
            var result = await graph.InvokeToolAsync(package.Manifest.Id, call.ArgumentsJson, cancellationToken)
                .ConfigureAwait(false);
            if (result.Ok)
                return Result<SessionToolResult>.Success(new SessionToolResult(result.Value ?? "", null));

            // A timeout is not "nothing happened": the call may have reached the host and taken effect before the
            // interpreter stopped. Saying so is the whole difference between a model that checks and one that retries
            // something that already happened — and it is the shape async tools will need anyway.
            var text = result.Error!.Code == ErrorCode.Timeout
                ? $"调用 {Name} 没有等到回音（超时）。它**可能已经做了**，也可能没有：这一路没有留下“完成”的记录，"
                  + "要确认就自己核一下（读一遍状态、看一遍日志），别直接重试。"
                : $"调用 {Name} 失败：{result.Error.Message}"
                  + (string.IsNullOrWhiteSpace(result.Error.Detail) ? "" : $"（{result.Error.Detail}）")
                  + "。这次没有完成，可以改参数再试，或者换一条路。";
            return Result<SessionToolResult>.Success(new SessionToolResult(text, null));
        }
    }
}


/// <summary>
/// The module that offers the script control tools: one instance, one tool source, no configuration of its own.
///
/// It is a module because a model's tools are a module's business everywhere else in this host, and because "who may
/// reload scripts" should be answerable by looking at the graph rather than at the host's source.
/// </summary>
[Module(TypeId, Category = "scripts")]
/// <summary>
/// The script module's two surfaces: the control tools (what is loaded, reload it) and every callable script offered as
/// a tool in its own right. One module owns both because they read the same catalogue and must not disagree about it.
/// </summary>
public sealed class ScriptToolsModule(ModuleId id, ScriptTools tools, ScriptCallTools callTools,
    ISessionExtensionRegistry extensions) : IKernelModule
{
    public const string TypeId = "scripts.control";

    private bool _registered;

    public ModuleId Id { get; } = id;

    public static Result<IKernelModule> FromManifest(ModuleManifest manifest, IGraphRuntime graph, IGraphScriptCatalog catalog,
        ISessionExtensionRegistry extensions, ScriptLibrary? scripts = null, ICatalog? modules = null,
        IControlPlane? plane = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Type, TypeId, StringComparison.Ordinal))
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid, $"This factory builds {TypeId}, not {manifest.Type}."));
        return Result<IKernelModule>.Success(
            new ScriptToolsModule(manifest.Id, new ScriptTools(graph, catalog, scripts, modules, plane),
                new ScriptCallTools(graph), extensions));
    }

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = extensions.Register(tools);
        if (!registered.Ok) return ValueTask.FromResult(registered);
        var callRegistered = extensions.Register(callTools);
        if (!callRegistered.Ok)
        {
            extensions.Unregister(tools.Id);
            return ValueTask.FromResult(callRegistered);
        }
        _registered = true;
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>Nothing to warm up: the tools answer per round, and both of them read what the graph holds right now.</summary>
    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            extensions.Unregister(callTools.Id);
            extensions.Unregister(tools.Id);
            _registered = false;
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}
