using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The keys of every loaded scene's network root candidates, taken as the scene loads, in every realm and above all
    /// in single player, and kept for as long as the scene stays loaded. A key names an object by its path in the
    /// hierarchy, so it is only stable as the scene loaded: an object destroyed or moved to another parent later shifts
    /// the sibling indices after it. Keyed at load, an object keeps its key whatever happens to the hierarchy since, and
    /// a joining player, which loads the scene fresh and keys it the same way, names every object alike. An object made
    /// after the load is not a scene object, and has no key. Taking the keys costs only the load; frames cost nothing.
    /// Main thread only.
    /// </summary>
    internal sealed class SceneKeyRegistry
    {
        private static readonly NetworkSceneObject[] None = Array.Empty<NetworkSceneObject>();

        private readonly INetworkWorldBackend backend;
        private readonly Dictionary<int, NetworkSceneObject[]> scenes = new Dictionary<int, NetworkSceneObject[]>();
        private readonly List<int> loaded = new List<int>();
        private readonly List<int> stale = new List<int>();

        internal SceneKeyRegistry(INetworkWorldBackend backend)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        /// <summary>Gets how many loaded scenes have their keys.</summary>
        internal int SceneCount => scenes.Count;

        /// <summary>Gets how many scene loads were keyed as they loaded.</summary>
        internal long Captures { get; private set; }

        /// <summary>Gets how many scenes were keyed late, after their load, because their load could not be keyed then.</summary>
        internal long LateCaptures { get; private set; }

        /// <summary>Gets how many scene loads could not be keyed; such a scene is keyed when a session first needs it.</summary>
        internal long Failures { get; private set; }

        /// <summary>Gets how many objects the last scene load keyed.</summary>
        internal int LastObjects { get; private set; }

        /// <summary>Gets how long the last scene load took to key, in microseconds.</summary>
        internal long LastMicros { get; private set; }

        /// <summary>Gets the longest any scene load took to key, in microseconds.</summary>
        internal long LongestMicros { get; private set; }

        /// <summary>
        /// Keys the scene that just loaded. A scene is keyed once per load: one already keyed keeps the keys it took as it
        /// loaded. A capture that fails keeps nothing, and its failure goes on to the caller.
        /// </summary>
        internal void Capture(int sceneHandle)
        {
            if (scenes.ContainsKey(sceneHandle)) return;
            ForgetUnloaded();
            var started = Stopwatch.GetTimestamp();
            NetworkSceneObject[] captured;
            try
            {
                captured = Collect(sceneHandle);
            }
            catch (Exception)
            {
                Failures++;
                throw;
            }

            scenes[sceneHandle] = captured;
            Captures++;
            LastObjects = captured.Length;
            LastMicros = (Stopwatch.GetTimestamp() - started) * 1_000_000 / Stopwatch.Frequency;
            LongestMicros = Math.Max(LongestMicros, LastMicros);
        }

        /// <summary>Forgets an unloaded scene's keys.</summary>
        internal void Forget(int sceneHandle) => scenes.Remove(sceneHandle);

        /// <summary>Gets the keys a scene took as it loaded; false for a scene that was never keyed.</summary>
        internal bool TryGet(int sceneHandle, out IReadOnlyList<NetworkSceneObject> captured)
        {
            if (scenes.TryGetValue(sceneHandle, out var found))
            {
                captured = found;
                return true;
            }

            captured = None;
            return false;
        }

        /// <summary>Appends every loaded scene's keyed objects, each scene's from its load. Returns how many scenes had to be keyed late.</summary>
        internal int CollectLoaded(List<NetworkSceneObject> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            ForgetUnloaded();
            loaded.Clear();
            backend.CollectLoadedScenes(loaded);
            var late = 0;
            for (var index = 0; index < loaded.Count; index++) late += CollectScene(loaded[index], into) ? 0 : 1;
            loaded.Clear();
            return late;
        }

        /// <summary>
        /// Appends one scene's keyed objects. A scene that was never keyed, because its load could not be, is keyed now
        /// from the hierarchy as it is, kept from then on, and false is returned.
        /// </summary>
        internal bool CollectScene(int sceneHandle, List<NetworkSceneObject> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            var keyedAtLoad = scenes.TryGetValue(sceneHandle, out var captured);
            if (!keyedAtLoad)
            {
                captured = Collect(sceneHandle);
                scenes[sceneHandle] = captured;
                LateCaptures++;
            }

            into.AddRange(captured!);
            return keyedAtLoad;
        }

        /// <summary>Forgets every scene's keys.</summary>
        internal void Clear() => scenes.Clear();

        private NetworkSceneObject[] Collect(int sceneHandle)
        {
            // A list of its own each time, so no working storage outlives the load.
            var census = new List<NetworkSceneObject>();
            backend.CollectSceneObjects(sceneHandle, census);
            return census.Count == 0 ? None : census.ToArray();
        }

        /// <summary>Drops the keys of scenes that are no longer loaded, in case an unload went unreported.</summary>
        private void ForgetUnloaded()
        {
            if (scenes.Count == 0) return;
            loaded.Clear();
            backend.CollectLoadedScenes(loaded);
            stale.Clear();
            foreach (var handle in scenes.Keys)
            {
                if (!loaded.Contains(handle)) stale.Add(handle);
            }

            foreach (var handle in stale) scenes.Remove(handle);
            stale.Clear();
            loaded.Clear();
        }
    }
}
