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
        Factory = 4,

        /// <summary>This player's own prediction, which the host adopted: its mod's instance stands in place of a replica (WS3.7).</summary>
        Own = 5,

        /// <summary>This player's shadow of the host's scripted spawn: its mod's instance stands in place of a replica (WS3.7).</summary>
        Shadow = 6
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

        /// <summary>
        /// Gets the mod context whose own handles built <see cref="AssetReplica"/>, which takes the replica with it when it
        /// ends, or through whose handles its build failed, perhaps because that context was ending; null when the replica
        /// came from a load of the network world's own (WS3.7). Either way the context's end builds the replica again.
        /// </summary>
        internal object? ReplicaOwner { get; set; }

        internal IInternalReplicaFactory? Factory { get; set; }

        internal IEntity? FactoryReplica { get; set; }

        internal AssetReplicaBuild? Build { get; set; }

        /// <summary>Gets whether the baseline under way has not listed the entity yet.</summary>
        internal bool Stale { get; set; }

        /// <summary>Gets whether building the replica failed, so only a new description tries again.</summary>
        internal bool Unbuildable { get; set; }

        internal SnapshotInterpolationBuffer? Buffer { get; set; }

        internal int DriverIndex { get; set; } = -1;

        /// <summary>Gets the shadow key the host's entry names: the spawn's number in its mod's shadowable spawns, or zero.</summary>
        internal long ShadowKey { get; set; }

        /// <summary>Gets the slot of the player whose objects the entity counts among, or zero for the host's world.</summary>
        internal int OwnerSlot { get; set; }

        /// <summary>Gets this player's own instance that stands for the entity: an adopted prediction or a bound shadow (WS3.7).</summary>
        internal LocalSpawn? Local { get; set; }
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
        private readonly SceneKeyRegistry sceneKeys;
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
        private readonly WorldWireWriter progress = new WorldWireWriter(16);
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
        private ulong worldInstance;
        private int chunksRead;
        private bool progressPending;

        internal NetworkWorldClient(
            NetworkWorldChannel channel,
            INetworkWorldBackend backend,
            SceneKeyRegistry sceneKeys,
            Func<long> clock,
            NetworkWorldCounters counters,
            Func<string, IInternalReplicaFactory?> factories,
            Func<string, IAssetService?> findAssets,
            ShadowKeyNumbering shadowKeys,
            ModAssetLoads modLoads)
        {
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.sceneKeys = sceneKeys ?? throw new ArgumentNullException(nameof(sceneKeys));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.counters = counters ?? throw new ArgumentNullException(nameof(counters));
            this.factories = factories ?? throw new ArgumentNullException(nameof(factories));
            this.findAssets = findAssets ?? throw new ArgumentNullException(nameof(findAssets));
            this.shadowKeys = shadowKeys ?? throw new ArgumentNullException(nameof(shadowKeys));
            this.modLoads = modLoads ?? throw new ArgumentNullException(nameof(modLoads));
            for (var type = WorldMessages.BaselineBegin; type <= WorldMessages.SceneRemoved; type++) channel.Register(type, this);
            channel.Register(AuthorityMessages.SpawnAdopted, this);
            channel.Register(AuthorityMessages.SpawnRefused, this);
        }

        /// <summary>Gets whether this process mirrors a host's world now.</summary>
        internal bool IsActive { get; private set; }

        /// <summary>Gets how many entities this player mirrors.</summary>
        internal int EntityCount => entities.Count;

        /// <summary>Gets the native settings this player turned off, so tests and diagnostics can see them.</summary>
        internal NativeAuthoritySuppressor Suppressor => suppressor;

        internal ReplicaTransformDriver Driver => driver;

        internal SceneObjectIndex Index => index;

        /// <summary>Gets how many keys of objects the host says are gone this player keeps, so tests and diagnostics can see the bound.</summary>
        internal int RemovedKeyCount => removedKeys.Count;

        /// <summary>Gets how many poses of objects the host says are unbound wait for their scene, so tests and diagnostics can see the bound.</summary>
        internal int PendingPoseCount => pendingPoses.Count;

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

            // This player follows the host's world from now, so its scripts number their shadows from there (WS3.7).
            shadowKeys.Restart();
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

            // This player's own instances were copies of the host's world, which it leaves: each ends, as the host ends what
            // it made for this player once it is gone (WS3.7).
            EndPredictions(keepWaiting: false);
            EndShadows();
            ClearChanges();
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
            worldInstance = 0;
            chunksRead = 0;
            progressPending = false;
            return restore;
        }

        /// <summary>
        /// Starts a different world of the same host from nothing: every replica is released and every scene object
        /// unbound, the objects the old world hid are shown again, and the loaded scenes are indexed afresh by the keys they
        /// took as they loaded. Native authority stays off, and the readiness hold stays until the new baseline is in place.
        /// </summary>
        private void ReplaceWorld()
        {
            scratch.Clear();
            scratch.AddRange(entities.Values);
            foreach (var mirrored in scratch) Release(mirrored);
            scratch.Clear();
            entities.Clear();

            // What the old world adopted or numbered is not the new one's: adopted instances and shadows end, and spawns still
            // waiting for a verdict are asked of the new world (WS3.7).
            EndPredictions(keepWaiting: true);
            EndShadows();
            ClearChanges();
            shadowKeys.Restart();
            driver.Clear();
            table.Clear();
            index.Clear();
            removedKeys.Clear();
            pendingPoses.Clear();
            ReleaseAssetLoads();
            var shown = suppressor.RestoreKind(NativeAuthorityKind.SceneObjectPresence);
            counters.RestoreFailures += shown.Failed;
            counters.WorldsReplaced++;
            Census(null);
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

        /// <summary>
        /// Forgets a scene that unloaded: entities bound to its objects lose their objects, and the native settings of its
        /// objects, which went with it, are forgotten rather than held until the player leaves.
        /// </summary>
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
            suppressor.ForgetGone();
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
            if (rebuilds.Count > 0) RebuildReplicas();
            AdvanceBuilds(now);
            if (requestPending && now >= nextRequestMicros) SendRequest(now);
            if (progressPending && receiving >= 0) SendProgress();
            if (synchronized && pendingBuilds == 0) ReleaseHold();

            // Authority routing (WS3.7): what this player asks of its host, and what its own instances wait for.
            PumpPredictions(now);
            PumpChanges(now);
            PumpShadows(now);
        }

        /// <summary>LateUpdate, before the mods: replicas move to the host's samples.</summary>
        internal void Drive()
        {
            if (!IsActive) return;
            driver.Drive(clock());
        }

        /// <summary>
        /// Indexes the scene objects, by the keys each scene took as it loaded, and turns native authority off, in every
        /// loaded scene or in one. Either may fail part way; whatever was found by then is still indexed or turned off, and
        /// the failure is counted.
        /// </summary>
        private void Census(int? sceneHandle)
        {
            // The native settings of objects gone since, as a scene that unloaded for this one takes its own, are forgotten
            // here as well as at the unload, whichever the engine reports first.
            if (sceneHandle.HasValue) suppressor.ForgetGone();
            census.Clear();
            try
            {
                if (!sceneHandle.HasValue) counters.ScenesKeyedLate += sceneKeys.CollectLoaded(census);
                else if (!sceneKeys.CollectScene(sceneHandle.Value, census)) counters.ScenesKeyedLate++;
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
