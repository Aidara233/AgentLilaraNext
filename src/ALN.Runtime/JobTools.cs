using System.Text;
using ALN.Contracts;
using ALN.Contracts.Models;
using ALN.Contracts.Sessions;

namespace ALN.Runtime;

/// <summary>
/// The job table as a model sees it: two verbs, both of them reads.
///
/// <b>Why there is no kill here.</b> The table records work; it does not own it. Stopping an SSH command means cancelling
/// a process on somebody's machine, and that belongs to the thing that started it — <c>ssh_job kill</c>. A <c>jobs kill</c>
/// that only wrote <see cref="JobState.Killed"/> into a row would leave a command running behind a table that says it
/// stopped: the exact failure mode the deleted capability table had (I-33), a second place claiming to know what
/// somebody else can do. The rule this follows is written down in <c>docs/28 §9.3</c> — a registry that <i>is</i> the
/// state may be read; one that answers for another component may not be built.
///
/// A module for the same reason the schedule and script tools are one: whether a Session may see what this host is still
/// running is answered by the graph, not by this file.
/// </summary>
public sealed class JobTools(IJobRegistry registry) : ISessionToolSource
{
    private readonly IJobRegistry _registry = registry;

    public const string TypeId = "jobs.control";
    public const string SourceId = "system.job-tools";
    public const string ToolName = "jobs";

    public string Id => SourceId;

    public static readonly ModelToolDefinition JobDefinition = new(ToolName,
        "Background work this conversation started: everything that was too long for the turn that asked for it (today the "
        + "SSH commands you started with ssh_run{\"wait\":false}). Read it instead of guessing whether something is still "
        + "running, how far it got, and how it ended — and read it after a restart, which is the one moment work can go "
        + "missing without an answer. It cannot stop anything: stopping is the tool that started it (ssh_job kill).",
        """
        {"type":"object","properties":{
          "action":{"type":"string","enum":["list","get"],
            "description":"list = every row of this conversation, oldest first. get = one row in full. Default list."},
          "id":{"type":"string","description":"With get: the id the tool that started it gave you."}},
         "additionalProperties":false}
        """);

    public ValueTask<Result<IReadOnlyList<SessionToolOffer>>> ToolsAsync(RoundContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionToolOffer> offers =
        [
            // Reading a local table: nothing leaves the process, so a turn that only did this has not spoken to anybody.
            new(new JobTool(this, request.SessionId), JobDefinition, ActsExternally: false)
        ];
        return ValueTask.FromResult(Result<IReadOnlyList<SessionToolOffer>>.Success(offers));
    }

    private sealed class JobTool(JobTools owner, string sessionId) : ISessionTool
    {
        public string Name => ToolName;
        public bool EndsTurn => false;

        public async ValueTask<Result<SessionToolResult>> ExecuteAsync(ModelToolCall call, CancellationToken cancellationToken)
        {
            var root = ToolArguments.Parse(call.ArgumentsJson);
            if (root is null) return ToolArguments.BadJson(ToolName);
            var action = (ToolArguments.Text(root.Value, "action")?.Trim().ToLowerInvariant()) ?? "list";

            switch (action)
            {
                case "list":
                {
                    var listed = await owner._registry.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    if (!listed.Ok || listed.Value is null)
                        return Result<SessionToolResult>.Failure(listed.Error ??
                            new(ErrorCode.Unavailable, "后台任务表读不出来。"));
                    if (listed.Value.Count == 0)
                        return ToolArguments.Ok("这个会话没有后台任务。" +
                            "长到一轮做不完的活会出现在这里：ssh_run{\"wait\":false} 起的那条命令，以后的下载也是。");

                    var text = new StringBuilder();
                    text.Append(listed.Value.Count).Append(" 个后台任务，最早的在最前：");
                    // Oldest first, like the table itself: "what was I waiting on" reads in the order it was asked for.
                    foreach (var job in listed.Value) text.Append('\n').Append(Line(job, withLabel: true));
                    text.Append("\n用 jobs{\"action\":\"get\",\"id\":…} 看一条的全文；要停掉还在跑的，用它自己的工具" +
                                "（SSH 的是 ssh_job{\"action\":\"kill\",\"id\":…}）。");
                    return ToolArguments.Ok(text.ToString());
                }
                case "get":
                {
                    var id = ToolArguments.Text(root.Value, "id")?.Trim();
                    if (string.IsNullOrWhiteSpace(id))
                        return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                            "get 需要 \"id\"：用 jobs{\"action\":\"list\"} 看现有的 id。"));
                    var found = await owner._registry.GetAsync(sessionId, id, cancellationToken).ConfigureAwait(false);
                    if (!found.Ok || found.Value is null)
                        return Result<SessionToolResult>.Failure(found.Error ??
                            new(ErrorCode.Invalid, $"没有这个 job：\"{id}\"。"));
                    var job = found.Value;
                    var text = new StringBuilder();
                    text.Append("job ").Append(job.Id).Append("（").Append(job.Kind).Append("）")
                        .Append(State(job.State)).Append('\n');
                    text.Append("它是什么：").Append(job.Label).Append('\n');
                    text.Append("开始：").Append(Stamp(job.StartedAt));
                    if (job.EndedAt is { } ended) text.Append("；结束：").Append(Stamp(ended));
                    text.Append('\n');
                    if (job.Progress is { Length: > 0 } progress) text.Append("到哪了：").Append(progress).Append('\n');
                    if (job.Result is { Length: > 0 } result) text.Append("结果：").Append(result).Append('\n');
                    if (job.Error is { Length: > 0 } error) text.Append("为什么没成：").Append(error).Append('\n');
                    if (job.State == JobState.Running)
                        text.Append("还在跑：这条命令属于某个连接，重启会把它带走，而重启之后的样子你在表里看不到——" +
                                    "它只会是\"被打断\"。要结果，就在这一轮里等，或者重开一次。\n");
                    return ToolArguments.Ok(text.ToString().TrimEnd());
                }
                default:
                    return Result<SessionToolResult>.Failure(new(ErrorCode.Invalid,
                        "\"action\" 只能是 list 或 get。这个工具只读：停掉还在跑的活，要用它自己的工具。"));
            }
        }

        /// <summary>One row on one line. The five states are kept apart on purpose: "failed" and "interrupted" lead to
        /// different next moves, and folding them into "not done" would make the model retry something that never ran.</summary>
        private static string Line(JobSnapshot job, bool withLabel)
        {
            var text = new StringBuilder();
            text.Append("- ").Append(job.Id).Append(' ').Append(State(job.State));
            if (withLabel) text.Append(' ').Append(job.Label);
            if (job.Progress is { Length: > 0 } progress) text.Append("（到 ").Append(progress).Append('）');
            if (job.State == JobState.Failed && job.Error is { Length: > 0 } error)
                text.Append("，失败：").Append(error);
            if (job.State is JobState.Done && job.Result is { Length: > 0 } result)
                text.Append("，结果：").Append(result);
            if (job.EndedAt is { } ended) text.Append(' ').Append(Stamp(ended));
            return text.ToString();
        }

        private static string State(JobState state) => state switch
        {
            JobState.Running => "跑着",
            JobState.Done => "完成",
            JobState.Failed => "失败",
            JobState.Killed => "被停了",
            // The one state that is not an outcome: nobody decided anything, the process that knew is gone.
            _ => "被打断"
        };

        /// <summary>The reader's own clock, in seconds: an id and a UTC stamp is not an answer to "how long ago".</summary>
        private static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
}

/// <summary>The module wrapper: a type id, a factory, and registration into the session's tool sources.</summary>
public sealed class JobToolsModule(ModuleId id, JobTools tools, ISessionExtensionRegistry extensions) : IKernelModule
{
    private bool _registered;

    public ModuleId Id { get; } = id;

    public static Result<IKernelModule> FromManifest(ModuleManifest manifest, IJobRegistry registry,
        ISessionExtensionRegistry extensions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Type, JobTools.TypeId, StringComparison.Ordinal))
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                $"This factory builds {JobTools.TypeId}, not {manifest.Type}."));
        return Result<IKernelModule>.Success(new JobToolsModule(manifest.Id, new JobTools(registry), extensions));
    }

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = extensions.Register(tools);
        if (!registered.Ok) return ValueTask.FromResult(registered);
        _registered = true;
        return ValueTask.FromResult(Result.Success());
    }

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
