using System;

namespace TopiaForge.Mods.Internal
{
    // Framework-only services that produce session resources must receive the exact consuming scope.
    internal interface IOwnerContextBoundExtensionFactory
    {
        object CreateOwnerFacade(Type contractType, IModContext context);
    }
}
