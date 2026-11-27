using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Transport.Crypto;
using TopiaForge.Multiplayer.Transport.Handshake;
using TopiaForge.Multiplayer.Transport.Packets;
using TopiaForge.Multiplayer.Transport.Shaping;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>Counters an endpoint keeps about traffic it turned away. None of them names a path.</summary>
    internal sealed class TransportEndpointStatistics
    {
        internal long DatagramsReceived { get; set; }

        /// <summary>Gets or sets the datagrams dropped as malformed, unknown, unauthenticated or out of place.</summary>
        internal long DatagramsDropped { get; set; }

        internal long RetriesSent { get; set; }

        /// <summary>Gets or sets the Initiations refused by rate limits, cookies, capacity or a malformed key.</summary>
        internal long InitiationsRefused { get; set; }

        /// <summary>Gets or sets the responder handshakes that timed out or failed to authenticate.</summary>
        internal long HandshakesAbandoned { get; set; }

        /// <summary>Gets or sets the datagrams discarded because the caller stopped draining the outbound queue.</summary>
        internal long OutboundOverflow { get; set; }

        internal long InfoRepliesSent { get; set; }
    }

    /// <summary>
    /// One socket's worth of transport state, sans-I/O: the caller hands it datagrams and the time, polls it when
    /// <see cref="NextTimeoutMicros"/> passes or after any call, and sends what <see cref="TryDequeueDatagram"/> yields.
    /// Packets are routed by connection id, so peers behind one relay or address never collapse into one.
    /// </summary>
    internal sealed partial class TransportEndpoint : IDisposable
    {
        internal const int MaximumQueuedDatagrams = 1024;
        private const int ConnectionIdAttempts = 16;

        private readonly byte[] staticPrivateKey;
        private readonly byte[] staticPublicKey;
        private readonly List<TransportConnection> connections = new List<TransportConnection>();
        private readonly Dictionary<ulong, TransportConnection> byConnectionId = new Dictionary<ulong, TransportConnection>();
        private readonly Queue<TransportConnection> accepted = new Queue<TransportConnection>();
        private readonly Queue<OutgoingDatagram> outbound = new Queue<OutgoingDatagram>();
        private readonly DatagramPool pool = new DatagramPool();
        private readonly byte[] idBytes = new byte[HandshakePackets.ConnectionIdSize];
        private long connectionCounter;
        private int halfOpen;
        private int pollOffset;
        private bool disposed;

        /// <summary>Creates an endpoint for this install's X25519 static key.</summary>
        internal TransportEndpoint(byte[] staticPrivateKey, TransportOptions options, long now)
        {
            if (staticPrivateKey == null) throw new ArgumentNullException(nameof(staticPrivateKey));
            if (staticPrivateKey.Length != X25519.KeySize) throw new ArgumentException("A static key is 32 bytes.", nameof(staticPrivateKey));
            Options = options ?? throw new ArgumentNullException(nameof(options));
            options.Validate();
            this.staticPrivateKey = (byte[])staticPrivateKey.Clone();
            staticPublicKey = X25519.DerivePublicKey(this.staticPrivateKey);
            cookies = new CookieGuard(options.Random, now);
            limiter = new HandshakeLimiter(options.HandshakesPerSecondPerSource, options.HandshakesPerSecond);
            queryLimiter = new HandshakeLimiter(options.HandshakesPerSecondPerSource, options.HandshakesPerSecond);
            Uplink = new UplinkGovernor(options.UplinkBytesPerSecond, now);
            EphemeralSource = NewEphemeralKey;
        }

        internal TransportOptions Options { get; }

        internal byte[] StaticPublicKey => (byte[])staticPublicKey.Clone();

        internal TransportEndpointStatistics Statistics { get; } = new TransportEndpointStatistics();

        /// <summary>Gets the connections that are not half-open responder handshakes.</summary>
        internal int ConnectionCount => connections.Count - halfOpen;

        internal int HalfOpenCount => halfOpen;

        /// <summary>Gets the session bytes all connections hold for their readers.</summary>
        internal long ReassemblyBytes { get; private set; }

        /// <summary>Gets the outbound budget every connection shares, and the shedding it drives.</summary>
        internal UplinkGovernor Uplink { get; }

        /// <summary>
        /// Gets how far the endpoint is shedding load: 0 normal, 1 voice halved, 2 voice stopped, 3 the engine should
        /// also lower its delta rate.
        /// </summary>
        internal int SheddingLevel => Uplink.SheddingLevel;

        /// <summary>Gets or sets who receives datagrams the transport does not own: STUN and TURN ChannelData.</summary>
        internal Action<TransportPath, ArraySegment<byte>>? ForeignDatagramHandler { get; set; }

        // Scratch space shared by every connection: all calls run on one thread, and nothing holds them across calls.
        internal PacketWriter SendWriter { get; } = new PacketWriter();

        internal PacketWriter HandshakeWriter { get; } = new PacketWriter();

        internal byte[] SendPlaintext { get; } = new byte[ProtectedPacket.MaximumFramesBytes];

        internal byte[] SendHeader { get; } = new byte[ProtectedPacket.HeaderSize];

        internal byte[] ReceivePlaintext { get; } = new byte[ProtectedPacket.MaximumFramesBytes];

        internal byte[] ReceiveHeader { get; } = new byte[ProtectedPacket.HeaderSize];

        internal Func<byte[]> EphemeralSource { get; }

        /// <summary>Gets the static key for new handshakes, which copy it. Nothing else reads it.</summary>
        internal byte[] StaticPrivateKey => staticPrivateKey;

        /// <summary>Starts a connection to <paramref name="path"/>, optionally pinning the peer's static key.</summary>
        internal TransportConnection Connect(TransportPath path, long now, byte[]? expectedRemoteStaticKey = null)
        {
            ThrowIfDisposed();
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (expectedRemoteStaticKey != null && expectedRemoteStaticKey.Length != X25519.KeySize)
            {
                throw new ArgumentException("A static key is 32 bytes.", nameof(expectedRemoteStaticKey));
            }

            if (!Permits(path)) throw new ArgumentException("A relay-only endpoint never connects over a direct path.", nameof(path));
            if (ConnectionCount >= Options.MaximumConnections) throw new InvalidOperationException("The endpoint already holds its maximum number of connections.");
            var connection = new TransportConnection(this, path, true, NewConnectionId(), ++connectionCounter, now);
            Register(connection);
            connection.Initiate(expectedRemoteStaticKey);
            return connection;
        }

        /// <summary>Takes one inbound datagram. Nothing a peer sends makes this throw.</summary>
        internal void Receive(TransportPath path, byte[] datagram, int offset, int count, long now)
        {
            ThrowIfDisposed();
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (datagram == null) throw new ArgumentNullException(nameof(datagram));
            if (offset < 0 || count < 0 || offset > datagram.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            Statistics.DatagramsReceived++;
            if (count == 0 || count > TransportOptions.MaximumDatagramBytes)
            {
                Statistics.DatagramsDropped++;
                return;
            }

            var first = datagram[offset];
            if (PacketType.IsForeign(first))
            {
                var handler = ForeignDatagramHandler;
                if (handler == null) Statistics.DatagramsDropped++;
                else handler(path, new ArraySegment<byte>(datagram, offset, count));
                return;
            }

            // A relay-only endpoint ignores a direct path entirely, so nothing it does ever answers one.
            if (!Permits(path))
            {
                Statistics.DatagramsDropped++;
                return;
            }

            switch (first)
            {
                case PacketType.InfoQuery:
                    ReceiveInfoQuery(path, datagram, offset, count, now);
                    return;
                case PacketType.InfoReply:
                    ReceiveInfoReply(path, datagram, offset, count, now);
                    return;
                case PacketType.Initiation:
                    ReceiveInitiation(path, datagram, offset, count, now);
                    return;
                case PacketType.Retry:
                    ReceiveRetry(path, datagram, offset, count, now);
                    return;
                case PacketType.Response:
                case PacketType.Completion:
                    ReceiveAddressed(path, first, datagram, offset, count, now);
                    return;
                case PacketType.Protected:
                    ReceiveProtected(path, datagram, offset, count, now);
                    return;
                default:
                    Statistics.DatagramsDropped++;
                    return;
            }
        }

        /// <summary>
        /// Runs every connection's timers and sends what their pacers and the shared uplink allow, then forgets closed
        /// connections. The order rotates each poll, so no connection always meets a spent uplink last.
        /// </summary>
        internal void Poll(long now)
        {
            ThrowIfDisposed();
            Uplink.Poll(now);
            ExpireQueries(now);
            var count = connections.Count;
            if (count > 0)
            {
                pollOffset = (pollOffset + 1) % count;
                for (var step = 0; step < count; step++) connections[(pollOffset + step) % count].Poll(now);
            }

            for (var index = connections.Count - 1; index >= 0; index--)
            {
                if (connections[index].State == TransportConnectionState.Closed) Unregister(index);
            }
        }

        /// <summary>Gets whether the endpoint's privacy setting allows <paramref name="path"/>: relay-only refuses direct ones.</summary>
        internal bool Permits(TransportPath path) => !Options.RelayOnly || path.Kind != TransportPathKind.Direct;

        /// <summary>Gets when <see cref="Poll"/> must next run, or <see cref="long.MaxValue"/> when nothing is pending.</summary>
        internal long NextTimeoutMicros()
        {
            var deadline = NextQueryExpiry();
            foreach (var connection in connections) deadline = Math.Min(deadline, connection.NextTimeout());
            return deadline;
        }

        /// <summary>Yields the next inbound connection whose handshake completed.</summary>
        internal bool TryAccept(out TransportConnection connection)
        {
            while (accepted.Count > 0)
            {
                connection = accepted.Dequeue();
                if (connection.State != TransportConnectionState.Closed) return true;
            }

            connection = null!;
            return false;
        }

        /// <summary>Yields the next datagram to send. Hand its buffer back with <see cref="Release"/> once it is sent.</summary>
        internal bool TryDequeueDatagram(out OutgoingDatagram datagram)
        {
            if (outbound.Count > 0)
            {
                datagram = outbound.Dequeue();
                return true;
            }

            datagram = default;
            return false;
        }

        internal void Release(OutgoingDatagram datagram) => pool.Return(datagram.Buffer);

        /// <summary>Ends every connection without a word, which wipes their keys, then wipes the static key and cookie secrets.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var connection in connections)
            {
                connection.Abort();
                connection.IsRegistered = false;
            }

            connections.Clear();
            byConnectionId.Clear();
            initiations.Clear();
            accepted.Clear();
            outbound.Clear();
            outstandingQueries.Clear();
            infoReplies.Clear();
            halfOpen = 0;
            ReassemblyBytes = 0;
            Array.Clear(staticPrivateKey, 0, staticPrivateKey.Length);
            cookies.Wipe();
        }

        internal byte[] RentBuffer() => pool.Rent();

        /// <summary>Queues a datagram written into a pooled buffer. A full queue drops it, as a congested link would.</summary>
        internal void Enqueue(TransportPath path, byte[] buffer, int length)
        {
            if (outbound.Count >= MaximumQueuedDatagrams)
            {
                pool.Return(buffer);
                Statistics.OutboundOverflow++;
                return;
            }

            outbound.Enqueue(new OutgoingDatagram(path, buffer, length));
        }

        internal void SendCopy(TransportPath path, byte[] datagram)
        {
            var buffer = pool.Rent();
            Buffer.BlockCopy(datagram, 0, buffer, 0, datagram.Length);
            Enqueue(path, buffer, datagram.Length);
        }

        internal void Report(TransportDiagnosticKind kind, long connectionNumber, string message) =>
            Options.Diagnostics?.Report(kind, connectionNumber, message);

        /// <summary>
        /// Gets whether <paramref name="connection"/> may buffer <paramref name="bytes"/> more: while the endpoint stays
        /// under its cap, or while the connection holds less than an equal share of the cap. Peers that fill the cap
        /// therefore cannot stall the others, and the endpoint never holds more than twice the cap.
        /// </summary>
        internal bool CanBuffer(TransportConnection connection, long bytes) =>
            ReassemblyBytes + bytes <= Options.ProcessReassemblyBytes ||
            connection.BufferedBytes + bytes <= Options.ProcessReassemblyBytes / Math.Max(1, ConnectionCount);

        internal void OnReassemblyChanged(TransportConnection connection, long delta)
        {
            if (connection.IsRegistered) ReassemblyBytes += delta;
        }

        /// <summary>Counts a completed handshake and hands an inbound connection out, unless the endpoint is full.</summary>
        internal void OnEstablished(TransportConnection connection)
        {
            if (connection.CountsAsHalfOpen)
            {
                connection.CountsAsHalfOpen = false;
                halfOpen--;
            }

            if (connection.IsInitiator) return;
            if (ConnectionCount > Options.MaximumConnections)
            {
                connection.Refuse();
                return;
            }

            accepted.Enqueue(connection);
        }

        private void Register(TransportConnection connection)
        {
            connections.Add(connection);
            byConnectionId.Add(connection.LocalConnectionId, connection);
            connection.IsRegistered = true;
            if (connection.IsInitiator) return;
            connection.CountsAsHalfOpen = true;
            halfOpen++;

            // The connection keeps its key: it may move to another path before it closes, and the entry must go with it.
            var key = new InitiationKey(connection.Path, connection.RemoteConnectionId);
            connection.Initiation = key;
            initiations[key] = connection;
        }

        private void Unregister(int index)
        {
            var connection = connections[index];
            connections.RemoveAt(index);
            byConnectionId.Remove(connection.LocalConnectionId);
            if (connection.Initiation is InitiationKey key)
            {
                if (initiations.TryGetValue(key, out var existing) && ReferenceEquals(existing, connection)) initiations.Remove(key);
                connection.Initiation = null;
            }

            if (connection.CountsAsHalfOpen)
            {
                connection.CountsAsHalfOpen = false;
                halfOpen--;
            }

            ReassemblyBytes -= connection.BufferedBytes;
            connection.IsRegistered = false;
        }

        private void UnregisterIfClosed(TransportConnection connection)
        {
            if (connection.State != TransportConnectionState.Closed || !connection.IsRegistered) return;
            Unregister(connections.IndexOf(connection));
        }

        private ulong NewConnectionId()
        {
            for (var attempt = 0; attempt < ConnectionIdAttempts; attempt++)
            {
                Options.Random.Fill(idBytes);
                var id = PacketWriter.ReadUInt64(idBytes, 0);
                if (!byConnectionId.ContainsKey(id)) return id;
            }

            throw new InvalidOperationException("The random source keeps repeating connection ids.");
        }

        private byte[] NewEphemeralKey()
        {
            var key = new byte[X25519.KeySize];
            Options.Random.Fill(key);
            return key;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(TransportEndpoint));
        }
    }
}
