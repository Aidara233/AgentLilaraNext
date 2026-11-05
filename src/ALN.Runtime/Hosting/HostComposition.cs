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
}
