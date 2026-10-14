using System;
using System.Collections.Generic;
using System.Net;
using TopiaForge.Multiplayer.NatTraversal.Stun;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.Turn
{
    /// <summary>
    /// Peers. A permission lets one peer address send through the allocation (RFC 8656, section 9); the client installs
    /// one for every address it is asked to, in batches of up to eight, and refreshes it while it is wanted. A channel
    /// binds one peer address and port to a number from 0x4000 to 0x4FFF (section 12), so data then travels with a
    /// four-byte header instead of a Send or Data indication. A channel is bound the first time a peer is sent to or
    /// heard from, numbers are never reused within an allocation, and data waits for the binding's answer rather than
    /// risk loss: until then it goes as Send indications.
    /// </summary>
    internal sealed partial class TurnClient
    {
        internal const int MaximumPeers = 256;
        internal const int MaximumPermissions = 256;
        internal const int PermissionsPerRequest = 8;

        private readonly Dictionary<TransportPath, Peer> peersByPath = new Dictionary<TransportPath, Peer>();
        private readonly Peer?[] peersByChannel;
        private readonly Dictionary<IPAddress, Permission> permissions = new Dictionary<IPAddress, Permission>();
        private readonly List<Permission> duePermissions = new List<Permission>();
        private readonly List<Peer> dueChannels = new List<Peer>();
        private readonly StunMessageWriter dataWriter = new StunMessageWriter();
        private int nextChannel = ChannelData.FirstChannel;

        /// <summary>Gets how many channels are bound or binding.</summary>
        internal int ChannelCount { get; private set; }

        /// <summary>Asks for a permission for <paramref name="address"/> and keeps it fresh until <see cref="Forget"/>.</summary>
        internal void Permit(IPAddress address, long now)
        {
            if (address == null) throw new ArgumentNullException(nameof(address));
            var permission = PermissionFor(NatAddresses.Unmap(address), true);
            if (permission != null) permission.Wanted = true;
        }

        /// <summary>Stops refreshing <paramref name="address"/>'s permission; the server lets it lapse.</summary>
        internal void Forget(IPAddress address)
        {
            if (address != null && permissions.TryGetValue(NatAddresses.Unmap(address), out var permission)) permission.Wanted = false;
        }

        /// <summary>Gets whether data from or to <paramref name="address"/> may pass the allocation now.</summary>
        internal bool IsPermitted(IPAddress address) =>
            address != null && permissions.TryGetValue(NatAddresses.Unmap(address), out var permission) && permission.State == PermissionState.Installed;

        /// <summary>Gets the relayed path that reaches <paramref name="peer"/> through this allocation.</summary>
        internal TransportPath PathTo(IPEndPoint peer)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var path = TurnRelayPath.For(Allocation, peer);
            return peersByPath.TryGetValue(path, out var known) ? known.Path : path;
        }

        /// <summary>
        /// Wraps a datagram for a peer reached through this allocation into <paramref name="output"/>: ChannelData once
        /// the channel is bound, a Send indication until then. Returns its length, or 0 when it cannot go yet because the
        /// allocation is not ready or the peer's permission is not installed. A bound peer's datagram allocates nothing.
        /// </summary>
        internal int Wrap(TransportPath path, byte[] data, int offset, int count, byte[] output, long now)
        {
            Peer? peer = null;
            if (State != TurnAllocationState.Ready || !TryGetPeer(path, true, now, out peer) || peer.Permission.State != PermissionState.Installed)
            {
                // A peer without its permission yet asks for one; the datagram is lost, as on any network, and resent.
                if (peer != null) peer.Permission.Wanted = true;
                Statistics.Held++;
                return 0;
            }

            peer.LastUsedAt = now;
            lastSentAt = now;
            if (peer.ChannelState == ChannelState.Bound)
            {
                Statistics.ChannelDataSent++;
                return ChannelData.Write(output, peer.Channel, data, offset, count);
            }

            if (peer.ChannelState == ChannelState.None && now >= peer.ChannelRetryAt) Bind(peer, now);
            dataWriter.Begin(output, StunMethod.Send, StunClass.Indication, stun.NewTransactionId());
            dataWriter.AddAddressBytes(StunAttribute.XorPeerAddress, peer.AddressBytes, peer.EndPoint.Port);
            dataWriter.AddBytes(StunAttribute.Data, data, offset, count);
            Statistics.SendIndicationsSent++;
            return dataWriter.Finish(null, false);
        }

        /// <summary>Unwraps ChannelData from the server: the peer's relayed path and where its data lies in the buffer.</summary>
        internal bool TryUnwrap(byte[] buffer, int offset, int count, out TransportPath path, out int dataOffset, out int dataLength)
        {
            path = null!;
            dataOffset = 0;
            dataLength = 0;
            if (State != TurnAllocationState.Ready || !ChannelData.TryRead(buffer, offset, count, out var channel, out dataOffset, out dataLength)) return false;
            var peer = peersByChannel[channel - ChannelData.FirstChannel];
            if (peer == null || (peer.ChannelState != ChannelState.Bound && peer.ChannelState != ChannelState.Binding)) return false;
            Statistics.ChannelDataReceived++;
            path = peer.Path;
            return true;
        }

        /// <summary>
        /// Reads a Data indication from the server: a peer's datagram that arrived before its channel was bound. The peer
        /// must hold a permission; its channel is bound now, so what follows arrives as ChannelData.
        /// </summary>
        internal bool TryReadData(StunMessage message, long now, out TransportPath path, out int dataOffset, out int dataLength)
        {
            path = null!;
            dataOffset = 0;
            dataLength = 0;
            if (State != TurnAllocationState.Ready || message.Method != StunMethod.Data || message.Class != StunClass.Indication) return false;
            if (!message.TryGetAddress(StunAttribute.XorPeerAddress, out var from) || !message.TryGetValue(StunAttribute.Data, out dataOffset, out dataLength)) return false;
            from = new IPEndPoint(NatAddresses.Unmap(from.Address), from.Port);
            if (!IsPermitted(from.Address) || !TryGetPeer(TurnRelayPath.For(Allocation, from), true, now, out var peer)) return false;
            Statistics.DataIndicationsReceived++;
            if (peer.ChannelState == ChannelState.None && now >= peer.ChannelRetryAt) Bind(peer, now);
            path = peer.Path;
            return true;
        }

        private Permission? PermissionFor(IPAddress address, bool create)
        {
            if (permissions.TryGetValue(address, out var permission)) return permission;
            if (!create || permissions.Count >= MaximumPermissions) return null;
            permission = new Permission(address);
            permissions.Add(address, permission);
            return permission;
        }

        private bool TryGetPeer(TransportPath path, bool create, long now, out Peer peer)
        {
            if (peersByPath.TryGetValue(path, out peer!)) return true;
            if (!create || peersByPath.Count >= MaximumPeers || !TurnRelayPath.TryParse(path, out var allocation, out var endpoint) || allocation != Allocation) return false;
            var permission = PermissionFor(NatAddresses.Unmap(endpoint.Address), true);
            if (permission == null) return false;
            peer = new Peer(endpoint, path, permission) { LastUsedAt = now };
            peersByPath.Add(path, peer);
            return true;
        }

        private void Bind(Peer peer, long now)
        {
            if (peer.Channel == 0)
            {
                if (nextChannel > ChannelData.LastChannel) return;
                peer.Channel = (ushort)nextChannel++;
                peersByChannel[peer.Channel - ChannelData.FirstChannel] = peer;
                ChannelCount++;
            }

            peer.ChannelState = peer.ChannelState == ChannelState.Bound ? ChannelState.Bound : ChannelState.Binding;
            peer.ChannelTransaction = Send(new Request(RequestKind.Channel, true, 0, null, peer), now);
            Statistics.ChannelBinds++;
            if (peer.ChannelTransaction == null) OnChannelFailed(new Request(RequestKind.Channel, true, 0, null, peer), true, now);
        }

        private void OnBound(Request request, long now)
        {
            var peer = request.Peer!;
            peer.ChannelTransaction = null;
            peer.ChannelState = ChannelState.Bound;
            peer.ChannelRefreshAt = now + ChannelRefreshMicros;

            // A binding refreshes its peer's permission too (RFC 8656, section 11.2).
            if (peer.Permission.State == PermissionState.Installed) peer.Permission.ExpiresAt = now + PermissionLifetimeMicros;
        }

        private void OnChannelFailed(Request request, bool transient, long now)
        {
            var peer = request.Peer!;
            peer.ChannelTransaction = null;
            if (peer.ChannelState == ChannelState.Bound && transient)
            {
                // A refresh that went unanswered leaves the binding to lapse; data falls back to Send indications.
                peer.ChannelState = ChannelState.Failed;
                return;
            }

            // A refused binding stays refused for this allocation; a lost one is tried again later.
            peer.ChannelState = transient ? ChannelState.None : ChannelState.Failed;
            peer.ChannelRetryAt = now + RetryMicros;
        }

        private void PollChannels(long now)
        {
            dueChannels.Clear();
            foreach (var peer in peersByPath.Values)
            {
                if (peer.ChannelState == ChannelState.Bound && peer.ChannelTransaction == null && now >= peer.ChannelRefreshAt) dueChannels.Add(peer);
            }

            foreach (var peer in dueChannels)
            {
                peer.ChannelRefreshAt = now + ChannelRefreshMicros;
                Bind(peer, now);
            }

            dueChannels.Clear();
        }

        private void PollPermissions(long now)
        {
            duePermissions.Clear();
            var solo = false;
            foreach (var permission in permissions.Values)
            {
                // A permission whose refreshes kept failing lapses on the server too, so it stops counting here.
                if (permission.State == PermissionState.Installed && now >= permission.ExpiresAt) permission.State = PermissionState.Wanted;
                if (!permission.Wanted || permission.InFlight || permission.State == PermissionState.Refused) continue;
                if (now < (permission.State == PermissionState.Installed ? permission.RefreshAt : permission.RetryAt)) continue;

                // A batch refused once is retried one address per request, so one forbidden peer cannot block the rest.
                if (permission.Solo)
                {
                    if (duePermissions.Count == 0)
                    {
                        duePermissions.Add(permission);
                        solo = true;
                    }

                    continue;
                }

                if (!solo && duePermissions.Count < PermissionsPerRequest) duePermissions.Add(permission);
            }

            if (duePermissions.Count == 0) return;
            var batch = new List<Permission>(duePermissions);
            duePermissions.Clear();
            foreach (var permission in batch) permission.InFlight = true;
            Statistics.PermissionRequests++;
            var request = new Request(RequestKind.Permission, true, 0, batch);
            if (Send(request, now) == null) OnPermissionFailed(request, false, now);
        }

        private void OnPermitted(Request request, long now)
        {
            foreach (var permission in request.Permissions!)
            {
                permission.InFlight = false;
                permission.State = PermissionState.Installed;
                permission.ExpiresAt = now + PermissionLifetimeMicros;
                permission.RefreshAt = now + PermissionRefreshMicros;
            }
        }

        private void OnPermissionFailed(Request request, bool forbidden, long now)
        {
            var batch = request.Permissions!;
            foreach (var permission in batch)
            {
                permission.InFlight = false;
                if (forbidden && batch.Count == 1)
                {
                    permission.State = PermissionState.Refused;
                }
                else if (forbidden)
                {
                    permission.Solo = true;
                    permission.RetryAt = now;
                    permission.RefreshAt = Math.Min(permission.RefreshAt, now);
                }
                else if (permission.State == PermissionState.Installed && now < permission.ExpiresAt)
                {
                    // Still installed on the server: try the refresh again before it lapses.
                    permission.RefreshAt = now + RetryMicros;
                }
                else
                {
                    permission.State = PermissionState.Wanted;
                    permission.RetryAt = now + RetryMicros;
                }
            }
        }

        private long NextPeerTimeout()
        {
            var next = long.MaxValue;
            foreach (var permission in permissions.Values)
            {
                if (!permission.Wanted || permission.InFlight || permission.State == PermissionState.Refused) continue;
                next = Math.Min(next, permission.State == PermissionState.Installed ? Math.Min(permission.RefreshAt, permission.ExpiresAt) : permission.RetryAt);
            }

            foreach (var peer in peersByPath.Values)
            {
                if (peer.ChannelState == ChannelState.Bound && peer.ChannelTransaction == null) next = Math.Min(next, peer.ChannelRefreshAt);
            }

            return next;
        }

        private void ClearPeers()
        {
            peersByPath.Clear();
            permissions.Clear();
            Array.Clear(peersByChannel, 0, peersByChannel.Length);
        }

        private enum PermissionState
        {
            Wanted,
            Installed,
            Refused
        }

        private enum ChannelState
        {
            None,
            Binding,
            Bound,
            Failed
        }

        /// <summary>One permitted address. It stays usable while a refresh is in flight, until the server's five minutes run out.</summary>
        private sealed class Permission
        {
            internal Permission(IPAddress address)
            {
                AddressBytes = address.GetAddressBytes();
            }

            internal byte[] AddressBytes { get; }

            internal bool Wanted { get; set; }

            /// <summary>Gets or sets whether a refused batch held this address, so it is next tried on its own.</summary>
            internal bool Solo { get; set; }

            internal PermissionState State { get; set; }

            internal bool InFlight { get; set; }

            internal long RefreshAt { get; set; }

            internal long RetryAt { get; set; }

            internal long ExpiresAt { get; set; }
        }

        private sealed class Peer
        {
            internal Peer(IPEndPoint endpoint, TransportPath path, Permission permission)
            {
                EndPoint = endpoint;
                Path = path;
                Permission = permission;
                AddressBytes = endpoint.Address.GetAddressBytes();
            }

            internal IPEndPoint EndPoint { get; }

            internal TransportPath Path { get; }

            internal Permission Permission { get; }

            internal byte[] AddressBytes { get; }

            internal ushort Channel { get; set; }

            internal ChannelState ChannelState { get; set; }

            internal long ChannelRefreshAt { get; set; }

            internal long ChannelRetryAt { get; set; }

            internal StunTransaction? ChannelTransaction { get; set; }

            internal long LastUsedAt { get; set; }
        }
    }
}
