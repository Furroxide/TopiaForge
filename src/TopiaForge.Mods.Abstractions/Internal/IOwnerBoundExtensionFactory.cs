using System;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// Internal bridge used by framework-owned extension providers to create a facade for the
    /// consuming mod. It is deliberately absent from the public SDK surface.
    /// </summary>
    internal interface IOwnerBoundExtensionFactory
    {
        /// <summary>Creates the contract implementation bound to one authenticated consumer lifetime.</summary>
        object CreateOwnerFacade(Type contractType, string ownerModId, IModLifetime lifetime);
    }
}
