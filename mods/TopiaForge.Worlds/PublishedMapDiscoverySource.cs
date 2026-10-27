using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds.Maps;

namespace TopiaForge.Worlds
{
    /// <summary>Discovers saved published maps and loads them through the shared map store.</summary>
    public sealed class PublishedMapDiscoverySource : IWorldDiscoverySource
    {
        public Task<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>> DiscoverAsync(IWorldDiscoveryContext context, CancellationToken cancellationToken)
        {
            try
            {
                if (context.FamilyId != PublishedMapSource.Family || context.MaximumResults < 1 || context.MaximumResults > PublishedMapLibrary.MaximumSources)
                    return DiscoveryFailure(ModErrorCode.InvalidArgument, "The saved map discovery request is invalid.");
                using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Context.Lifetime.StoppingToken);
                var sources = Read(context.Context, stopping.Token);
                var result = sources.Take(context.MaximumResults).Select(source => new DiscoveredWorldDescriptor(source.WorldId,
                    PublishedMapSource.Family, source.Name, "Published map from robotopia.gg; downloaded when you play. Saved copies work offline.")).ToArray();
                stopping.Token.ThrowIfCancellationRequested();
                return Task.FromResult(OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>.Success(Array.AsReadOnly(result)));
            }
            catch (OperationCanceledException) { return DiscoveryFailure(ModErrorCode.Cancelled, "Saved map discovery was cancelled."); }
            catch (InvalidOperationException) { return DiscoveryFailure(ModErrorCode.Unavailable, "Saved map storage is unavailable. Update TopiaForge and restart."); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException || error is ArgumentException)
            { return DiscoveryFailure(ModErrorCode.InvalidArgument, "The saved map list could not be read. Repair it in launcher Setup or with world source."); }
        }
        public async Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, CancellationToken cancellationToken)
        {
            if (context.WorldFamilyId != PublishedMapSource.Family || context.Transition != WorldLoadTransition.SceneReplacement)
                return Failure(ModErrorCode.InvalidArgument, "Choose a saved map with a scene replacement transition.");
            try
            {
                using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Context.Lifetime.StoppingToken);
                var selected = Read(context.Context, stopping.Token).SingleOrDefault(s => string.Equals(s.WorldId, context.WorldId, StringComparison.Ordinal));
                if (selected == null) return Failure(ModErrorCode.NotFound, "This saved map was removed or changed. Refresh the world list and choose it again.");
                if (!context.Context.Extensions.TryGet<IInternalMapWorldService>(out var maps))
                    return Failure(ModErrorCode.Unavailable, "Saved maps need TopiaForge Worlds. Enable it and restart the game.");
                stopping.Token.ThrowIfCancellationRequested();
                return await maps.LoadAsync(context, new InternalMapWorldRequest(selected.Map, selected.Scene, null), stopping.Token);
            }
            catch (OperationCanceledException) { return Failure(ModErrorCode.Cancelled, "Saved map loading was cancelled."); }
            catch (Exception) { return Failure(ModErrorCode.External, "The saved map could not be loaded. Repair its source in launcher Setup and try again."); }
        }
        private static IReadOnlyList<PublishedMapSource> Read(IModContext context, CancellationToken token)
        {
            if (!(context is IInternalMapStorageContext storage)) throw new InvalidOperationException("Map storage is unavailable.");
            return PublishedMapLibrary.Read(storage.PublishedMapLibraryPath, token);
        }
        private static Task<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>> DiscoveryFailure(ModErrorCode code, string message) =>
            Task.FromResult(OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>.Failure(code, message));
        private static OperationResult<IWorldInstance> Failure(ModErrorCode code, string message) => OperationResult<IWorldInstance>.Failure(code, message);
    }
}
