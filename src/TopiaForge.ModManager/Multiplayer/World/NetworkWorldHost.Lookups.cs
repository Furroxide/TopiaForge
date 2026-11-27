using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// What the host's world answers about its entities: the session's lookups and references, and authority routing's
    /// (WS3.7) checks of a player's request against the entity it names.
    /// </summary>
    internal sealed partial class NetworkWorldHost
    {
        internal bool TryBindMotion(MotionTarget target, out HostWorldEntity hosted)
        {
            if (!target.IsScene) return TryGetHosted(target.Id, out hosted);
            if (index.TryGetBindable(target.SceneKey, out var slot) && slot.Object.IsAlive)
            {
                if (!slot.IsBound) Bind(slot);
                if (slot.IsBound) return TryGetHosted(slot.Bound, out hosted);
            }

            hosted = null!;
            return false;
        }

        /// <summary>Gets the entity a network id names here, if it is alive.</summary>
        internal bool TryGetEntity(NetworkEntityId id, out IEntity entity)
        {
            if (entities.TryGetValue(id.Value, out var hosted) && hosted.IsAlive)
            {
                entity = hosted.Object?.Entity ?? hosted.Record!.Entity;
                return true;
            }

            entity = null!;
            return false;
        }

        /// <summary>Whether this host replicates the entity of <paramref name="id"/>.</summary>
        internal bool Knows(NetworkEntityId id) => entities.ContainsKey(id.Value);

        /// <summary>Gets the live entity <paramref name="id"/> names, for authority routing (WS3.7) to check a player's request against.</summary>
        internal bool TryGetHosted(NetworkEntityId id, out HostWorldEntity hosted)
        {
            if (entities.TryGetValue(id.Value, out var found) && found.IsAlive)
            {
                hosted = found;
                return true;
            }

            hosted = null!;
            return false;
        }

        /// <summary>Gets the id the host gave a recorded spawn when it registered it.</summary>
        internal bool TryGetId(MultiplayerSpawnRecord record, out NetworkEntityId id)
        {
            if (record != null && byRecord.TryGetValue(record, out var hosted))
            {
                id = hosted.Id;
                return true;
            }

            id = NetworkEntityId.None;
            return false;
        }

        /// <summary>Appends the ids of the live entities this host replicates, in ascending order.</summary>
        internal void CollectIds(List<uint> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            foreach (var hosted in ordered)
            {
                if (hosted.IsAlive) into.Add(hosted.Id.Value);
            }
        }
    }
}
