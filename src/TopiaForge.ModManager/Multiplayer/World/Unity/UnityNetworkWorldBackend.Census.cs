using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    internal sealed partial class UnityNetworkWorldBackend
    {
        /// <summary>A parent with more children than this is searched for a child's index only a few times.</summary>
        private const int WideParent = 16;

        /// <summary>How often a wide parent is searched for a child's index before every child's index is read at once.</summary>
        private const int SearchesBeforeOnePass = 16;

        private readonly SceneKeyHasher hasher = new SceneKeyHasher();

        /// <summary>
        /// Keys each loaded scene's candidates, or one scene's: every rigidbody with no other above it on its branch, keyed
        /// by its scene, its path from the root as the hierarchy is now, and its rigidbody, exactly as the replication
        /// model keys a descriptor. The network world asks as each scene loads, inside the load's event, so the cost is
        /// the load's. The engine lists the loaded rigidbodies and marks, and looks above each rigidbody for another, so
        /// nothing here walks a hierarchy except a candidate's own path; each object on such a path is named and hashed
        /// once; and the working sets go with the census.
        /// </summary>
        public void CollectSceneObjects(int? sceneHandle, List<NetworkSceneObject> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            UnityMainThreadGuard.AssertCurrent();
            var player = UnityNativeAuthority.PlayerTransform();
            var census = new Census();
            var everyBody = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            // Offline no mark carries an id, so a load skips the engine's search through every behaviour for one.
            if (NetworkMarks.AnyLive)
            {
                foreach (var mark in UnityEngine.Object.FindObjectsByType<TopiaForgeNetworkIdentity>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (mark != null && mark.NetworkId != 0) census.Marked.Add(mark.transform.GetInstanceID());
                }
            }

            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded || (sceneHandle.HasValue && scene.handle != sceneHandle.Value)) continue;
                CollectScene(scene, player, everyBody, census, into);
            }
        }

        public void CollectLoadedScenes(List<int> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            UnityMainThreadGuard.AssertCurrent();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.isLoaded) into.Add(scene.handle);
            }
        }

        /// <summary>
        /// Runs the census's own steps once on <paramref name="sample"/>, an object of the runtime's that no scene holds, so
        /// the engine compiles them before the first scene that needs them, rather than inside its load.
        /// </summary>
        internal void WarmUp(Transform sample)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            UnityMainThreadGuard.AssertCurrent();
            var census = new Census();
            census.BeginScene();
            var gameObject = sample.gameObject;
            if (IsTopmost(gameObject, census) && IsCandidate(sample, null, census))
            {
                var key = hasher.Finish(PathState(sample, hasher.Scene(string.Empty), census), RigidbodyComponent);
                GC.KeepAlive(new NetworkSceneObject(0, key, new UnityNetworkWorldObject(gameObject, entities)));
            }

            var scenes = new List<int>();
            CollectLoadedScenes(scenes);
            GC.KeepAlive(UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            GC.KeepAlive(UnityEngine.Object.FindObjectsByType<TopiaForgeNetworkIdentity>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
        }

        private void CollectScene(Scene scene, Transform? player, Rigidbody[] everyBody, Census census, List<NetworkSceneObject> into)
        {
            var handle = scene.handle;
            census.BeginScene();
            foreach (var body in everyBody)
            {
                if (body == null) continue;
                var gameObject = body.gameObject;
                if (gameObject.scene.handle != handle) continue;
                census.Bodies.Add(body);
                census.Objects.Add(gameObject);
            }

            if (census.Bodies.Count == 0) return;
            var sceneState = hasher.Scene(scene.name ?? string.Empty);
            for (var index = 0; index < census.Bodies.Count; index++)
            {
                var gameObject = census.Objects[index];
                if (!IsTopmost(gameObject, census)) continue;
                var transform = census.Bodies[index].transform;
                if (!IsCandidate(transform, player, census)) continue;
                var key = hasher.Finish(PathState(transform, sceneState, census), RigidbodyComponent);
                into.Add(new NetworkSceneObject(handle, key, new UnityNetworkWorldObject(gameObject, entities, census.Bodies[index])));
            }
        }

        /// <summary>
        /// Whether no rigidbody is above an object's own. The engine looks through the parents itself, so none of them is
        /// made a managed object only to be looked at; and an object holds one rigidbody at most, so its own is the only
        /// one found when none is above it.
        /// </summary>
        private static bool IsTopmost(GameObject gameObject, Census census)
        {
            gameObject.GetComponentsInParent(true, census.Above);
            var topmost = census.Above.Count <= 1;
            census.Above.Clear();
            return topmost;
        }

        /// <summary>Whether a topmost rigidbody is a candidate: neither inside the local player nor holding it, and not under a marked network root.</summary>
        private static bool IsCandidate(Transform transform, Transform? player, Census census)
        {
            if (player != null && (transform.IsChildOf(player) || player.IsChildOf(transform))) return false;
            if (census.Marked.Count == 0) return true;
            for (var current = transform; current != null; current = current.parent)
            {
                if (census.Marked.Contains(current.GetInstanceID())) return false;
            }

            return true;
        }

        /// <summary>Gets the hash state after an object's whole path, from the nearest ancestor already hashed down to it.</summary>
        private ulong PathState(Transform transform, ulong sceneState, Census census)
        {
            var chain = census.Chain;
            var ids = census.ChainIds;
            var state = sceneState;
            var cached = false;
            var current = transform;
            while (current != null)
            {
                var id = current.GetInstanceID();
                if (census.Paths.TryGetValue(id, out state))
                {
                    cached = true;
                    break;
                }

                chain.Add(current);
                ids.Add(id);
                current = current.parent;
            }

            if (!cached) state = sceneState;

            // From the top down: the first segment's parent is the ancestor hashed already, or none at a root.
            var parent = cached ? current : null;
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                var segment = chain[index];
                state = hasher.Segment(state, segment.name ?? string.Empty, SiblingIndex(segment, ids[index], parent, census), first: parent == null);
                census.Paths[ids[index]] = state;
                parent = segment;
            }

            chain.Clear();
            ids.Clear();
            return state;
        }

        /// <summary>
        /// Gets an object's index among its siblings. The engine finds one by searching the parent's children, so a wide
        /// parent searched often has every child's index read in one pass instead, and one searched seldom is left alone.
        /// </summary>
        private static int SiblingIndex(Transform segment, int id, Transform? parent, Census census)
        {
            if (parent == null) return segment.GetSiblingIndex();
            if (census.Indices.TryGetValue(id, out var known)) return known;
            var children = parent.childCount;
            if (children <= WideParent) return segment.GetSiblingIndex();
            var parentId = parent.GetInstanceID();
            census.Searches.TryGetValue(parentId, out var searched);
            if (searched < SearchesBeforeOnePass)
            {
                census.Searches[parentId] = searched + 1;
                return segment.GetSiblingIndex();
            }

            for (var child = 0; child < children; child++) census.Indices[parent.GetChild(child).GetInstanceID()] = child;
            return census.Indices.TryGetValue(id, out known) ? known : segment.GetSiblingIndex();
        }

        /// <summary>One census's working sets, made for it and let go of with it, so nothing of a load outlives it.</summary>
        private sealed class Census
        {
            internal List<Rigidbody> Bodies { get; } = new List<Rigidbody>();

            /// <summary>Gets each listed rigidbody's object, read once.</summary>
            internal List<GameObject> Objects { get; } = new List<GameObject>();

            /// <summary>Gets the rigidbodies the engine found at and above one object.</summary>
            internal List<Rigidbody> Above { get; } = new List<Rigidbody>();

            /// <summary>Gets the marked network roots, by instance id, in every loaded scene.</summary>
            internal HashSet<int> Marked { get; } = new HashSet<int>();

            /// <summary>Gets the hash state after each object's path, by instance id, for the scene being keyed.</summary>
            internal Dictionary<int, ulong> Paths { get; } = new Dictionary<int, ulong>();

            /// <summary>Gets the sibling indices read from wide parents, by instance id.</summary>
            internal Dictionary<int, int> Indices { get; } = new Dictionary<int, int>();

            /// <summary>Gets how often each wide parent was searched, by instance id.</summary>
            internal Dictionary<int, int> Searches { get; } = new Dictionary<int, int>();

            internal List<Transform> Chain { get; } = new List<Transform>();

            internal List<int> ChainIds { get; } = new List<int>();

            /// <summary>Starts one scene's census; the marks, read once for every scene, stay.</summary>
            internal void BeginScene()
            {
                Bodies.Clear();
                Objects.Clear();
                Paths.Clear();
                Indices.Clear();
                Searches.Clear();
            }
        }
    }
}
