using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    /// <summary>One scene object the index keys: its key, its object, its pose at the census and its network binding.</summary>
    internal sealed class SceneObjectSlot
    {
        internal SceneObjectSlot(SceneObjectKey key, int sceneHandle, INetworkWorldObject sceneObject, bool originKnown)
        {
            Key = key;
            SceneHandle = sceneHandle;
            Object = sceneObject;
            OriginKnown = originKnown;
        }

        internal SceneObjectKey Key { get; }

        internal int SceneHandle { get; }

        internal INetworkWorldObject Object { get; }

        /// <summary>
        /// Gets whether the census saw the object at its scene's load pose: a scene censused as it loaded. A census at a
        /// promotion comes after the solo session may have moved anything, so a baseline carries such an object's pose.
        /// </summary>
        internal bool OriginKnown { get; }

        /// <summary>Gets whether the census read the object's pose; first motion compares against it.</summary>
        internal bool HasCensusPose { get; private set; }

        internal NetQuantizedPosition CensusPosition { get; private set; }

        internal uint CensusRotation { get; private set; }

        /// <summary>Gets the network entity the object is bound to, or none while it has not moved.</summary>
        internal NetworkEntityId Bound { get; private set; }

        /// <summary>Gets whether the object is gone on the host, or the host said so to a joined player.</summary>
        internal bool Removed { get; private set; }

        /// <summary>Gets whether a scene loaded later produced the same key, so the key binds nothing new.</summary>
        internal bool Ambiguous { get; private set; }

        internal bool IsBound => Bound.IsValid;

        /// <summary>Gets whether the slot may still bind: unbound, present and unambiguous.</summary>
        internal bool IsBindable => !Bound.IsValid && !Removed && !Ambiguous;

        internal void RecordCensusPose(in NetPose pose)
        {
            CensusPosition = NetQuantization.QuantizePosition(pose.Position);
            CensusRotation = NetQuantization.QuantizeRotation(pose.Rotation);
            HasCensusPose = true;
        }

        internal void Bind(NetworkEntityId id)
        {
            if (!id.IsValid) throw new ArgumentException("A binding needs a valid id.", nameof(id));
            Bound = id;
        }

        internal void Unbind() => Bound = NetworkEntityId.None;

        internal void MarkRemoved() => Removed = true;

        internal void MarkAmbiguous() => Ambiguous = true;

        /// <summary>Whether <paramref name="pose"/> differs from the census pose on the quantization grid.</summary>
        internal bool HasMovedTo(in NetPose pose)
        {
            if (!HasCensusPose) return true;
            return !NetQuantization.QuantizePosition(pose.Position).Equals(CensusPosition) ||
                   NetQuantization.QuantizeRotation(pose.Rotation) != CensusRotation;
        }
    }

    /// <summary>
    /// The scene objects every peer loads from the same scenes, keyed by the replication model's
    /// <see cref="SceneObjectKey"/>, so the host and its players name an object without a spawn message. A key that more
    /// than one object produces is excluded, every copy of it, rather than guessing which is which; a key a scene loaded
    /// later collides with keeps its existing binding and binds nothing new. The host binds a key to a network entity
    /// lazily, when the object first moves away from its census pose; a joined player binds the keys the host announces.
    /// Main thread only.
    /// </summary>
    internal sealed class SceneObjectIndex
    {
        private readonly Dictionary<SceneObjectKey, SceneObjectSlot> slots = new Dictionary<SceneObjectKey, SceneObjectSlot>();
        private readonly Dictionary<SceneObjectKey, List<int>> excluded = new Dictionary<SceneObjectKey, List<int>>();
        private readonly List<SceneObjectSlot> scan = new List<SceneObjectSlot>();
        private readonly Dictionary<SceneObjectKey, int> batch = new Dictionary<SceneObjectKey, int>();
        private readonly HashSet<int> indexedScenes = new HashSet<int>();
        private int scanCursor;

        /// <summary>Gets how many keys bind one object each.</summary>
        internal int UniqueCount => slots.Count;

        /// <summary>Gets how many keys are excluded because more than one object produces them.</summary>
        internal int ExcludedCount => excluded.Count;

        /// <summary>
        /// Adds the objects one census found. Their keys are computed now, and each object's pose is read for first motion.
        /// A key produced twice, within the census or with one already known, is excluded. <paramref name="originKnown"/>
        /// says whether the census came as the scenes loaded, so the poses it read are the scenes' own. A scene already keyed
        /// is passed over: a census of every loaded scene may meet one whose own load is reported after it.
        /// </summary>
        internal void Add(IReadOnlyList<NetworkSceneObject> objects, bool originKnown = true)
        {
            if (objects == null) throw new ArgumentNullException(nameof(objects));
            batch.Clear();
            var keys = new SceneObjectKey[objects.Count];
            for (var index = 0; index < objects.Count; index++)
            {
                if (indexedScenes.Contains(objects[index].SceneHandle)) continue;
                keys[index] = SceneObjectKey.Compute(objects[index].Descriptor);
                batch.TryGetValue(keys[index], out var seen);
                batch[keys[index]] = seen + 1;
            }

            for (var index = 0; index < objects.Count; index++)
            {
                var key = keys[index];
                var sceneObject = objects[index];
                if (indexedScenes.Contains(sceneObject.SceneHandle))
                {
                    AlreadyIndexed++;
                    continue;
                }

                if (excluded.TryGetValue(key, out var scenes))
                {
                    scenes.Add(sceneObject.SceneHandle);
                    continue;
                }

                if (slots.TryGetValue(key, out var existing))
                {
                    Exclude(existing, sceneObject.SceneHandle);
                    continue;
                }

                if (batch[key] > 1)
                {
                    excluded.Add(key, new List<int> { sceneObject.SceneHandle });
                    continue;
                }

                var slot = new SceneObjectSlot(key, sceneObject.SceneHandle, sceneObject.Object, originKnown);
                if (sceneObject.Object.TryReadPose(out var pose)) slot.RecordCensusPose(pose);
                slots.Add(key, slot);
                scan.Add(slot);
            }

            for (var index = 0; index < objects.Count; index++) indexedScenes.Add(objects[index].SceneHandle);
            batch.Clear();
        }

        /// <summary>Gets how many objects a census offered again, from a scene already keyed, and the index passed over.</summary>
        internal int AlreadyIndexed { get; private set; }

        /// <summary>
        /// Forgets the objects of an unloaded scene. Slots that were bound are appended to <paramref name="unbound"/>, so the
        /// caller can end their entities.
        /// </summary>
        internal void RemoveScene(int sceneHandle, List<SceneObjectSlot>? unbound)
        {
            indexedScenes.Remove(sceneHandle);
            List<SceneObjectKey>? gone = null;
            foreach (var pair in slots)
            {
                if (pair.Value.SceneHandle != sceneHandle) continue;
                (gone ??= new List<SceneObjectKey>()).Add(pair.Key);
                if (pair.Value.IsBound) unbound?.Add(pair.Value);
            }

            if (gone != null)
            {
                foreach (var key in gone) slots.Remove(key);
                scan.RemoveAll(slot => slot.SceneHandle == sceneHandle);
                scanCursor = 0;
            }

            List<SceneObjectKey>? emptied = null;
            foreach (var pair in excluded)
            {
                pair.Value.RemoveAll(handle => handle == sceneHandle);
                if (pair.Value.Count == 0) (emptied ??= new List<SceneObjectKey>()).Add(pair.Key);
            }

            if (emptied != null)
            {
                foreach (var key in emptied) excluded.Remove(key);
            }
        }

        /// <summary>Finds the one object a key names; excluded, ambiguous, removed and unknown keys are not found.</summary>
        internal bool TryGetBindable(SceneObjectKey key, out SceneObjectSlot slot)
        {
            if (slots.TryGetValue(key, out var found) && !found.Ambiguous && !found.Removed && !excluded.ContainsKey(key))
            {
                slot = found;
                return true;
            }

            slot = null!;
            return false;
        }

        /// <summary>Finds the slot of a key, bound or not; excluded and unknown keys are not found.</summary>
        internal bool TryGet(SceneObjectKey key, out SceneObjectSlot slot)
        {
            var found = slots.TryGetValue(key, out var value) && !excluded.ContainsKey(key);
            slot = found ? value! : null!;
            return found;
        }

        internal bool IsExcluded(SceneObjectKey key) => excluded.ContainsKey(key);

        /// <summary>
        /// Host: checks at most <paramref name="maximum"/> unbound objects, taking turns, and reports those that moved away
        /// from their census pose and those that are gone. Nothing is bound here: the caller allocates the ids.
        /// </summary>
        internal void ScanForMotion(int maximum, List<SceneObjectSlot> moved, List<SceneObjectSlot> removed)
        {
            if (moved == null) throw new ArgumentNullException(nameof(moved));
            if (removed == null) throw new ArgumentNullException(nameof(removed));
            CompactScan();
            var count = scan.Count;
            if (count == 0 || maximum <= 0) return;
            var checks = Math.Min(maximum, count);
            for (var step = 0; step < checks; step++)
            {
                if (scanCursor >= scan.Count) scanCursor = 0;
                var slot = scan[scanCursor++];
                if (!slot.IsBindable) continue;
                if (!slot.Object.IsAlive || !slot.Object.TryReadPose(out var pose))
                {
                    removed.Add(slot);
                    continue;
                }

                if (slot.HasMovedTo(pose)) moved.Add(slot);
            }
        }

        /// <summary>Gets how many objects may still bind on first motion.</summary>
        internal int UnboundCount
        {
            get
            {
                var count = 0;
                foreach (var slot in scan)
                {
                    if (slot.IsBindable) count++;
                }

                return count;
            }
        }

        /// <summary>Appends, in ascending order, the keys of objects that are gone, for a baseline.</summary>
        internal void CollectRemovedKeys(List<SceneObjectKey> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            var start = into.Count;
            foreach (var slot in slots.Values)
            {
                if (slot.Removed) into.Add(slot.Key);
            }

            into.Sort(start, into.Count - start, null);
        }

        /// <summary>
        /// Appends, in ascending key order, the unbound objects whose load pose the census did not see: a baseline carries
        /// their pose now, since a joining player loads them where the scene put them.
        /// </summary>
        internal void CollectUnknownOrigins(List<SceneObjectSlot> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            var start = into.Count;
            foreach (var slot in slots.Values)
            {
                if (!slot.OriginKnown && slot.IsBindable) into.Add(slot);
            }

            into.Sort(start, into.Count - start, SlotOrder.Instance);
        }

        /// <summary>Forgets every object and binding.</summary>
        internal void Clear()
        {
            slots.Clear();
            excluded.Clear();
            indexedScenes.Clear();
            scan.Clear();
            scanCursor = 0;
        }

        private void Exclude(SceneObjectSlot existing, int sceneHandle)
        {
            // Either way the key binds nothing new, and the first-motion scan skips the slot from now on.
            existing.MarkAmbiguous();
            if (existing.IsBound || existing.Removed) return;

            // An unbound object loses its key to the duplicate, as every copy of a duplicated key does.
            slots.Remove(existing.Key);
            excluded.Add(existing.Key, new List<int> { existing.SceneHandle, sceneHandle });
        }

        private sealed class SlotOrder : IComparer<SceneObjectSlot>
        {
            internal static readonly SlotOrder Instance = new SlotOrder();

            public int Compare(SceneObjectSlot? left, SceneObjectSlot? right) => left!.Key.CompareTo(right!.Key);
        }

        private void CompactScan()
        {
            var bindable = 0;
            foreach (var slot in scan)
            {
                if (slot.IsBindable) bindable++;
            }

            if (bindable * 2 >= scan.Count) return;
            scan.RemoveAll(slot => !slot.IsBindable || !slots.ContainsKey(slot.Key));
            scanCursor = 0;
        }
    }
}
