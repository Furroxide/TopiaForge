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
    internal interface ILocalAuthoredMapBackend
    {
        Task<OperationResult<IMapImportTransaction>> PrepareMapAsync(MapDocument document, string? sceneId, string label,
            WorldSceneIdentity scene, IReadOnlyList<WorldAssetOverride> overrides, CancellationToken token);
    }
    internal sealed partial class LocalWorldService
    {
        public Task<OperationResult<IAuthoredMap>> ImportMapAsync(string sessionId, string path, CancellationToken token = default) =>
            Task.FromResult(OperationResult<IAuthoredMap>.Failure(ModErrorCode.InvalidState, "Use the local service from the owning mod context."));

        private sealed partial class Facade
        {
            public async Task<OperationResult<IAuthoredMap>> ImportMapAsync(string sessionId, string path, CancellationToken token = default)
            {
                if (service.disposed || context.Lifetime.IsStopping || token.IsCancellationRequested)
                    return OperationResult<IAuthoredMap>.Failure(ModErrorCode.Cancelled, "The import owner is stopping.");
                if (!service.enabled) return OperationResult<IAuthoredMap>.Failure(ModErrorCode.Unavailable, "Local worlds are disabled.");
                if (context is IInternalReplicationContext network && network.Replication.Realm != InternalNetRealm.Offline)
                    return OperationResult<IAuthoredMap>.Failure(ModErrorCode.Unavailable, "Indexed map imports currently support single player.");
                AuthoredMapIndex? map = null;
                var result = await operations.RunContentOperationAsync(sessionId, async (content, cancellation) =>
                {
                    var owned = new WorldResourceScope();
                    try
                    {
                        if (!(content.Context is IInternalSceneTransitionContext transitions))
                            return Refuse(ModErrorCode.Unavailable, "The shared transition executor is unavailable.");
                        using var host = service.createHost();
                        if (!(host is ILocalAuthoredMapBackend backend) || !host.IsAvailable)
                            return Refuse(ModErrorCode.Unavailable, "This game build cannot index local maps yet. Update TopiaForge.");
                        if (!RoboWorldImportPlan.TryPlan(service.ResolveFolder(host), path, File.Exists, out var plan, out _))
                            return Refuse(ModErrorCode.InvalidArgument, "Choose an ordinary map export inside the configured local import folder.");
                        var document = MapLocalSource.Read(plan!, cancellation);
                        var prepared = await backend.PrepareMapAsync(document, null, "local-map", content.World.Scene,
                            overrides.Values.Select(item => item.Value).ToArray(), cancellation);
                        if (!prepared.TryGetValue(out var transaction)) return Refuse(prepared.ErrorCode, "The local map could not be prepared.");
                        using (transaction)
                        {
                            var imported = await LocalWorldImportOperation.RunAsync(transitions.SceneTransitions, content.World.Scene, cancellation, transaction);
                            if (!imported.TryGetValue(out var resource)) return Refuse(imported.ErrorCode, "The local map could not be imported.");
                            owned.Add(resource);
                            cancellation.ThrowIfCancellationRequested();
                            map = owned.Add(transaction.CreateMap(document, "file:" + plan!.FileName, context));
                            cancellation.ThrowIfCancellationRequested();
                            return OperationResult<IDisposable>.Success(owned);
                        }
                    }
                    catch (OperationCanceledException) { return Refuse(ModErrorCode.Cancelled, "Map import was cancelled."); }
                    catch (FormatException error) { return Refuse(ModErrorCode.InvalidArgument, error.Message); }
                    catch (Exception) { return Refuse(ModErrorCode.External, "The local map could not be indexed. Update TopiaForge or re-export the map."); }

                    OperationResult<IDisposable> Refuse(ModErrorCode code, string message)
                    {
                        try { owned.Dispose(); }
                        catch (Exception) { return OperationResult<IDisposable>.Failure(ModErrorCode.External, "The failed map import could not finish cleanup."); }
                        return OperationResult<IDisposable>.Failure(code, message);
                    }
                }, token);
                return result.Succeeded && map != null && map.IsAlive ? OperationResult<IAuthoredMap>.Success(map)
                    : OperationResult<IAuthoredMap>.Failure(result.Succeeded ? ModErrorCode.Cancelled : result.ErrorCode,
                        result.Succeeded ? "Map import ended before its index became available." : result.ErrorMessage);
            }
        }
    }
}
