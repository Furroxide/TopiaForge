using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Worlds.Maps;

namespace TopiaForge.Worlds
{
    internal sealed partial class UgcImportHostBridge
    {
        public Uri PublicBase
        {
            get
            {
                var type = Type.GetType("UgcPublishedProjectLoader, GameCode", false);
                var baseField = type?.GetField("DefaultPublicBaseUrl", PublicStatic);
                if (baseField?.FieldType != typeof(string) || !(baseField.GetValue(null) is string value) ||
                    !Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw Unavailable();
                return uri;
            }
        }

        public Uri ResolvePublished(string id)
        {
            if (!MapDocument.IsId(id, 64)) throw new MapProblem(ModErrorCode.InvalidArgument, "Choose a valid published map id.");
            var type = Type.GetType("UgcPublishedProjectLoader, GameCode", false);
            var byRef = typeof(string).MakeByRefType();
            var resolve = type?.GetMethod("TryResolveProjectUrl", PublicStatic, null,
                new[] { typeof(string), byRef, byRef, byRef, byRef }, null);
            if (resolve?.ReturnType != typeof(bool)) throw Unavailable();
            var arguments = new object?[] { id, null, null, null, null };
            if (!(resolve.Invoke(null, arguments) is bool success) || !success)
                throw new MapProblem(ModErrorCode.InvalidArgument, "The game could not resolve this published map. Check its id or ask the mod's author for an update.");
            var policy = new MapEndpointPolicy(PublicBase);
            // Select the resolved project URL, never the separate public-base URL or a diagnostic string.
            foreach (var value in arguments.Skip(1).OfType<string>())
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith("/project.roboworld", StringComparison.Ordinal))
                { policy.Require(uri); return uri; }
            throw Unavailable();
        }

        public async Task<OperationResult<IMapImportTransaction>> PrepareAsync(MapDocument document, string? sceneId,
            string label, WorldSceneIdentity scene, CancellationToken token)
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
                    Array.Empty<WorldAssetOverride>(), scene, project, exportProjectType, selected, label));
            }
            catch (OperationCanceledException) { return OperationResult<IMapImportTransaction>.Failure(ModErrorCode.Cancelled, "Map loading was cancelled."); }
            catch (MapProblem error) { return OperationResult<IMapImportTransaction>.Failure(error.Code, error.Message); }
            catch (Exception) { return OperationResult<IMapImportTransaction>.Failure(ModErrorCode.External, "The game could not prepare this map. Update TopiaForge or try another map."); }
        }
        private static MapProblem Unavailable() => new MapProblem(ModErrorCode.Unavailable,
            "This game build cannot load maps through TopiaForge yet. Update TopiaForge.");
    }
}
