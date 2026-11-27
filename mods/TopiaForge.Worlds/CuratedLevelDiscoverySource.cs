using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    /// <summary>Discovers and loads the game's curated checkpoint entry points.</summary>
    public sealed class CuratedLevelDiscoverySource : IWorldDiscoverySource
    {
        public Task<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>> DiscoverAsync(IWorldDiscoveryContext context, CancellationToken cancellationToken) =>
            NativeWorldDiscoveryProvider.DiscoverAsync(NativeWorldSource.CuratedLevels, context, cancellationToken);
        public Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, CancellationToken cancellationToken) =>
            NativeWorldDiscoveryProvider.LoadAsync(NativeWorldSource.CuratedLevels, context, cancellationToken);
    }
}
