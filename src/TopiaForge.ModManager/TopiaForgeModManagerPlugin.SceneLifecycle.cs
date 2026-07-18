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
                loadedSceneModes.Clear();
                suppressNextActivation.Clear();
            }

            loadedSceneModes[scene.handle] = sdkMode;
            if (lastActiveSceneHandle != scene.handle
                && (isActive || sdkMode == SceneLoadMode.Single))
            {
                // Some Unity versions publish sceneLoaded just before activeSceneChanged. The load callback below
                // already carries authoritative metadata (Single is authoritative even if activation state has not
                // caught up), so suppress only that immediately pending activation.
                suppressNextActivation.Add(scene.handle);
            }

            if (runtime.DispatchSceneLoaded(scene.handle, scene.name, scene.IsValid(), sdkMode, isActive))
            {
                menuButtonInjector.ResetForScene(scene.name);
            }
        }

        private void OnSceneUnloaded(Scene scene)
        {
            loadedSceneModes.Remove(scene.handle);
            suppressNextActivation.Remove(scene.handle);
        }

        private void OnActiveSceneChanged(Scene previous, Scene current)
        {
            lastActiveSceneHandle = current.IsValid() ? current.handle : 0;
            if (current.IsValid() && suppressNextActivation.Remove(current.handle))
            {
                return;
            }

            if (!current.IsValid() || !loadedSceneModes.TryGetValue(current.handle, out var mode))
            {
                // When activation precedes sceneLoaded, that later callback samples the scene as active and emits
                // the authoritative notification exactly once.
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
            }

            if (scene.IsValid())
            {
                lastActiveSceneHandle = scene.handle;
            }

            if (runtime.DispatchInitialScene(scene.handle, scene.name, scene.IsValid()))
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
