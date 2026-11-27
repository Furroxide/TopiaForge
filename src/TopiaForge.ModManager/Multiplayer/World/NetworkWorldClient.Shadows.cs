using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// A joined player's shadows (WS3.7): a scripted spawn made here, which the host's own run of the same script makes
    /// canonically. It is never sent and never recorded; it stays frozen here and stands, in place of a replica, for the
    /// host's spawn with the same mod and shadow key (<see cref="ShadowKeyNumbering"/>). When the host's spawn with its key
    /// is another asset or kind, or another copy already stands for it, the shadow ends and the host's replica is built;
    /// when it does not come within <see cref="SpawnAuthorityRules.ShadowWaitMicros"/> of this player's synchronized world,
    /// the shadow ends. Either way this player sees exactly the host's world, and nothing is made twice.
    /// </summary>
    internal sealed partial class NetworkWorldClient
    {
        private readonly Dictionary<NetSpawnOrigin, ClientShadow> waitingShadows = new Dictionary<NetSpawnOrigin, ClientShadow>();
        private readonly List<ClientShadow> shadowScratch = new List<ClientShadow>();
        private readonly ShadowKeyNumbering shadowKeys;
        private long synchronizedSince;

        /// <summary>Gets how many shadows wait for the host's spawns.</summary>
        internal int WaitingShadows => waitingShadows.Count;

        /// <summary>Gets whether this player may make another shadow now: one more fits beside those that wait.</summary>
        internal bool CanShadow => IsActive && waitingShadows.Count < SpawnAuthorityRules.MaximumWaitingShadows;

        /// <summary>
        /// Takes a scripted spawn this player's mod made here as a shadow: numbers it, freezes it, and binds it at once to the
        /// host's spawn with its key when that is here already, in place of the replica built for it.
        /// </summary>
        internal ClientShadow Shadow(string modId, string kind, byte[] description, IEntity entity, IDisposable? ownedInstance, Mods.Internal.IInternalSpawnHandler? handler)
        {
            var shadow = new ClientShadow(shadowKeys.Next(modId), modId, kind, description, entity, ownedInstance, handler, clock());
            counters.ShadowsMade++;
            Freeze(shadow);
            localByEntity[shadow.EntityKey] = shadow;
            if (table.TryFindByOrigin(shadow.Origin, out var id) && entities.TryGetValue(id.Value, out var mirrored) && mirrored.ShadowKey == shadow.Key)
            {
                if (mirrored.Local != null || mirrored.Source != WorldEntrySource.SdkSpawn || !shadow.Matches(mirrored.Kind, mirrored.Payload))
                {
                    counters.ShadowsMismatched++;
                    EndShadow(shadow, ShadowEvent.Mismatched);
                    return shadow;
                }

                Release(mirrored);
                shadow.Apply(ShadowEvent.Matched);
                BindLocal(mirrored, shadow, ClientEntityBinding.Shadow);
                counters.ShadowsBound++;
                return shadow;
            }

            waitingShadows[shadow.Origin] = shadow;
            return shadow;
        }

        /// <summary>Build's step for an entity whose key a waiting shadow has: the shadow stands for it, or ends when it is something else.</summary>
        private bool TryBindShadow(ClientWorldEntity mirrored)
        {
            if (mirrored.ShadowKey < 1 || waitingShadows.Count == 0) return false;
            if (!NetSpawnOrigin.TryCreate(mirrored.OwnerModId, mirrored.ShadowKey, out var origin) || !waitingShadows.TryGetValue(origin, out var shadow)) return false;
            if (!shadow.IsAlive || !shadow.Matches(mirrored.Kind, mirrored.Payload))
            {
                counters.ShadowsMismatched++;
                EndShadow(shadow, ShadowEvent.Mismatched);
                return false;
            }

            waitingShadows.Remove(origin);
            shadow.Apply(ShadowEvent.Matched);
            BindLocal(mirrored, shadow, ClientEntityBinding.Shadow);
            counters.ShadowsBound++;
            return true;
        }

        /// <summary>Ends a shadow here: its instance goes, and its handle dies.</summary>
        private void EndShadow(ClientShadow shadow, ShadowEvent happened)
        {
            shadow.Apply(happened);
            if (shadow.State != ShadowState.Ended) shadow.Apply(ShadowEvent.Ended);
            if (waitingShadows.TryGetValue(shadow.Origin, out var waiting) && ReferenceEquals(waiting, shadow)) waitingShadows.Remove(shadow.Origin);
            if (localByEntity.TryGetValue(shadow.EntityKey, out var local) && ReferenceEquals(local, shadow)) localByEntity.Remove(shadow.EntityKey);
            shadow.End();
        }

        /// <summary>
        /// The shadow half of a frame: a waiting shadow whose mod disposed it is forgotten, and one whose host spawn has not
        /// come within the wait, counted while this player's world is synchronized, ends. Nothing runs, or allocates, while no
        /// shadow waits.
        /// </summary>
        private void PumpShadows(long now)
        {
            if (waitingShadows.Count == 0) return;
            shadowScratch.Clear();
            foreach (var shadow in waitingShadows.Values)
            {
                if (!shadow.IsAlive)
                {
                    shadowScratch.Add(shadow);
                    continue;
                }

                if (!synchronized) continue;
                var since = Math.Max(shadow.MadeMicros, synchronizedSince);
                if (now - since > SpawnAuthorityRules.ShadowWaitMicros) shadowScratch.Add(shadow);
            }

            foreach (var shadow in shadowScratch)
            {
                if (shadow.IsAlive) counters.ShadowsExpired++;
                EndShadow(shadow, shadow.IsAlive ? ShadowEvent.Expired : ShadowEvent.Ended);
            }

            shadowScratch.Clear();
        }

        /// <summary>This player's world went: every waiting shadow ends, as every bound one did with its entity.</summary>
        private void EndShadows()
        {
            if (waitingShadows.Count == 0) return;
            shadowScratch.Clear();
            shadowScratch.AddRange(waitingShadows.Values);
            foreach (var shadow in shadowScratch) EndShadow(shadow, ShadowEvent.Ended);
            shadowScratch.Clear();
        }
    }
}
