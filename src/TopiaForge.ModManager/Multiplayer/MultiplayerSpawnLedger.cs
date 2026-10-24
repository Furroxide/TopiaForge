using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>What a recorded spawn is.</summary>
    internal enum MultiplayerSpawnSource
    {
        /// <summary>Spawned by mod code, through <c>Assets.Spawn</c> or a first-party provider's own spawn.</summary>
        SdkSpawn = 0,

        /// <summary>World content every peer builds alike, bound by its content key rather than spawned again.</summary>
        WorldContent = 1
    }

    /// <summary>The replica kinds the runtime itself describes; first-party providers cannot claim them.</summary>
    internal static class MultiplayerSpawnKinds
    {
        /// <summary>A prefab spawned through a mod context's asset service; its bundle and asset name are its description.</summary>
        internal const string AssetPrefab = "asset.prefab";

        /// <summary>A scene object every peer loads, bound by its scene object key.</summary>
        internal const string SceneObject = "asset.scene";

        /// <summary>The prefix of every kind the runtime describes itself.</summary>
        internal const string RuntimePrefix = "asset.";

        /// <summary>Whether <paramref name="kind"/> belongs to the runtime rather than to a provider.</summary>
        internal static bool IsReserved(string kind) => kind.StartsWith(RuntimePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// One spawn the runtime recorded when it happened: who made it, with which network profile, what it is, how peers
    /// build it, and the live entity. A record ends when its entity does, when it is forgotten, or when the scope that
    /// made it ends; an ended record lets go of its entity, so it keeps nothing of an ended scope reachable. Main thread
    /// only.
    /// </summary>
    internal sealed class MultiplayerSpawnRecord
    {
        private IEntity? entity;
        private bool removed;

        internal MultiplayerSpawnRecord(long sequence, string entityKey, MultiplayerSpawnDescription description, IEntity entity, object? owner)
            : this(sequence, entityKey, description, entity, owner, 0, 0)
        {
        }

        internal MultiplayerSpawnRecord(
            long sequence,
            string entityKey,
            MultiplayerSpawnDescription description,
            IEntity entity,
            object? owner,
            long shadowKey,
            int ownerSlot)
        {
            if (shadowKey < 0) throw new ArgumentOutOfRangeException(nameof(shadowKey));
            if (ownerSlot < 0) throw new ArgumentOutOfRangeException(nameof(ownerSlot));
            Sequence = sequence;
            EntityKey = entityKey;
            Description = description;
            this.entity = entity;
            Owner = owner;
            ShadowKey = shadowKey;
            OwnerSlot = ownerSlot;
        }

        /// <summary>Gets the order this process recorded the spawn in, from 1. A sequence is never reused.</summary>
        internal long Sequence { get; }

        /// <summary>
        /// Gets the spawn's number in its mod's shadowable spawns (<see cref="ShadowKeyNumbering"/>), which a joined
        /// player's own run of the same script binds its shadow by, or zero when no joined player shadows it: a player's own
        /// action, a participant's or the gamemode server's code, or a spawn the host made for a player (WS3.7).
        /// </summary>
        internal long ShadowKey { get; private set; }

        /// <summary>
        /// Gets the participant slot of the player whose objects this spawn counts among, or zero for the host's world: a
        /// listen host makes a joined player's adopted spawn under that player's slot, and ends it when they leave (WS3.7).
        /// </summary>
        internal int OwnerSlot { get; private set; }

        /// <summary>Gets the entity's process-local id when it was recorded.</summary>
        internal string EntityKey { get; }

        /// <summary>Gets the live entity, or <see cref="ReleasedEntity.Instance"/> once the record has ended.</summary>
        internal IEntity Entity => entity ?? ReleasedEntity.Instance;

        /// <summary>Gets the seam that recorded it, whose scope ending ends the record; null when nothing owns it.</summary>
        internal object? Owner { get; }

        /// <summary>Gets who made it and how peers build it; registering the entity again replaces it.</summary>
        internal MultiplayerSpawnDescription Description { get; private set; }

        /// <summary>Gets whether the record still describes a live entity.</summary>
        internal bool IsLive => !removed && entity != null && entity.IsAlive;

        internal void Redescribe(MultiplayerSpawnDescription description) => Description = description;

        /// <summary>Makes the spawn a player's: it counts among their objects from now, and no shadow binds to it.</summary>
        internal void AssignOwner(int ownerSlot)
        {
            if (ownerSlot < 1) throw new ArgumentOutOfRangeException(nameof(ownerSlot));
            OwnerSlot = ownerSlot;
            ShadowKey = 0;
        }

        /// <summary>Ends the record and lets go of its entity.</summary>
        internal void MarkRemoved()
        {
            removed = true;
            entity = null;
        }

        public override string ToString() => "#" + Sequence + " " + Description.Kind + " of " + Description.OwnerModId;

        /// <summary>The entity an ended record reports: never alive, and holding nothing.</summary>
        internal sealed class ReleasedEntity : IEntity
        {
            internal static readonly ReleasedEntity Instance = new ReleasedEntity();

            public string Id => string.Empty;

            public string Name => "Ended spawn";

            public bool IsAlive => false;

            public Vec3 Position => Vec3.Zero;
        }
    }

    /// <summary>Who made a recorded spawn and how peers build it. Immutable, and it holds no handle of the scope that made it.</summary>
    internal sealed class MultiplayerSpawnDescription
    {
        /// <param name="ownerModId">The mod whose context made the spawn.</param>
        /// <param name="profile">The network profile of the context that made it.</param>
        /// <param name="source">Whether mod code spawned it or a world provider built it.</param>
        /// <param name="kind">The replica kind, a lowercase token, such as <c>asset.prefab</c>.</param>
        /// <param name="contentKey">World content only: the key every peer computes alike for it.</param>
        /// <param name="transform">Where the entity was when it was made.</param>
        /// <param name="descriptor">
        /// What a joined player builds it from: a <see cref="MultiplayerAssetDescriptor"/> for an asset spawn, the
        /// provider's own <c>InternalSpawnDescriptor</c> otherwise.
        /// </param>
        internal MultiplayerSpawnDescription(
            string ownerModId,
            ModNetProfile profile,
            MultiplayerSpawnSource source,
            string kind,
            string? contentKey,
            TransformState transform,
            object? descriptor)
        {
            if (string.IsNullOrEmpty(ownerModId)) throw new ArgumentException("A spawn needs the mod that made it.", nameof(ownerModId));
            if (source != MultiplayerSpawnSource.SdkSpawn && source != MultiplayerSpawnSource.WorldContent) throw new ArgumentOutOfRangeException(nameof(source));
            if (!NetworkEntityTable.IsValidKind(kind)) throw new ArgumentException("The spawn kind must be a lowercase token.", nameof(kind));
            var hasContentKey = !string.IsNullOrEmpty(contentKey);
            if (hasContentKey != (source == MultiplayerSpawnSource.WorldContent))
            {
                throw new ArgumentException("World content, and only world content, has a content key.", nameof(contentKey));
            }

            OwnerModId = ownerModId;
            Profile = profile;
            Source = source;
            Kind = kind;
            ContentKey = contentKey;
            Transform = transform;
            Descriptor = descriptor;
        }

        internal string OwnerModId { get; }

        internal ModNetProfile Profile { get; }

        internal MultiplayerSpawnSource Source { get; }

        internal string Kind { get; }

        internal string? ContentKey { get; }

        internal TransformState Transform { get; }

        internal object? Descriptor { get; }
    }

    /// <summary>
    /// Hears about spawns the ledger records while it is set: the network world (WS3.6) announces each one a listen host
    /// makes after its census. It is called on the main thread, inside the spawn, and must not throw or spawn.
    /// </summary>
    internal interface IMultiplayerSpawnObserver
    {
        /// <summary>
        /// A spawn was recorded, or, when <paramref name="redescribed"/>, an entity already recorded was described again in
        /// place.
        /// </summary>
        void Recorded(MultiplayerSpawnRecord record, bool redescribed);
    }

    /// <summary>
    /// The spawns the runtime records at spawn time, even offline, so that opening the running session to other players
    /// later can baseline them: SDK spawns and world content, each with the network profile of the context that made it.
    /// Recording happens only when something spawns, so an offline frame costs nothing. A record ends with its entity:
    /// dead records are swept as recording goes on, in amortized constant time, and whenever the live records are read. A
    /// record also ends when the scope whose seam recorded it ends, at once, so an ended session scope stays reachable
    /// through no record. While an observer is set, each new record and each new description is reported to it. Main
    /// thread only.
    /// </summary>
    internal sealed class MultiplayerSpawnLedger
    {
        /// <summary>How many records the ledger holds before its first sweep.</summary>
        internal const int MinimumSweepThreshold = 64;

        private readonly List<MultiplayerSpawnRecord> records = new List<MultiplayerSpawnRecord>();
        private readonly Dictionary<string, MultiplayerSpawnRecord> byEntity = new Dictionary<string, MultiplayerSpawnRecord>(StringComparer.Ordinal);
        private long nextSequence = 1;
        private int sweepThreshold = MinimumSweepThreshold;

        /// <summary>Gets how many records the ledger holds, including dead ones not yet swept.</summary>
        internal int Count => records.Count;

        /// <summary>
        /// Gets each mod's numbering of its shadowable spawns: a listen host's scripted spawns take their keys from it as
        /// they are recorded, and a joined player's shadows theirs as they are made (WS3.7). It starts over when this
        /// process's world session starts (<see cref="BeginWorldSession"/>) and when it joins a session.
        /// </summary>
        internal ShadowKeyNumbering ShadowKeys { get; } = new ShadowKeyNumbering();

        /// <summary>
        /// This process's world session started: every mod's shadow numbering starts over, as a joined player's does when it
        /// follows the host's session. Records already made keep their keys.
        /// </summary>
        internal void BeginWorldSession() => ShadowKeys.Restart();

        /// <summary>
        /// Gets or sets who hears about later records: the network world, while this process hosts. Null by default, so an
        /// offline spawn reports to nobody.
        /// </summary>
        internal IMultiplayerSpawnObserver? Observer { get; set; }

        /// <summary>Gets how many times the observer threw; the spawn that reported it succeeded regardless.</summary>
        internal int ObserverFailures { get; private set; }

        /// <summary>
        /// Whether spawns from a context with <paramref name="profile"/> are recorded: those a listen host would baseline.
        /// A client-local mod is never networked, and a standalone one never goes online.
        /// </summary>
        internal static bool Records(ModNetProfile profile) =>
            ExecutionRouting.Route(NetRealm.ListenHost, profile, IntentSource.None) == ExecutionRoute.Canonical;

        /// <summary>
        /// Records a spawn, or returns null when its profile is never baselined. An entity the ledger already holds alive is
        /// described again in place, keeping its place in the order: a first-party provider that spawns through the asset
        /// service and then registers the entity describes it best. <paramref name="owner"/> is the seam that recorded it;
        /// <see cref="ReleaseOwner"/> ends its records when its scope ends.
        /// </summary>
        /// <exception cref="ArgumentException">The entity has no id.</exception>
        internal MultiplayerSpawnRecord? Record(IEntity entity, MultiplayerSpawnDescription description, object? owner = null) =>
            Record(entity, description, owner, 0, 0);

        /// <summary>
        /// Records a spawn as <see cref="Record(IEntity, MultiplayerSpawnDescription, object)"/> does, with its shadow key
        /// and the participant slot it belongs to. Describing a recorded entity again under a player's slot makes it that
        /// player's (<see cref="MultiplayerSpawnRecord.AssignOwner"/>); otherwise it keeps the key and slot it was recorded
        /// with.
        /// </summary>
        internal MultiplayerSpawnRecord? Record(IEntity entity, MultiplayerSpawnDescription description, object? owner, long shadowKey, int ownerSlot)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (description == null) throw new ArgumentNullException(nameof(description));
            if (shadowKey < 0) throw new ArgumentOutOfRangeException(nameof(shadowKey));
            if (ownerSlot < 0) throw new ArgumentOutOfRangeException(nameof(ownerSlot));
            if (!Records(description.Profile)) return null;
            var key = entity.Id;
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A recorded entity needs its process-local id.", nameof(entity));
            if (byEntity.TryGetValue(key, out var existing) && existing.IsLive)
            {
                existing.Redescribe(description);
                if (ownerSlot > 0) existing.AssignOwner(ownerSlot);
                Notify(existing, true);
                return existing;
            }

            if (records.Count >= sweepThreshold) Sweep();
            var record = new MultiplayerSpawnRecord(nextSequence++, key, description, entity, owner, ownerSlot > 0 ? 0 : shadowKey, ownerSlot);
            records.Add(record);
            byEntity[key] = record;
            Notify(record, false);
            return record;
        }

        /// <summary>Forgets a record before its entity ends, as when a provider stops networking it.</summary>
        internal void Remove(MultiplayerSpawnRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            record.MarkRemoved();
            if (byEntity.TryGetValue(record.EntityKey, out var current) && ReferenceEquals(current, record)) byEntity.Remove(record.EntityKey);
        }

        /// <summary>Ends every record <paramref name="owner"/> made, at once: its scope has ended. Returns how many.</summary>
        internal int ReleaseOwner(object owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            var kept = 0;
            var released = 0;
            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];
                if (!ReferenceEquals(record.Owner, owner))
                {
                    records[kept++] = record;
                    continue;
                }

                Forget(record);
                released++;
            }

            records.RemoveRange(kept, records.Count - kept);
            return released;
        }

        /// <summary>Gets the record of a live entity, or null.</summary>
        internal MultiplayerSpawnRecord? Find(IEntity entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            var key = entity.Id;
            return !string.IsNullOrEmpty(key) && byEntity.TryGetValue(key, out var record) && record.IsLive ? record : null;
        }

        /// <summary>Gets the live records in the order they were recorded, sweeping the dead ones first.</summary>
        internal IReadOnlyList<MultiplayerSpawnRecord> Live()
        {
            Sweep();
            return records.Count == 0 ? Array.Empty<MultiplayerSpawnRecord>() : records.ToArray();
        }

        private void Notify(MultiplayerSpawnRecord record, bool redescribed)
        {
            var observer = Observer;
            if (observer == null) return;
            try
            {
                observer.Recorded(record, redescribed);
            }
            catch (Exception)
            {
                // The spawn already succeeded; its announcement is the observer's to retry.
                ObserverFailures++;
            }
        }

        private void Sweep()
        {
            var kept = 0;
            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];
                if (record.IsLive)
                {
                    records[kept++] = record;
                    continue;
                }

                Forget(record);
            }

            records.RemoveRange(kept, records.Count - kept);
            sweepThreshold = Math.Max(MinimumSweepThreshold, records.Count * 2);
        }

        private void Forget(MultiplayerSpawnRecord record)
        {
            if (byEntity.TryGetValue(record.EntityKey, out var current) && ReferenceEquals(current, record)) byEntity.Remove(record.EntityKey);
            record.MarkRemoved();
        }
    }
}
