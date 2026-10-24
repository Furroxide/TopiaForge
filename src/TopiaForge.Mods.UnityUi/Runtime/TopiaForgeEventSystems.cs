using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// EventSystem management: the game's own event system always comes first. Unity runs only the first event system
    /// enabled, so the kit makes its own (StandaloneInputModule under legacy/both; InputSystemUIInputModule when legacy
    /// input is disabled) only while a kit canvas takes input and no event system is active, and removes it as soon as the
    /// game's, or another mod's, is active again: after a scene load, a few frames later, and at least once a second while
    /// the kit's own exists. A canvas that takes no input, such as a HUD made before the game's first scene, makes none.
    /// The kit never disables or destroys an event system it did not make (<see cref="TopiaForgeEventSystemPolicy"/>).
    /// </summary>
    public static class TopiaForgeEventSystems
    {
        // Frames between checks for another event system while the kit's own exists: about a second.
        private const int OwnedRecheckFrames = 60;

        // Checks after a scene load, so an event system the scene's objects make in Start is seen too.
        private const int SceneLoadRechecks = 2;

        private static readonly List<GraphicRaycaster> KitRaycasters = new List<GraphicRaycaster>();
        private static GameObject? ownedEventSystem;
        private static bool watchingScenes;
        private static int pendingRechecks;
        private static int framesSinceCheck;

        /// <summary>Ensures that a compatible Unity event system is available, the game's own when it has one.</summary>
        public static void EnsureEventSystem()
        {
            WatchScenes();
            if (FindOtherActive() == null && ownedEventSystem == null)
            {
                CreateOwn();
            }
        }

        /// <summary>
        /// Follows a kit canvas's raycaster: while it is enabled on an active canvas the kit takes input, and the next
        /// frame finds an event system for it if none is active. A HUD's disabled raycaster asks for none.
        /// </summary>
        internal static void Track(GraphicRaycaster raycaster)
        {
            if (raycaster == null)
            {
                return;
            }

            WatchScenes();
            if (!KitRaycasters.Contains(raycaster))
            {
                KitRaycasters.Add(raycaster);
            }
        }

        /// <summary>
        /// Called every frame by the kit's driver. With no event system active, a kit canvas that takes input gets one; while
        /// the kit's own exists, it goes as soon as another is found. A frame that finds nothing to do allocates nothing.
        /// </summary>
        internal static void Tick()
        {
            var sceneRecheck = pendingRechecks > 0;
            if (sceneRecheck)
            {
                pendingRechecks--;
            }

            if (ownedEventSystem != null)
            {
                if (sceneRecheck || ++framesSinceCheck >= OwnedRecheckFrames)
                {
                    Reconcile();
                }
            }
            else if (EventSystem.current == null && KitTakesInput())
            {
                Reconcile();
            }
        }

        internal static void Reset()
        {
            if (watchingScenes)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                watchingScenes = false;
            }

            RetireOwn();
            KitRaycasters.Clear();
            pendingRechecks = 0;
            framesSinceCheck = 0;
        }

        private static void Reconcile()
        {
            framesSinceCheck = 0;
            var action = TopiaForgeEventSystemPolicy.Decide(KitTakesInput(), ownedEventSystem != null, FindOtherActive() != null);
            if (action == TopiaForgeEventSystemAction.CreateOwn)
            {
                CreateOwn();
            }
            else if (action == TopiaForgeEventSystemAction.RetireOwn)
            {
                RetireOwn();
                TopiaForgeLog.Info("Removed the kit's EventSystem: another EventSystem is active.");
            }
        }

        // A scene brings its own event system: the kit's own, if it has one, goes at once rather than within a second.
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            pendingRechecks = SceneLoadRechecks;
            if (ownedEventSystem != null)
            {
                Reconcile();
            }
        }

        private static void WatchScenes()
        {
            if (!watchingScenes)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                watchingScenes = true;
            }
        }

        // Whether a kit canvas takes input now. Raycasters of destroyed canvases are dropped on the way.
        private static bool KitTakesInput()
        {
            var takesInput = false;
            for (var index = KitRaycasters.Count - 1; index >= 0; index--)
            {
                var raycaster = KitRaycasters[index];
                if (raycaster == null)
                {
                    KitRaycasters.RemoveAt(index);
                }
                else if (raycaster.isActiveAndEnabled)
                {
                    takesInput = true;
                }
            }

            return takesInput;
        }

        // An active event system the kit did not make: the game's own, or another mod's.
        private static EventSystem? FindOtherActive()
        {
            foreach (var candidate in Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (candidate != null && candidate.isActiveAndEnabled && candidate.gameObject != ownedEventSystem)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void CreateOwn()
        {
            var go = new GameObject("TopiaForgeEventSystem");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
            ownedEventSystem = go;
            framesSinceCheck = 0;

            if (TopiaForgeInput.LegacyAvailable)
            {
                go.AddComponent<StandaloneInputModule>();
                TopiaForgeLog.Info("Created EventSystem with StandaloneInputModule.");
            }
            else
            {
                go.AddComponent<InputSystemUIInputModule>();
                TopiaForgeLog.Info("Created EventSystem with InputSystemUIInputModule (InputSystem-only mode).");
            }
        }

        // Only the kit's own event system is ever removed. Deactivating it first takes it out of Unity's list at once, so
        // the game's own runs from this frame rather than from the next.
        private static void RetireOwn()
        {
            var owned = ownedEventSystem;
            ownedEventSystem = null;
            if (owned != null)
            {
                owned.SetActive(false);
                Object.Destroy(owned);
            }
        }
    }
}
