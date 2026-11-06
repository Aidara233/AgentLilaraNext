using System.Text;
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
public sealed class ScriptTools(IGraphRuntime graph, IGraphScriptCatalog catalog) : ISessionToolSource
{
    public const string SourceId = "system.script-tools";
    public const string ListToolName = "scripts_list";
    public const string ReloadToolName = "scripts_reload";

    private readonly IGraphRuntime _graph = graph;
    private readonly IGraphScriptCatalog _catalog = catalog;

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

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> offers =
        [
            new(new ListTool(this), ListDefinition, ActsExternally: false),
            new(new ReloadTool(this), ReloadDefinition, ActsExternally: false)
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

    private sealed class ReloadTool(ScriptTools owner) : ISessionTool
    {
        public string Name => ReloadToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            var reloaded = await ScriptReload.RunAsync(owner._graph, owner._catalog, cancellationToken).ConfigureAwait(false);
            if (!reloaded.Ok || reloaded.Value is null)
                return Result<SessionToolResult>.Failure(reloaded.Error ?? new(ErrorCode.Internal, "Reload failed."));

            var text = new StringBuilder();
            var result = reloaded.Value;
            text.Append(result.Scripts.Count == 0
                ? "Reloaded. No script is loaded: nothing would answer a message."
                : $"Reloaded. {result.Scripts.Count} script(s) now loaded:");
            foreach (var status in result.Scripts)
                text.Append("\n- ").Append(status.Id).Append(" [").Append(status.Library).Append("] ")
                    .Append(status.Enabled ? "enabled" : "disabled")
                    .Append(string.Equals(status.Library, "draft", StringComparison.Ordinal)
                        ? " (draft: runs only after somebody mounts it)"
                        : "");
            if (result.Rejected.Count > 0)
            {
                text.Append("\n\n").Append(result.Rejected.Count).Append(" refused:");
                foreach (var issue in result.Rejected)
                    text.Append("\n- ").Append(issue.Path).Append(": ").Append(issue.Reason);
            }
            return Result<SessionToolResult>.Success(new(text.ToString()));
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
public sealed class ScriptToolsModule(ModuleId id, ScriptTools tools, ISessionExtensionRegistry extensions) : IKernelModule
{
    public const string TypeId = "scripts.control";

    private bool _registered;

    public ModuleId Id { get; } = id;

    public static Result<IKernelModule> FromManifest(ModuleManifest manifest, IGraphRuntime graph, IGraphScriptCatalog catalog,
        ISessionExtensionRegistry extensions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Type, TypeId, StringComparison.Ordinal))
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid, $"This factory builds {TypeId}, not {manifest.Type}."));
        return Result<IKernelModule>.Success(new ScriptToolsModule(manifest.Id, new ScriptTools(graph, catalog), extensions));
    }

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = extensions.Register(tools);
        if (!registered.Ok) return ValueTask.FromResult(registered);
        _registered = true;
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>Nothing to warm up: the tools answer per round, and both of them read what the graph holds right now.</summary>
    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            extensions.Unregister(tools.Id);
            _registered = false;
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}
