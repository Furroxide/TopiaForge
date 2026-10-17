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
        /// <summary>A prefab spawned through a mod context's asset service; the spawn request is its description.</summary>
        internal const string AssetPrefab = "asset.prefab";

        /// <summary>The prefix of every kind the runtime describes itself.</summary>
        internal const string RuntimePrefix = "asset.";

        /// <summary>Whether <paramref name="kind"/> belongs to the runtime rather than to a provider.</summary>
        internal static bool IsReserved(string kind) => kind.StartsWith(RuntimePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// One spawn the runtime recorded when it happened: who made it, with which network profile, what it is, how peers
    /// build it, and the live entity. A record ends when its entity does. Main thread only.
    /// </summary>
    internal sealed class MultiplayerSpawnRecord
    {
        private bool removed;

        internal MultiplayerSpawnRecord(long sequence, string entityKey, MultiplayerSpawnDescription description, IEntity entity)
        {
            Sequence = sequence;
            EntityKey = entityKey;
            Description = description;
            Entity = entity;
        }

        /// <summary>Gets the order this process recorded the spawn in, from 1. A sequence is never reused.</summary>
        internal long Sequence { get; }

        /// <summary>Gets the entity's process-local id when it was recorded.</summary>
        internal string EntityKey { get; }

        /// <summary>Gets the live entity.</summary>
        internal IEntity Entity { get; }

        /// <summary>Gets who made it and how peers build it; registering the entity again replaces it.</summary>
        internal MultiplayerSpawnDescription Description { get; private set; }

        /// <summary>Gets whether the record still describes a live entity.</summary>
        internal bool IsLive => !removed && Entity.IsAlive;

        internal void Redescribe(MultiplayerSpawnDescription description) => Description = description;

        internal void MarkRemoved() => removed = true;

        public override string ToString() => "#" + Sequence + " " + Description.Kind + " of " + Description.OwnerModId;
    }

    /// <summary>Who made a recorded spawn and how peers build it. Immutable.</summary>
    internal sealed class MultiplayerSpawnDescription
    {
        /// <param name="ownerModId">The mod whose context made the spawn.</param>
        /// <param name="profile">The network profile of the context that made it.</param>
        /// <param name="source">Whether mod code spawned it or a world provider built it.</param>
        /// <param name="kind">The replica kind, a lowercase token, such as <c>asset.prefab</c>.</param>
        /// <param name="contentKey">World content only: the key every peer computes alike for it.</param>
        /// <param name="transform">Where the entity was when it was made.</param>
        /// <param name="descriptor">What it was made from: the spawn request, or the provider's own descriptor.</param>
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
    /// The spawns the runtime records at spawn time, even offline, so that opening the running session to other players
    /// later can baseline them: SDK spawns and world content, each with the network profile of the context that made it.
    /// Recording happens only when something spawns, so an offline frame costs nothing. A record ends with its entity:
    /// dead records are swept as recording goes on, in amortized constant time, and whenever the live records are read.
    /// Main thread only.
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
        /// Whether spawns from a context with <paramref name="profile"/> are recorded: those a listen host would baseline.
        /// A client-local mod is never networked, and a standalone one never goes online.
        /// </summary>
        internal static bool Records(ModNetProfile profile) =>
            ExecutionRouting.Route(NetRealm.ListenHost, profile, IntentSource.None) == ExecutionRoute.Canonical;

        /// <summary>
        /// Records a spawn, or returns null when its profile is never baselined. An entity the ledger already holds alive is
        /// described again in place, keeping its place in the order: a first-party provider that spawns through the asset
        /// service and then registers the entity describes it best.
        /// </summary>
        /// <exception cref="ArgumentException">The entity has no id.</exception>
        internal MultiplayerSpawnRecord? Record(IEntity entity, MultiplayerSpawnDescription description)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (description == null) throw new ArgumentNullException(nameof(description));
            if (!Records(description.Profile)) return null;
            var key = entity.Id;
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A recorded entity needs its process-local id.", nameof(entity));
            if (byEntity.TryGetValue(key, out var existing) && existing.IsLive)
            {
                existing.Redescribe(description);
                return existing;
            }

            if (records.Count >= sweepThreshold) Sweep();
            var record = new MultiplayerSpawnRecord(nextSequence++, key, description, entity);
            records.Add(record);
            byEntity[key] = record;
            return record;
        }

        /// <summary>Forgets a record before its entity ends, as when a provider stops networking it.</summary>
        internal void Remove(MultiplayerSpawnRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            record.MarkRemoved();
            if (byEntity.TryGetValue(record.EntityKey, out var current) && ReferenceEquals(current, record)) byEntity.Remove(record.EntityKey);
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

                if (byEntity.TryGetValue(record.EntityKey, out var current) && ReferenceEquals(current, record)) byEntity.Remove(record.EntityKey);
            }

            records.RemoveRange(kept, records.Count - kept);
            sweepThreshold = Math.Max(MinimumSweepThreshold, records.Count * 2);
        }
    }
}
