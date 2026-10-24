using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>One entity a listen host replicates: a bound scene object, an SDK spawn or world content.</summary>
    internal sealed class HostWorldEntity
    {
        internal HostWorldEntity(NetworkEntityId id, WorldEntrySource source, string kind)
        {
            Id = id;
            Source = source;
            Kind = kind;
        }

        internal NetworkEntityId Id { get; }

        internal WorldEntrySource Source { get; set; }

        internal string Kind { get; set; }

        /// <summary>Gets the object whose pose the host samples; null for a spawn the engine side cannot adopt.</summary>
        internal INetworkWorldObject? Object { get; set; }

        internal SceneObjectSlot? Slot { get; set; }

        internal MultiplayerSpawnRecord? Record { get; set; }

        internal string OwnerModId { get; set; } = string.Empty;

        /// <summary>Gets the spawn's shadow key in its mod's numbering, which its entry names; zero when nothing shadows it.</summary>
        internal long OriginSequence { get; set; }

        /// <summary>Gets the participant slot of the player whose objects the entity counts among, or zero for the host's world.</summary>
        internal int OwnerSlot { get; set; }

        internal string? ContentKey { get; set; }

        internal NetVector3 Scale { get; set; } = new NetVector3(1f, 1f, 1f);

        internal NetPose SpawnPose { get; set; }

        internal ReadOnlyMemory<byte> Payload { get; set; }

        internal bool AnnouncePending { get; set; }

        /// <summary>Gets the pose the last snapshot tick read.</summary>
        internal NetPose Pose { get; set; }

        internal bool HasSample { get; set; }

        internal NetQuantizedPosition SampledPosition { get; set; }

        internal uint SampledRotation { get; set; }

        internal bool IsAlive => Slot != null ? Object != null && Object.IsAlive : Record != null && Record.IsLive;
    }

    /// <summary>
    /// The listen host's side of world replication. At promotion it registers the spawns the runtime recorded, then keys
    /// the loaded scenes; it registers later spawns as the spawn ledger reports them, binds a scene object the first time
    /// it moves, and announces every new, changed or ended entity to every player at once. At 10 Hz it sends the poses
    /// the replication scheduler picks under its byte budget, and it sends each player that asks a chunked baseline paced
    /// by what that player reports it has read and by its window (the other part of this class). Main thread only.
    /// </summary>
    internal sealed partial class NetworkWorldHost : IMultiplayerSpawnObserver, IWorldMessageHandler
    {
        /// <summary>The downstream snapshot interval: 10 Hz.</summary>
        internal const long DownstreamIntervalMicros = 100_000;

        /// <summary>How many unbound scene objects each downstream tick checks for first motion.</summary>
        internal const int FirstMotionChecksPerTick = 512;

        private readonly NetworkWorldChannel channel;
        private readonly INetworkWorldBackend backend;
        private readonly SceneKeyRegistry sceneKeys;
        private readonly Func<long> clock;
        private readonly NetworkWorldCounters counters;
        private readonly NetworkEntityTable table = new NetworkEntityTable();
        private readonly Dictionary<uint, HostWorldEntity> entities = new Dictionary<uint, HostWorldEntity>();
        private readonly List<HostWorldEntity> ordered = new List<HostWorldEntity>();
        private readonly Dictionary<MultiplayerSpawnRecord, HostWorldEntity> byRecord = new Dictionary<MultiplayerSpawnRecord, HostWorldEntity>();
        private readonly SceneObjectIndex index = new SceneObjectIndex();
        private readonly List<NetworkSceneObject> census = new List<NetworkSceneObject>();
        private readonly List<HostWorldEntity> announcements = new List<HostWorldEntity>();
        private readonly List<NetworkEntityId> despawns = new List<NetworkEntityId>();
        private readonly List<SceneObjectKey> removals = new List<SceneObjectKey>();
        private readonly List<SceneObjectSlot> moved = new List<SceneObjectSlot>();
        private readonly List<SceneObjectSlot> vanished = new List<SceneObjectSlot>();
        private readonly List<SceneObjectSlot> unloaded = new List<SceneObjectSlot>();
        private readonly ReplicationScheduler scheduler = new ReplicationScheduler();
        private readonly List<ReplicationCandidate> candidates = new List<ReplicationCandidate>();
        private readonly List<NetworkEntityId> selected = new List<NetworkEntityId>();
        private readonly WorldWireWriter message = new WorldWireWriter();
        private readonly WorldWireWriter body = new WorldWireWriter(WorldMessages.MaximumMessageBytes - 16);
        private readonly WorldWireWriter chunk = new WorldWireWriter(BaselineChunkBytes);
        private readonly WorldWireWriter entry = new WorldWireWriter(20 * 1024);
        private readonly List<ParticipantId> skipped = new List<ParticipantId>();
        private MultiplayerSpawnLedger? ledger;
        private long nextTickMicros;
        private ulong worldInstance;

        internal NetworkWorldHost(NetworkWorldChannel channel, INetworkWorldBackend backend, SceneKeyRegistry sceneKeys, Func<long> clock, NetworkWorldCounters counters)
        {
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.sceneKeys = sceneKeys ?? throw new ArgumentNullException(nameof(sceneKeys));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.counters = counters ?? throw new ArgumentNullException(nameof(counters));
            channel.Register(WorldMessages.BaselineRequest, this);
            channel.Register(WorldMessages.BaselineProgress, this);
        }

        /// <summary>Gets whether this process hosts world replication now.</summary>
        internal bool IsActive { get; private set; }

        /// <summary>Gets how many entities the host replicates.</summary>
        internal int EntityCount => entities.Count;

        /// <summary>Gets the scene index, so tests and diagnostics can see what the census keyed.</summary>
        internal SceneObjectIndex Index => index;

        /// <summary>
        /// Gets the world this host serves, a random value drawn at each promotion and named in every baseline. A player
        /// whose host's process restarted under the same identity rejoins within the grace into a different world, with its
        /// own entity ids; the new value tells it to start that world from nothing.
        /// </summary>
        internal ulong WorldInstance => worldInstance;

        /// <summary>
        /// Starts hosting the world: registers the live spawns the promotion carries, in the order they were recorded, indexes
        /// the loaded scenes' objects by the keys they took as each scene loaded, and from then hears about later spawns. A
        /// second promotion starts again from nothing.
        /// </summary>
        internal void Activate(MultiplayerPromotion promotion, MultiplayerSpawnLedger spawnLedger)
        {
            if (promotion == null) throw new ArgumentNullException(nameof(promotion));
            if (spawnLedger == null) throw new ArgumentNullException(nameof(spawnLedger));
            if (IsActive) Deactivate();
            IsActive = true;
            ledger = spawnLedger;
            nextTickMicros = clock();
            worldInstance = NewWorldInstance();
            ResetPeers();

            // Spawns are marked first, so the scene census that follows leaves them out.
            foreach (var record in promotion.Spawns) Register(record);
            spawnLedger.Observer = this;
            counters.Promotions++;

            // The scene census comes last: one that fails leaves a host that replicates its spawns, and the failure goes on
            // to the composition root, which reports it once. Each object keeps the key it took as its scene loaded, so an
            // object destroyed or moved to another parent since shifts no other object's key, and one destroyed since is
            // indexed as removed, which every baseline names. The solo session may have moved anything, so the poses read
            // now are not the scenes' own: baselines carry them for the objects still unbound.
            census.Clear();
            try
            {
                counters.ScenesKeyedLate += sceneKeys.CollectLoaded(census);
            }
            finally
            {
                index.Add(census, originKnown: false);
                census.Clear();
            }
        }

        /// <summary>Stops hosting the world: every mark comes off, and nothing more is announced.</summary>
        internal void Deactivate()
        {
            if (!IsActive) return;
            IsActive = false;
            if (ledger != null && ReferenceEquals(ledger.Observer, this)) ledger.Observer = null;
            ledger = null;
            foreach (var hosted in ordered)
            {
                try
                {
                    hosted.Object?.SetNetworkIdentity(NetworkEntityId.None);
                }
                catch (Exception)
                {
                    counters.MarkFailures++;
                }
            }

            entities.Clear();
            ordered.Clear();
            byRecord.Clear();
            table.Clear();
            index.Clear();
            announcements.Clear();
            despawns.Clear();
            removals.Clear();
            candidates.Clear();
            scheduler.Schedule(candidates, selected);
            ResetPeers();
        }

        /// <summary>Indexes a scene that loaded while hosting, by the keys it took as it loaded.</summary>
        internal void SceneLoaded(int sceneHandle)
        {
            if (!IsActive) return;
            census.Clear();
            try
            {
                if (!sceneKeys.CollectScene(sceneHandle, census)) counters.ScenesKeyedLate++;
            }
            finally
            {
                index.Add(census);
                census.Clear();
            }
        }

        /// <summary>Forgets a scene that unloaded; its bound objects end, and every player hears so.</summary>
        internal void SceneUnloaded(int sceneHandle)
        {
            if (!IsActive) return;
            unloaded.Clear();
            index.RemoveScene(sceneHandle, unloaded);
            foreach (var slot in unloaded)
            {
                if (entities.TryGetValue(slot.Bound.Value, out var hosted)) End(hosted);
            }

            unloaded.Clear();
        }

        public void Recorded(MultiplayerSpawnRecord record, bool redescribed)
        {
            if (!IsActive) return;
            if (byRecord.TryGetValue(record, out var known))
            {
                Describe(known, record);
                Announce(known);
                return;
            }

            var hosted = Register(record);
            if (hosted != null) Announce(hosted);
        }

        /// <summary>Frame step 2: takes what the players sent, and notices their streams starting, restarting and ending.</summary>
        internal void Receive()
        {
            if (!IsActive) return;
            CheckGenerations();
            channel.Receive();
        }

        /// <summary>
        /// Frame step 4: ends what died, binds what moved, announces what changed, sends a snapshot every downstream tick, and
        /// paces each player's baseline.
        /// </summary>
        internal void Send()
        {
            if (!IsActive) return;
            var now = clock();
            var tick = now >= nextTickMicros;
            if (tick)
            {
                // A long frame skips the ticks it missed rather than sending them in a burst.
                nextTickMicros = Math.Max(nextTickMicros + DownstreamIntervalMicros, now);
                CollectEnded();
                ScanFirstMotion(FirstMotionChecksPerTick);
            }

            if (peers.Count == 0)
            {
                // Nobody to tell: whoever joins asks for a baseline, which lists everything there is then.
                announcements.Clear();
                despawns.Clear();
                removals.Clear();
                return;
            }

            SendEnded();
            SendAnnouncements();
            if (tick) SendSnapshot(now);
            PumpBaselines(now);
        }

        private HostWorldEntity? Register(MultiplayerSpawnRecord record)
        {
            if (!record.IsLive) return null;
            var description = record.Description;
            var source = description.Source == MultiplayerSpawnSource.WorldContent ? WorldEntrySource.WorldContent : WorldEntrySource.SdkSpawn;

            // An SDK spawn a joined player's own scripts make too carries its shadow key as its origin, so their shadow binds
            // to it (WS3.7). A key still live from an earlier world session is never given twice: that spawn goes keyless.
            var shadowKey = source == WorldEntrySource.SdkSpawn ? record.ShadowKey : 0;
            var origin = shadowKey > 0 ? new NetSpawnOrigin(description.OwnerModId, shadowKey) : default;
            if (origin.IsSpecified && table.TryFindByOrigin(origin, out _))
            {
                origin = default;
                shadowKey = 0;
                counters.ShadowKeysReused++;
            }

            NetworkEntityId id;
            try
            {
                id = table.Allocate(description.Kind, OwnerOf(record.OwnerSlot), origin);
            }
            catch (InvalidOperationException)
            {
                counters.RegistrationFailures++;
                return null;
            }

            var hosted = new HostWorldEntity(id, source, description.Kind) { Record = record };
            Describe(hosted, record);
            hosted.OriginSequence = shadowKey;
            try
            {
                hosted.Object = backend.Adopt(record.Entity);
                hosted.Object?.SetNetworkIdentity(id);
            }
            catch (Exception)
            {
                counters.MarkFailures++;
            }

            entities.Add(id.Value, hosted);
            ordered.Add(hosted);
            byRecord.Add(record, hosted);
            return hosted;
        }

        private void Describe(HostWorldEntity hosted, MultiplayerSpawnRecord record)
        {
            var description = record.Description;
            hosted.Source = description.Source == MultiplayerSpawnSource.WorldContent ? WorldEntrySource.WorldContent : WorldEntrySource.SdkSpawn;
            hosted.Kind = description.Kind;
            hosted.OwnerModId = description.OwnerModId;

            // A spawn described again as a player's keeps its id, and loses its shadow key: no shadow stands for a player's object.
            if (hosted.OwnerSlot != record.OwnerSlot)
            {
                hosted.OwnerSlot = record.OwnerSlot;
                table.TrySetOwner(hosted.Id, OwnerOf(record.OwnerSlot));
            }

            if (record.ShadowKey == 0) hosted.OriginSequence = 0;
            hosted.ContentKey = description.ContentKey;
            hosted.Scale = ToNet(description.Transform.Scale);
            hosted.SpawnPose = new NetPose(ToNet(description.Transform.Position), ToNet(description.Transform.Rotation));
            hosted.Payload = description.Kind == MultiplayerSpawnKinds.AssetPrefab
                ? WorldEntryCodec.EncodeAsset(description.Descriptor as MultiplayerAssetDescriptor ?? MultiplayerAssetDescriptor.Unknown)
                : description.Descriptor is InternalSpawnDescriptor provided ? provided.Payload : ReadOnlyMemory<byte>.Empty;
        }

        private void Announce(HostWorldEntity hosted)
        {
            if (hosted.AnnouncePending) return;
            hosted.AnnouncePending = true;
            announcements.Add(hosted);
        }

        private void CollectEnded()
        {
            var kept = 0;
            for (var index = 0; index < ordered.Count; index++)
            {
                var hosted = ordered[index];
                if (hosted.IsAlive)
                {
                    ordered[kept++] = hosted;
                    continue;
                }

                Forget(hosted);
            }

            ordered.RemoveRange(kept, ordered.Count - kept);
        }

        private void End(HostWorldEntity hosted)
        {
            Forget(hosted);
            ordered.Remove(hosted);
        }

        private void Forget(HostWorldEntity hosted)
        {
            table.Remove(hosted.Id);
            entities.Remove(hosted.Id.Value);
            if (hosted.Record != null) byRecord.Remove(hosted.Record);
            hosted.Slot?.MarkRemoved();
            hosted.AnnouncePending = false;
            despawns.Add(hosted.Id);
            scheduler.Forget(hosted.Id);
            counters.Despawned++;
        }

        private void ScanFirstMotion(int maximum)
        {
            moved.Clear();
            vanished.Clear();
            index.ScanForMotion(maximum, moved, vanished);
            foreach (var slot in vanished)
            {
                slot.MarkRemoved();
                removals.Add(slot.Key);
                counters.SceneObjectsRemoved++;
            }

            foreach (var slot in moved) Bind(slot);
            moved.Clear();
            vanished.Clear();
        }

        private void Bind(SceneObjectSlot slot)
        {
            // A scene object registered as a spawn or world content since its scene loaded is that entity already.
            if (slot.Object.IsNetworkRoot)
            {
                slot.MarkAmbiguous();
                return;
            }

            var id = table.Allocate(MultiplayerSpawnKinds.SceneObject, NetOwner.Server);
            var hosted = new HostWorldEntity(id, WorldEntrySource.SceneObject, MultiplayerSpawnKinds.SceneObject) { Slot = slot, Object = slot.Object };
            slot.Bind(id);
            try
            {
                slot.Object.SetNetworkIdentity(id);
            }
            catch (Exception)
            {
                counters.MarkFailures++;
            }

            entities.Add(id.Value, hosted);
            ordered.Add(hosted);
            Announce(hosted);
            counters.SceneObjectsBound++;
        }

        private void SendEnded()
        {
            // Keys take 8 bytes and ids at most 5, so a batch always fits one message.
            const int KeysPerMessage = (WorldMessages.MaximumMessageBytes - 16) / 8;
            const int IdsPerMessage = (WorldMessages.MaximumMessageBytes - 16) / 5;
            for (var item = 0; item < removals.Count;)
            {
                var batch = Math.Min(removals.Count - item, Math.Min(WorldMessages.MaximumItems, KeysPerMessage));
                message.Begin(WorldMessages.SceneRemoved);
                message.WriteVarUInt((ulong)batch);
                for (var written = 0; written < batch; written++) message.WriteUInt64(removals[item++].Value);
                Broadcast();
            }

            removals.Clear();
            for (var item = 0; item < despawns.Count;)
            {
                var batch = Math.Min(despawns.Count - item, Math.Min(WorldMessages.MaximumItems, IdsPerMessage));
                message.Begin(WorldMessages.Despawn);
                message.WriteVarUInt((ulong)batch);
                for (var written = 0; written < batch; written++) message.WriteVarUInt(despawns[item++].Value);
                Broadcast();
            }

            despawns.Clear();
        }

        private void SendAnnouncements()
        {
            if (announcements.Count == 0) return;
            body.Reset();
            var count = 0;
            foreach (var hosted in announcements)
            {
                hosted.AnnouncePending = false;
                if (!hosted.IsAlive) continue;
                entry.Reset();
                WriteEntry(entry, hosted);
                if (count == WorldMessages.MaximumItems || !body.TryAppend(entry))
                {
                    FlushAnnouncements(count);
                    count = 0;
                    body.Reset();
                    body.TryAppend(entry);
                }

                count++;
                counters.Announced++;
            }

            if (count > 0) FlushAnnouncements(count);
            announcements.Clear();
        }

        private void FlushAnnouncements(int count)
        {
            message.Begin(WorldMessages.Spawn);
            message.WriteVarUInt((ulong)count);
            message.TryAppend(body);
            Broadcast();
        }

        private void SendSnapshot(long now)
        {
            candidates.Clear();
            foreach (var hosted in ordered)
            {
                if (hosted.Object == null || !hosted.Object.TryReadPose(out var pose)) continue;
                var position = NetQuantization.QuantizePosition(pose.Position);
                var rotation = NetQuantization.QuantizeRotation(pose.Rotation);
                var atRest = hosted.HasSample && position.Equals(hosted.SampledPosition) && rotation == hosted.SampledRotation;
                hosted.Pose = pose;
                hosted.SampledPosition = position;
                hosted.SampledRotation = rotation;
                hosted.HasSample = true;
                candidates.Add(new ReplicationCandidate(hosted.Id, 1, VarUIntBytes(hosted.Id.Value) + (pose.HasVelocity ? 18 : 14), atRest));
            }

            scheduler.Schedule(candidates, selected);
            if (selected.Count == 0) return;
            message.Begin(WorldMessages.Snapshot);
            message.WriteVarUInt((ulong)Math.Min(now, WorldMessages.MaximumTimeMicros));
            message.WriteVarUInt((ulong)selected.Count);
            foreach (var id in selected)
            {
                var hosted = entities[id.Value];
                message.WriteVarUInt(id.Value);
                message.WriteTransform(hosted.Pose);
            }

            counters.SnapshotEntries += selected.Count;
            Broadcast();
        }

        private void Broadcast()
        {
            skipped.Clear();
            channel.Broadcast(message, skipped);
            if (skipped.Count > 0) Skipped(skipped);
        }

        private void WriteEntry(WorldWireWriter writer, HostWorldEntity hosted)
        {
            var pose = hosted.Object != null && hosted.Object.TryReadPose(out var current) ? current : hosted.SpawnPose;
            switch (hosted.Source)
            {
                case WorldEntrySource.SceneObject:
                    WorldEntryCodec.WriteSceneObject(writer, hosted.Id, hosted.Slot!.Key, pose);
                    break;
                case WorldEntrySource.WorldContent:
                    WorldEntryCodec.WriteContent(writer, hosted.Id, hosted.Kind, hosted.ContentKey!, hosted.OwnerModId, hosted.OwnerSlot, hosted.Scale, pose, hosted.Payload.Span);
                    break;
                default:
                    WorldEntryCodec.WriteSpawn(writer, hosted.Id, hosted.Kind, hosted.OwnerModId, hosted.OriginSequence, hosted.OwnerSlot, hosted.Scale, pose, hosted.Payload.Span);
                    break;
            }
        }

        private static int VarUIntBytes(uint value)
        {
            var bytes = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                bytes++;
            }

            return bytes;
        }

        private static NetOwner OwnerOf(int slot) => slot > 0 ? NetOwner.ForParticipant(slot) : NetOwner.Server;

        internal static NetVector3 ToNet(Vec3 value) => new NetVector3(value.X, value.Y, value.Z);

        internal static NetQuaternion ToNet(Quat value) => new NetQuaternion(value.X, value.Y, value.Z, value.W);
    }
}
