using System;
using System.Collections.Generic;
using System.Net;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.Turn
{
    /// <summary>
    /// Who an allocation's permissions and peer-paths are for. Each is claimed by a scope, one per remote side the NAT agent
    /// talks to, and stays exactly as long as some scope claims it. A scope that ends, or claims past its share, gives up
    /// only its own claims: an entry two scopes claim, as a relay address several peers share is, stays while either does,
    /// and nothing one scope does removes what another claims. The shares are sized so that every scope can always hold its
    /// whole share at once: at most <see cref="MaximumScopes"/> scopes, each claiming at most
    /// <see cref="MaximumPermissionsPerScope"/> permissions and <see cref="MaximumPathsPerScope"/> peer-paths, and tables
    /// with room for all of them. However much hostile peers claim, the next peer still gets its own, and none loses a path
    /// it holds. Datagrams from a port no scope claims, as from a spray over many source ports, land in a small pool of
    /// their own, at most <see cref="MaximumUnclaimedPerAddress"/> from one address, where the least recently used makes
    /// way. A pooled path never binds a channel, so letting it go leaves nothing on the server. A channel number a
    /// peer-path gives up comes back to the same peer address and port, since the server may still hold that binding, and
    /// to any other only once the binding would have lapsed plus the five-minute guard (RFC 8656, section 12). None of this
    /// runs per datagram: only when an entry is claimed, given up, or first heard from.
    /// </summary>
    internal sealed partial class TurnClient
    {
        /// <summary>The most scopes one allocation serves at once: one for each session the NAT agent may run.</summary>
        internal const int MaximumScopes = 32;

        /// <summary>The most permissions one scope may claim: a single ICE offer carries at most this many candidates.</summary>
        internal const int MaximumPermissionsPerScope = 16;

        /// <summary>The most peer-paths one scope may claim: one per remote candidate its checks pair with this allocation.</summary>
        internal const int MaximumPathsPerScope = 24;

        /// <summary>The most peer-paths no scope claims, heard from or sent to before anything paired them.</summary>
        internal const int MaximumUnclaimedPaths = 64;

        /// <summary>The most unclaimed peer-paths one permitted address may hold, so one sprayer cannot take the whole pool.</summary>
        internal const int MaximumUnclaimedPerAddress = 8;

        /// <summary>Every scope's whole share of permissions at once.</summary>
        internal const int MaximumPermissions = MaximumScopes * MaximumPermissionsPerScope;

        /// <summary>Every scope's whole share of peer-paths at once, and the pool.</summary>
        internal const int MaximumPeers = (MaximumScopes * MaximumPathsPerScope) + MaximumUnclaimedPaths;

        /// <summary>How long a channel number is kept from any other peer after its binding would lapse (RFC 8656, section 12).</summary>
        internal const long ChannelGuardMicros = 300_000_000;

        private readonly Dictionary<string, Scope> scopes = new Dictionary<string, Scope>(StringComparer.Ordinal);
        private readonly List<Peer> unclaimed = new List<Peer>();
        private readonly List<Peer> evicting = new List<Peer>();
        private readonly long[] channelFreeAt = new long[ChannelData.ChannelCount];

        /// <summary>The peer address and port each retired channel number was bound to, kept once the first number retires.</summary>
        private PeerKey[]? channelKeys;

        /// <summary>Gets how many scopes hold claims on this allocation.</summary>
        internal int Scopes => scopes.Count;

        /// <summary>Gets how many peer-paths no scope claims.</summary>
        internal int UnclaimedPaths => unclaimed.Count;

        /// <summary>
        /// Gives up every claim <paramref name="scope"/> holds, as when its session ends: what no other scope claims goes at
        /// once, and the channels it held retire under their guard.
        /// </summary>
        internal void Release(string scope, long now)
        {
            if (scope == null || !scopes.TryGetValue(scope, out var held)) return;
            scopes.Remove(scope);
            for (var index = held.Paths.Count - 1; index >= 0; index--) Unclaim(held, held.Paths[index], now);
            for (var index = held.Permissions.Count - 1; index >= 0; index--) Unclaim(held, held.Permissions[index], now);
        }

        /// <summary>
        /// Claims the permission for <paramref name="address"/> for <paramref name="scope"/>, adding it when new. A scope at
        /// its share gives up its own oldest claim first. Null when the allocation already serves as many scopes as it may.
        /// </summary>
        private Permission? ClaimPermission(IPAddress address, string scope, long now)
        {
            var held = ScopeNamed(scope);
            if (held == null) return null;
            permissions.TryGetValue(address, out var permission);
            if (permission != null && permission.Claimants.Contains(held)) return permission;
            if (held.Permissions.Count >= MaximumPermissionsPerScope) Unclaim(held, held.Permissions[0], now);
            if (permission == null)
            {
                // Every scope's whole share fits, so this holds whatever the others claim.
                if (permissions.Count >= MaximumPermissions) return null;
                permission = new Permission(address);
                permissions.Add(address, permission);
            }

            permission.Claimants.Add(held);
            held.Permissions.Add(permission);
            return permission;
        }

        /// <summary>
        /// Claims the peer-path to <paramref name="endpoint"/> for <paramref name="scope"/>, and its address's permission. A
        /// path already pooled becomes claimed; a scope at its share gives up its own least recently used path first.
        /// </summary>
        private Peer? ClaimPath(IPEndPoint endpoint, TransportPath path, string scope, long now)
        {
            var permission = ClaimPermission(endpoint.Address, scope, now);
            if (permission == null) return null;
            var held = scopes[scope];
            if (peersByPath.TryGetValue(path, out var peer))
            {
                if (peer.Claimants.Contains(held)) return peer;
                if (peer.Claimants.Count == 0) LeavePool(peer);
            }
            else
            {
                if (peersByPath.Count >= MaximumPeers && !EvictUnclaimed(null)) return null;
                peer = AddPeer(endpoint, path, permission, true, now);
            }

            if (held.Paths.Count >= MaximumPathsPerScope) Unclaim(held, LeastRecentlyUsed(held.Paths), now);
            peer.Claimants.Add(held);
            held.Paths.Add(peer);
            return peer;
        }

        /// <summary>
        /// Adds a peer-path no scope claims, at an address that holds a permission: a port heard from, or sent to, before
        /// anything paired it. The pool's least recently used path makes way, of the same address when that one is full.
        /// </summary>
        private Peer? AddUnclaimed(IPEndPoint endpoint, TransportPath path, bool solicited, long now)
        {
            if (!permissions.TryGetValue(endpoint.Address, out var permission)) return null;
            if (permission.Unclaimed >= MaximumUnclaimedPerAddress) EvictUnclaimed(permission);
            else if (unclaimed.Count >= MaximumUnclaimedPaths) EvictUnclaimed(null);
            if (peersByPath.Count >= MaximumPeers && !EvictUnclaimed(null)) return null;
            var peer = AddPeer(endpoint, path, permission, solicited, now);
            permission.Unclaimed++;
            unclaimed.Add(peer);
            return peer;
        }

        private Peer AddPeer(IPEndPoint endpoint, TransportPath path, Permission permission, bool solicited, long now)
        {
            var peer = new Peer(endpoint, path, permission) { LastUsedAt = now, Solicited = solicited };
            peersByPath.Add(path, peer);
            peersByKey[peer.Key] = peer;
            return peer;
        }

        /// <summary>Lets the least recently used pooled path go, of one address or of all; one never sent to goes before one that was.</summary>
        private bool EvictUnclaimed(Permission? of)
        {
            Peer? stale = null;
            foreach (var peer in unclaimed)
            {
                if (of != null && !ReferenceEquals(peer.Permission, of)) continue;
                if (stale == null || (peer.Solicited != stale.Solicited ? !peer.Solicited : peer.LastUsedAt < stale.LastUsedAt)) stale = peer;
            }

            if (stale == null) return false;
            RemovePeer(stale, 0);
            return true;
        }

        private void LeavePool(Peer peer)
        {
            if (unclaimed.Remove(peer)) peer.Permission.Unclaimed--;
        }

        private Scope? ScopeNamed(string scope)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (scopes.TryGetValue(scope, out var held)) return held;
            if (scopes.Count >= MaximumScopes) return null;
            held = new Scope();
            scopes.Add(scope, held);
            return held;
        }

        private void Unclaim(Scope scope, Permission permission, long now)
        {
            scope.Permissions.Remove(permission);
            permission.Claimants.Remove(scope);
            if (permission.Claimants.Count == 0) RemovePermission(permission, now);
        }

        private void Unclaim(Scope scope, Peer peer, long now)
        {
            scope.Paths.Remove(peer);
            peer.Claimants.Remove(scope);
            if (peer.Claimants.Count == 0) RemovePeer(peer, now);
        }

        /// <summary>A permission nobody claims goes, and every peer-path on its address with it.</summary>
        private void RemovePermission(Permission permission, long now)
        {
            evicting.Clear();
            foreach (var peer in peersByPath.Values)
            {
                if (ReferenceEquals(peer.Permission, permission)) evicting.Add(peer);
            }

            foreach (var peer in evicting) RemovePeer(peer, now);
            evicting.Clear();
            foreach (var scope in permission.Claimants) scope.Permissions.Remove(permission);
            permission.Claimants.Clear();
            permissions.Remove(permission.Address);
        }

        /// <summary>Drops one peer-path and retires its channel number under the guard, keeping which peer it was bound to.</summary>
        private void RemovePeer(Peer peer, long now)
        {
            peersByPath.Remove(peer.Path);
            if (peersByKey.TryGetValue(peer.Key, out var indexed) && ReferenceEquals(indexed, peer)) peersByKey.Remove(peer.Key);
            if (peer.Claimants.Count == 0) LeavePool(peer);
            foreach (var scope in peer.Claimants) scope.Paths.Remove(peer);
            peer.Claimants.Clear();
            if (peer.ChannelTransaction != null)
            {
                stun.Cancel(peer.ChannelTransaction);
                peer.ChannelTransaction = null;
            }

            if (peer.Channel == 0) return;
            var index = peer.Channel - ChannelData.FirstChannel;
            if (!ReferenceEquals(peersByChannel[index], peer)) return;
            peersByChannel[index] = null;
            ChannelCount--;

            // The server may hold the binding for ten minutes after the last bind request for it, answered or not.
            var lapses = Math.Max(peer.BoundUntil, peer.BindRequestedAt + ChannelLifetimeMicros);
            channelFreeAt[index] = Math.Max(lapses, now) + ChannelGuardMicros;
            (channelKeys ??= new PeerKey[ChannelData.ChannelCount])[index] = peer.Key;
        }

        private static Peer LeastRecentlyUsed(List<Peer> paths)
        {
            var stale = paths[0];
            foreach (var peer in paths)
            {
                if (peer.LastUsedAt < stale.LastUsedAt) stale = peer;
            }

            return stale;
        }

        /// <summary>
        /// Takes a channel number for a new binding: the one this peer's address and port last had while the server may
        /// still hold it, else a fresh one from the frontier, or once that is spent one whose guard has passed. False when
        /// none is free, and data stays on Send indications until one is.
        /// </summary>
        private bool TryAcquireChannel(Peer peer, long now, out ushort number)
        {
            if (channelKeys != null)
            {
                for (var index = 0; index < channelKeys.Length; index++)
                {
                    if (peersByChannel[index] != null || channelFreeAt[index] <= now || !channelKeys[index].Equals(peer.Key)) continue;
                    channelFreeAt[index] = 0;
                    number = (ushort)(ChannelData.FirstChannel + index);
                    return true;
                }
            }

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

        /// <summary>One scope's claims, each list in the order they were made.</summary>
        private sealed class Scope
        {
            internal List<Permission> Permissions { get; } = new List<Permission>();

            internal List<Peer> Paths { get; } = new List<Peer>();
        }
    }
}
