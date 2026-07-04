using System.Collections.Generic;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.Sandbox
{
    /// <summary>
    /// Tracks everything the sandbox spawns — props (plain scene rigidbodies) and robots (RobotKit
    /// handles) — as one LIFO undo stack, and owns the bulk operations over them (freeze, cleanup).
    /// Entries whose object died some other way (scene change, robot killed) are skipped, not errors.
    /// </summary>
    internal sealed class SpawnRegistry
    {
        internal enum EntryKind
        {
            Prop,
            Robot
        }

        internal sealed class SpawnedEntry
        {
            public SpawnedEntry(EntryKind kind, GameObject? prop, IRobotAgent? robot, string displayName, string? targetName)
            {
                Kind = kind;
                Prop = prop;
                Robot = robot;
                DisplayName = displayName;
                TargetName = targetName;
            }

            public EntryKind Kind { get; }
            public GameObject? Prop { get; }
            public IRobotAgent? Robot { get; }
            public string DisplayName { get; }

            /// <summary>The objective-target name registered for this spawn, or null when it has none.</summary>
            public string? TargetName { get; }

            public bool IsAlive => Kind == EntryKind.Prop ? Prop != null : Robot != null && Robot.IsAlive;
        }

        private readonly List<SpawnedEntry> entries = new List<SpawnedEntry>();
        private readonly int maxObjects;

        /// <summary>
        /// Raised whenever an entry leaves the registry for any reason (undo, cleanup, or pruning a spawn that died
        /// elsewhere) so the controller can unregister its objective target.
        /// </summary>
        public event System.Action<SpawnedEntry>? EntryRemoved;

        public SpawnRegistry(int maxObjects)
        {
            this.maxObjects = maxObjects;
        }

        public int PropCount
        {
            get
            {
                var count = 0;
                foreach (var entry in entries)
                {
                    if (entry.Kind == EntryKind.Prop && entry.IsAlive)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public int RobotCount
        {
            get
            {
                var count = 0;
                foreach (var entry in entries)
                {
                    if (entry.Kind == EntryKind.Robot && entry.IsAlive)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public int LiveCount => PropCount + RobotCount;

        /// <summary>False when the spawn cap is reached (the caller should refuse the spawn and say why).</summary>
        public bool HasCapacity => LiveCount < maxObjects;

        public void PushProp(GameObject prop, string displayName, string? targetName = null)
        {
            Prune();
            entries.Add(new SpawnedEntry(EntryKind.Prop, prop, null, displayName, targetName));
        }

        public void PushRobot(IRobotAgent robot, string displayName, string? targetName = null)
        {
            Prune();
            entries.Add(new SpawnedEntry(EntryKind.Robot, null, robot, displayName, targetName));
        }

        /// <summary>
        /// Destroys the most recent still-alive spawn (LIFO); dead entries in between are dropped silently.
        /// Returns the display name of what was undone, or null when there was nothing left to undo.
        /// </summary>
        public string? Undo()
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var entry = entries[index];
                entries.RemoveAt(index);
                EntryRemoved?.Invoke(entry);
                if (!entry.IsAlive)
                {
                    continue;
                }

                DestroyEntry(entry);
                return entry.DisplayName;
            }

            return null;
        }

        /// <summary>
        /// Toggles physics freeze (Rigidbody.isKinematic) on the spawned prop the camera looks at.
        /// Only objects this registry spawned are eligible — the world and game objects stay untouched.
        /// Returns null when the crosshair hits nothing spawned; otherwise "frozen"/"unfrozen".
        /// </summary>
        public string? ToggleFreezeUnderCrosshair(Camera camera, float maxDistance)
        {
            var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!Physics.Raycast(ray, out var hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                return null;
            }

            var body = hit.rigidbody != null ? hit.rigidbody : hit.collider.GetComponentInParent<Rigidbody>();
            if (body == null || !IsRegisteredProp(body.gameObject))
            {
                return null;
            }

            body.isKinematic = !body.isKinematic;
            if (!body.isKinematic)
            {
                body.WakeUp();
            }

            return body.isKinematic ? "frozen" : "unfrozen";
        }

        /// <summary>Freezes (true) or unfreezes (false) every live spawned prop. Returns how many changed.</summary>
        public int SetAllFrozen(bool frozen)
        {
            var changed = 0;
            foreach (var entry in entries)
            {
                if (entry.Kind != EntryKind.Prop || entry.Prop == null)
                {
                    continue;
                }

                var body = entry.Prop.GetComponent<Rigidbody>();
                if (body == null || body.isKinematic == frozen)
                {
                    continue;
                }

                body.isKinematic = frozen;
                if (!frozen)
                {
                    body.WakeUp();
                }

                changed++;
            }

            return changed;
        }

        /// <summary>Destroys everything the sandbox spawned. Returns how many live objects were removed.</summary>
        public int DestroyAll()
        {
            var removed = 0;
            foreach (var entry in entries)
            {
                EntryRemoved?.Invoke(entry);
                if (!entry.IsAlive)
                {
                    continue;
                }

                DestroyEntry(entry);
                removed++;
            }

            entries.Clear();
            return removed;
        }

        private bool IsRegisteredProp(GameObject candidate)
        {
            foreach (var entry in entries)
            {
                if (entry.Kind == EntryKind.Prop && entry.Prop == candidate)
                {
                    return true;
                }
            }

            return false;
        }

        private static void DestroyEntry(SpawnedEntry entry)
        {
            if (entry.Kind == EntryKind.Robot)
            {
                // The RobotKit handle owns native teardown (locomotion stop, pooled cleanup).
                entry.Robot?.Despawn();
                return;
            }

            if (entry.Prop != null)
            {
                Object.Destroy(entry.Prop);
            }
        }

        // Undo/counting stays O(live entries): drop stack entries whose object already died elsewhere
        // (scene change, a robot killed by gameplay) so the list cannot grow without bound in a long session.
        private void Prune()
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                if (!entries[index].IsAlive)
                {
                    EntryRemoved?.Invoke(entries[index]);
                    entries.RemoveAt(index);
                }
            }
        }
    }
}
