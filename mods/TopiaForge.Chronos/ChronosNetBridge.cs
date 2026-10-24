using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Chronos
{
    /// <summary>
    /// Chronos in a multiplayer session (WS3.13), through the runtime's replication seam of Chronos' own context. It follows
    /// the realm once a frame. A listen host publishes the time its leases derive, which every joined player follows, and a
    /// joined player follows the host's time, a late joiner included, rather than its own leases. A change of realm ends
    /// every lease, driver and turn scheduler whose holder may no longer change time there, as a scene change ends them all:
    /// opening a game to other players ends a participant's freeze, while the gamemode's server controller keeps its own.
    /// Online the game's own pause menu is one player's alone, so the clock no longer yields to it. Without a seam, as in a
    /// test double's context, this is single player, unchanged. Unity-free; main thread.
    /// </summary>
    internal sealed class ChronosNetBridge
    {
        private readonly IRuntimeReplication? seam;
        private readonly List<HeldLease> leases = new List<HeldLease>();
        private ITurnScheduler? scheduler;
        private IRuntimeReplication? schedulerHolder;
        private InternalNetRealm realm;
        private InternalSessionTime published;
        private bool mustPublish = true;

        internal ChronosNetBridge(IRuntimeReplication? seam)
        {
            this.seam = seam;
            realm = seam?.Realm ?? InternalNetRealm.Offline;
        }

        /// <summary>Gets the realm as of the last <see cref="Update"/>.</summary>
        internal InternalNetRealm Realm => realm;

        /// <summary>Gets whether this process follows its host's time: a joined player.</summary>
        internal bool IsFollowing => realm == InternalNetRealm.Client;

        /// <summary>Gets whether the clock yields to the game's own pause menu: in single player only.</summary>
        internal bool YieldsToNativePause => seam == null || seam.NativePauseFreezesWorld;

        /// <summary>Gets the time a joined player follows: the host's, or real time until the host's first arrives.</summary>
        internal InternalSessionTime FollowedTime => seam?.SessionTime ?? InternalSessionTime.Realtime;

        /// <summary>Gets how many leases from holders that could lose authority are followed.</summary>
        internal int FollowedLeases => leases.Count;

        /// <summary>
        /// Follows the realm, once a frame. On a change it ends what lost its authority and publishes this process's time
        /// again, then answers true. It allocates nothing.
        /// </summary>
        internal bool Update()
        {
            if (seam == null) return false;
            var now = seam.Realm;
            if (now == realm) return false;
            realm = now;
            mustPublish = true;
            Revoke();
            return true;
        }

        /// <summary>Follows a lease <paramref name="holder"/>'s code took, so a change of realm can end it.</summary>
        internal void Track(ITimeLease lease, IRuntimeReplication? holder)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            if (seam == null || holder == null) return;
            for (var index = leases.Count - 1; index >= 0; index--)
            {
                if (!leases[index].Lease.IsActive) leases.RemoveAt(index);
            }

            leases.Add(new HeldLease(lease, holder));
        }

        /// <summary>Follows the turn scheduler <paramref name="holder"/>'s code began: Chronos runs one at a time.</summary>
        internal void Track(ITurnScheduler turns, IRuntimeReplication? holder)
        {
            if (turns == null) throw new ArgumentNullException(nameof(turns));
            if (seam == null || holder == null)
            {
                scheduler = null;
                schedulerHolder = null;
                return;
            }

            scheduler = turns;
            schedulerHolder = holder;
        }

        /// <summary>
        /// Publishes this process's derived time when it changed, or the realm did: offline it is kept for a later
        /// promotion, and a listen host's players follow it. A joined player follows and never publishes.
        /// </summary>
        internal void Publish(float worldScale, TimeMode mode)
        {
            if (seam == null || IsFollowing) return;
            if (!ChronosNetRules.TryDescribe(worldScale, mode, out var time)) return;
            if (!mustPublish && time.Equals(published)) return;

            // A refusal, while the context stops or before this frame's Update saw a join, leaves it to the next change.
            if (!seam.PublishSessionTime(time).Succeeded) return;
            published = time;
            mustPublish = false;
        }

        private void Revoke()
        {
            for (var index = leases.Count - 1; index >= 0; index--)
            {
                var held = leases[index];
                if (held.Lease.IsActive && MayChangeTime(held.Holder)) continue;
                leases.RemoveAt(index);
                held.Lease.Release();
            }

            var turns = scheduler;
            if (turns == null || MayChangeTime(schedulerHolder!)) return;
            scheduler = null;
            schedulerHolder = null;
            turns.Dispose();
        }

        private static bool MayChangeTime(IRuntimeReplication holder) => holder.TimeAuthority == InternalTimeAuthority.Apply;

        private readonly struct HeldLease
        {
            internal HeldLease(ITimeLease lease, IRuntimeReplication holder)
            {
                Lease = lease;
                Holder = holder;
            }

            internal ITimeLease Lease { get; }

            internal IRuntimeReplication Holder { get; }
        }
    }
}
