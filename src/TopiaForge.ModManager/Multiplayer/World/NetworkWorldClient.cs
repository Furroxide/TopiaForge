using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>How a joined player's entity is present here.</summary>
    internal enum ClientEntityBinding
    {
        /// <summary>Not present: unbuilt, still loading, or waiting for its scene or its world content.</summary>
        None = 0,

        /// <summary>A scene object this player loaded too, bound by its key.</summary>
        SceneObject = 1,

        /// <summary>World content this player's own world provider built, bound by its content key.</summary>
        Content = 2,

        /// <summary>A replica of a prefab spawned through the asset service, which this player built.</summary>
        Asset = 3,

        /// <summary>A replica a provider's factory built.</summary>
        Factory = 4
    }

    /// <summary>One entity a joined player mirrors from its host, and how it is present here.</summary>
    internal sealed class ClientWorldEntity
    {
        internal ClientWorldEntity(NetworkEntityId id)
        {
            Id = id;
        }

        internal NetworkEntityId Id { get; }

        internal WorldEntrySource Source { get; set; }

        internal string Kind { get; set; } = string.Empty;

        internal SceneObjectKey SceneKey { get; set; }

        internal string? ContentKey { get; set; }

        internal string OwnerModId { get; set; } = string.Empty;

        internal ulong DescriptionHash { get; set; }

        internal byte[] Payload { get; set; } = Array.Empty<byte>();

        internal NetVector3 Scale { get; set; } = new NetVector3(1f, 1f, 1f);

        /// <summary>Gets the pose the host's latest entry for the entity gave.</summary>
        internal NetPose EntryPose { get; set; }

        internal ClientEntityBinding Binding { get; set; }

        internal INetworkWorldObject? Object { get; set; }

        internal SceneObjectSlot? Slot { get; set; }

        internal ISpawnedEntity? AssetReplica { get; set; }

        internal IInternalReplicaFactory? Factory { get; set; }

        internal IEntity? FactoryReplica { get; set; }

        internal AssetReplicaBuild? Build { get; set; }

        /// <summary>Gets whether the baseline under way has not listed the entity yet.</summary>
        internal bool Stale { get; set; }

        /// <summary>Gets whether building the replica failed, so only a new description tries again.</summary>
        internal bool Unbuildable { get; set; }

        internal SnapshotInterpolationBuffer? Buffer { get; set; }

        internal int DriverIndex { get; set; } = -1;
    }

    /// <summary>
    /// A joined player's side of world replication. When this process joins it holds readiness, keys its loaded scenes,
    /// turns native authority off and asks the host for its world; it applies the baseline the host sends, every
    /// announcement and every snapshot, and releases readiness once the baseline is applied and its replicas are built.
    /// A stream that restarts, after a rejoin or a replacement, starts again from a new baseline. Leaving releases every
    /// replica, unbinds every scene object and restores every native setting. Main thread only.
    /// </summary>
    internal sealed partial class NetworkWorldClient : IWorldMessageHandler, IMultiplayerLocalContent
    {
        /// <summary>The most entities a joined player mirrors, however many a host announces.</summary>
        internal const int MaximumEntities = 16_384;

        /// <summary>How long a request that could not be sent waits before it is tried again.</summary>
        internal const long RequestRetryMicros = 250_000;

        /// <summary>The hold's reason, as the provider sees it.</summary>
        internal const string HoldReason = "Loading the host's world";

        /// <summary>
        /// The most scene keys a joined player keeps for scenes it has not loaded: removed objects to hide, and unbound
        /// objects' poses to place, once their scene loads. Whatever a host sends past it is counted and dropped.
        /// </summary>
        internal const int MaximumPendingKeys = 65_536;

        private readonly NetworkWorldChannel channel;
        private readonly INetworkWorldBackend backend;
        private readonly Func<long> clock;
        private readonly NetworkWorldCounters counters;
        private readonly NetworkEntityTable table = new NetworkEntityTable();
        private readonly Dictionary<uint, ClientWorldEntity> entities = new Dictionary<uint, ClientWorldEntity>();
        private readonly List<ClientWorldEntity> scratch = new List<ClientWorldEntity>();
        private readonly SceneObjectIndex index = new SceneObjectIndex();
        private readonly HashSet<SceneObjectKey> removedKeys = new HashSet<SceneObjectKey>();
        private readonly Dictionary<SceneObjectKey, NetPose> pendingPoses = new Dictionary<SceneObjectKey, NetPose>();
        private readonly List<SceneObjectKey> placed = new List<SceneObjectKey>();
        private readonly NativeAuthoritySuppressor suppressor = new NativeAuthoritySuppressor();
        private readonly ReplicaTransformDriver driver = new ReplicaTransformDriver();
        private readonly List<NetworkSceneObject> census = new List<NetworkSceneObject>();
        private readonly List<NativeAuthorityTarget> natives = new List<NativeAuthorityTarget>();
        private readonly List<SceneObjectSlot> unloaded = new List<SceneObjectSlot>();
        private readonly WorldWireWriter request = new WorldWireWriter(16);
        private readonly WorldEntry entry = new WorldEntry();
        private IInternalMultiplayerControl? control;
        private IInternalReadinessHold? hold;
        private ulong streamGeneration;
        private bool requestPending;
        private long nextRequestMicros;
        private int receiving = -1;
        private int expected;
        private int received;
        private bool synchronized;

        internal NetworkWorldClient(
            NetworkWorldChannel channel,
            INetworkWorldBackend backend,
            Func<long> clock,
            NetworkWorldCounters counters,
            Func<string, IInternalReplicaFactory?> factories,
            Func<string, IAssetService?> findAssets)
        {
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.counters = counters ?? throw new ArgumentNullException(nameof(counters));
            this.factories = factories ?? throw new ArgumentNullException(nameof(factories));
            this.findAssets = findAssets ?? throw new ArgumentNullException(nameof(findAssets));
            for (var type = WorldMessages.BaselineBegin; type <= WorldMessages.SceneRemoved; type++) channel.Register(type, this);
        }

        /// <summary>Gets whether this process mirrors a host's world now.</summary>
        internal bool IsActive { get; private set; }

        /// <summary>Gets how many entities this player mirrors.</summary>
        internal int EntityCount => entities.Count;

        /// <summary>Gets the native settings this player turned off, so tests and diagnostics can see them.</summary>
        internal NativeAuthoritySuppressor Suppressor => suppressor;

        internal ReplicaTransformDriver Driver => driver;

        internal SceneObjectIndex Index => index;

        /// <summary>Gets whether a readiness hold keeps this player short of Ready.</summary>
        internal bool HoldsReadiness => hold != null;

        internal NetworkWorldProgress Progress
        {
            get
            {
                if (!IsActive) return new NetworkWorldProgress(NetworkWorldPhase.Offline, 0, 0, 0);
                if (synchronized)
                {
                    return pendingBuilds > 0
                        ? new NetworkWorldProgress(NetworkWorldPhase.BuildingReplicas, received, expected, pendingBuilds)
                        : new NetworkWorldProgress(NetworkWorldPhase.Synchronized, received, expected, 0);
                }

                return receiving >= 0
                    ? new NetworkWorldProgress(NetworkWorldPhase.ReceivingBaseline, received, expected, pendingBuilds)
                    : new NetworkWorldProgress(NetworkWorldPhase.Requesting, 0, 0, pendingBuilds);
            }
        }

        /// <summary>
        /// Starts mirroring the host's world: holds readiness, keys the loaded scenes, turns native authority off, and asks
        /// for a baseline. A failure part way is counted, and whatever was suppressed by then is restored on leave.
        /// </summary>
        internal void Activate(IInternalMultiplayerControl joined)
        {
            if (joined == null) throw new ArgumentNullException(nameof(joined));
            if (IsActive) Deactivate();
            IsActive = true;
            control = joined;
            counters.Joins++;
            TakeHold();
            streamGeneration = channel.Generation;
            requestPending = true;
            nextRequestMicros = 0;
            try
            {
                Census(null);
            }
            catch (Exception)
            {
                counters.ActivationFailures++;
            }
        }

        /// <summary>
        /// Stops mirroring: releases every replica, unbinds every scene object, restores every native setting and lets go
        /// of the readiness hold. Each step is tried whatever the one before it did.
        /// </summary>
        internal NativeAuthorityRestore Deactivate()
        {
            if (!IsActive) return default;
            IsActive = false;
            scratch.Clear();
            scratch.AddRange(entities.Values);
            foreach (var mirrored in scratch) Release(mirrored);
            scratch.Clear();
            entities.Clear();
            driver.Clear();
            table.Clear();
            index.Clear();
            removedKeys.Clear();
            pendingPoses.Clear();
            ReleaseAssetLoads();
            var restore = suppressor.Restore();
            counters.RestoredSwitches += restore.Restored;
            counters.RestoreFailures += restore.Failed;
            ReleaseHold();
            control = null;
            receiving = -1;
            expected = 0;
            received = 0;
            synchronized = false;
            requestPending = false;
            return restore;
        }

        /// <summary>Keys a scene that loaded while joined, turns its native authority off, and binds what waited for it.</summary>
        internal void SceneLoaded(int sceneHandle)
        {
            if (!IsActive) return;
            Census(sceneHandle);
            foreach (var mirrored in entities.Values)
            {
                if (mirrored.Source == WorldEntrySource.SceneObject && mirrored.Binding == ClientEntityBinding.None) scratch.Add(mirrored);
            }

            foreach (var mirrored in scratch)
            {
                mirrored.Unbuildable = false;
                Build(mirrored);
            }

            scratch.Clear();
        }

        /// <summary>Forgets a scene that unloaded; entities bound to its objects lose their objects.</summary>
        internal void SceneUnloaded(int sceneHandle)
        {
            if (!IsActive) return;
            unloaded.Clear();
            index.RemoveScene(sceneHandle, unloaded);
            foreach (var slot in unloaded)
            {
                if (!entities.TryGetValue(slot.Bound.Value, out var mirrored)) continue;
                Release(mirrored);
                mirrored.Unbuildable = true;
            }

            unloaded.Clear();
        }

        /// <summary>Gets the entity a network id names here, if it is present.</summary>
        internal bool TryGetEntity(NetworkEntityId id, out IEntity entity)
        {
            if (entities.TryGetValue(id.Value, out var mirrored) && mirrored.Object != null && mirrored.Object.IsAlive)
            {
                entity = mirrored.Object.Entity;
                return true;
            }

            entity = null!;
            return false;
        }

        internal bool Knows(NetworkEntityId id) => entities.ContainsKey(id.Value);

        /// <summary>Appends the ids this player mirrors, in ascending order.</summary>
        internal void CollectIds(List<uint> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            var start = into.Count;
            into.AddRange(entities.Keys);
            into.Sort(start, into.Count - start, null);
        }

        /// <summary>Gets how this player has the entity of <paramref name="id"/>, or <see cref="ClientEntityBinding.None"/>.</summary>
        internal ClientEntityBinding BindingOf(NetworkEntityId id) => entities.TryGetValue(id.Value, out var mirrored) ? mirrored.Binding : ClientEntityBinding.None;

        /// <summary>
        /// Frame step 2: notices a restarted stream, applies what the host sent, moves replica builds on, asks for a
        /// baseline when one is due, and lets readiness go once the world is in place.
        /// </summary>
        internal void Receive()
        {
            if (!IsActive) return;
            var generation = channel.Generation;
            if (generation != streamGeneration)
            {
                streamGeneration = generation;
                StreamRestarted();
            }

            channel.Receive();
            var now = clock();
            AdvanceBuilds(now);
            if (requestPending && now >= nextRequestMicros) SendRequest(now);
            if (synchronized && pendingBuilds == 0) ReleaseHold();
        }

        /// <summary>LateUpdate, before the mods: replicas move to the host's samples.</summary>
        internal void Drive()
        {
            if (!IsActive) return;
            driver.Drive(clock());
        }

        /// <summary>
        /// Keys the scene objects and turns native authority off, in every loaded scene or in one. Either census may fail
        /// part way; whatever it found by then is still keyed or turned off, and the failure is counted.
        /// </summary>
        private void Census(int? sceneHandle)
        {
            census.Clear();
            try
            {
                backend.CollectSceneObjects(sceneHandle, census);
            }
            catch (Exception)
            {
                counters.ActivationFailures++;
            }

            index.Add(census);
            census.Clear();
            foreach (var key in removedKeys)
            {
                if (index.TryGet(key, out var slot) && !slot.Removed) Hide(slot);
            }

            // Poses the baseline gave before this scene loaded, as when the host's world arrives ahead of the scene.
            foreach (var pending in pendingPoses)
            {
                if (index.TryGet(pending.Key, out _) || index.IsExcluded(pending.Key)) placed.Add(pending.Key);
            }

            foreach (var key in placed)
            {
                var pose = pendingPoses[key];
                pendingPoses.Remove(key);
                PlaceSceneObject(key, pose);
            }

            placed.Clear();

            natives.Clear();
            try
            {
                backend.CollectNativeAuthority(sceneHandle, natives);
            }
            catch (Exception)
            {
                counters.ActivationFailures++;
            }

            var before = suppressor.SuppressionFailures;
            counters.SuppressedSwitches += suppressor.Suppress(natives);
            counters.SuppressionFailures += suppressor.SuppressionFailures - before;
            natives.Clear();
        }

        private void TakeHold()
        {
            if (hold != null || control == null) return;
            try
            {
                hold = control.HoldReadiness(HoldReason);
            }
            catch (Exception)
            {
                counters.HoldFailures++;
            }
        }

        private void ReleaseHold()
        {
            var current = hold;
            if (current == null) return;
            hold = null;
            try
            {
                current.Dispose();
            }
            catch (Exception)
            {
                counters.HoldFailures++;
            }
        }

        private void ReportProgress()
        {
            try
            {
                hold?.ReportProgress();
            }
            catch (Exception)
            {
                counters.HoldFailures++;
            }
        }
    }
}
