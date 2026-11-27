using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Transport.Packets;
using TopiaForge.Multiplayer.Transport.Recovery;
using TopiaForge.Multiplayer.Transport.Streams;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>
    /// Protected packets in: replay check, then authentication, then the frames. A packet that fails either check is
    /// dropped without a trace, so a forgery never moves the replay window or the timers. An authenticated packet
    /// counts from any path the endpoint allows, but answers go only to the validated path until another is proven.
    /// </summary>
    internal sealed partial class TransportConnection
    {
        private const int MaximumCloseResends = 3;

        private CloseCode? violation;
        private TransportPath? receivingFrom;
        private int receivingSize;
        private ulong largestReceived;
        private bool anyReceived;

        /// <summary>Takes one protected packet addressed to this connection; returns whether it authenticated.</summary>
        internal bool ReceiveProtected(TransportPath from, byte[] data, int offset, int count, long time)
        {
            now = time;
            var key = receiveKey;
            if (key == null || State == TransportConnectionState.Closed) return false;
            var number = ProtectedPacket.ReadPacketNumber(data, offset);
            if (!replay.CanAccept(number)) return false;
            var header = endpoint.ReceiveHeader;
            Buffer.BlockCopy(data, offset, header, 0, ProtectedPacket.HeaderSize);
            var plaintext = endpoint.ReceivePlaintext;
            var sealedBytes = count - ProtectedPacket.HeaderSize;
            if (!key.TryOpen(number, header, data, offset + ProtectedPacket.HeaderSize, sealedBytes, plaintext, 0, out var length)) return false;
            replay.Accept(number);
            PacketsReceived++;
            lastReceivedAt = now;
            IsStalled = false;
            peerConfirmed = true;
            var newest = !anyReceived || number > largestReceived;
            if (newest)
            {
                largestReceived = number;
                anyReceived = true;
            }

            if (State == TransportConnectionState.Draining)
            {
                if (sentClose && closeResends < MaximumCloseResends) closeResendPending = true;
                return true;
            }

            FrameRead read;
            receivingFrom = from;
            receivingSize = count;
            answeredThisDatagram = false;
            try
            {
                read = FrameReader.Read(plaintext, 0, length, this, ackRanges);
            }
            catch (ProtocolException exception)
            {
                var code = violation ?? CloseCode.ProtocolViolation;
                violation = null;
                CloseWithError(code, TransportCloseReason.ProtocolViolation, exception.Message);
                return true;
            }
            finally
            {
                receivingFrom = null;
            }

            // A refused packet could not buffer its stream bytes: leave it unacknowledged so they come again.
            if ((read & FrameRead.Refused) == 0) acks.OnPacketReceived(number, (read & FrameRead.AckEliciting) != 0, now);
            if (newest && (read & FrameRead.NonProbing) != 0) OnPeerUsing(from);
            return true;
        }

        void IFrameSink.OnPing()
        {
        }

        void IFrameSink.OnAck(List<PacketRange> ranges, ulong ackDelayMicros)
        {
            if (ranges[0].Largest >= nextPacketNumber) throw new ProtocolException("An ACK named a packet that was never sent.");
            var delay = ackDelayMicros > long.MaxValue ? long.MaxValue : (long)ackDelayMicros;
            loss.OnAck(ranges, delay, now, rtt, options.MaximumAckDelayMicros, onAcknowledged, onLost);
        }

        void IFrameSink.OnClose(CloseCode code)
        {
            if (State >= TransportConnectionState.Draining) return;
            PeerCloseCode = code;
            CloseReason = TransportCloseReason.Peer;
            EnterDraining(false);
        }

        bool IFrameSink.OnStream(ulong offset, byte[] data)
        {
            if (State >= TransportConnectionState.Draining) return true;
            if (offset + (ulong)data.Length > receiveStream.AdvertisedMaximum)
            {
                violation = CloseCode.FlowControl;
                throw new ProtocolException("A stream frame passed the advertised window.");
            }

            // The process-wide cap refuses bytes it cannot hold; the sender retransmits them later.
            if (!endpoint.CanBuffer(this, receiveStream.NewBytes(offset, data))) return false;
            var before = receiveStream.BufferedBytes;
            var result = receiveStream.Accept(offset, data);
            endpoint.OnReassemblyChanged(this, receiveStream.BufferedBytes - before);
            if (result == StreamAcceptResult.FrameTooLarge) throw new ProtocolException("A session frame exceeded the largest frame.");
            if (result == StreamAcceptResult.MalformedLength) throw new ProtocolException("A session frame length was not a minimal varint.");
            return true;
        }

        void IFrameSink.OnWindow(ulong maximumOffset) => sendStream.OnWindow(maximumOffset);

        void IFrameSink.OnDatagram(byte channel, ulong sequence, byte[] payload)
        {
            var target = ChannelFor(channel) ?? throw new ProtocolException("A datagram named an unknown channel.");
            if (State != TransportConnectionState.Established || !target.ShouldDeliver(sequence)) return;
            if (inboundDatagrams.Count == MaximumInboundDatagrams) inboundDatagrams.Dequeue();
            inboundDatagrams.Enqueue(new KeyValuePair<EngineChannel, byte[]>((EngineChannel)channel, payload));
        }

        private void OnPacketAcknowledged(SentPacket packet)
        {
            foreach (var range in packet.Streams) sendStream.OnAcknowledged(range);
        }

        private void OnPacketLost(SentPacket packet)
        {
            PacketsLost++;
            foreach (var range in packet.Streams) sendStream.OnLost(range);
            if (packet.CarriesWindow) windowPending = true;
        }
    }
}
