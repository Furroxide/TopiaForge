using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Worlds.Maps;

namespace TopiaForge.Worlds
{
    internal sealed partial class UgcImportHostBridge : ILocalAuthoredMapBackend, IMapManifestBackend
    {
        private NativePublishedMapBindings? published;
        private NativePublishedMapBindings Published => published ??= new NativePublishedMapBindings(
            Type.GetType("UgcPublishedProjectLoader, GameCode", false) ?? throw Unavailable());
        public Uri PublicBase => Published.PublicBase;
        public Uri ResolvePublished(string id) => Published.Resolve(id);
        public IMapTransport PublishedTransport(IMapTransport transport) => Published.UsesManifest
            ? new MapManifestTransport(transport, new MapEndpointPolicy(PublicBase), Published.ReadProject,
                SynchronizationContext.Current ?? throw Unavailable()) : transport;

        public Task<OperationResult<IMapImportTransaction>> PrepareAsync(MapDocument document, string? sceneId,
            string label, WorldSceneIdentity scene, CancellationToken token)
            => PrepareMapAsync(document, sceneId, label, scene, Array.Empty<WorldAssetOverride>(), token);

        public async Task<OperationResult<IMapImportTransaction>> PrepareMapAsync(MapDocument document, string? sceneId,
            string label, WorldSceneIdentity scene, IReadOnlyList<WorldAssetOverride> overrides, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var load = exportLoaderType?.GetMethod("LoadProjectFromBytes", PublicStatic, null, new[] { typeof(byte[]), typeof(string) }, null);
                if (load == null || exportProjectType == null || load.ReturnType != exportProjectType || importHostControllerType == null)
                    throw Unavailable();
                var project = load.Invoke(null, new object[] { document.Json, label });
                if (project == null) throw new MapProblem(ModErrorCode.InvalidArgument, "The game could not read the map. Ask its creator for a new export.");
                var resolve = RequireMethod(exportProjectType, "ResolveScene", "UgcExportScene", typeof(string));
                var nativeScene = resolve.Invoke(project, new object?[] { sceneId });
                var idField = nativeScene?.GetType().GetField("id", PublicInstance);
                if (idField?.FieldType != typeof(string) || !(idField.GetValue(nativeScene) is string selected))
                    throw new MapProblem(ModErrorCode.NotFound, "The map no longer contains the selected scene. Ask the mod's author for an update.");
                document.Select(selected);
                if (!UnityWorldScene.TryGetLoaded(scene, out var loaded)) throw Unavailable();
                var hosts = loaded.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(importHostControllerType, true)).ToArray();
                if (hosts.Length != 1) throw Unavailable();
                var host = hosts[0];
                var config = RequireProperty(importHostControllerType, "RuntimeAssetConfig", "UgcRuntimeAssetConfig").GetValue(host);
                if (config == null) throw Unavailable();
                // The default mirrors native map play. Its adapter isolates the lore-loading choice for native verification.
                await MapLorePreparation.PrepareAsync(project, config, token);
                token.ThrowIfCancellationRequested();
                return OperationResult<IMapImportTransaction>.Success(new ImportTransaction(host, null,
                    overrides, scene, project, exportProjectType, selected, label));
            }
            catch (OperationCanceledException) { return OperationResult<IMapImportTransaction>.Failure(ModErrorCode.Cancelled, "Map loading was cancelled."); }
            catch (MapProblem error) { return OperationResult<IMapImportTransaction>.Failure(error.Code, error.Message); }
            catch (Exception) { return OperationResult<IMapImportTransaction>.Failure(ModErrorCode.External, "The game could not prepare this map. Update TopiaForge or try another map."); }
        }
        private static MapProblem Unavailable() => new MapProblem(ModErrorCode.Unavailable,
            "This game build cannot load maps through TopiaForge yet. Update TopiaForge.");
    }
}
