using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds.Maps
{
    internal interface IMapNativeBackend : IDisposable
    {
        Uri PublicBase { get; }
        Uri ResolvePublished(string id);
        Task<OperationResult<IMapImportTransaction>> PrepareAsync(MapDocument document, string? sceneId, string label,
            WorldSceneIdentity scene, CancellationToken token);
    }
    internal interface IMapImportTransaction : ILocalImportTransaction
    {
        string SceneId { get; }
        OperationResult<TransformState> PlaceRespawn(string entityId);
    }

    internal sealed class MapWorldService : IInternalMapWorldService, IDisposable
    {
        private readonly MapCache cache;
        private readonly IMapTransport transport;
        private readonly Func<IMapNativeBackend> createBackend;
        private MapStore? store;
        private bool disposed;
        internal MapWorldService(MapCache cache, IMapTransport transport, Func<IMapNativeBackend> createBackend)
        { this.cache = cache; this.transport = transport; this.createBackend = createBackend; }

        public async Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, InternalMapWorldRequest request, CancellationToken token)
        {
            if (disposed) return OperationResult<IWorldInstance>.Failure(ModErrorCode.Cancelled, "Map loading stopped.");
            using var progress = new MapLoadProgress(context.Context.Ui);
            string? safeFailure = null;
            OperationResult<WorldReadiness> Failure(ModErrorCode code, string? message)
            { safeFailure = message ?? "The map could not be loaded."; return OperationResult<WorldReadiness>.Failure(code, safeFailure); }
            // Never allow a native exception's URL, file path or backend reply to escape the map boundary.
            var result = await WorldProviderLoader.LoadAsync(context, NativeWorldLoadRequest.OpenSandbox(context.Transition), token,
                async (preparation, resources) =>
                {
                    try
                    {
                        resources.ThrowIfStopping();
                        using var backend = createBackend();
                        MapDocument document;
                        if (request.PublishedId != null)
                        {
                            store ??= new MapStore(cache, new MapEndpointPolicy(backend.PublicBase), transport);
                            var acquired = await store.GetAsync(request.PublishedId, backend.ResolvePublished(request.PublishedId), resources.StoppingToken, progress.Download);
                            if (!acquired.TryGetValue(out var value)) return Failure(acquired.ErrorCode, acquired.ErrorMessage);
                            document = value.Document;
                            if (value.Offline)
                            {
                                var saved = DateTime.TryParse(value.SavedUtc, System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.RoundtripKind, out var date)
                                    ? date.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "an earlier play";
                                context.Context.Ui.ShowToast("Playing the copy of " + document.Name + " saved on " + saved +
                                    "; robotopia.gg could not be reached.", UiTone.Warning);
                            }
                        }
                        else
                        {
                            if (request.ReadFile == null) return Failure(ModErrorCode.InvalidArgument, "The map has no declared source.");
                            var read = await request.ReadFile(resources.StoppingToken);
                            if (!read.TryGetValue(out var bytes)) return Failure(read.ErrorCode, read.ErrorMessage);
                            document = new MapDocument(bytes);
                        }
                        resources.ThrowIfStopping();
                        progress.Building();
                        if (request.SceneId != null) document.Select(request.SceneId);
                        var prepared = await backend.PrepareAsync(document, request.SceneId, "map:" + (request.PublishedId ?? "package"),
                            preparation.Scene, resources.StoppingToken);
                        if (!prepared.TryGetValue(out var transaction)) return Failure(prepared.ErrorCode, prepared.ErrorMessage);
                        using (transaction)
                        {
                            resources.ThrowIfStopping();
                            var scene = document.Select(transaction.SceneId);
                            var spawn = scene.Spawn(context.SpawnPolicy.Kind == WorldSpawnKind.AuthoredMarker ? context.SpawnPolicy.MarkerName : null);
                            if (!(context.Context is IInternalSceneTransitionContext transitions))
                                return Failure(ModErrorCode.Unavailable, "The shared map import service is unavailable. Update TopiaForge.");
                            var imported = await LocalWorldImportOperation.RunAsync(transitions.SceneTransitions, preparation.Scene,
                                resources.StoppingToken, transaction);
                            if (!imported.TryGetValue(out var content)) return Failure(imported.ErrorCode, "The map could not be built. Try again or choose another map.");
                            resources.Add(content);
                            resources.ThrowIfStopping();
                            var placed = transaction.PlaceRespawn(spawn.Id);
                            if (!placed.TryGetValue(out var transform)) return Failure(placed.ErrorCode, placed.ErrorMessage);
                            resources.ThrowIfStopping();
                            // Map editor markers are resolved above, by source id. The generic readiness layer
                            // applies and verifies that transform; it must not search for an SDK prefab marker.
                            var ready = await preparation.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null,
                                transform, resources.StoppingToken);
                            if (ready.Succeeded)
                            {
                                var modified = DateTime.TryParse(document.Modified, System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date.ToUniversalTime().ToString("O") : "unknown";
                                context.Context.Logger.Info("Map loaded: id=" + (request.PublishedId ?? "package") + "; scene=" + scene.Id +
                                    "; sha256=" + document.Sha256 + "; bytes=" + document.Packed.Length + "; modified=" + modified);
                            }
                            return ready;
                        }
                    }
                    catch (OperationCanceledException) { return Failure(ModErrorCode.Cancelled, "Map loading was cancelled."); }
                    catch (MapProblem error) { return Failure(error.Code, error.Message); }
                    catch (FormatException error) { return Failure(ModErrorCode.InvalidArgument, error.Message); }
                    catch (Exception) { return Failure(ModErrorCode.External, "The map could not be built. Update TopiaForge or try another map."); }
                });
            return result.Succeeded ? result : OperationResult<IWorldInstance>.Failure(result.ErrorCode,
                result.ErrorCode == ModErrorCode.External ? "The map could not be built or cleaned up. Update TopiaForge or restart the game."
                : result.ErrorCode == ModErrorCode.Cancelled ? "Map loading was cancelled."
                : safeFailure ?? "The game's map loading service could not prepare this world. Update TopiaForge or try again.");
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            (transport as IDisposable)?.Dispose();
        }
    }
    internal sealed class MapProblem : Exception
    {
        internal MapProblem(ModErrorCode code, string message) : base(message) { Code = code; }
        internal ModErrorCode Code { get; }
    }
}
