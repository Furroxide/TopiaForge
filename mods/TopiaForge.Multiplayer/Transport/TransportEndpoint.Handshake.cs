using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Transport.Handshake;
using TopiaForge.Multiplayer.Transport.Packets;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>
    /// Handshake intake and routing. An Initiation passes, in order: parsing, the duplicate check, the cookie demand
    /// under load, capacity, and the rate limits. Only then does the endpoint spend Diffie-Hellman work on it.
    /// </summary>
    internal sealed partial class TransportEndpoint
    {
        private readonly Dictionary<InitiationKey, TransportConnection> initiations = new Dictionary<InitiationKey, TransportConnection>();
        private readonly CookieGuard cookies;
        private readonly HandshakeLimiter limiter;

        private void ReceiveInitiation(TransportPath path, byte[] datagram, int offset, int count, long now)
        {
            if (!Options.AcceptsInbound)
            {
                Statistics.InitiationsRefused++;
                return;
            }

            if (!HandshakePackets.TryReadInitiation(datagram, offset, count, out var version, out var initiatorId, out var cookie, out var message1) ||
                version != HandshakePackets.Version)
            {
                Statistics.DatagramsDropped++;
                return;
            }

            var underLoad = limiter.MeterInitiation(now);
            if (initiations.TryGetValue(new InitiationKey(path, initiatorId), out var existing))
            {
                existing.OnDuplicateInitiation(message1);
                return;
            }

            if (Options.AlwaysRequireCookie || underLoad || halfOpen >= Options.CookieHalfOpenThreshold)
            {
                if (cookie == null)
                {
                    SendRetry(path, initiatorId, now);
                    return;
                }

                if (!cookies.Verify(cookie, path, initiatorId, now))
                {
                    Statistics.InitiationsRefused++;
                    return;
                }
            }

            if (halfOpen >= Options.MaximumHalfOpen || ConnectionCount >= Options.MaximumConnections || !limiter.TryAdmit(path, now))
            {
                Statistics.InitiationsRefused++;
                return;
            }

            var connection = new TransportConnection(this, path, false, NewConnectionId(), ++connectionCounter, now);
            if (!connection.Respond(initiatorId, message1))
            {
                Statistics.InitiationsRefused++;
                return;
            }

            Register(connection);
        }

        private void SendRetry(TransportPath path, ulong initiatorId, long now)
        {
            var buffer = pool.Rent();
            HandshakeWriter.Reset(buffer, 0, HandshakePackets.RetrySize);
            HandshakePackets.WriteRetry(HandshakeWriter, initiatorId, cookies.Issue(path, initiatorId, now));
            Enqueue(path, buffer, HandshakePackets.RetrySize);
            Statistics.RetriesSent++;
        }

        private void ReceiveRetry(TransportPath path, byte[] datagram, int offset, int count, long now)
        {
            if (!HandshakePackets.TryReadRetry(datagram, offset, count, out var initiatorId, out var cookie) ||
                !byConnectionId.TryGetValue(initiatorId, out var connection) ||
                !connection.ReceiveRetry(path, cookie, now))
            {
                Statistics.DatagramsDropped++;
            }
        }

        private void ReceiveAddressed(TransportPath path, byte type, byte[] datagram, int offset, int count, long now)
        {
            if (!HandshakePackets.TryReadAddressed(datagram, offset, count, type, out var receiverId, out var message) ||
                !byConnectionId.TryGetValue(receiverId, out var connection))
            {
                Statistics.DatagramsDropped++;
                return;
            }

            var accepted = type == PacketType.Response
                ? connection.ReceiveResponse(path, message, now)
                : connection.ReceiveCompletion(path, message, now);
            if (!accepted) Statistics.DatagramsDropped++;
            UnregisterIfClosed(connection);
        }

        private void ReceiveProtected(TransportPath path, byte[] datagram, int offset, int count, long now)
        {
            if (count < ProtectedPacket.MinimumSize ||
                !byConnectionId.TryGetValue(ProtectedPacket.ReadConnectionId(datagram, offset), out var connection) ||
                !connection.ReceiveProtected(path, datagram, offset, count, now))
            {
                Statistics.DatagramsDropped++;
                return;
            }

            UnregisterIfClosed(connection);
        }

        /// <summary>An initiator as the responder knows it: the path it came from and the connection id it chose.</summary>
        private readonly struct InitiationKey : IEquatable<InitiationKey>
        {
            internal InitiationKey(TransportPath path, ulong connectionId)
            {
                Path = path;
                ConnectionId = connectionId;
            }

            internal TransportPath Path { get; }

            internal ulong ConnectionId { get; }

            public bool Equals(InitiationKey other) => ConnectionId == other.ConnectionId && Path.Equals(other.Path);

            public override bool Equals(object? obj) => obj is InitiationKey other && Equals(other);

            public override int GetHashCode() => unchecked((Path.GetHashCode() * 397) ^ ConnectionId.GetHashCode());
        }
    }
}
