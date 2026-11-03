using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// Builds the address book the way the host does: modules registered under the names in their contract, and every
/// owner observed so its instances can be found.
///
/// A manager is both (a moduled thing and an owner); a bare owner is only the second, which is why the parameters
/// are untyped rather than <see cref="IModule"/>. Tests assemble managers by hand, which is exactly where a wiring
/// mistake would hide — so the wiring itself is written once, here, instead of being retyped per fixture.
/// </summary>
internal static class ModuleWiring
{
    public static ModuleCatalog With(params object[] parts)
    {
        var catalog = new ModuleCatalog();
        foreach (var part in parts)
        {
            if (part is IModule module)
            {
                var attached = catalog.Attach(module);
                if (!attached.Ok) throw new InvalidOperationException(attached.Error?.Message ?? $"Could not register {module.Id}.");
            }
            if (part is IModuleIndex index)
            {
                var observed = catalog.Observe(index);
                if (!observed.Ok) throw new InvalidOperationException(observed.Error?.Message ?? $"Could not observe {index.Category}.");
            }
        }
        return catalog;
    }
}
