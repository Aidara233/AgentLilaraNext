using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// The graph, seen through the port a manager uses to announce its instances (docs/23 §4 step 1).
///
/// It is a thin adapter on purpose: every decision about what an edge means belongs to the kernel, and this class
/// only translates "the manager holds this instance" into an adopt/release. It also swallows the absent-graph case
/// (a manager used without a kernel, which is how most tests build one) instead of forcing every manager to know
/// whether a graph exists.
/// </summary>
public sealed class InstanceHost(Kernel kernel, ModuleId master) : IInstanceHost
{
    public string Category => master.Value;

    public async ValueTask<Result> AttachAsync(ModuleId masterId, ModuleId instance, string typeId,
        IReadOnlyList<string>? kinds, CancellationToken cancellationToken, ITriggerSink? trigger = null)
    {
        // The manager names itself as the master; a caller that names another one would be claiming somebody
        // else's instance, which is the one thing this port must not allow.
        if (masterId != master)
            return Result.Failure(new(ErrorCode.Denied, $"This host announces instances for {master}, not {masterId}."));

        var adopted = await kernel.AdoptAsync(master, new Instance(instance, trigger), kinds, typeId, cancellationToken)
            .ConfigureAwait(false);
        if (!adopted.Ok) return Result.Failure(adopted.Error!);
        // A refused edge means the master would not take it: the instance still exists, but the graph now says so
        // out loud instead of leaving it invisible.
        if (adopted.Value!.BadEdges.Count > 0)
            return Result.Failure(adopted.Value.BadEdges[0] is var edge
                ? new Error(ErrorCode.Invalid, $"The graph refused the edge: {edge.Detail}")
                : new Error(ErrorCode.Internal, "The graph refused the edge."));
        return Result.Success();
    }

    public async ValueTask<Result> DetachAsync(ModuleId masterId, ModuleId instance, CancellationToken cancellationToken)
    {
        if (masterId != master)
            return Result.Failure(new(ErrorCode.Denied, $"This host announces instances for {master}, not {masterId}."));
        // One edge, nothing else: the instance keeps existing and keeps its other masters.
        return await kernel.ReleaseAsync(master, instance, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result> ForgetAsync(ModuleId masterId, ModuleId instance, CancellationToken cancellationToken)
    {
        if (masterId != master)
            return Result.Failure(new(ErrorCode.Denied, $"This host announces instances for {master}, not {masterId}."));
        // The constructor took it apart, so the graph lets go of it — including the edge it held here.
        var removed = await kernel.RemoveAsync(instance, cancellationToken).ConfigureAwait(false);
        // Absent is what forgetting wanted.
        return removed.Ok || kernel.Find(instance) is null ? Result.Success() : Result.Failure(removed.Error!);
    }
}
