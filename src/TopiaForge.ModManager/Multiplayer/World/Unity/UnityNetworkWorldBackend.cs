using System;
using System.Collections.Generic;
using System.Reflection;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The network world's engine side over Unity: the scene census keys the loaded scenes' topmost objects that carry a
    /// rigidbody (the other part of this class), the native census finds what a joined player turns off, and network
    /// roots are the root game objects the runtime's entity registry resolves. The local player's hierarchy is never
    /// touched. Main thread only.
    /// </summary>
    internal sealed partial class UnityNetworkWorldBackend : INetworkWorldBackend, INetworkMotionBackend, IBlockerBackend
    {
        public OperationResult<IBlocker> CreateBlocker(BlockerRequest request) => UnityBlocker.Create(entities, request);

        /// <summary>The primary component every rigidbody root is keyed by.</summary>
        internal const string RigidbodyComponent = "UnityEngine.Rigidbody";

        private readonly UnityEntityRegistry entities;
        private readonly List<GameObject> roots = new List<GameObject>();
        private readonly List<Rigidbody> bodies = new List<Rigidbody>();
        private readonly List<Behaviour> behaviours = new List<Behaviour>();

        internal UnityNetworkWorldBackend(UnityEntityRegistry entities)
        {
            this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
        }

        public void CollectNativeAuthority(int? sceneHandle, List<NativeAuthorityTarget> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            UnityMainThreadGuard.AssertCurrent();
            var player = UnityNativeAuthority.PlayerTransform();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded || (sceneHandle.HasValue && scene.handle != sceneHandle.Value)) continue;
                roots.Clear();
                scene.GetRootGameObjects(roots);
                foreach (var root in roots) UnityNativeAuthority.Collect(root, player, bodies, behaviours, into);
            }

            roots.Clear();
            bodies.Clear();
            behaviours.Clear();
        }

        public INetworkWorldObject? Adopt(IEntity entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            UnityMainThreadGuard.AssertCurrent();
            return entities.TryGetGameObject(entity, out var gameObject) && gameObject != null
                ? new UnityNetworkWorldObject(gameObject, entities)
                : null;
        }

        public bool TryGetNetworkId(IEntity entity, out NetworkEntityId id)
        {
            id = NetworkEntityId.None;
            if (entity == null) return false;
            UnityMainThreadGuard.AssertCurrent();
            if (!entities.TryGetGameObject(entity, out var gameObject) || gameObject == null) return false;
            for (var current = gameObject.transform; current != null; current = current.parent)
            {
                var mark = current.GetComponent<TopiaForgeNetworkIdentity>();
                if (mark != null && mark.NetworkId != 0) return NetworkEntityId.TryCreate(mark.NetworkId, out id);
            }

            return false;
        }

        public void PrepareReplica(INetworkWorldObject replica)
        {
            if (!(replica is UnityNetworkWorldObject native) || !native.IsAlive) return;
            bodies.Clear();
            native.GameObject.GetComponentsInChildren(true, bodies);
            foreach (var body in bodies)
            {
                if (body != null) body.isKinematic = true;
            }

            bodies.Clear();
        }

        public INativeAuthoritySwitch? PresenceSwitch(INetworkWorldObject sceneObject) =>
            sceneObject is UnityNetworkWorldObject native && native.IsAlive ? new UnityNativeAuthority.PresenceSwitch(native.GameObject) : null;
    }

    /// <summary>A network root over a Unity game object: its pose, its entity and its mark.</summary>
    internal sealed class UnityNetworkWorldObject : INetworkMotionBody
    {
        private readonly UnityEntityRegistry entities;
        private readonly Rigidbody? body;
        private IEntity? entity;

        internal UnityNetworkWorldObject(GameObject gameObject, UnityEntityRegistry entities, Rigidbody? body = null)
        {
            GameObject = gameObject;
            this.entities = entities;
            this.body = body != null ? body : gameObject.GetComponent<Rigidbody>();
        }

        internal GameObject GameObject { get; }

        public bool IsAlive => GameObject != null;

        public bool IsNetworkRoot
        {
            get
            {
                if (GameObject == null) return false;
                var mark = GameObject.GetComponent<TopiaForgeNetworkIdentity>();
                return mark != null && mark.NetworkId != 0;
            }
        }

        public IEntity Entity => entity != null && entity.IsAlive ? entity : entity = entities.GetOrCreate(GameObject);

        public bool IsKinematic
        {
            get => body == null || body.isKinematic;
            set { if (body != null) body.isKinematic = value; }
        }

        public bool TryReadMotion(out NetPose pose, out NetVector3 angularVelocity)
        {
            angularVelocity = default;
            if (!TryReadPose(out pose) || body == null) return false;
            var angular = body.angularVelocity;
            angularVelocity = new NetVector3(angular.x, angular.y, angular.z);
            return true;
        }

        public void WriteMotion(in NetPose pose, NetVector3 angularVelocity)
        {
            WritePose(pose);
            if (body == null || body.isKinematic) return;
            body.linearVelocity = new Vector3(pose.Velocity.X, pose.Velocity.Y, pose.Velocity.Z);
            body.angularVelocity = new Vector3(angularVelocity.X, angularVelocity.Y, angularVelocity.Z);
        }

        public bool TryReadPose(out NetPose pose)
        {
            pose = default;
            if (GameObject == null) return false;
            var transform = GameObject.transform;
            var position = transform.position;
            var rotation = transform.rotation;
            var at = new NetVector3(position.x, position.y, position.z);
            var turned = new NetQuaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            if (body != null && !body.isKinematic)
            {
                var velocity = body.linearVelocity;
                pose = new NetPose(at, turned, new NetVector3(velocity.x, velocity.y, velocity.z));
                return true;
            }

            pose = new NetPose(at, turned);
            return true;
        }

        public void WritePose(in NetPose pose)
        {
            if (GameObject == null) return;
            GameObject.transform.SetPositionAndRotation(
                new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z),
                new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W));
        }

        public void SetNetworkIdentity(NetworkEntityId id)
        {
            if (GameObject == null) return;
            var mark = GameObject.GetComponent<TopiaForgeNetworkIdentity>();
            if (!id.IsValid)
            {
                if (mark == null) return;

                // The id goes at once, so a lookup in the same frame finds nothing; the component goes at the end of it.
                mark.NetworkId = 0;
                UnityEngine.Object.Destroy(mark);
                return;
            }

            if (mark == null) mark = GameObject.AddComponent<TopiaForgeNetworkIdentity>();
            mark.NetworkId = id.Value;
        }
    }

    /// <summary>
    /// The native systems a joined player turns off, found by name as the game names them: robot brains (Behavior
    /// Designer's behaviour tree, and the language model behind <c>LLMAgent</c>), locomotion (<c>LocomotionController</c>,
    /// which also follows paths), path followers (<c>NavMeshAgent</c>), and spawners (<c>PooledSpawner</c>). The game's
    /// own path service answers requests only, and nothing on a joined player asks it once brains and locomotion are
    /// off, so it stays on.
    /// </summary>
    internal static class UnityNativeAuthority
    {
        private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly Type? LlmAgentType = Type.GetType("LLMAgent, GameCode", false);
        private static readonly Type? LocomotionType = Type.GetType("LocomotionController, GameCode", false);
        private static readonly Type? SpawnerType = Type.GetType("PooledSpawner, GameCode", false);
        private static readonly Type? PlayerType = Type.GetType("PlayerController, GameCode", false);
        private static readonly FieldInfo? LlmDisabledField = LlmAgentType?.GetField("llmDisabled", InstanceFlags);
        private static readonly MethodInfo? FindPlayerMethod = PlayerType?.GetMethod("FindPlayer", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

        /// <summary>Gets the local player's transform, or null when there is none.</summary>
        internal static Transform? PlayerTransform()
        {
            try
            {
                return FindPlayerMethod?.Invoke(null, Array.Empty<object>()) is Component player && player != null ? player.transform : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Appends every native switch under <paramref name="root"/>, leaving the local player's hierarchy alone.</summary>
        internal static void Collect(GameObject root, Transform? player, List<Rigidbody> bodies, List<Behaviour> behaviours, List<NativeAuthorityTarget> into)
        {
            bodies.Clear();
            root.GetComponentsInChildren(true, bodies);
            foreach (var body in bodies)
            {
                if (body == null || BelongsToPlayer(body.transform, player)) continue;
                into.Add(new NativeAuthorityTarget(new KinematicSwitch(body), true));
            }

            behaviours.Clear();
            root.GetComponentsInChildren(true, behaviours);
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null || BelongsToPlayer(behaviour.transform, player)) continue;
                var type = behaviour.GetType();
                if (LlmAgentType != null && LlmAgentType.IsAssignableFrom(type))
                {
                    if (LlmDisabledField != null && LlmDisabledField.FieldType == typeof(bool))
                    {
                        into.Add(new NativeAuthorityTarget(new FieldSwitch(behaviour, LlmDisabledField, NativeAuthorityKind.RobotBrain), true));
                    }

                    continue;
                }

                var kind = KindOf(type);
                if (kind.HasValue) into.Add(new NativeAuthorityTarget(new EnabledSwitch(behaviour, kind.Value), false));
            }
        }

        private static NativeAuthorityKind? KindOf(Type type)
        {
            if (LocomotionType != null && LocomotionType.IsAssignableFrom(type)) return NativeAuthorityKind.Locomotion;
            if (SpawnerType != null && SpawnerType.IsAssignableFrom(type)) return NativeAuthorityKind.Spawner;
            for (var current = type; current != null; current = current.BaseType)
            {
                if (string.Equals(current.Name, "BehaviorTree", StringComparison.Ordinal)) return NativeAuthorityKind.RobotBrain;
                if (string.Equals(current.Name, "NavMeshAgent", StringComparison.Ordinal)) return NativeAuthorityKind.Pathing;
            }

            return null;
        }

        // A body holding the player, as a parent rigidbody might, is the player's own as much as one inside it.
        private static bool BelongsToPlayer(Transform candidate, Transform? player) =>
            player != null && (candidate == player || candidate.IsChildOf(player) || player.IsChildOf(candidate));

        internal sealed class KinematicSwitch : INativeAuthoritySwitch
        {
            private readonly Rigidbody body;

            internal KinematicSwitch(Rigidbody body)
            {
                this.body = body;
            }

            public NativeAuthorityKind Kind => NativeAuthorityKind.SceneBody;

            public object Target => body;

            public bool IsAlive => body != null;

            public bool Read() => body.isKinematic;

            public void Write(bool value) => body.isKinematic = value;
        }

        internal sealed class EnabledSwitch : INativeAuthoritySwitch
        {
            private readonly Behaviour behaviour;

            internal EnabledSwitch(Behaviour behaviour, NativeAuthorityKind kind)
            {
                this.behaviour = behaviour;
                Kind = kind;
            }

            public NativeAuthorityKind Kind { get; }

            public object Target => behaviour;

            public bool IsAlive => behaviour != null;

            public bool Read() => behaviour.enabled;

            public void Write(bool value) => behaviour.enabled = value;
        }

        internal sealed class FieldSwitch : INativeAuthoritySwitch
        {
            private readonly Component component;
            private readonly FieldInfo field;

            internal FieldSwitch(Component component, FieldInfo field, NativeAuthorityKind kind)
            {
                this.component = component;
                this.field = field;
                Kind = kind;
            }

            public NativeAuthorityKind Kind { get; }

            public object Target => component;

            public bool IsAlive => component != null;

            public bool Read() => field.GetValue(component) is bool value && value;

            public void Write(bool value) => field.SetValue(component, value);
        }

        internal sealed class PresenceSwitch : INativeAuthoritySwitch
        {
            private readonly GameObject gameObject;

            internal PresenceSwitch(GameObject gameObject)
            {
                this.gameObject = gameObject;
            }

            public NativeAuthorityKind Kind => NativeAuthorityKind.SceneObjectPresence;

            public object Target => gameObject;

            public bool IsAlive => gameObject != null;

            public bool Read() => gameObject.activeSelf;

            public void Write(bool value) => gameObject.SetActive(value);
        }
    }
}
