using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Transport.Handshake;
using TopiaForge.Multiplayer.Transport.Packets;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>One info reply: who answered, its protocol version, its info, and the round trip it took.</summary>
    internal sealed class TransportInfoReply
    {
        internal TransportInfoReply(TransportPath path, byte version, byte[] info, long roundTripMicros)
        {
            Path = path;
            Version = version;
            Info = info;
            RoundTripMicros = roundTripMicros;
        }

        internal TransportPath Path { get; }

        internal byte Version { get; }

        /// <summary>Gets the responder's info: empty when its protocol version differs from this side's.</summary>
        internal byte[] Info { get; }

        internal long RoundTripMicros { get; }

        internal bool IsCompatible => Version == HandshakePackets.Version;
    }

    /// <summary>
    /// The info query. Before joining, a browser asks a host for its session info without a handshake. A host answers
    /// only while it has info set, within per-source and endpoint budgets like the handshake's. A reply is matched to
    /// its query by a random id and the path the query went to, so nobody else can answer for a host.
    /// </summary>
    internal sealed partial class TransportEndpoint
    {
        private const int MaximumOutstandingQueries = 64;
        private const int MaximumQueuedReplies = 64;
        private const long QueryLifetimeMicros = 5_000_000;

        private readonly Dictionary<ulong, OutstandingQuery> outstandingQueries = new Dictionary<ulong, OutstandingQuery>();
        private readonly Queue<TransportInfoReply> infoReplies = new Queue<TransportInfoReply>();
        private readonly HandshakeLimiter queryLimiter;
        private readonly byte[] queryIdBytes = new byte[InfoPackets.IdSize];
        private byte[]? sessionInfo;

        /// <summary>
        /// Sets the info this endpoint gives browsers, or clears it to stop answering. It travels unauthenticated to
        /// anyone who asks, so it must never name an address or anything private.
        /// </summary>
        internal void SetInfo(byte[]? info)
        {
            ThrowIfDisposed();
            if (info != null && info.Length > InfoPackets.MaximumInfoBytes)
            {
                throw new ArgumentException("Session info must fit one reply: " + InfoPackets.MaximumInfoBytes + " bytes.", nameof(info));
            }

            sessionInfo = info == null ? null : (byte[])info.Clone();
        }

        /// <summary>Asks whoever is at <paramref name="path"/> for its info; the answer comes out of <see cref="TryReceiveInfo"/>.</summary>
        internal void QueryInfo(TransportPath path, long now)
        {
            ThrowIfDisposed();
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (!Permits(path)) throw new ArgumentException("A relay-only endpoint never queries a direct path.", nameof(path));
            ExpireQueries(now);
            if (outstandingQueries.Count >= MaximumOutstandingQueries) ForgetOldestQuery();
            var id = NewQueryId();
            outstandingQueries[id] = new OutstandingQuery(path, now);
            var buffer = pool.Rent();
            HandshakeWriter.Reset(buffer, 0, InfoPackets.QuerySize);
            InfoPackets.WriteQuery(HandshakeWriter, id);
            Enqueue(path, buffer, InfoPackets.QuerySize);
        }

        /// <summary>Yields the next info reply that matched a query.</summary>
        internal bool TryReceiveInfo(out TransportInfoReply reply)
        {
            if (infoReplies.Count > 0)
            {
                reply = infoReplies.Dequeue();
                return true;
            }

            reply = null!;
            return false;
        }

        private void ReceiveInfoQuery(TransportPath path, byte[] datagram, int offset, int count, long now)
        {
            var info = sessionInfo;
            if (info == null || !InfoPackets.TryReadQuery(datagram, offset, count, out var version, out var id) || !queryLimiter.TryAdmit(path, now))
            {
                Statistics.DatagramsDropped++;
                return;
            }

            // A browser that speaks another version still learns ours, so it can say why it cannot join.
            var buffer = pool.Rent();
            HandshakeWriter.Reset(buffer, 0, buffer.Length);
            InfoPackets.WriteReply(HandshakeWriter, id, version == HandshakePackets.Version ? info : Array.Empty<byte>());
            Enqueue(path, buffer, HandshakeWriter.Written);
            Statistics.InfoRepliesSent++;
        }

        private void ReceiveInfoReply(TransportPath path, byte[] datagram, int offset, int count, long now)
        {
            // A query's lifetime holds however rarely the endpoint is polled: a late reply matches nothing.
            ExpireQueries(now);
            if (!InfoPackets.TryReadReply(datagram, offset, count, out var version, out var id, out var info) ||
                !outstandingQueries.TryGetValue(id, out var query) || !query.Path.Equals(path))
            {
                Statistics.DatagramsDropped++;
                return;
            }

            outstandingQueries.Remove(id);
            if (infoReplies.Count >= MaximumQueuedReplies) infoReplies.Dequeue();
            infoReplies.Enqueue(new TransportInfoReply(path, version, info, now - query.SentAt));
        }

        /// <summary>Gets how many queries await a reply.</summary>
        internal int OutstandingQueryCount => outstandingQueries.Count;

        /// <summary>Gets when the oldest outstanding query expires, or <see cref="long.MaxValue"/> when none is outstanding.</summary>
        private long NextQueryExpiry()
        {
            var deadline = long.MaxValue;
            foreach (var entry in outstandingQueries) deadline = Math.Min(deadline, entry.Value.SentAt + QueryLifetimeMicros);
            return deadline;
        }

        private void ExpireQueries(long now)
        {
            if (outstandingQueries.Count == 0) return;
            List<ulong>? expired = null;
            foreach (var entry in outstandingQueries)
            {
                if (now - entry.Value.SentAt >= QueryLifetimeMicros) (expired ??= new List<ulong>()).Add(entry.Key);
            }

            if (expired == null) return;
            foreach (var id in expired) outstandingQueries.Remove(id);
        }

        private void ForgetOldestQuery()
        {
            var oldest = 0UL;
            var oldestAt = long.MaxValue;
            foreach (var entry in outstandingQueries)
            {
                if (entry.Value.SentAt >= oldestAt) continue;
                oldest = entry.Key;
                oldestAt = entry.Value.SentAt;
            }

            outstandingQueries.Remove(oldest);
        }

        private ulong NewQueryId()
        {
            for (var attempt = 0; attempt < ConnectionIdAttempts; attempt++)
            {
                Options.Random.Fill(queryIdBytes);
                var id = PacketWriter.ReadUInt64(queryIdBytes, 0);
                if (!outstandingQueries.ContainsKey(id)) return id;
            }

            throw new InvalidOperationException("The random source keeps repeating query ids.");
        }

        private readonly struct OutstandingQuery
        {
            internal OutstandingQuery(TransportPath path, long sentAt)
            {
                Path = path;
                SentAt = sentAt;
            }

            internal TransportPath Path { get; }

            internal long SentAt { get; }
        }
    }
}
