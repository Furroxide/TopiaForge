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
        private const double OwnedRecheckSeconds = 1d;

        // Checks after a scene load, so an event system the scene's objects make in Start is seen too.
        private const int SceneLoadRechecks = 2;

        private static readonly List<TrackedRaycaster> KitRaycasters = new List<TrackedRaycaster>();
        private static readonly List<GameObject> SceneRoots = new List<GameObject>();
        private static readonly List<EventSystem> SceneEventSystems = new List<EventSystem>();
        private static GameObject? ownedEventSystem;
        private static bool watchingScenes;
        private static int pendingRechecks;
        private static double nextCheckAt;

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
        /// frame finds an event system for it if none is active. A HUD's disabled raycaster asks for none. Windows and
        /// tools supply their content root because hiding that child does not disable the canvas's own raycaster.
        /// </summary>
        internal static void Track(GraphicRaycaster raycaster, GameObject? inputRoot = null)
        {
            if (raycaster == null)
            {
                return;
            }

            WatchScenes();
            DropDestroyed();
            var tracked = new TrackedRaycaster(raycaster, inputRoot != null ? inputRoot : raycaster.gameObject);
            for (var index = 0; index < KitRaycasters.Count; index++)
            {
                if (KitRaycasters[index].Raycaster == raycaster)
                {
                    KitRaycasters[index] = tracked;
                    return;
                }
            }

            KitRaycasters.Add(tracked);
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
                if (sceneRecheck || Time.unscaledTimeAsDouble >= nextCheckAt)
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
            nextCheckAt = 0;
            SceneRoots.Clear();
            SceneEventSystems.Clear();
        }

        private static void Reconcile()
        {
            nextCheckAt = Time.unscaledTimeAsDouble + OwnedRecheckSeconds;
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
            DropDestroyed();
            foreach (var tracked in KitRaycasters)
            {
                if (tracked.Raycaster.isActiveAndEnabled && tracked.InputRoot.activeInHierarchy)
                {
                    return true;
                }
            }

            return false;
        }

        // Canvases come and go with every modal and every scene, so the list holds only live raycasters.
        private static void DropDestroyed()
        {
            for (var index = KitRaycasters.Count - 1; index >= 0; index--)
            {
                if (KitRaycasters[index].Raycaster == null || KitRaycasters[index].InputRoot == null)
                {
                    KitRaycasters.RemoveAt(index);
                }
            }
        }

        // An active event system the kit did not make: the game's own, or another mod's.
        private static EventSystem? FindOtherActive()
        {
            var current = EventSystem.current;
            if (current != null && current.isActiveAndEnabled && current.gameObject != ownedEventSystem)
            {
                return current;
            }

            // Unity's array-returning global search allocates on every poll. Reuse the list overloads instead;
            // capacity grows only when the scene changes. The persistent scene is not in SceneManager's list.
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var candidate = FindOtherActive(SceneManager.GetSceneAt(index));
                if (candidate != null)
                {
                    return candidate;
                }
            }

            return ownedEventSystem != null ? FindOtherActive(ownedEventSystem.scene) : null;
        }

        private static EventSystem? FindOtherActive(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return null;
            }

            try
            {
                scene.GetRootGameObjects(SceneRoots);
                foreach (var root in SceneRoots)
                {
                    if (root == null) continue;
                    root.GetComponentsInChildren(false, SceneEventSystems);
                    foreach (var candidate in SceneEventSystems)
                    {
                        if (candidate != null && candidate.isActiveAndEnabled && candidate.gameObject != ownedEventSystem)
                        {
                            return candidate;
                        }
                    }
                }

                return null;
            }
            finally
            {
                // Do not retain another scene's roots or components between checks, including on an early return.
                SceneRoots.Clear();
                SceneEventSystems.Clear();
            }
        }

        private static void CreateOwn()
        {
            var go = new GameObject("TopiaForgeEventSystem");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
            ownedEventSystem = go;
            nextCheckAt = Time.unscaledTimeAsDouble + OwnedRecheckSeconds;

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

        private readonly struct TrackedRaycaster
        {
            public TrackedRaycaster(GraphicRaycaster raycaster, GameObject inputRoot)
            {
                Raycaster = raycaster;
                InputRoot = inputRoot;
            }

            public GraphicRaycaster Raycaster { get; }

            public GameObject InputRoot { get; }
        }
    }
}
