using System;
using TopiaForge.Multiplayer.Transport.Crypto;
using TopiaForge.Multiplayer.Transport.Packets;
using TopiaForge.Multiplayer.Transport.Streams;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>
    /// Protected packets out. Each packet carries, in order: an ACK when anything new arrived, a WINDOW update, queued
    /// datagrams, then as much of the session stream as fits. The pacer gates every packet that asks for an
    /// acknowledgement except probes; ACK-only and CLOSE packets go regardless.
    /// </summary>
    internal sealed partial class TransportConnection
    {
        private const int ProbesPerTimeout = 2;

        private ulong nextPacketNumber;
        private bool pingPending;
        private bool windowPending;
        private int probesPending;
        private CloseCode? pendingClose;
        private CloseCode lastCloseCode;
        private bool gracefulClose;
        private bool sentClose;
        private int closeResends;
        private bool closeResendPending;
        private long drainDeadline;

        private enum PacketOutcome
        {
            Nothing = 0,
            AckOnly = 1,
            AckEliciting = 2
        }

        /// <summary>
        /// Gets whether something waits to go out in a packet that asks for an acknowledgement. A queued datagram counts
        /// only once its class may send it.
        /// </summary>
        private bool HasElicitingWork =>
            probesPending > 0 || pingPending || windowPending || receiveStream.IsWindowUpdateDue || sendStream.HasDataToSend ||
            IsDatagramReady(realtime) || IsDatagramReady(voice);

        /// <summary>Gets whether a graceful close has delivered everything and only its CLOSE remains.</summary>
        private bool IsReadyToClose => State == TransportConnectionState.Closing && gracefulClose && sendStream.IsFullyAcknowledged;

        /// <summary>Ends the connection because of a fault: CLOSE goes out at the next poll, then it drains.</summary>
        private void CloseWithError(CloseCode code, TransportCloseReason reason, string message)
        {
            if (State >= TransportConnectionState.Draining) return;
            CloseReason = reason;
            var kind = reason == TransportCloseReason.ProtocolViolation ? TransportDiagnosticKind.ProtocolViolation : TransportDiagnosticKind.Closed;
            endpoint.Report(kind, Number, message);
            if (sendKey == null)
            {
                Finish(reason);
                return;
            }

            State = TransportConnectionState.Closing;
            gracefulClose = false;
            pendingClose = code;
            realtime.ClearQueued();
            voice.ClearQueued();
        }

        /// <summary>Turns the closing connection away because the endpoint is full.</summary>
        internal void Refuse() => CloseWithError(CloseCode.Overloaded, TransportCloseReason.Overloaded, "The endpoint is full.");

        private void SendPackets()
        {
            while (State == TransportConnectionState.Established || State == TransportConnectionState.Closing)
            {
                if (nextPacketNumber >= CipherState.ReservedNonce - 1)
                {
                    // Unreachable in practice: the connection would have sent 2^64 packets. It must not reuse a nonce.
                    Finish(TransportCloseReason.Overloaded);
                    return;
                }

                if (IsReadyToClose && !pendingClose.HasValue)
                {
                    CloseReason = TransportCloseReason.Local;
                    pendingClose = CloseCode.Normal;
                }

                if (pendingClose.HasValue)
                {
                    SendClose(pendingClose.Value);
                    EnterDraining(true);
                    return;
                }

                // The connection's own pacer gates first, so only a connection it would let through presses on the uplink.
                var ackDue = acks.IsAckDue(now, options.MaximumAckDelayMicros);
                var probe = probesPending > 0;
                if (!HasElicitingWork ||
                    (!probe && (!pacer.CanSend(TransportOptions.MaximumDatagramBytes, now) ||
                                !endpoint.Uplink.CanSend(TransportOptions.MaximumDatagramBytes, now))))
                {
                    if (ackDue) SendAckOnly();
                    return;
                }

                var outcome = SendDataPacket(probe);
                if (outcome == PacketOutcome.Nothing) return;
                if (outcome == PacketOutcome.AckEliciting && probe) probesPending--;
            }
        }

        private PacketOutcome SendDataPacket(bool probe)
        {
            var writer = BeginPacket();
            var plaintext = endpoint.SendPlaintext;
            var packet = loss.Rent(nextPacketNumber, now);
            var wroteAck = WriteAckIfNew(writer);
            var eliciting = false;
            if (windowPending || receiveStream.IsWindowUpdateDue)
            {
                FrameWriter.WriteWindow(writer, receiveStream.NextWindow());
                packet.CarriesWindow = true;
                windowPending = false;
                eliciting = true;
            }

            eliciting |= WriteDatagrams(writer, realtime);
            eliciting |= WriteDatagrams(writer, voice);
            if (probe && !sendStream.HasDataToSend) sendStream.QueueProbe(StreamRoom(writer));
            while (true)
            {
                var room = StreamRoom(writer);
                if (room <= 0 || !sendStream.TryTake(room, out var range)) break;
                FrameWriter.WriteStreamHeader(writer, range.Offset, range.Length);
                sendStream.CopyTo(range, plaintext, writer.Position);
                writer.Advance(range.Length);
                packet.Streams.Add(range);
                eliciting = true;
            }

            if (!eliciting && (pingPending || probe))
            {
                writer.WriteByte(FrameType.Ping);
                eliciting = true;
            }

            if (!eliciting && !wroteAck)
            {
                loss.Return(packet);
                return PacketOutcome.Nothing;
            }

            if (eliciting) pingPending = false;
            packet.AckEliciting = eliciting;
            var size = Seal(writer.Written);
            packet.Size = size;
            loss.OnSent(packet);
            if (eliciting)
            {
                pacer.OnSent(size);
                endpoint.Uplink.OnSent(size);
            }

            return eliciting ? PacketOutcome.AckEliciting : PacketOutcome.AckOnly;
        }

        private void SendAckOnly()
        {
            var writer = BeginPacket();
            if (WriteAckIfNew(writer)) Seal(writer.Written);
        }

        private void SendClose(CloseCode code)
        {
            var writer = BeginPacket();
            WriteAckIfNew(writer);
            FrameWriter.WriteClose(writer, code);
            lastCloseCode = code;
            Seal(writer.Written);
        }

        private void EnterDraining(bool closeSent)
        {
            State = TransportConnectionState.Draining;
            sentClose = closeSent;
            drainDeadline = now + (3 * rtt.ProbeTimeout(options.MaximumAckDelayMicros));
            pendingClose = null;
            probesPending = 0;
            pingPending = false;
            windowPending = false;
            loss.Clear();

            // Nothing more of the stream will ever go out, so its buffers go now rather than when the link is dropped.
            sendStream.Release();
            realtime.ClearQueued();
            voice.ClearQueued();
            ClearPaths();
        }

        private PacketWriter BeginPacket()
        {
            var writer = endpoint.SendWriter;
            writer.Reset(endpoint.SendPlaintext, 0, ProtectedPacket.MaximumFramesBytes);
            return writer;
        }

        /// <summary>Writes an ACK covering everything received when anything arrived since the last one.</summary>
        private bool WriteAckIfNew(PacketWriter writer)
        {
            if (!acks.HasNewPackets || acks.Ranges.Count == 0) return false;

            // At most 32 ranges of two ten-byte varints: an ACK always fits an empty packet.
            FrameWriter.WriteAck(writer, acks.Ranges, acks.AckDelay(now));
            acks.OnAckSent();
            return true;
        }

        /// <summary>Writes queued datagrams while they fit the packet and their class's share allows them.</summary>
        private bool WriteDatagrams(PacketWriter writer, DatagramChannel channel)
        {
            var wrote = false;
            while (channel.QueuedCount > 0)
            {
                var payload = channel.PeekQueued();
                var size = FrameWriter.DatagramSize(channel.NextSequence, payload.Length);
                if (size > writer.Remaining || !shaper.CanSend(channel.Number, size, now)) break;
                FrameWriter.WriteDatagram(writer, channel.Number, channel.TakeQueued(), payload);
                shaper.OnSent(channel.Number, size);
                wrote = true;
            }

            return wrote;
        }

        private bool IsDatagramReady(DatagramChannel channel) =>
            channel.QueuedCount > 0 && shaper.CanSend(channel.Number, HeadDatagramSize(channel), now);

        /// <summary>Gets when the head of a channel's queue may go, or <see cref="long.MaxValue"/> when nothing waits.</summary>
        private long DatagramReadyAt(DatagramChannel channel) =>
            channel.QueuedCount == 0 ? long.MaxValue : shaper.NextAvailable(channel.Number, HeadDatagramSize(channel), now);

        private static int HeadDatagramSize(DatagramChannel channel) =>
            FrameWriter.DatagramSize(channel.NextSequence, channel.PeekQueued().Length);

        /// <summary>Gets how many stream bytes fit in the rest of the packet, after a STREAM header for them.</summary>
        private int StreamRoom(PacketWriter writer) =>
            writer.Remaining - 1 - PacketWriter.VarUIntSize(sendStream.WriteOffset) - PacketWriter.VarUIntSize((ulong)writer.Remaining);

        /// <summary>
        /// Seals the frames written so far into a pooled datagram and queues it for <paramref name="destination"/>, the
        /// validated path unless a path frame says otherwise; returns its size.
        /// </summary>
        private int Seal(int frameBytes, TransportPath? destination = null)
        {
            var number = nextPacketNumber++;
            var buffer = endpoint.RentBuffer();
            ProtectedPacket.WriteHeader(buffer, RemoteConnectionId, number);
            var header = endpoint.SendHeader;
            Buffer.BlockCopy(buffer, 0, header, 0, ProtectedPacket.HeaderSize);
            var size = ProtectedPacket.HeaderSize + sendKey!.Seal(number, header, endpoint.SendPlaintext, 0, frameBytes, buffer, ProtectedPacket.HeaderSize);
            endpoint.Enqueue(destination ?? Path, buffer, size);
            PacketsSent++;
            BytesSent += size;
            lastSentAt = now;
            return size;
        }
    }
}
