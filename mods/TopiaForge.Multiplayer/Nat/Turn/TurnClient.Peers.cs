using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
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
    /// risk loss: until then it goes as Send indications. A refresh that goes unanswered is tried again with the same
    /// number while the binding lasts, and ChannelData is accepted for as long as the server can hold the binding: ten
    /// minutes after the last bind request for it, answered or not.
    /// </summary>
    internal sealed partial class TurnClient
    {
        internal const int MaximumPeers = 256;
        internal const int MaximumPermissions = 256;
        internal const int PermissionsPerRequest = 8;
        internal const long ChannelLifetimeMicros = 600_000_000;

        /// <summary>The bytes one XOR-PEER-ADDRESS takes in a request, for an IPv6 peer: the larger family bounds the batch.</summary>
        private const int PeerAddressBytes = 24;

        private readonly Dictionary<TransportPath, Peer> peersByPath = new Dictionary<TransportPath, Peer>();
        private readonly Dictionary<PeerKey, Peer> peersByKey = new Dictionary<PeerKey, Peer>();
        private readonly Peer?[] peersByChannel;
        private readonly Dictionary<IPAddress, Permission> permissions = new Dictionary<IPAddress, Permission>();
        private readonly List<Permission> duePermissions = new List<Permission>();
        private readonly List<Peer> dueChannels = new List<Peer>();
        private readonly StunMessageWriter dataWriter = new StunMessageWriter();
        private readonly byte[] peerScratch = new byte[16];
        private int nextChannel = ChannelData.FirstChannel;

        /// <summary>Gets how many channels are bound or binding.</summary>
        internal int ChannelCount { get; private set; }

        /// <summary>
        /// Asks for a permission for <paramref name="address"/> and keeps it fresh until <see cref="Forget"/>.
        /// <paramref name="scope"/> names the remote peer whose candidate asked for it, so no one peer fills the table.
        /// </summary>
        internal void Permit(IPAddress address, string? scope, long now)
        {
            if (address == null) throw new ArgumentNullException(nameof(address));
            var permission = PermissionFor(NatAddresses.Unmap(address), scope, true, now);
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

        /// <summary>Gets whether data to or from <paramref name="peer"/> travels as ChannelData now, for tests.</summary>
        internal bool UsesChannel(IPEndPoint peer, long now) => peersByPath.TryGetValue(TurnRelayPath.For(Allocation, peer), out var known) && known.HasChannel(now);

        /// <summary>Gets the relayed path that reaches <paramref name="peer"/> through this allocation.</summary>
        internal TransportPath PathTo(IPEndPoint peer)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var path = TurnRelayPath.For(Allocation, peer);
            return peersByPath.TryGetValue(path, out var known) ? known.Path : path;
        }

        /// <summary>
        /// Wraps a datagram for a peer reached through this allocation into <paramref name="output"/>: ChannelData while
        /// the channel is bound, a Send indication otherwise. Returns its length, or 0 when it cannot go yet because the
        /// allocation is not ready or the peer's permission is not installed. A known peer's datagram allocates nothing.
        /// </summary>
        internal int Wrap(TransportPath path, byte[] data, int offset, int count, byte[] output, long now)
        {
            Peer? peer = null;
            if (State != TurnAllocationState.Ready || !TryGetPeer(path, true, true, now, out peer) || peer.Permission.State != PermissionState.Installed)
            {
                // A peer without its permission yet asks for one; the datagram is lost, as on any network, and resent.
                if (peer != null) peer.Permission.Wanted = true;
                Statistics.Held++;
                return 0;
            }

            peer.LastUsedAt = now;
            lastSentAt = now;
            if (peer.HasChannel(now))
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

        /// <summary>
        /// Unwraps ChannelData from the server: the peer's relayed path and where its data lies in the buffer. A channel
        /// counts while the server can still hold its binding, so a refresh whose answers were lost silences nothing.
        /// </summary>
        internal bool TryUnwrap(byte[] buffer, int offset, int count, long now, out TransportPath path, out int dataOffset, out int dataLength)
        {
            path = null!;
            dataOffset = 0;
            dataLength = 0;
            if (State != TurnAllocationState.Ready || !ChannelData.TryRead(buffer, offset, count, out var channel, out dataOffset, out dataLength)) return false;
            var peer = peersByChannel[channel - ChannelData.FirstChannel];
            if (peer == null || !peer.AcceptsChannelData(now)) return false;
            Statistics.ChannelDataReceived++;
            peer.LastUsedAt = now;
            path = peer.Path;
            return true;
        }

        /// <summary>
        /// Reads a Data indication from the server where it lies: a peer's datagram that arrived without a channel. The
        /// peer must hold a permission. A peer heard before is found by its address bytes, so this allocates nothing; a
        /// new one is bound now, so what follows arrives as ChannelData once the server agrees.
        /// </summary>
        internal bool TryReadData(byte[] buffer, int offset, int count, long now, out TransportPath path, out int dataOffset, out int dataLength)
        {
            path = null!;
            if (!StunMessage.TryReadDataIndication(buffer, offset, count, peerScratch, out var size, out var port, out dataOffset, out dataLength) ||
                State != TurnAllocationState.Ready)
            {
                return false;
            }

            if (peersByKey.TryGetValue(PeerKey.From(peerScratch, size, port), out var peer))
            {
                if (peer.Permission.State != PermissionState.Installed) return false;
            }
            else
            {
                var address = new byte[size];
                Buffer.BlockCopy(peerScratch, 0, address, 0, size);
                var from = new IPEndPoint(NatAddresses.Unmap(new IPAddress(address)), port);
                if (!IsPermitted(from.Address) || !TryGetPeer(TurnRelayPath.For(Allocation, from), true, false, now, out peer)) return false;
            }

            Statistics.DataIndicationsReceived++;
            peer.LastUsedAt = now;
            if (peer.ChannelState == ChannelState.None && now >= peer.ChannelRetryAt) Bind(peer, now);
            path = peer.Path;
            return true;
        }

        private Permission? PermissionFor(IPAddress address, string? scope, bool create, long now)
        {
            if (permissions.TryGetValue(address, out var permission)) return permission;
            if (!create || !MakePermissionRoom(scope, now)) return null;
            permission = new Permission(address) { Scope = scope, Order = permissionOrder++ };
            permissions.Add(address, permission);
            return permission;
        }

        private bool TryGetPeer(TransportPath path, bool create, bool solicited, long now, out Peer peer)
        {
            if (peersByPath.TryGetValue(path, out peer!))
            {
                // A path this side now sends to is solicited for good, so inbound data on it no longer counts as a spray.
                if (solicited) peer.Solicited = true;
                return true;
            }

            if (!create || !TurnRelayPath.TryParse(path, out var allocation, out var endpoint) || allocation != Allocation) return false;
            if (endpoint.Port == 0 || !MakePeerRoom(endpoint, solicited, now)) return false;

            // A peer-path's address was permitted before any datagram reached it, so this finds the existing permission;
            // its scope lives on the permission, so the per-peer cap is enforced there, not again here.
            var permission = PermissionFor(NatAddresses.Unmap(endpoint.Address), null, true, now);
            if (permission == null) return false;
            peer = new Peer(endpoint, path, permission) { LastUsedAt = now, Solicited = solicited };
            peersByPath.Add(path, peer);
            peersByKey[PeerKey.From(endpoint)] = peer;
            return true;
        }

        private void Bind(Peer peer, long now)
        {
            if (peer.Channel == 0)
            {
                if (!TryAcquireChannel(now, out var number)) return;
                peer.Channel = number;
                peersByChannel[peer.Channel - ChannelData.FirstChannel] = peer;
                ChannelCount++;
            }

            if (peer.ChannelState != ChannelState.Bound) peer.ChannelState = ChannelState.Binding;
            peer.BindRequestedAt = now;
            peer.ChannelTransaction = Send(new Request(RequestKind.Channel, true, 0, null, peer), now);
            Statistics.ChannelBinds++;
            if (peer.ChannelTransaction == null) OnChannelFailed(new Request(RequestKind.Channel, true, 0, null, peer), true, now);
        }

        private void OnBound(Request request, long now)
        {
            var peer = request.Peer!;
            peer.ChannelTransaction = null;
            peer.ChannelState = ChannelState.Bound;
            peer.BoundUntil = now + ChannelLifetimeMicros;
            peer.ChannelRefreshAt = now + ChannelRefreshMicros;

            // A binding refreshes its peer's permission too (RFC 8656, section 11.2).
            if (peer.Permission.State == PermissionState.Installed) peer.Permission.ExpiresAt = now + PermissionLifetimeMicros;
        }

        private void OnChannelFailed(Request request, bool transient, long now)
        {
            var peer = request.Peer!;
            peer.ChannelTransaction = null;
            if (peer.ChannelState == ChannelState.Bound && now < peer.BoundUntil)
            {
                // A refresh failed while the binding still holds on the server. An unanswered one is tried again soon with
                // the same number; a refused one is not, and data falls back to Send indications once the binding lapses.
                if (transient) peer.ChannelRefreshAt = now + RetryMicros;
                else peer.ChannelState = ChannelState.Refused;
                return;
            }

            // A refused binding stays refused for this allocation; a lost one is tried again later, with the same number.
            peer.ChannelState = transient ? ChannelState.None : ChannelState.Refused;
            peer.ChannelRetryAt = now + RetryMicros;
        }

        private void PollChannels(long now)
        {
            dueChannels.Clear();
            foreach (var peer in peersByPath.Values)
            {
                if (peer.ChannelState != ChannelState.Bound || peer.ChannelTransaction != null) continue;
                if (now >= peer.BoundUntil)
                {
                    // Every refresh failed and the binding lapsed: the next datagram binds the same number afresh.
                    peer.ChannelState = ChannelState.None;
                    peer.ChannelRetryAt = now;
                }
                else if (now >= peer.ChannelRefreshAt)
                {
                    dueChannels.Add(peer);
                }
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
            var batch = PermissionBatch();
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

                if (!solo && duePermissions.Count < batch) duePermissions.Add(permission);
            }

            if (duePermissions.Count == 0) return;
            var request = new Request(RequestKind.Permission, true, 0, new List<Permission>(duePermissions));
            duePermissions.Clear();
            foreach (var permission in request.Permissions!) permission.InFlight = true;
            Statistics.PermissionRequests++;
            if (Send(request, now) == null) OnPermissionFailed(request, false, now);
        }

        /// <summary>
        /// How many peer addresses one CreatePermission carries: up to eight, as many as fit beside the credential, the
        /// realm and the nonce the server chose, so a long nonce shrinks the batch rather than overflowing the request.
        /// </summary>
        private int PermissionBatch()
        {
            var fixedBytes = StunMessage.HeaderSize + TextBytes(username) + TextBytes(realm) + TextBytes(nonce) + 4 + StunMessage.IntegritySize + 8;
            var room = (StunMessage.MaximumBytes - fixedBytes) / PeerAddressBytes;
            return Math.Max(1, Math.Min(PermissionsPerRequest, room));
        }

        private static int TextBytes(string? text) => text == null ? 0 : 4 + ((Encoding.UTF8.GetByteCount(text) + 3) & ~3);

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
                if (peer.ChannelState == ChannelState.Bound && peer.ChannelTransaction == null) next = Math.Min(next, Math.Min(peer.ChannelRefreshAt, peer.BoundUntil));
            }

            return next;
        }

        private void ClearPeers()
        {
            peersByPath.Clear();
            peersByKey.Clear();
            permissions.Clear();
            Array.Clear(peersByChannel, 0, peersByChannel.Length);
            Array.Clear(channelFreeAt, 0, channelFreeAt.Length);
            nextChannel = ChannelData.FirstChannel;
            ChannelCount = 0;
            permissionOrder = 0;
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
            Refused
        }

        /// <summary>One permitted address. It stays usable while a refresh is in flight, until the server's five minutes run out.</summary>
        private sealed class Permission
        {
            internal Permission(IPAddress address)
            {
                Address = address;
                AddressBytes = address.GetAddressBytes();
            }

            /// <summary>Gets the permitted address, the key this permission is held under.</summary>
            internal IPAddress Address { get; }

            internal byte[] AddressBytes { get; }

            /// <summary>Gets or sets the remote peer whose candidate caused this permission; null for the local side.</summary>
            internal string? Scope { get; set; }

            /// <summary>Gets or sets when this permission was installed, in this allocation's order, so the oldest can go first.</summary>
            internal long Order { get; set; }

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

            /// <summary>Gets or sets whether this side ever sent a check or datagram to this port, so inbound data on it is not a spray.</summary>
            internal bool Solicited { get; set; }

            internal ushort Channel { get; set; }

            internal ChannelState ChannelState { get; set; }

            /// <summary>Gets or sets when the binding the server last confirmed lapses: ten minutes after its answer.</summary>
            internal long BoundUntil { get; set; }

            /// <summary>Gets or sets when the last bind or refresh for the channel went out, answered or not.</summary>
            internal long BindRequestedAt { get; set; } = long.MinValue / 2;

            internal long ChannelRefreshAt { get; set; }

            internal long ChannelRetryAt { get; set; }

            internal StunTransaction? ChannelTransaction { get; set; }

            internal long LastUsedAt { get; set; }

            /// <summary>Gets whether data goes out as ChannelData: the server confirmed the binding and it has not lapsed.</summary>
            internal bool HasChannel(long now) => (ChannelState == ChannelState.Bound || ChannelState == ChannelState.Refused) && now < BoundUntil;

            /// <summary>
            /// Gets whether ChannelData on this peer's number is taken: the server may hold a binding for as long as ten
            /// minutes after it last confirmed one or was last asked to, whether or not the answer came back.
            /// </summary>
            internal bool AcceptsChannelData(long now) =>
                Channel != 0 && (now < BoundUntil || now < BindRequestedAt + ChannelLifetimeMicros);
        }

        /// <summary>A peer's address in its 16-byte IPv6 form, and its port: how a Data indication finds its peer without allocating.</summary>
        private readonly struct PeerKey : IEquatable<PeerKey>
        {
            private readonly ulong high;
            private readonly ulong low;
            private readonly int port;

            private PeerKey(ulong high, ulong low, int port)
            {
                this.high = high;
                this.low = low;
                this.port = port;
            }

            /// <summary>Builds a key from 4 or 16 address bytes; IPv4 takes its mapped form, so both spellings agree.</summary>
            internal static PeerKey From(byte[] address, int size, int port)
            {
                if (size == 4) return new PeerKey(0, 0x0000FFFF00000000UL | ((ulong)address[0] << 24) | ((ulong)address[1] << 16) | ((ulong)address[2] << 8) | address[3], port);
                ulong high = 0;
                ulong low = 0;
                for (var index = 0; index < 8; index++)
                {
                    high = (high << 8) | address[index];
                    low = (low << 8) | address[index + 8];
                }

                return new PeerKey(high, low, port);
            }

            internal static PeerKey From(IPEndPoint endpoint)
            {
                var bytes = NatAddresses.Unmap(endpoint.Address).GetAddressBytes();
                return From(bytes, bytes.Length, endpoint.Port);
            }

            public bool Equals(PeerKey other) => high == other.high && low == other.low && port == other.port;

            public override bool Equals(object? obj) => obj is PeerKey other && Equals(other);

            public override int GetHashCode() => unchecked((int)(high ^ (high >> 32) ^ (low * 16777619UL) ^ (low >> 32)) ^ port);

            public override string ToString() => "TURN peer";
        }
    }
}
