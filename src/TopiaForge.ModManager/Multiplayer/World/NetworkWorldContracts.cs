using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>A network root's world pose: position and rotation, and its body's linear velocity while it simulates.</summary>
    internal readonly struct NetPose
    {
        internal NetPose(NetVector3 position, NetQuaternion rotation)
        {
            Position = position;
            Rotation = rotation;
            HasVelocity = false;
            Velocity = default;
        }

        internal NetPose(NetVector3 position, NetQuaternion rotation, NetVector3 velocity)
        {
            Position = position;
            Rotation = rotation;
            HasVelocity = true;
            Velocity = velocity;
        }

        internal NetVector3 Position { get; }

        internal NetQuaternion Rotation { get; }

        internal bool HasVelocity { get; }

        internal NetVector3 Velocity { get; }
    }

    /// <summary>
    /// One network root in this process: a scene object, an SDK spawn or world content the network world tracks, or a
    /// replica it built. The engine side implements it over a root game object; every member runs on the main thread.
    /// </summary>
    internal interface INetworkWorldObject
    {
        /// <summary>Gets whether the object still exists.</summary>
        bool IsAlive { get; }

        /// <summary>Gets the entity mods see for this object, as the runtime's entity registry names it.</summary>
        IEntity Entity { get; }

        /// <summary>Reads the object's world pose; false once it is gone.</summary>
        bool TryReadPose(out NetPose pose);

        /// <summary>Moves the object to <paramref name="pose"/>. A replica is kinematic, so only this moves it.</summary>
        void WritePose(in NetPose pose);

        /// <summary>
        /// Marks the object as the network root <paramref name="id"/>, or removes the mark for
        /// <see cref="NetworkEntityId.None"/>. The mark is what the session's lookups find.
        /// </summary>
        void SetNetworkIdentity(NetworkEntityId id);
    }

    /// <summary>One network root candidate a scene census found: the scene it belongs to, what keys it, and the object.</summary>
    internal readonly struct NetworkSceneObject
    {
        internal NetworkSceneObject(int sceneHandle, SceneObjectDescriptor descriptor, INetworkWorldObject sceneObject)
        {
            SceneHandle = sceneHandle;
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Object = sceneObject ?? throw new ArgumentNullException(nameof(sceneObject));
        }

        /// <summary>Gets the engine's handle of the loaded scene the object belongs to.</summary>
        internal int SceneHandle { get; }

        internal SceneObjectDescriptor Descriptor { get; }

        internal INetworkWorldObject Object { get; }
    }

    /// <summary>What a native authority switch controls on a joined player.</summary>
    internal enum NativeAuthorityKind
    {
        /// <summary>A scene rigidbody, made kinematic so only the host's samples move it.</summary>
        SceneBody = 0,

        /// <summary>A robot's native brain: its behaviour tree, or its language model.</summary>
        RobotBrain = 1,

        /// <summary>A robot's native locomotion, which also follows its paths.</summary>
        Locomotion = 2,

        /// <summary>A native path follower.</summary>
        Pathing = 3,

        /// <summary>A native spawner, which would spawn objects the host never sees.</summary>
        Spawner = 4,

        /// <summary>A scene object the host removed, hidden here.</summary>
        SceneObjectPresence = 5
    }

    /// <summary>
    /// One reversible native setting the network world turns off on a joined player. The suppressor reads it before it
    /// writes, and writes the value it read back when the player leaves. Main thread only.
    /// </summary>
    internal interface INativeAuthoritySwitch
    {
        NativeAuthorityKind Kind { get; }

        /// <summary>Gets the native object the switch belongs to, so the same setting is never suppressed twice.</summary>
        object Target { get; }

        /// <summary>Gets whether the native object still exists.</summary>
        bool IsAlive { get; }

        bool Read();

        void Write(bool value);
    }

    /// <summary>A native switch and the value it takes while this process is a joined player.</summary>
    internal readonly struct NativeAuthorityTarget
    {
        internal NativeAuthorityTarget(INativeAuthoritySwitch nativeSwitch, bool suppressedValue)
        {
            Switch = nativeSwitch ?? throw new ArgumentNullException(nameof(nativeSwitch));
            SuppressedValue = suppressedValue;
        }

        internal INativeAuthoritySwitch Switch { get; }

        internal bool SuppressedValue { get; }
    }

    /// <summary>
    /// What the network world needs from the game engine: the scene census, the native systems a joined player turns
    /// off, and the network roots of spawned entities. The Unity side implements it over the loaded scenes and the
    /// runtime's entity registry; tests implement it over a scene of their own. Main thread only.
    /// </summary>
    internal interface INetworkWorldBackend
    {
        /// <summary>
        /// Appends the network root candidates of every loaded scene, or of the scene <paramref name="sceneHandle"/> names:
        /// each topmost object that carries a rigidbody, keyed by its scene, its path and its rigidbody. Objects already
        /// marked as network roots, and the local player, are left out.
        /// </summary>
        void CollectSceneObjects(int? sceneHandle, List<NetworkSceneObject> into);

        /// <summary>
        /// Appends the native systems a joined player turns off in every loaded scene, or in one: every scene rigidbody,
        /// robot brains and locomotion, path followers and spawners. The local player and network roots this
        /// process built are left out.
        /// </summary>
        void CollectNativeAuthority(int? sceneHandle, List<NativeAuthorityTarget> into);

        /// <summary>Wraps the root of a spawned entity as a network object, or returns null when it has none here.</summary>
        INetworkWorldObject? Adopt(IEntity entity);

        /// <summary>
        /// Finds the network root an entity belongs to, through the runtime's entity registry: the entity's own object or
        /// the nearest parent that carries a network identity.
        /// </summary>
        bool TryGetNetworkId(IEntity entity, out NetworkEntityId id);

        /// <summary>Prepares a replica the network world built: its bodies become kinematic, so only the host's samples move it.</summary>
        void PrepareReplica(INetworkWorldObject replica);

        /// <summary>Gets the switch that hides a scene object the host removed, or null when it cannot be hidden.</summary>
        INativeAuthoritySwitch? PresenceSwitch(INetworkWorldObject sceneObject);
    }

    /// <summary>A gameplay host that gives the network world its engine side; the Unity host does, over the loaded scenes.</summary>
    internal interface INetworkWorldBackendSource
    {
        INetworkWorldBackend NetworkWorldBackend { get; }
    }

    /// <summary>A process without a game engine behind it: no scene, no native systems, and no network roots to adopt.</summary>
    internal sealed class DetachedNetworkWorldBackend : INetworkWorldBackend
    {
        internal static readonly DetachedNetworkWorldBackend Instance = new DetachedNetworkWorldBackend();

        public void CollectSceneObjects(int? sceneHandle, List<NetworkSceneObject> into)
        {
        }

        public void CollectNativeAuthority(int? sceneHandle, List<NativeAuthorityTarget> into)
        {
        }

        public INetworkWorldObject? Adopt(IEntity entity) => null;

        public bool TryGetNetworkId(IEntity entity, out NetworkEntityId id)
        {
            id = NetworkEntityId.None;
            return false;
        }

        public void PrepareReplica(INetworkWorldObject replica)
        {
        }

        public INativeAuthoritySwitch? PresenceSwitch(INetworkWorldObject sceneObject) => null;
    }

    /// <summary>
    /// Where a prefab handle came from: its bundle's path inside the mod's package and its asset name. The runtime's own
    /// prefab handles carry it, so a spawn through the asset service can be described to a joined player, who loads the
    /// same prefab from its identical copy of the package.
    /// </summary>
    internal interface IPrefabAssetProvenance
    {
        string BundlePath { get; }

        string AssetName { get; }
    }

    /// <summary>
    /// How a joined player builds its replica of a prefab spawned through the asset service: the bundle's path inside
    /// the owning mod's package and the asset's name. A spawn whose prefab carries no provenance is described without
    /// them, and a joined player cannot build it. Immutable, and it holds no handle, so a record that outlives its scope
    /// keeps nothing of the scope reachable.
    /// </summary>
    internal sealed class MultiplayerAssetDescriptor
    {
        /// <summary>The longest bundle path a description carries.</summary>
        internal const int MaximumBundlePathLength = 512;

        /// <summary>The longest asset name a description carries.</summary>
        internal const int MaximumAssetNameLength = 256;

        internal static readonly MultiplayerAssetDescriptor Unknown = new MultiplayerAssetDescriptor(null, null);

        private MultiplayerAssetDescriptor(string? bundlePath, string? assetName)
        {
            BundlePath = bundlePath;
            AssetName = assetName;
        }

        internal string? BundlePath { get; }

        internal string? AssetName { get; }

        /// <summary>Gets whether a joined player can build the replica from this description.</summary>
        internal bool IsPortable => BundlePath != null && AssetName != null;

        /// <summary>Describes a prefab from its provenance; one without it, or with text past the bounds, is <see cref="Unknown"/>.</summary>
        internal static MultiplayerAssetDescriptor Of(IPrefabAsset prefab)
        {
            if (!(prefab is IPrefabAssetProvenance provenance)) return Unknown;
            return Create(provenance.BundlePath, provenance.AssetName);
        }

        /// <summary>Creates a portable description, or <see cref="Unknown"/> when either part is empty or past its bound.</summary>
        internal static MultiplayerAssetDescriptor Create(string? bundlePath, string? assetName)
        {
            if (string.IsNullOrEmpty(bundlePath) || bundlePath!.Length > MaximumBundlePathLength) return Unknown;
            if (string.IsNullOrEmpty(assetName) || assetName!.Length > MaximumAssetNameLength) return Unknown;
            return new MultiplayerAssetDescriptor(bundlePath, assetName);
        }
    }

    /// <summary>
    /// The network world's entity ids as the session names them: <c>world-</c> and the id in decimal. They never collide
    /// with the engine's replicated objects, which it names <c>net-object-</c>.
    /// </summary>
    internal static class NetworkWorldIds
    {
        internal const string Prefix = "world-";

        internal static string Format(NetworkEntityId id) => Prefix + id.Value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Reads an id in exactly its canonical form: the prefix, then a decimal without a sign or leading zeros.</summary>
        internal static bool TryParse(string? text, out NetworkEntityId id)
        {
            id = NetworkEntityId.None;
            if (text == null || text.Length <= Prefix.Length || text.Length > Prefix.Length + 10) return false;
            if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            if (text[Prefix.Length] == '0') return false;
            ulong value = 0;
            for (var index = Prefix.Length; index < text.Length; index++)
            {
                var digit = text[index] - '0';
                if (digit < 0 || digit > 9) return false;
                value = value * 10 + (ulong)digit;
            }

            if (value == 0 || value > uint.MaxValue) return false;
            return NetworkEntityId.TryCreate((uint)value, out id);
        }
    }
}
