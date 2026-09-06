using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    internal static class NativeWorldDiscoveryProvider
    {
        public static async Task<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>> DiscoverAsync(
            NativeWorldSource source, IWorldDiscoveryContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!(context.Context is IInternalWorldRuntimeContext native))
                return DiscoveryFailure(ModErrorCode.Unavailable, "The owning context does not provide native world discovery.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Context.Lifetime.StoppingToken);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                // Inventory the bounded source before paging so stable identity collision checks cannot depend on result count.
                var result = await native.WorldRuntime.DiscoverAsync(source, WorldDiscoveryIdentity.MaximumNativeEntries, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (!result.TryGetValue(out var entries)) return DiscoveryFailure(result.ErrorCode, result.ErrorMessage);
                var mapped = WorldDiscoveryIdentity.Map(context.FamilyId, source, entries, context.MaximumResults);
                if (!mapped.TryGetValue(out var worlds)) return DiscoveryFailure(mapped.ErrorCode, mapped.ErrorMessage);
                return OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>.Success(Array.AsReadOnly(worlds.Select(x => x.Descriptor).ToArray()));
            }
            catch (OperationCanceledException) { return DiscoveryFailure(ModErrorCode.Cancelled, "World discovery was cancelled."); }
            catch (Exception error) { return DiscoveryFailure(ModErrorCode.External, "World discovery failed: " + error); }
        }
        public static async Task<OperationResult<IWorldInstance>> LoadAsync(NativeWorldSource source,
            IWorldLoadContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.WorldFamilyId == null)
                return LoadFailure(ModErrorCode.InvalidArgument, "A discovered world requires its declared family.");
            if (!(context.Context is IInternalWorldRuntimeContext native))
                return LoadFailure(ModErrorCode.Unavailable, "The owning context does not provide native world discovery.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Context.Lifetime.StoppingToken);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                // The discovery object is temporary. Resolve the stable key again from the current native inventory.
                var result = await native.WorldRuntime.DiscoverAsync(source, WorldDiscoveryIdentity.MaximumNativeEntries, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (!result.TryGetValue(out var entries)) return LoadFailure(result.ErrorCode, result.ErrorMessage);
                var mapped = WorldDiscoveryIdentity.Map(context.WorldFamilyId, source, entries, WorldDiscoveryIdentity.MaximumNativeEntries);
                if (!mapped.TryGetValue(out var worlds)) return LoadFailure(mapped.ErrorCode, mapped.ErrorMessage);
                var selected = worlds.SingleOrDefault(x => string.Equals(x.Descriptor.Id, context.WorldId, StringComparison.OrdinalIgnoreCase));
                if (selected == null) return LoadFailure(ModErrorCode.NotFound, "The selected discovered world is no longer available.");
                var request = source == NativeWorldSource.CuratedLevels
                    ? NativeWorldLoadRequest.Curated(selected.SourceKey, context.Transition)
                    : NativeWorldLoadRequest.Scene(selected.SourceKey, context.Transition);
                // Pass the original session token: WorldProviderLoader owns its own lifetime link beyond this method.
                return await WorldProviderLoader.LoadAsync(context, request, cancellationToken);
            }
            catch (OperationCanceledException) { return LoadFailure(ModErrorCode.Cancelled, "World discovery was cancelled before load."); }
            catch (Exception error) { return LoadFailure(ModErrorCode.External, "Discovered world load failed: " + error); }
        }
        private static OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>> DiscoveryFailure(ModErrorCode code, string message) =>
            OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>.Failure(code, message);
        private static OperationResult<IWorldInstance> LoadFailure(ModErrorCode code, string message) =>
            OperationResult<IWorldInstance>.Failure(code, message);
    }
}
