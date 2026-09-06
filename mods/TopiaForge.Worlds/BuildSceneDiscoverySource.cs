using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    /// <summary>Discovers and loads gameplay scenes independently of curated checkpoints.</summary>
    public sealed class BuildSceneDiscoverySource : IWorldDiscoverySource
    {
        public Task<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>> DiscoverAsync(IWorldDiscoveryContext context, CancellationToken cancellationToken) =>
            NativeWorldDiscoveryProvider.DiscoverAsync(NativeWorldSource.BuildScenes, context, cancellationToken);
        public Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, CancellationToken cancellationToken) =>
            NativeWorldDiscoveryProvider.LoadAsync(NativeWorldSource.BuildScenes, context, cancellationToken);
    }
}
