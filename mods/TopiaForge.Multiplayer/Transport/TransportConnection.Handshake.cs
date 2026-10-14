using System;
using TopiaForge.Multiplayer.Transport.Crypto;
using TopiaForge.Multiplayer.Transport.Packets;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>
    /// The handshake over datagrams. The initiator retransmits its Initiation with backoff and becomes established on
    /// the Response, then resends the Completion until an authenticated packet shows the responder has it. The
    /// responder never retransmits on its own: it answers a duplicate Initiation with the Response it cached.
    /// </summary>
    internal sealed partial class TransportConnection
    {
        private const long MaximumRetransmitInterval = 2_000_000;

        private NoiseHandshakeXX? handshake;
        private byte[]? initiationMessage;
        private byte[]? cookie;
        private byte[]? pinnedKey;
        private bool retryAccepted;
        private long handshakeDeadline;
        private long retransmitAt;
        private long retransmitInterval;

        // Initiator, once established: the Response it read, the Completion it resends, and whether the peer has it.
        private byte[]? responseMessage;
        private byte[]? completionDatagram;
        private bool peerConfirmed;
        private long completionResendAt;
        private long completionInterval;

        // Responder: the Response it answers duplicates with, and the Completion that established it.
        private byte[]? responseDatagram;
        private byte[]? completionMessage;

        // The handshake's round trip seeds the RTT estimate, but only when its message went once (Karn's rule).
        private long handshakeSentAt;
        private int handshakeSends;

        /// <summary>Starts the handshake as initiator, optionally pinning the responder's static key.</summary>
        internal void Initiate(byte[]? expectedRemoteStaticKey)
        {
            pinnedKey = expectedRemoteStaticKey == null ? null : (byte[])expectedRemoteStaticKey.Clone();
            handshake = new NoiseHandshakeXX(true, endpoint.StaticPrivateKey, HandshakePackets.Prologue(LocalConnectionId), endpoint.EphemeralSource);
            initiationMessage = handshake.WriteMessage(Array.Empty<byte>());
            handshakeDeadline = now + options.HandshakeTimeoutMicros;
            retransmitInterval = options.InitialRetransmitMicros;
            retransmitAt = now + retransmitInterval;
            SendInitiation();
        }

        /// <summary>
        /// Answers an admitted Initiation with a Response. Returns false, leaving nothing behind, when the Initiation is
        /// malformed or carries a low-order key.
        /// </summary>
        internal bool Respond(ulong initiatorConnectionId, byte[] message1)
        {
            RemoteConnectionId = initiatorConnectionId;
            initiationMessage = message1;
            handshakeDeadline = now + options.HandshakeTimeoutMicros;
            handshake = new NoiseHandshakeXX(false, endpoint.StaticPrivateKey, HandshakePackets.Prologue(initiatorConnectionId), endpoint.EphemeralSource);
            if (!handshake.TryReadMessage(message1, out var payload) || payload.Length != 0)
            {
                DisposeHandshake();
                return false;
            }

            byte[] message2;
            try
            {
                message2 = handshake.WriteMessage(HandshakePayloads.WriteResponse(LocalConnectionId, (ulong)options.StreamWindowBytes));
            }
            catch (InvalidOperationException)
            {
                DisposeHandshake();
                return false;
            }

            responseDatagram = Addressed(PacketType.Response, initiatorConnectionId, message2);
            endpoint.SendCopy(Path, responseDatagram);
            handshakeSentAt = now;
            handshakeSends = 1;
            return true;
        }

        /// <summary>Resends the cached Response when the same Initiation arrives again.</summary>
        internal void OnDuplicateInitiation(byte[] message1)
        {
            if (IsInitiator || State != TransportConnectionState.Handshaking || responseDatagram == null) return;
            if (!BytesEqual(message1, initiationMessage!)) return;
            endpoint.SendCopy(Path, responseDatagram);
            handshakeSends++;
        }

        internal bool ReceiveRetry(TransportPath from, byte[] retryCookie, long time)
        {
            now = time;
            if (!IsInitiator || State != TransportConnectionState.Handshaking || retryAccepted || !from.Equals(Path)) return false;

            // One Retry per attempt: a second one could only come from someone replaying or guessing.
            retryAccepted = true;
            cookie = retryCookie;
            handshakeSends = 0;
            SendInitiation();
            retransmitInterval = options.InitialRetransmitMicros;
            retransmitAt = now + retransmitInterval;
            return true;
        }

        internal bool ReceiveResponse(TransportPath from, byte[] message, long time)
        {
            now = time;
            if (!IsInitiator || !from.Equals(Path)) return false;
            if (State != TransportConnectionState.Handshaking)
            {
                // A duplicate Response means the responder saw a retransmitted Initiation; it may lack the Completion.
                if (State != TransportConnectionState.Established || peerConfirmed || !BytesEqual(message, responseMessage!)) return false;
                endpoint.SendCopy(Path, completionDatagram!);
                return true;
            }

            var noise = handshake!;
            if (!noise.TryReadMessage(message, out var payload) || !HandshakePayloads.TryReadResponse(payload, out var responderId, out var window))
            {
                FailHandshake("The handshake response did not authenticate.");
                return false;
            }

            var remoteKey = noise.RemoteStaticKey;
            if (pinnedKey != null && !BytesEqual(remoteKey, pinnedKey))
            {
                FailHandshake("The peer's key did not match the expected key.");
                return false;
            }

            if (window < MinimumPeerWindow)
            {
                FailHandshake("The peer's stream window cannot hold a frame.");
                return false;
            }

            byte[] message3;
            try
            {
                message3 = noise.WriteMessage(HandshakePayloads.WriteCompletion((ulong)options.StreamWindowBytes));
            }
            catch (InvalidOperationException)
            {
                FailHandshake("The peer's key was a low-order point.");
                return false;
            }

            CompleteHandshake(remoteKey, window);
            RemoteConnectionId = responderId;
            responseMessage = message;
            SampleHandshakeRoundTrip();
            completionDatagram = Addressed(PacketType.Completion, responderId, message3);
            endpoint.SendCopy(Path, completionDatagram);
            completionInterval = options.InitialRetransmitMicros;
            completionResendAt = now + completionInterval;
            EnterEstablished();
            return true;
        }

        internal bool ReceiveCompletion(TransportPath from, byte[] message, long time)
        {
            now = time;
            if (IsInitiator || !from.Equals(Path)) return false;
            if (State != TransportConnectionState.Handshaking)
            {
                // The initiator has not seen an authenticated packet yet; answer its resent Completion with one.
                if (State != TransportConnectionState.Established || !BytesEqual(message, completionMessage!)) return false;
                pingPending = true;
                return true;
            }

            var noise = handshake!;
            if (!noise.TryReadMessage(message, out var payload) || !HandshakePayloads.TryReadCompletion(payload, out var window))
            {
                FailHandshake("The handshake completion did not authenticate.");
                return false;
            }

            if (window < MinimumPeerWindow)
            {
                FailHandshake("The peer's stream window cannot hold a frame.");
                return false;
            }

            CompleteHandshake(noise.RemoteStaticKey, window);
            completionMessage = message;
            SampleHandshakeRoundTrip();
            pingPending = true;
            EnterEstablished();
            return true;
        }

        /// <summary>Gets the smallest initial window a peer may advertise: one maximum frame and its prefix.</summary>
        private ulong MinimumPeerWindow => (ulong)(options.MaximumFrameBytes + TransportOptions.FramePrefixBytes);

        private void PollHandshake()
        {
            if (now >= handshakeDeadline)
            {
                FailHandshake("The handshake timed out.", TransportCloseReason.HandshakeTimedOut);
                return;
            }

            if (!IsInitiator || now < retransmitAt) return;
            SendInitiation();
            retransmitInterval = Math.Min(retransmitInterval * 2, MaximumRetransmitInterval);
            retransmitAt = now + retransmitInterval;
        }

        private void PollCompletion()
        {
            if (!IsInitiator || peerConfirmed || completionDatagram == null || now < completionResendAt) return;
            endpoint.SendCopy(Path, completionDatagram);
            completionInterval = Math.Min(completionInterval * 2, MaximumRetransmitInterval);
            completionResendAt = now + completionInterval;
        }

        private void SendInitiation()
        {
            var buffer = endpoint.RentBuffer();
            var writer = endpoint.HandshakeWriter;
            writer.Reset(buffer, 0, HandshakePackets.InitiationSize);
            HandshakePackets.WriteInitiation(writer, LocalConnectionId, cookie, initiationMessage!);
            endpoint.Enqueue(Path, buffer, writer.Written);
            handshakeSentAt = now;
            handshakeSends++;
        }

        /// <summary>Times the handshake message this side sent against the reply that just arrived, if it went once.</summary>
        private void SampleHandshakeRoundTrip()
        {
            if (handshakeSends == 1) rtt.OnSample(now - handshakeSentAt, 0, options.MaximumAckDelayMicros);
        }

        private void CompleteHandshake(byte[] remoteKey, ulong peerWindow)
        {
            var noise = handshake!;
            var keys = noise.Split();
            sendKey = keys.Send;
            receiveKey = keys.Receive;
            handshakeHash = noise.HandshakeHash;
            remoteStaticKey = remoteKey;
            DisposeHandshake();
            sendStream.OnWindow(peerWindow);
        }

        private void EnterEstablished()
        {
            State = TransportConnectionState.Established;
            lastReceivedAt = now;
            lastSentAt = now;
            endpoint.Report(TransportDiagnosticKind.Established, Number, "The handshake completed.");
            endpoint.OnEstablished(this);
        }

        /// <summary>
        /// Ends a failed handshake. The local player started an initiator's attempt, so diagnostics hear of it; a
        /// responder's half-open handshake is unauthenticated traffic and only counted.
        /// </summary>
        private void FailHandshake(string message, TransportCloseReason reason = TransportCloseReason.HandshakeFailed)
        {
            DisposeHandshake();
            if (IsInitiator) endpoint.Report(TransportDiagnosticKind.HandshakeFailed, Number, message);
            else endpoint.Statistics.HandshakesAbandoned++;
            Finish(reason);
        }

        private void DisposeHandshake()
        {
            handshake?.Dispose();
            handshake = null;
        }

        private static byte[] Addressed(byte type, ulong receiverConnectionId, byte[] message)
        {
            var datagram = new byte[1 + HandshakePackets.ConnectionIdSize + message.Length];
            var writer = new PacketWriter();
            writer.Reset(datagram, 0, datagram.Length);
            HandshakePackets.WriteAddressed(writer, type, receiverConnectionId, message);
            return datagram;
        }
    }
}
