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
        AuthoredMapIndex CreateMap(MapDocument document, string mapId, IModContext context);
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
            var follow = context as IInternalMapFollowContext;
            var expected = follow?.ExpectedMap;
            var joined = context.Context is IInternalReplicationContext network && network.Replication.Realm == InternalNetRealm.Client;
            if ((expected != null && (request.PublishedId != expected.MapId || (request.SceneId != null && request.SceneId != expected.SceneId)))
                || (joined && request.PublishedId != null && expected == null))
                return OperationResult<IWorldInstance>.Failure(ModErrorCode.Conflict, "The host's map selection does not match this world.");
            var hosting = follow?.BeginMapHost?.Invoke();
            if (hosting != null && !hosting.Succeeded)
                return OperationResult<IWorldInstance>.Failure(hosting.ErrorCode, "The map could not begin a new multiplayer world. Try again.");
            InternalMapIdentity? identity = null;
            byte[] packed = Array.Empty<byte>();
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
                            var uri = backend.ResolvePublished(request.PublishedId);
                            var acquired = expected == null
                                ? await store.GetAsync(request.PublishedId, uri, resources.StoppingToken, progress.Download)
                                : await store.GetExactAsync(expected, uri, follow!.ReadHostMap, resources.StoppingToken, progress.Download);
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
                        var selectedScene = expected?.SceneId ?? request.SceneId;
                        if (selectedScene != null) document.Select(selectedScene);
                        var prepared = await backend.PrepareAsync(document, selectedScene, "map:" + (request.PublishedId ?? "package"),
                            preparation.Scene, resources.StoppingToken);
                        if (!prepared.TryGetValue(out var transaction)) return Failure(prepared.ErrorCode, prepared.ErrorMessage);
                        using (transaction)
                        {
                            resources.ThrowIfStopping();
                            var scene = document.Select(transaction.SceneId);
                            if (selectedScene != null && scene.Id != selectedScene)
                                return Failure(ModErrorCode.Conflict, "The imported map scene did not match the selected scene.");
                            if (request.PublishedId != null)
                            {
                                identity = new InternalMapIdentity(request.PublishedId, scene.Id, document.Sha256, document.Packed.Length, document.Modified);
                                packed = document.Packed;
                            }
                            var spawn = scene.Spawn(context.SpawnPolicy.Kind == WorldSpawnKind.AuthoredMarker ? context.SpawnPolicy.MarkerName : null);
                            if (!(context.Context is IInternalSceneTransitionContext transitions))
                                return Failure(ModErrorCode.Unavailable, "The shared map import service is unavailable. Update TopiaForge.");
                            var imported = await LocalWorldImportOperation.RunAsync(transitions.SceneTransitions, preparation.Scene,
                                resources.StoppingToken, transaction);
                            if (!imported.TryGetValue(out var content)) return Failure(imported.ErrorCode, "The map could not be built. Try again or choose another map.");
                            resources.Add(content);
                            var map = resources.Add(transaction.CreateMap(document,
                                request.PublishedId ?? "file:" + (request.PackagePath ?? "map.roboworld"), context.Context));
                            resources.ThrowIfStopping();
                            var placed = transaction.PlaceRespawn(spawn.Id);
                            if (!placed.TryGetValue(out var transform)) return Failure(placed.ErrorCode, placed.ErrorMessage);
                            resources.ThrowIfStopping();
                            // Map editor markers are resolved above, by source id. The generic readiness layer
                            // applies and verifies that transform; it must not search for an SDK prefab marker.
                            var ready = await preparation.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null,
                                transform, resources.StoppingToken);
                            if (ready.Succeeded && !map.IsAlive)
                                return Failure(ModErrorCode.InvalidState, "The imported map ended before world readiness. Load the world again.");
                            if (ready.Succeeded)
                            {
                                var modified = DateTime.TryParse(document.Modified, System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date.ToUniversalTime().ToString("O") : "unknown";
                                context.Context.Logger.Info("Map loaded: id=" + (request.PublishedId ?? "package") + "; scene=" + scene.Id +
                                    "; sha256=" + document.Sha256 + "; bytes=" + document.Packed.Length + "; modified=" + modified);
                            }
                            return ready.TryGetValue(out var readiness)
                                ? OperationResult<WorldReadiness>.Success(new WorldReadiness(readiness.Scene, readiness.Spawn, map)) : ready;
                        }
                    }
                    catch (OperationCanceledException) { return Failure(ModErrorCode.Cancelled, "Map loading was cancelled."); }
                    catch (MapProblem error) { return Failure(error.Code, error.Message); }
                    catch (FormatException error) { return Failure(ModErrorCode.InvalidArgument, error.Message); }
                    // Propagate a sanitized fault so a concurrent cancellation cannot
                    // replace failed native cleanup with an ordinary cancelled result.
                    catch (Exception) { throw new InvalidOperationException("The map could not be built or cleaned up."); }
                });
            return result.TryGetValue(out var instance) ? OperationResult<IWorldInstance>.Success(new MapInstance(instance, identity, packed)) : OperationResult<IWorldInstance>.Failure(result.ErrorCode,
                result.ErrorCode == ModErrorCode.External && result.ErrorMessage != safeFailure
                    ? "The map could not be built or cleaned up. Update TopiaForge or restart the game."
                : result.ErrorCode == ModErrorCode.Cancelled ? "Map loading was cancelled."
                : safeFailure ?? "The game's map loading service could not prepare this world. Update TopiaForge or try again.");
        }
        private sealed class MapInstance : IInternalMapWorldInstance
        {
            private readonly IWorldInstance inner;
            internal MapInstance(IWorldInstance inner, InternalMapIdentity? identity, byte[] packed)
            { this.inner = inner; Identity = identity; PackedMap = packed; }
            public InternalMapIdentity? Identity { get; }
            public ReadOnlyMemory<byte> PackedMap { get; }
            public WorldReadiness Readiness => inner.Readiness;
            public void Dispose() => inner.Dispose();
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
