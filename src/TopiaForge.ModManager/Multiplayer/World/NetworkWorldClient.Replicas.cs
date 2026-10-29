using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkWorldClient
    {
        private readonly Func<string, IInternalReplicaFactory?> factories;
        private readonly Dictionary<string, LocalContentEntry> localContent = new Dictionary<string, LocalContentEntry>(StringComparer.Ordinal);

        public void RegisterLocalContent(object owner, string contentKey, IEntity entity)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (contentKey == null) throw new ArgumentNullException(nameof(contentKey));
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            localContent[contentKey] = new LocalContentEntry(owner, entity);
            if (!IsActive) return;
            foreach (var mirrored in entities.Values)
            {
                if (mirrored.Source == WorldEntrySource.WorldContent && mirrored.Binding == ClientEntityBinding.None &&
                    string.Equals(mirrored.ContentKey, contentKey, StringComparison.Ordinal))
                {
                    scratch.Add(mirrored);
                }
            }

            foreach (var mirrored in scratch) Build(mirrored);
            scratch.Clear();
        }

        public void ReleaseOwner(object owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (IsActive) QueueRebuilds(owner);
            List<string>? gone = null;
            foreach (var pair in localContent)
            {
                if (ReferenceEquals(pair.Value.Owner, owner)) (gone ??= new List<string>()).Add(pair.Key);
            }

            if (gone == null) return;
            foreach (var key in gone) localContent.Remove(key);
        }

        /// <summary>
        /// Applies one entry: a new entity is mirrored and built, a known one is described again in place. A baseline's
        /// entry is the host's state as it sent it, newer than every sample this player buffered before it, so a known
        /// entity drops those samples and stands at the entry's pose until the next one: after a stall, the samples left
        /// over would otherwise play back where the host's entity used to be.
        /// </summary>
        private void Apply(WorldEntry update, bool baseline)
        {
            counters.EntriesApplied++;
            if (update.Source == WorldEntrySource.RemovedSceneObject)
            {
                RemoveSceneObject(update.SceneKey);
                return;
            }

            if (update.Source == WorldEntrySource.ScenePose)
            {
                PlaceSceneObject(update.SceneKey, update.Pose);
                return;
            }

            var hash = HashOf(update);
            if (entities.TryGetValue(update.Id.Value, out var known))
            {
                known.Stale = false;
                if (baseline) known.Buffer?.Reset();
                if (known.Source != update.Source || known.DescriptionHash != hash)
                {
                    // This player's own instance keeps standing for the entity while it is the same kind of thing (WS3.7).
                    if (TryUpdateProvider(known, update) || KeepsLocalBinding(known, update))
                    {
                        Describe(known, update, hash);
                        if (known.Object != null && (known.Buffer == null || known.Buffer.Count == 0)) Place(known);
                        return;
                    }

                    Release(known);
                    Describe(known, update, hash);
                    known.Unbuildable = false;
                    Build(known);
                    return;
                }

                known.ShadowKey = update.Source == WorldEntrySource.SdkSpawn ? update.OriginSequence : 0;
                known.OwnerSlot = update.OwnerSlot;

                known.EntryPose = update.Pose;
                if (known.Binding == ClientEntityBinding.None && known.Build == null && !known.Unbuildable) Build(known);
                else if (known.Object != null && (known.Buffer == null || known.Buffer.Count == 0)) Place(known);
                return;
            }

            if (entities.Count >= MaximumEntities)
            {
                counters.EntitiesRefused++;
                return;
            }

            var origin = default(NetSpawnOrigin);
            if (update.Source == WorldEntrySource.SdkSpawn) NetSpawnOrigin.TryCreate(update.OwnerModId, update.OriginSequence, out origin);
            NetOwner.TryFromSlot(update.OwnerSlot, out var owner);
            var kind = update.Source == WorldEntrySource.SceneObject ? MultiplayerSpawnKinds.SceneObject : update.Kind;
            var added = table.TryAdd(update.Id, kind, owner, origin);
            if (added == NetworkEntityAddResult.DuplicateOrigin && EndStaleHolder(origin)) added = table.TryAdd(update.Id, kind, owner, origin);
            if (added != NetworkEntityAddResult.Added)
            {
                if (added == NetworkEntityAddResult.Tombstoned) counters.StaleMessages++;
                else counters.EntitiesRefused++;
                return;
            }

            var mirrored = new ClientWorldEntity(update.Id);
            Describe(mirrored, update, hash);
            entities.Add(update.Id.Value, mirrored);
            driver.Track(mirrored);
            Build(mirrored);
        }

        /// <summary>
        /// Ends the entity here that holds <paramref name="origin"/> when the baseline under way has not listed it. Shadow keys
        /// start over with each of the host's world sessions (WS3.7), and an honest host never holds two live entities with one
        /// key, so that entity is one the host ended while its despawn went with an earlier stream; keeping it would refuse
        /// the entity that holds the key now.
        /// </summary>
        private bool EndStaleHolder(NetSpawnOrigin origin)
        {
            if (!table.TryFindByOrigin(origin, out var holder) || !entities.TryGetValue(holder.Value, out var stale) || !stale.Stale) return false;
            Remove(stale, true);
            return true;
        }

        private static void Describe(ClientWorldEntity mirrored, WorldEntry update, ulong hash)
        {
            mirrored.Source = update.Source;
            mirrored.Kind = update.Source == WorldEntrySource.SceneObject ? MultiplayerSpawnKinds.SceneObject : update.Kind;
            mirrored.SceneKey = update.SceneKey;
            mirrored.ContentKey = update.ContentKey;
            mirrored.OwnerModId = update.OwnerModId;
            mirrored.Payload = update.Payload;
            mirrored.Scale = update.Source == WorldEntrySource.SceneObject ? new NetVector3(1f, 1f, 1f) : update.Scale;
            mirrored.EntryPose = update.Pose;
            mirrored.DescriptionHash = hash;
            mirrored.ShadowKey = update.Source == WorldEntrySource.SdkSpawn ? update.OriginSequence : 0;
            mirrored.OwnerSlot = update.OwnerSlot;
        }

        /// <summary>Makes the entity present here: binds its scene object or world content, or builds its replica.</summary>
        private void Build(ClientWorldEntity mirrored)
        {
            if (mirrored.Binding != ClientEntityBinding.None || mirrored.Build != null) return;
            switch (mirrored.Source)
            {
                case WorldEntrySource.SceneObject:
                    if (index.TryGetBindable(mirrored.SceneKey, out var slot))
                    {
                        pendingPoses.Remove(mirrored.SceneKey);
                        slot.Bind(mirrored.Id);
                        mirrored.Slot = slot;
                        Attach(mirrored, slot.Object, ClientEntityBinding.SceneObject);
                    }
                    else
                    {
                        mirrored.Unbuildable = true;
                        counters.SceneKeysUnbound++;
                    }

                    break;
                case WorldEntrySource.WorldContent:
                    // Content this player has not built yet binds when its world provider registers it.
                    if (mirrored.ContentKey != null && localContent.TryGetValue(mirrored.ContentKey, out var content) && content.Entity.IsAlive)
                    {
                        var adopted = Adopt(content.Entity);
                        if (adopted != null) Attach(mirrored, adopted, ClientEntityBinding.Content);
                    }

                    break;
                default:
                    // This player's own prediction or shadow stands for the entity in place of a replica (WS3.7).
                    if (TryBindLocal(mirrored)) break;
                    if (mirrored.Kind == MultiplayerSpawnKinds.AssetPrefab) StartAssetBuild(mirrored);
                    else BuildWithFactory(mirrored);
                    break;
            }
        }

        private void BuildWithFactory(ClientWorldEntity mirrored)
        {
            TrackProvider(mirrored);
            mirrored.ProviderRetryAt = clock() + 5_000_000;
            IInternalReplicaFactory? factory;
            OperationResult<IEntity>? made;
            try
            {
                factory = factories(mirrored.Kind);
                made = factory?.CreateReplica(new InternalSpawnDescriptor(mirrored.Kind, ToTransform(mirrored.EntryPose, mirrored.Scale), mirrored.Payload));
            }
            catch (Exception)
            {
                factory = null;
                made = null;
            }

            if (factory == null || made == null || !made.TryGetValue(out var replica) || replica == null || !replica.IsAlive)
            {
                Unbuildable(mirrored);
                return;
            }

            mirrored.Factory = factory;
            mirrored.FactoryReplica = replica;
            mirrored.Binding = ClientEntityBinding.Factory;
            var adopted = Adopt(replica);
            if (adopted == null)
            {
                Release(mirrored);
                Unbuildable(mirrored);
                return;
            }
            Attach(mirrored, adopted, ClientEntityBinding.Factory);
            counters.ReplicasBuilt++;
        }

        private void Unbuildable(ClientWorldEntity mirrored)
        {
            mirrored.Unbuildable = true;
            counters.ReplicasUnbuildable++;
        }

        private INetworkWorldObject? Adopt(IEntity entity)
        {
            try
            {
                return backend.Adopt(entity);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Marks the object as the entity's network root and places it where the host's latest entry has it.</summary>
        private void Attach(ClientWorldEntity mirrored, INetworkWorldObject present, ClientEntityBinding binding)
        {
            mirrored.Object = present;
            mirrored.Binding = binding;
            mirrored.SimulatedHere = IsSimulating?.Invoke(present.Entity) == true;
            try
            {
                present.SetNetworkIdentity(mirrored.Id);
                if (!mirrored.SimulatedHere && binding != ClientEntityBinding.SceneObject && binding != ClientEntityBinding.Content) backend.PrepareReplica(present);
            }
            catch (Exception)
            {
                counters.MarkFailures++;
            }

            if (mirrored.Buffer == null || mirrored.Buffer.Count == 0) Place(mirrored);
        }

        private void Place(ClientWorldEntity mirrored)
        {
            if (mirrored.SimulatedHere) return;
            try
            {
                mirrored.Object?.WritePose(mirrored.EntryPose);
            }
            catch (Exception)
            {
                counters.MarkFailures++;
            }
        }

        /// <summary>Ends an entity here: its replica is released, or its scene object or content unbound.</summary>
        private void Remove(ClientWorldEntity mirrored, bool tombstone)
        {
            UntrackProvider(mirrored);
            Release(mirrored);
            driver.Untrack(mirrored);
            entities.Remove(mirrored.Id.Value);
            if (tombstone) table.Remove(mirrored.Id);
        }

        private void Release(ClientWorldEntity mirrored)
        {
            if (mirrored.Build != null) FinishBuild(mirrored, true);
            try
            {
                switch (mirrored.Binding)
                {
                    case ClientEntityBinding.SceneObject:
                        mirrored.Object?.SetNetworkIdentity(NetworkEntityId.None);
                        mirrored.Slot?.Unbind();
                        break;
                    case ClientEntityBinding.Content:
                        mirrored.Object?.SetNetworkIdentity(NetworkEntityId.None);
                        break;
                    case ClientEntityBinding.Asset:
                        mirrored.AssetReplica?.Dispose();
                        counters.ReplicasReleased++;
                        break;
                    case ClientEntityBinding.Factory:
                        if (mirrored.FactoryReplica != null) mirrored.Factory?.ReleaseReplica(mirrored.FactoryReplica);
                        counters.ReplicasReleased++;
                        break;
                    case ClientEntityBinding.Own:
                    case ClientEntityBinding.Shadow:
                        // The entity ended, or this player's world did: the instance that stood for it ends, and its handle dies.
                        EndLocal(mirrored.Local);
                        break;
                }
            }
            catch (Exception)
            {
                counters.ReleaseFailures++;
            }

            mirrored.Local = null;
            mirrored.Binding = ClientEntityBinding.None;
            mirrored.Object = null;
            mirrored.Slot = null;
            mirrored.AssetReplica = null;
            mirrored.ReplicaOwner = null;
            mirrored.Factory = null;
            mirrored.FactoryReplica = null;
        }

        /// <summary>
        /// The host has this unbound scene object somewhere else than the scene put it: its copy here, kinematic like every
        /// scene body, is placed there. A bound object follows its entity instead.
        /// </summary>
        private void PlaceSceneObject(SceneObjectKey key, in NetPose pose)
        {
            if (!index.TryGetBindable(key, out var slot))
            {
                // A key of a scene this player has not loaded yet waits for it; one it has, excluded or gone, binds nothing.
                if (index.TryGet(key, out _) || index.IsExcluded(key)) counters.SceneKeysUnbound++;
                else if (pendingPoses.Count < MaximumPendingKeys || pendingPoses.ContainsKey(key)) pendingPoses[key] = pose;
                else counters.SceneKeysDropped++;
                return;
            }

            // An object bound since follows its entity's samples instead.
            if (slot.IsBound || IsSimulating?.Invoke(slot.Object.Entity) == true) return;
            try
            {
                slot.Object.WritePose(pose);
                counters.ScenePosesPlaced++;
            }
            catch (Exception)
            {
                counters.MarkFailures++;
            }
        }

        /// <summary>The host no longer has this scene object: its copy here is hidden, and restored when this player leaves.</summary>
        private void RemoveSceneObject(SceneObjectKey key)
        {
            // Kept for a scene that loads later, or loads again, up to the bound.
            pendingPoses.Remove(key);
            if (removedKeys.Count < MaximumPendingKeys) removedKeys.Add(key);
            else if (!removedKeys.Contains(key)) counters.SceneKeysDropped++;
            if (!index.TryGet(key, out var slot))
            {
                counters.SceneKeysUnbound++;
                return;
            }

            if (slot.IsBound && entities.TryGetValue(slot.Bound.Value, out var mirrored)) Remove(mirrored, true);
            if (!slot.Removed) Hide(slot);
        }

        private void Hide(SceneObjectSlot slot)
        {
            slot.MarkRemoved();
            INativeAuthoritySwitch? presence;
            try
            {
                presence = backend.PresenceSwitch(slot.Object);
            }
            catch (Exception)
            {
                presence = null;
            }

            if (presence != null && suppressor.Suppress(new NativeAuthorityTarget(presence, false))) counters.SceneObjectsHidden++;
        }

        private static TransformState ToTransform(in NetPose pose, NetVector3 scale) =>
            new TransformState(
                new Vec3(pose.Position.X, pose.Position.Y, pose.Position.Z),
                new Quat(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W),
                new Vec3(scale.X, scale.Y, scale.Z));

        /// <summary>FNV-1a over what decides how an entity is built: its source, kind, keys, owner and description.</summary>
        private static ulong HashOf(WorldEntry update)
        {
            var hash = NetHash.Fnv1a64OffsetBasis;
            hash = Mix(hash, (byte)update.Source);
            hash = Mix(hash, update.Kind);
            hash = Mix(hash, update.OwnerModId);
            hash = Mix(hash, update.ContentKey ?? string.Empty);
            var key = update.SceneKey.Value;
            for (var shift = 0; shift < 64; shift += 8) hash = Mix(hash, (byte)(key >> shift));
            foreach (var value in update.Payload) hash = Mix(hash, value);
            return hash;
        }

        private static ulong Mix(ulong hash, string text)
        {
            foreach (var character in text)
            {
                hash = Mix(hash, (byte)character);
                hash = Mix(hash, (byte)(character >> 8));
            }

            return Mix(hash, 0xFF);
        }

        private static ulong Mix(ulong hash, byte value) => unchecked((hash ^ value) * NetHash.Fnv1a64Prime);

        /// <summary>World content this player built, waiting to bind by its key.</summary>
        private readonly struct LocalContentEntry
        {
            internal LocalContentEntry(object owner, IEntity entity)
            {
                Owner = owner;
                Entity = entity;
            }

            internal object Owner { get; }

            internal IEntity Entity { get; }
        }
    }
}
