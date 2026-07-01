using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.UgcLiveSync
{
    /// <summary>
    /// Clean-room reflection bridge into the game's UGC import/diff/patch engine. Mirrors the patterns in
    /// <c>Robotopia.Worlds.GameLevelBridge</c>: every <c>GameCode</c> type is resolved with
    /// <c>Type.GetType("X, GameCode", throwOnError: false)</c>, members are cached, and every call is wrapped so a
    /// missing/renamed symbol degrades gracefully instead of crashing the game. The local-folder channel drives
    /// the game's <b>public</b> APIs directly and never touches <c>UgcLiveSyncController</c> (which is welded to
    /// the Automerge WebSocket client); it reproduces only that controller's apply <i>logic</i>.
    /// </summary>
    internal sealed class UgcGameBridge : IUgcLiveSyncBridge
    {
        private const string PlaySceneName = "UgcPlay";
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        private readonly IModLogger logger;

        private readonly Type? sceneUtilType;
        private readonly Type? launchRequestType;
        private readonly Type? lastRunType;
        private readonly Type? importHostType;
        private readonly Type? importHostConfigType;
        private readonly Type? exportLoaderType;
        private readonly Type? differType;
        private readonly Type? patcherType;
        private readonly Type? liveControllerType;
        private readonly Type? exportProjectType;
        private readonly Type? exportSceneType;

        // Cached members (resolved lazily).
        private MethodInfo? loadProjectFromBytes;
        private MethodInfo? resolveScene;
        private MethodInfo? diff;
        private MethodInfo? importProject;
        private MethodInfo? applyPatches;
        private MethodInfo? refreshEntityIndex;
        private ConstructorInfo? patcherCtor;

        // Per-session apply state (local channel).
        private object? prevProject;
        private object? patcher;

        // Automerge session state.
        private Action<UgcApplyOutcome>? automergeOnRevision;
        private bool automergePending;
        private string automergeSceneId = string.Empty;

        public UgcGameBridge(IModLogger logger)
        {
            this.logger = logger;
            sceneUtilType = Type.GetType("SceneUtil, GameCode", throwOnError: false);
            launchRequestType = Type.GetType("UgcPlayLaunchRequest, GameCode", throwOnError: false);
            lastRunType = Type.GetType("UgcPlayLauncherLastRun, GameCode", throwOnError: false);
            importHostType = Type.GetType("UgcImportHostSceneController, GameCode", throwOnError: false);
            importHostConfigType = Type.GetType("UgcImportHostConfig, GameCode", throwOnError: false);
            exportLoaderType = Type.GetType("UgcExportLoader, GameCode", throwOnError: false);
            differType = Type.GetType("UgcProjectDiffer, GameCode", throwOnError: false);
            patcherType = Type.GetType("UgcScenePatcher, GameCode", throwOnError: false);
            liveControllerType = Type.GetType("UgcLiveSyncController, GameCode", throwOnError: false);
            exportProjectType = Type.GetType("UgcExportProject, GameCode", throwOnError: false);
            exportSceneType = Type.GetType("UgcExportScene, GameCode", throwOnError: false);

            if (!IsAvailable)
            {
                logger.Warn("UGC live sync unavailable: required GameCode symbols were not found.");
            }
        }

        public bool IsAvailable =>
            sceneUtilType != null && importHostType != null && exportLoaderType != null
            && differType != null && patcherType != null && exportProjectType != null && exportSceneType != null;

        public string GetDefaultWatchFolder()
        {
            try
            {
                return importHostConfigType?.GetMethod("GetDefaultImportFolderPath", PublicStatic)?.Invoke(null, null) as string
                       ?? string.Empty;
            }
            catch (Exception ex)
            {
                logger.Debug("UGC live sync: could not read the default import folder: " + ex.Message);
                return string.Empty;
            }
        }

        public bool IsImportControllerReady()
        {
            return FindImportController() != null;
        }

        public bool EnsurePlaySceneLoaded()
        {
            // Build a no-op launch request (empty import folder => nothing imported), then load the play scene so
            // its bootstrap spawns a player and creates the import host. Our watcher drives the content afterwards.
            try
            {
                if (lastRunType != null && launchRequestType != null)
                {
                    var values = Activator.CreateInstance(lastRunType);
                    if (values != null)
                    {
                        var emptyFolder = Path.Combine(Path.GetTempPath(), "RobotopiaUgcLiveSync");
                        Directory.CreateDirectory(emptyFolder);
                        lastRunType.GetField("Mode")?.SetValue(values, "SelectedFile");
                        lastRunType.GetField("ImportFolderPath")?.SetValue(values, emptyFolder);
                        lastRunType.GetField("SelectedExportFilePath")?.SetValue(values, string.Empty);
                        launchRequestType.GetMethod("Create", PublicStatic, null, new[] { lastRunType }, null)
                            ?.Invoke(null, new[] { values });
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug("UGC live sync: could not queue a no-op launch request: " + ex.Message);
            }

            return LoadPlayScene();
        }

        public void ResetApplyState()
        {
            prevProject = null;
            patcher = null;
        }

        public void ApplyAssetOverrides(IReadOnlyList<UgcAssetOverride> overrides)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return;
            }

            var assetMap = GetAssetMap();
            if (assetMap == null)
            {
                logger.Debug("UGC live sync: no built-in asset map available; overrides will not apply.");
                return;
            }

            var setOverride = assetMap.GetType().GetMethod("SetRuntimeOverride", PublicInstance);
            if (setOverride == null)
            {
                return;
            }

            foreach (var item in overrides)
            {
                if (item.Prefab is not GameObject prefab)
                {
                    logger.Warn("UGC live sync: asset override '" + item.AssetId + "' is not a GameObject; skipped.");
                    continue;
                }

                Vector3? offset = null;
                if (item.LocalPositionOffset is { Length: >= 3 } o)
                {
                    offset = new Vector3(o[0], o[1], o[2]);
                }

                try
                {
                    setOverride.Invoke(assetMap, new object?[] { item.AssetId, prefab, offset });
                }
                catch (Exception ex)
                {
                    logger.Debug("UGC live sync: could not set override '" + item.AssetId + "': " + ex.Message);
                }
            }
        }

        public void ClearAssetOverrides()
        {
            try
            {
                var assetMap = GetAssetMap();
                assetMap?.GetType().GetMethod("ClearRuntimeOverrides", PublicInstance)?.Invoke(assetMap, null);
            }
            catch (Exception ex)
            {
                logger.Debug("UGC live sync: could not clear asset overrides: " + ex.Message);
            }
        }

        public UgcApplyOutcome ApplyLocalSnapshot(byte[] bytes, string sceneId, string label)
        {
            var controller = FindImportController()
                ?? throw new InvalidOperationException("UGC import host controller is not present in the active scene.");

            EnsureLocalMembers();

            var project = loadProjectFromBytes!.Invoke(null, new object?[] { bytes, label })
                ?? throw new InvalidOperationException("Export loader returned no project.");
            var sceneArg = string.IsNullOrWhiteSpace(sceneId) ? null : sceneId;
            var scene = resolveScene!.Invoke(project, new object?[] { sceneArg })
                ?? throw new InvalidOperationException("Project contains no resolvable scene.");

            var resolvedSceneId = GetStringField(scene, "id");
            bool wasFirst;
            bool isFullRebuild;

            if (prevProject == null || patcher == null)
            {
                importProject!.Invoke(controller, new object?[] { project, resolvedSceneId, label });
                patcher = NewPatcher(controller);
                refreshEntityIndex!.Invoke(patcher, null);
                prevProject = project;
                wasFirst = true;
                isFullRebuild = true;
                logger.Info("UGC live sync: imported initial snapshot '" + label + "'.");
            }
            else
            {
                var patches = diff!.Invoke(null, new object?[] { prevProject, project, resolvedSceneId });
                var applied = applyPatches!.Invoke(patcher, new object?[] { project, scene, patches }) as bool? ?? false;
                if (applied)
                {
                    isFullRebuild = false;
                    logger.Info("UGC live sync: applied incremental patch (" + CountPatches(patches) + " patches) from '" + label + "'.");
                }
                else
                {
                    importProject!.Invoke(controller, new object?[] { project, resolvedSceneId, label });
                    patcher = NewPatcher(controller);
                    refreshEntityIndex!.Invoke(patcher, null);
                    isFullRebuild = true;
                    logger.Info("UGC live sync: full rebuild from '" + label + "' (incremental patch could not apply).");
                }

                prevProject = project;
                wasFirst = false;
            }

            return new UgcApplyOutcome(
                GetStringField(project, "name"),
                resolvedSceneId,
                GetStringField(scene, "name"),
                CountEntities(scene),
                isFullRebuild,
                wasFirst);
        }

        public bool StartAutomerge(string documentUrl, string syncServerUrl, string sceneId, Action<UgcApplyOutcome> onRevision)
        {
            if (lastRunType == null || launchRequestType == null)
            {
                logger.Warn("UGC live sync: Automerge launch types are unavailable.");
                return false;
            }

            try
            {
                var values = Activator.CreateInstance(lastRunType);
                if (values == null)
                {
                    return false;
                }

                lastRunType.GetField("Mode")?.SetValue(values, "LiveAutomerge");
                lastRunType.GetField("LiveDocumentUrl")?.SetValue(values, documentUrl);
                lastRunType.GetField("LiveSceneId")?.SetValue(values, sceneId ?? string.Empty);
                lastRunType.GetField("SyncUrl")?.SetValue(values, syncServerUrl);
                launchRequestType.GetMethod("Create", PublicStatic, null, new[] { lastRunType }, null)
                    ?.Invoke(null, new[] { values });

                automergeOnRevision = onRevision;
                automergeSceneId = sceneId ?? string.Empty;
                automergePending = true;
                return LoadPlayScene();
            }
            catch (Exception ex)
            {
                logger.Warn("UGC live sync: could not start the Automerge session: " + ex.Message);
                return false;
            }
        }

        public void StopAutomerge()
        {
            automergeOnRevision = null;
            automergePending = false;

            if (liveControllerType == null)
            {
                return;
            }

            try
            {
                if (UnityEngine.Object.FindAnyObjectByType(liveControllerType) is Component controller)
                {
                    // No public Stop exists; the live loop is cancelled by the GameObject's lifetime token.
                    UnityEngine.Object.Destroy(controller.gameObject);
                }
            }
            catch (Exception ex)
            {
                logger.Debug("UGC live sync: could not stop the Automerge controller: " + ex.Message);
            }
        }

        public void NotifySceneLoaded(string sceneName)
        {
            if (!automergePending || liveControllerType == null)
            {
                return;
            }

            // The native UgcLiveSyncController does the connect/import/patch itself; we only surface a single
            // "live" event when it is present. Per-revision granularity for the Automerge channel is documented
            // as relying on the game's own Player.log lines (see docs/UgcLiveSync.md).
            try
            {
                if (UnityEngine.Object.FindAnyObjectByType(liveControllerType) != null)
                {
                    automergePending = false;
                    automergeOnRevision?.Invoke(new UgcApplyOutcome("(live Automerge)", automergeSceneId, sceneName, 0, false, true));
                    logger.Info("UGC live sync: live Automerge session is active in scene '" + sceneName + "'.");
                }
            }
            catch (Exception ex)
            {
                logger.Debug("UGC live sync: could not confirm the Automerge session: " + ex.Message);
            }
        }

        private bool LoadPlayScene()
        {
            try
            {
                var method = sceneUtilType?.GetMethod(
                    "LoadScene", PublicStatic, null, new[] { typeof(string), typeof(CancellationToken) }, null);
                if (method == null)
                {
                    logger.Warn("UGC live sync: SceneUtil.LoadScene is unavailable.");
                    return false;
                }

                method.Invoke(null, new object[] { PlaySceneName, CancellationToken.None });
                logger.Info("UGC live sync: dispatched load of scene '" + PlaySceneName + "'.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn("UGC live sync: could not load scene '" + PlaySceneName + "': " + ex.Message);
                return false;
            }
        }

        private object? FindImportController()
        {
            try
            {
                return importHostType == null ? null : UnityEngine.Object.FindAnyObjectByType(importHostType);
            }
            catch (Exception ex)
            {
                logger.Debug("UGC live sync: could not search for the import host controller: " + ex.Message);
                return null;
            }
        }

        private object? GetAssetMap()
        {
            var controller = FindImportController();
            if (controller == null)
            {
                return null;
            }

            return importHostType?.GetProperty("BuiltInAssetMap", PublicInstance)?.GetValue(controller);
        }

        private object NewPatcher(object controller)
        {
            var sceneRoot = importHostType!.GetProperty("SceneRoot", PublicInstance)?.GetValue(controller);
            var assetMap = importHostType.GetProperty("BuiltInAssetMap", PublicInstance)?.GetValue(controller);
            var environmentMap = importHostType.GetProperty("EnvironmentPrefabMap", PublicInstance)?.GetValue(controller);

            patcherCtor ??= patcherType!.GetConstructors()[0];
            return patcherCtor.Invoke(new[] { sceneRoot, assetMap, environmentMap, controller });
        }

        private void EnsureLocalMembers()
        {
            loadProjectFromBytes ??= exportLoaderType!.GetMethod(
                "LoadProjectFromBytes", PublicStatic, null, new[] { typeof(byte[]), typeof(string) }, null);
            resolveScene ??= exportProjectType!.GetMethod("ResolveScene", PublicInstance);
            diff ??= differType!.GetMethod("Diff", PublicStatic);
            importProject ??= importHostType!.GetMethod("ImportProject", PublicInstance);
            applyPatches ??= patcherType!.GetMethod("ApplyPatches", PublicInstance);
            refreshEntityIndex ??= patcherType!.GetMethod("RefreshEntityIndex", PublicInstance);

            if (loadProjectFromBytes == null || resolveScene == null || diff == null
                || importProject == null || applyPatches == null || refreshEntityIndex == null)
            {
                throw new InvalidOperationException("UGC live sync: a required GameCode member could not be resolved.");
            }
        }

        private static string GetStringField(object target, string fieldName)
        {
            return target.GetType().GetField(fieldName, PublicInstance)?.GetValue(target) as string ?? string.Empty;
        }

        private static int CountEntities(object scene)
        {
            var entities = scene.GetType().GetField("entities", PublicInstance)?.GetValue(scene);
            return entities is ICollection collection ? collection.Count : 0;
        }

        private static int CountPatches(object? patches)
        {
            return patches is ICollection collection ? collection.Count : 0;
        }
    }
}
