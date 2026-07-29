using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var sdkMode = mode == LoadSceneMode.Additive
                ? SceneLoadMode.Additive
                : SceneLoadMode.Single;
            var isActive = SceneManager.GetActiveScene().handle == scene.handle;
            if (sdkMode == SceneLoadMode.Single)
            {
                suppressNextActivation.Clear();
                lifecycleActivationPublishedAtLoad.Clear();
            }

            loadedSceneModes[scene.handle] = sdkMode;
            if (lastActiveSceneHandle != scene.handle
                && (isActive || sdkMode == SceneLoadMode.Single))
            {
                // Some Unity versions publish sceneLoaded just before activeSceneChanged. The load callback below
                // already carries world-replacement metadata (Single replaces the world even if activation has not
                // caught up), so suppress only that immediately pending activation.
                suppressNextActivation.Add(scene.handle);
                if (isActive)
                {
                    lifecycleActivationPublishedAtLoad.Add(scene.handle);
                }
            }

            if (runtime.DispatchSceneLoaded(scene.handle, scene.name, scene.IsValid(), sdkMode, isActive))
            {
                menuButtonInjector.ResetForScene(scene.name);
            }
        }

        private void OnSceneUnloaded(Scene scene)
        {
            var knownScene = loadedSceneModes.TryGetValue(scene.handle, out var loadedMode);
            var mode = knownScene
                ? loadedMode
                : SceneLoadMode.Single;
            // Unity may invalidate a Scene before invoking sceneUnloaded. A handle recorded at load/initial replay
            // is sufficient provenance; unknown callbacks still require Unity validity and a usable name.
            runtime.DispatchSceneUnloaded(scene.handle, scene.name, knownScene || scene.IsValid(), mode);
            loadedSceneModes.Remove(scene.handle);
            suppressNextActivation.Remove(scene.handle);
            lifecycleActivationPublishedAtLoad.Remove(scene.handle);
        }

        private void OnActiveSceneChanged(Scene previous, Scene current)
        {
            lastActiveSceneHandle = current.IsValid() ? current.handle : 0;
            if (current.IsValid() && suppressNextActivation.Remove(current.handle))
            {
                if (!lifecycleActivationPublishedAtLoad.Remove(current.handle)
                    && loadedSceneModes.TryGetValue(current.handle, out var suppressedMode))
                {
                    // The detailed SceneLoadEvent stream already treats a Single load as a world replacement even when
                    // Unity activates it one callback later. Publish only the exact activation to the new lifecycle
                    // stream so existing detailed subscribers do not gain a duplicate callback.
                    runtime.DispatchSceneLifecycleActivated(
                        current.handle,
                        current.name,
                        isValid: true,
                        mode: suppressedMode);
                }

                return;
            }

            if (!current.IsValid() || !loadedSceneModes.TryGetValue(current.handle, out var mode))
            {
                // When activation precedes sceneLoaded, that later callback samples the scene as active and emits
                // the world-replacement notification exactly once.
                return;
            }

            if (runtime.DispatchSceneActivated(current.handle, current.name, true, mode))
            {
                menuButtonInjector.ResetForScene(current.name);
            }
        }

        private void DeliverInitialScene()
        {
            var scene = SceneManager.GetActiveScene();
            loadedSceneModes.Clear();
            suppressNextActivation.Clear();
            lifecycleActivationPublishedAtLoad.Clear();
            var initialScenes = new List<ModRuntime.InitialSceneReplay>();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var loaded = SceneManager.GetSceneAt(index);
                if (!loaded.IsValid())
                {
                    continue;
                }

                loadedSceneModes[loaded.handle] = loaded.handle == scene.handle
                    ? SceneLoadMode.Single
                    : SceneLoadMode.Additive;
                if (loaded.handle != scene.handle)
                {
                    initialScenes.Add(new ModRuntime.InitialSceneReplay(
                        loaded.handle,
                        loaded.name,
                        isValid: true,
                        mode: SceneLoadMode.Additive,
                        isActive: false));
                }
            }

            if (scene.IsValid())
            {
                lastActiveSceneHandle = scene.handle;
                loadedSceneModes[scene.handle] = SceneLoadMode.Single;
                // Background additive scenes are replayed first, then the active scene retains the established
                // legacy/detailed startup callback and gains its normalized Loaded -> Activated lifecycle pair.
                initialScenes.Add(new ModRuntime.InitialSceneReplay(
                    scene.handle,
                    scene.name,
                    isValid: true,
                    mode: SceneLoadMode.Single,
                    isActive: true));
            }

            if (runtime.DispatchInitialScenes(initialScenes))
            {
                menuButtonInjector.ResetForScene(scene.name);
                managerLogger.Debug("Delivered initial active scene '" + scene.name + "' to loaded mods.");
            }
            else if (!scene.IsValid() || string.IsNullOrWhiteSpace(scene.name))
            {
                managerLogger.Debug("Initial active scene is not valid yet; waiting for Unity's scene-loaded callback.");
            }
        }

        /// <summary>
        /// Installs everything waiting in the package-inbox before any mod loads, so a freshly staged
        /// dev-install (or a file the user dropped in) is live on the very next launch — no F10 install
        /// step, and no window where an updated loader runs against binary-stale installed packages.
        /// </summary>
    }
}
