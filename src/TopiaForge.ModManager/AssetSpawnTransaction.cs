using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Owns a native allocation across initialization and its final lifetime handoff.</summary>
    internal static class AssetSpawnTransaction
    {
        internal static TResource CreatePrefab<TAllocation, TResource>(Func<TAllocation> allocate,
            string authoredName, Action<TAllocation, string> setName,
            Func<TAllocation, TResource> initialize, Action<TAllocation> destroy, IModLifetime lifetime)
            where TAllocation : class where TResource : class, IDisposable
            => Create(allocate, allocation =>
            {
                // Unity appends a clone suffix; preserve the authored root name exactly.
                // This runs while the allocation is owned, before publishing its entity.
                setName(allocation, authoredName);
                return initialize(allocation);
            }, destroy, lifetime);

        internal static TResource Create<TAllocation, TResource>(Func<TAllocation> allocate,
            Func<TAllocation, TResource> initialize, Action<TAllocation> destroy, IModLifetime lifetime)
            where TAllocation : class where TResource : class, IDisposable
        {
            var allocation = allocate();
            var transferred = false;
            try
            {
                var resource = initialize(allocation);
                // Track owns cleanup even if it rejects a stopping lifetime.
                transferred = true;
                lifetime.Track(resource);
                return resource;
            }
            catch (Exception initializationFailure)
            {
                if (!transferred)
                {
                    try { destroy(allocation); }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException("Asset initialization and cleanup both failed.",
                            initializationFailure, cleanupFailure);
                    }
                }
                throw;
            }
        }
    }
}
