namespace ALN.Contracts;

/// <summary>
/// Where a module is in the five-phase lifecycle (docs/22 §4). The phases are not a progress bar: each one has
/// its own completion rule, and <see cref="Loaded"/> / <see cref="Initialized"/> exist because the difference
/// between "read from disk" and "built its own things" is the difference between "the graph knows the name" and
/// "its edges can be resolved".
/// </summary>
public enum ModulePhase
{
    /// <summary>On disk and parsed. Nothing of it exists yet.</summary>
    Loaded,
    /// <summary>Turned off in its manifest: it stays in the graph, and never reaches Ready.</summary>
    Disabled,
    /// <summary>It built its own things. Its edges cannot be trusted before every instance has reached this.</summary>
    Initialized,
    /// <summary>Its edges are wired and its master has let it start working.</summary>
    Ready,
    Stopping,
    Stopped,
    /// <summary>A lifecycle method returned a failure. The module stays in the graph, but is not working.</summary>
    Failed,
    /// <summary>A lifecycle method threw. Same as failed, except something has to be read to find out why.</summary>
    Error
}

/// <summary>
/// One slave a master declares: which module, for which kinds of call, and whether the master may proceed
/// before it is ready.
///
/// The list lives on the <b>master</b> because the interface belongs to the master (docs/22 §1.2). A slave never
/// says who its master is: if it could, the two files would have to agree forever, and the day they disagree is
/// the day one of them silently loses its calls.
/// </summary>
/// <param name="Kinds">
/// What the master will call it for, named by the master's own vocabulary (e.g. <c>message.accepted</c>).
/// The kernel does not interpret these; it hands them back to the master during Wire.
/// </param>
/// <param name="RequireAll">
/// True (the default) means the master is not ready until this slave is ready. False means "use whatever is up".
/// The default is the strict one on purpose: a master that starts without one of its slaves behaves exactly like
/// a healthy one, which is the failure mode nobody notices.
/// </param>
public sealed record ModuleSlave(ModuleId Id, IReadOnlyList<string> Kinds, bool RequireAll = true);

/// <summary>
/// One module as it exists on disk: <c>modules/&lt;id&gt;/module.json</c> (docs/22 §2.1).
///
/// The kernel understands <see cref="Id"/>, <see cref="Type"/>, <see cref="Enabled"/>, <see cref="Slaves"/> and
/// <see cref="Dependencies"/> and nothing else. Everything a module wants for itself goes in
/// <see cref="ConfigJson"/>, unread and unvalidated: the moment the kernel parses a module's configuration,
/// the kernel owns a copy of it that will never be deleted.
/// </summary>
public sealed record ModuleManifest(
    ModuleId Id,
    string Type,
    bool Enabled = true,
    string? ConfigJson = null,
    IReadOnlyList<ModuleSlave>? Slaves = null,
    IReadOnlyList<ModuleId>? Dependencies = null);

/// <summary>
/// A module as the kernel drives it: four phases and one error hook, all returning a result instead of throwing.
///
/// What the module <b>is</b> comes from the other interfaces it implements (adapter, channel, session, model
/// contracts). This one only says "you can start me, stop me, and tell me when it went wrong".
/// </summary>
public interface IKernelModule
{
    ModuleId Id { get; }

    /// <summary>Build your own things. Do not touch other modules: not every one of them exists yet.</summary>
    ValueTask<Result> InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Your edges are wired. Start working.</summary>
    ValueTask<Result> ReadyAsync(CancellationToken cancellationToken);

    /// <summary>Stop in an orderly way. Callers wait, and give up after a timeout.</summary>
    ValueTask<Result> StopAsync(CancellationToken cancellationToken);

    /// <summary>Stop now. Callers do not wait, and nothing is cleaned up afterwards on your behalf.</summary>
    ValueTask<Result> AbortAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Something you did threw or failed. This is your last chance to say "I know, and I am marking myself dirty"
    /// — not a retry hook, and not a channel for passing the exception up (docs/22 §6).
    /// </summary>
    ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken);
}

/// <summary>What went wrong, in the only shape the kernel can know it: which call, what it said.</summary>
public sealed record ModuleError(string Method, string Message, Exception? Exception = null);

/// <summary>
/// A master that declares slaves: during Wire the kernel asks it to accept each one, and takes the answer.
///
/// This is where "the interface belongs to the master" becomes code. The kernel cannot check that a module
/// implements <c>IChannelSlave</c> — it does not reference the channel contract, and it should not start
/// referencing every module's contract to be able to judge. So the master judges, and a refusal is a bad edge
/// with the master's own reason in it (docs/22 §5).
/// </summary>
public interface IModuleWiring
{
    Result WireSlave(ModuleId slaveId, IReadOnlyList<string> kinds, IKernelModule instance);
}

/// <summary>
/// The names a module will look up while it works. Nothing is ordered by this list — it is an existence claim, checked
/// once before the graph is declared ready, so a missing peer is a startup failure instead of a failure on the first
/// message that happens to need it. A module that finds its peers by name (rather than by being handed them) has to
/// say which names it will ask for.
/// </summary>
public interface IRequireModules
{
    IReadOnlyList<string> Requires { get; }
}

/// <summary>Maps a manifest's <c>type</c> to the code that implements it. Types are declared, never derived.</summary>
public interface IModuleFactoryRegistry
{
    Result Register(string typeId, Func<ModuleManifest, Result<IKernelModule>> factory);

    Result<IKernelModule> Create(ModuleManifest manifest);

    bool IsRegistered(string typeId);

    IReadOnlyList<string> List();
}

/// <summary>Why an edge never became a call.</summary>
public enum BadEdgeReason
{
    /// <summary>The id names nothing on disk.</summary>
    MissingModule,
    /// <summary>It exists but is disabled, so there is nothing to call.</summary>
    DisabledModule,
    /// <summary>The master does not accept slaves at all — it does not implement <see cref="IModuleWiring"/>.</summary>
    MasterDoesNotWire,
    /// <summary>The master looked at the instance and refused it (wrong interface, wrong kind, …).</summary>
    RejectedByMaster,
    /// <summary>The master itself failed before Wire, so there was nobody to ask.</summary>
    MasterFailed
}

/// <summary>
/// One edge that was declared and dropped, with the reason. Reported one line at a time: a count tells a reader
/// that something is wrong, the line tells them what (docs/22 §5.2).
/// </summary>
public sealed record BadEdge(ModuleId Master, ModuleId Slave, BadEdgeReason Reason, string Detail);

/// <summary>A problem found while reading the disk: a directory that is not a module, a broken manifest, a duplicate.</summary>
public sealed record ModuleIssue(string Path, string Message);

/// <summary>One instance as the receipt reports it: what it is, where it got to, and why it stopped.</summary>
public sealed record InstanceInfo(ModuleId Id, string Type, ModulePhase Phase, string? Failure = null);

/// <summary>
/// The graph as it stands right now, for surfaces that cannot see the startup receipt — <c>system.info</c> is the
/// one that matters, because a health check that has to read the log is not a health check (docs/22 §5.2).
///
/// It is a snapshot of the same four numbers the receipt carries, plus the rhythm lines: a runtime addition or
/// removal changes them, and the point of asking later is to see that change.
/// </summary>
public sealed record ModuleGraphStatus(
    int Instances,
    int Ready,
    int Degraded,
    IReadOnlyList<RhythmInfo> Rhythms,
    IReadOnlyList<BadEdge> BadEdges);

/// <summary>
/// One sink that is actually on a schedule, and what pushes it (docs/22 §1.4).
///
/// A trigger is not an edge, so the receipt cannot draw it — but "this adapter looks at its inbox every 200ms, and
/// the clock is what makes it" is the kind of fact that has no other place to live: it is not in a file (nobody
/// declares it), and it changes while the process runs (a script may become the one pushing). So it is reported,
/// next to the edges, as a run-time fact instead of being modelled as a relation.
/// </summary>
public sealed record RhythmInfo(ModuleId Sink, TimeSpan Interval, string Source)
{
    /// <summary>The one line a reader gets: <c>adapter.file#file-1 200ms by Clock</c>.</summary>
    public string Line => $"{Sink} {Interval.TotalMilliseconds:0}ms by {Source}";
}

/// <summary>A strongly connected component with more than one member: modules that can only be ready together.</summary>
public sealed record CycleInfo(IReadOnlyList<ModuleId> Members);

/// <summary>
/// What one startup produced. Four separate sections, never collapsed into a single ok/not-ok (docs/22 §5.2):
/// a system that came up minus two edges is a different system from the one that was written down, and the only
/// way to know which one is running is for the startup to say so.
///
/// <see cref="Rhythms"/> is the fifth: not an edge, not a instance, but the thing that makes a polling module move.
/// </summary>
public sealed record ModuleReceipt(
    IReadOnlyList<InstanceInfo> Instances,
    int WiredEdges,
    IReadOnlyList<BadEdge> BadEdges,
    IReadOnlyList<CycleInfo> Cycles,
    IReadOnlyList<ModuleIssue> Issues,
    IReadOnlyList<RhythmInfo> Rhythms)
{
    public int Ready => Instances.Count(instance => instance.Phase == ModulePhase.Ready);    public int Failed => Instances.Count(instance => instance.Phase is ModulePhase.Failed or ModulePhase.Error);

    /// <summary>Everything that makes this startup different from a clean one, in one number for health checks.</summary>
    public int Degraded => BadEdges.Count + Issues.Count + Failed;

    public bool AllReady => Instances.Count > 0 && Ready == Instances.Count;
}

/// <summary>
/// What adding a instance at runtime produced (docs/22, D1): where the instance got to, the edges that did not become calls,
/// and how many did. A instance that arrives late can be refused for reasons that did not exist at startup — the slave
/// it wanted is gone, the master it needed never got ready — so the answer carries them instead of only a success.
/// </summary>
public sealed record ModuleAddition(InstanceInfo Instance, IReadOnlyList<BadEdge> BadEdges, int WiredEdges = 0);
