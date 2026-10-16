using System;
using System.Collections.Generic;
using System.Net;

namespace TopiaForge.Multiplayer.NatTraversal.Turn
{
    /// <summary>
    /// Quotas, reclaim and channel-number lifetime for one allocation's peers and permissions. Every entry is
    /// attributed to the remote peer that caused it: a permission to the signaling peer whose candidate asked for it,
    /// a peer-path to the permitted address traffic arrived from. No one peer may hold more than its honest share, so a
    /// hostile or buggy peer that floods candidates or sprays source ports loses only its own entries and never the
    /// allocation's whole table, and the next peer still gets in. Entries whose server-side lifetime has passed and that
    /// nothing wants any more are reclaimed, so churn over an allocation's life never exhausts the table either. A
    /// channel number a peer-path gives up is held out of reuse until its binding would have expired plus the five-minute
    /// guard (RFC 8656, section 12), so a number is never rebound to a different peer early. None of this runs in the
    /// steady state: it is reached only when a new address or port is first seen, so a bound channel still allocates
    /// nothing per packet.
    /// </summary>
    internal sealed partial class TurnClient
    {
        /// <summary>The most permissions one signaling peer may hold: a single ICE offer carries at most this many candidates.</summary>
        internal const int MaximumPermissionsPerScope = 16;

        /// <summary>
        /// The most unsolicited source ports one permitted address may hold: ports this side never sent a check or datagram
        /// to, seen only in inbound relayed data. An honest peer's ports are all solicited (ICE checks every candidate it
        /// pairs with, bounded by the checker's own remote-candidate cap), so it needs none of these; eight is slack for
        /// reordering, and a peer spraying one address from many ports is held to it. Solicited ports are not counted here.
        /// </summary>
        internal const int MaximumUnsolicitedPortsPerAddress = 8;

        /// <summary>How long a channel number is kept out of reuse after its binding would lapse (RFC 8656, section 12).</summary>
        internal const long ChannelGuardMicros = 300_000_000;

        private readonly long[] channelFreeAt = new long[ChannelData.ChannelCount];
        private readonly List<Peer> evicting = new List<Peer>();
        private long permissionOrder;

        /// <summary>
        /// Makes room for a permission the given scope is about to cause: when that scope already holds its cap, its own
        /// oldest goes, so only it loses service; otherwise, when the table is globally full, a reclaimable entry goes.
        /// False when neither is possible.
        /// </summary>
        private bool MakePermissionRoom(string? scope, long now)
        {
            Permission? oldest = null;
            var held = 0;
            foreach (var permission in permissions.Values)
            {
                if (!ScopeEquals(permission.Scope, scope)) continue;
                held++;
                if (oldest == null || permission.Order < oldest.Order) oldest = permission;
            }

            if (held >= MaximumPermissionsPerScope)
            {
                if (oldest == null) return false;
                RemovePermission(oldest, now);
                return true;
            }

            return permissions.Count < MaximumPermissions || ReclaimPermission(now);
        }

        /// <summary>Frees the oldest permission whose server-side lifetime has passed and that nothing wants any more.</summary>
        private bool ReclaimPermission(long now)
        {
            Permission? stale = null;
            foreach (var permission in permissions.Values)
            {
                if (permission.Wanted || permission.InFlight) continue;
                if (permission.State == PermissionState.Installed && now < permission.ExpiresAt) continue;
                if (stale == null || permission.Order < stale.Order) stale = permission;
            }

            if (stale == null) return false;
            RemovePermission(stale, now);
            return true;
        }

        /// <summary>Removes a permission and every peer-path on its address, so a scope's whole footprint goes together.</summary>
        private void RemovePermission(Permission permission, long now)
        {
            RemovePeersAt(permission.Address, now);
            permissions.Remove(permission.Address);
        }

        /// <summary>
        /// Makes room for a peer-path to <paramref name="endpoint"/>. A solicited path (one this side is sending to) is
        /// bounded already by the checker that drives it, so only the global table is guarded. An unsolicited path, seen
        /// only in inbound data, is held to a small cap per address: when it is reached, that address's least-recently-used
        /// unsolicited port goes, so a sprayer from one address loses only its own idle ports. False when no room is possible.
        /// </summary>
        private bool MakePeerRoom(IPEndPoint endpoint, bool solicited, long now)
        {
            if (!solicited)
            {
                var address = NatAddresses.Unmap(endpoint.Address);
                Peer? stale = null;
                var held = 0;
                foreach (var peer in peersByPath.Values)
                {
                    if (peer.Solicited || !NatAddresses.SameAddress(peer.EndPoint.Address, address)) continue;
                    held++;

                    // Evict the least-recently-used unsolicited port, never a path a check or datagram keeps warm.
                    if (stale == null || peer.LastUsedAt < stale.LastUsedAt) stale = peer;
                }

                if (held >= MaximumUnsolicitedPortsPerAddress)
                {
                    if (stale == null) return false;
                    RemovePeer(stale, now);
                    return true;
                }
            }

            return peersByPath.Count < MaximumPeers || ReclaimPeer(now);
        }

        /// <summary>Frees the least-recently-used peer whose permission has lapsed and nothing wants, and whose channel is not live.</summary>
        private bool ReclaimPeer(long now)
        {
            Peer? stale = null;
            foreach (var peer in peersByPath.Values)
            {
                if (peer.Permission.Wanted) continue;
                if (peer.Permission.State == PermissionState.Installed && now < peer.Permission.ExpiresAt) continue;
                if (peer.Channel != 0 && now < peer.BoundUntil) continue;
                if (stale == null || peer.LastUsedAt < stale.LastUsedAt) stale = peer;
            }

            if (stale == null) return false;
            RemovePeer(stale, now);
            return true;
        }

        private void RemovePeersAt(IPAddress address, long now)
        {
            evicting.Clear();
            foreach (var peer in peersByPath.Values)
            {
                if (NatAddresses.SameAddress(peer.EndPoint.Address, address)) evicting.Add(peer);
            }

            foreach (var peer in evicting) RemovePeer(peer, now);
            evicting.Clear();
        }

        /// <summary>Drops one peer-path and retires its channel number under the guard, so no other peer rebinds it early.</summary>
        private void RemovePeer(Peer peer, long now)
        {
            peersByPath.Remove(peer.Path);
            peersByKey.Remove(PeerKey.From(peer.EndPoint));
            if (peer.ChannelTransaction != null)
            {
                stun.Cancel(peer.ChannelTransaction);
                peer.ChannelTransaction = null;
            }

            if (peer.Channel != 0)
            {
                var index = peer.Channel - ChannelData.FirstChannel;
                if (ReferenceEquals(peersByChannel[index], peer))
                {
                    peersByChannel[index] = null;
                    ChannelCount--;
                    channelFreeAt[index] = Math.Max(peer.BoundUntil, now) + ChannelGuardMicros;
                }
            }
        }

        /// <summary>
        /// Takes a channel number for a new binding: a fresh one from the frontier, or, once that is spent, one whose
        /// guard has passed. False when none is free, and data stays on Send indications until one is.
        /// </summary>
        private bool TryAcquireChannel(long now, out ushort number)
        {
            if (nextChannel <= ChannelData.LastChannel)
            {
                number = (ushort)nextChannel++;
                channelFreeAt[number - ChannelData.FirstChannel] = 0;
                return true;
            }

            for (var index = 0; index < channelFreeAt.Length; index++)
            {
                if (peersByChannel[index] == null && channelFreeAt[index] != 0 && now >= channelFreeAt[index])
                {
                    channelFreeAt[index] = 0;
                    number = (ushort)(ChannelData.FirstChannel + index);
                    return true;
                }
            }

            number = 0;
            return false;
        }

        private static bool ScopeEquals(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    }
}
