using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Whether gameplay HUDs show: only while the active scene is a loaded gameplay scene, by the game-scene classification
    /// every mod shares (<see cref="GameScenes.IsNonGameplayScene"/>), and no scene is loading. So a HUD made once in
    /// <c>OnLoad</c>, before the game's first scene, stays hidden at the main menu and during loads and shows in every
    /// world. Unity-free: the runtime samples the scene counts once a frame, before mods update, and a sample that changes
    /// nothing allocates nothing.
    /// </summary>
    internal sealed class UiGameplayGate
    {
        private readonly List<Action<bool>> listeners = new List<Action<bool>>();
        private Action<bool>[] snapshot = Array.Empty<Action<bool>>();
        private bool snapshotStale;

        /// <summary>Gets whether gameplay HUDs show now.</summary>
        public bool IsGameplay { get; private set; }

        /// <summary>Classifies a scene by name: true for a gameplay scene, false for the menu, boot, loader and splash scenes.</summary>
        public static bool IsGameplayScene(string? sceneName) =>
            !string.IsNullOrWhiteSpace(sceneName) && !GameScenes.IsNonGameplayScene(sceneName!);

        /// <summary>
        /// Records the scene state: whether the active scene is a gameplay scene and has loaded, and how many scenes the
        /// engine lists against how many have finished loading. Unity lists a scene from the moment its load starts, so
        /// more scenes than loaded ones means a load is under way. Tells every listener when the answer changes.
        /// </summary>
        public bool Observe(bool activeIsGameplayScene, bool activeLoaded, int sceneCount, int loadedSceneCount)
        {
            var gameplay = activeIsGameplayScene && activeLoaded && sceneCount <= loadedSceneCount;
            if (gameplay == IsGameplay)
            {
                return false;
            }

            IsGameplay = gameplay;
            if (snapshotStale)
            {
                snapshot = listeners.ToArray();
                snapshotStale = false;
            }

            foreach (var listener in snapshot)
            {
                try
                {
                    listener(gameplay);
                }
                catch (Exception)
                {
                    // One owner's failure never stops the others from hiding or showing their HUDs.
                }
            }

            return true;
        }

        /// <summary>Calls <paramref name="listener"/> whenever <see cref="IsGameplay"/> changes, until the result is disposed.</summary>
        public IDisposable Subscribe(Action<bool> listener)
        {
            if (listener == null) throw new ArgumentNullException(nameof(listener));
            listeners.Add(listener);
            snapshotStale = true;
            return new Subscription(this, listener);
        }

        private void Unsubscribe(Action<bool> listener)
        {
            if (listeners.Remove(listener))
            {
                snapshotStale = true;
            }
        }

        private sealed class Subscription : IDisposable
        {
            private UiGameplayGate? gate;
            private readonly Action<bool> listener;

            internal Subscription(UiGameplayGate gate, Action<bool> listener)
            {
                this.gate = gate;
                this.listener = listener;
            }

            public void Dispose()
            {
                var current = gate;
                gate = null;
                current?.Unsubscribe(listener);
            }
        }
    }
}
