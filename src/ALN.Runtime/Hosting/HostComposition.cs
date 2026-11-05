using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Runtime.Hosting;

/// <summary>One module the host builds itself: it has no factory, so nothing on disk could produce it.</summary>
public sealed record DeclaredModule(IKernelModule Module, string TypeId, string Label);

public static class HostComposition
{
    /// <summary>
    /// Declares the modules the host builds itself. One table and one loop, because what used to sit here was five
    /// copies of the same four lines — declare, and if it fails print, cancel, complete the queue, return 1 — each
    /// with its own variable and its own message. The table is also the only place that has to change when a module
    /// joins or leaves, which is what makes it movable to a declaration later.
    /// </summary>
    public static Result Declare(Kernel kernel, IReadOnlyList<DeclaredModule> modules)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var module in modules)
        {
            var declared = kernel.Declare(module.Module, typeId: module.TypeId);
            if (!declared.Ok)
                return Result.Failure(new(declared.Error!.Code, $"{module.Label}: {declared.Error.Message}"));
        }
        return Result.Success();
    }

    /// <summary>
    /// Puts the host's managers in the catalog: the ones that are modules are attached, the ones that hold instances
    /// are announced as their owner. Two tables and two loops, because a manager that joins or leaves the host should
    /// be one edit, and every failure should be reported in the same way — all of them, not the first.
    /// </summary>
    public static Result Register(ModuleCatalog catalog, IReadOnlyList<IModule> modules, IReadOnlyList<IModuleIndex> owners)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(owners);
        var failures = new List<string>();
        foreach (var module in modules)
        {
            var attached = catalog.Attach(module);
            if (!attached.Ok) failures.Add(attached.Error?.Message ?? $"Could not register {module.Id}.");
        }
        foreach (var owner in owners)
        {
            var observed = catalog.Observe(owner);
            if (!observed.Ok) failures.Add(observed.Error?.Message ?? $"Could not observe {owner.Category}.");
        }
        return failures.Count == 0 ? Result.Success() : Result.Failure(new(ErrorCode.Invalid, string.Join(Environment.NewLine, failures)));
    }
}
