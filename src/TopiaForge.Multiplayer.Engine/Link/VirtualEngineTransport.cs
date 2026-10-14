using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>Deterministic fault cadences for <see cref="VirtualEngineTransport"/>, counted over every frame sent.</summary>
    internal sealed class VirtualLinkConditions
    {
        internal int LatencyTicks { get; set; }
        internal int DropEvery { get; set; }
        internal int DuplicateEvery { get; set; }
        internal int ReorderEvery { get; set; }
        internal int RetryDelayTicks { get; set; } = 1;

        /// <summary>Gets or sets the per-direction link rate; zero means frames take no time to serialize.</summary>
        internal int BandwidthBytesPerSecond { get; set; }

        /// <summary>Gets or sets how many transport ticks make one second, for converting the link rate.</summary>
        internal int TicksPerSecond { get; set; } = 60;
    }

    /// <summary>
    /// A deterministic, tick-driven model of the channel contracts the real transport provides. The Session channel is
    /// reliable and ordered: per-direction FIFO delivery, loss turned into a retry delay that also holds later frames,
    /// duplicates discarded by sequence. Realtime and Voice are unreliable and sequenced, one datagram per frame: loss
    /// is final, a datagram older than one already delivered on its channel is dropped, and nothing on them waits behind
    /// Session frames. Every channel shares the link's rate, and in-flight frames are dropped when a link is severed.
    /// The test rig drives it; the bytes are the engine's own.
    /// </summary>
    internal sealed class VirtualEngineTransport
    {
        private readonly VirtualLinkConditions conditions;
        private readonly List<ScheduledFrame> inFlight = new List<ScheduledFrame>();
        private readonly List<ScheduledFrame> due = new List<ScheduledFrame>();
        private readonly HashSet<VirtualDirection> blocked = new HashSet<VirtualDirection>();
        private ulong frameOrdinal;
        private ulong scheduleOrdinal;
        private int linkOrdinal;

        internal VirtualEngineTransport(VirtualLinkConditions conditions)
        {
            this.conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
        }

        /// <summary>Gets or sets the current tick; frames sent now are scheduled relative to it.</summary>
        internal ulong Now { get; set; }

        /// <summary>
        /// Gets whether a frame that will still be delivered without outside help is in flight. Frames held until a
        /// test releases them, or behind an interrupted path, do not count; frames in a timed partition do.
        /// </summary>
        internal bool HasInFlight
        {
            get
            {
                foreach (var frame in inFlight)
                {
                    var direction = frame.Direction;
                    if (direction.Severed) continue;
                    if (frame.IsDatagram) return true;
                    if (direction.Held || IsBehindTestHold(frame)) continue;
                    if (frame.IsCloseMarker || frame.LostForever || frame.Sequence > direction.DeliveredSequence) return true;
                }

                return false;
            }
        }

        /// <summary>Creates a connected pair: the host's end (whose peer is <paramref name="clientIdentity"/>) and the client's end.</summary>
        internal VirtualLinkPair Connect(string clientIdentity, string hostIdentity = "host")
        {
            // Both ends share one binding, different for every link, as a real handshake hash would be.
            var binding = new byte[32];
            var ordinal = ++linkOrdinal;
            for (var index = 0; index < binding.Length; index++) binding[index] = (byte)((ordinal * 131) + (index * 29) + (ordinal >> (index % 8)));
            var hostEnd = new VirtualLinkEnd(this, clientIdentity, binding);
            var clientEnd = new VirtualLinkEnd(this, hostIdentity, binding);
            hostEnd.Outbound = new VirtualDirection(clientEnd);
            clientEnd.Outbound = new VirtualDirection(hostEnd);
            hostEnd.Peer = clientEnd;
            clientEnd.Peer = hostEnd;
            return new VirtualLinkPair(hostEnd, clientEnd);
        }

        /// <summary>Severs a link at once: both ends close and every frame still in flight is lost.</summary>
        internal void Sever(VirtualLinkEnd end)
        {
            foreach (var side in new[] { end, end.Peer! })
            {
                side.Outbound!.Severed = true;
                side.State = EngineLinkState.Closed;
            }

            inFlight.RemoveAll(frame => frame.Direction.Severed);
        }

        /// <summary>Moves every frame due by <paramref name="tick"/> into its receiver's inbox, in order.</summary>
        internal void DeliverDue(ulong tick)
        {
            due.Clear();
            foreach (var frame in inFlight)
            {
                if (frame.DeliverAt <= tick && (frame.IsDatagram || !frame.Direction.Held)) due.Add(frame);
            }

            if (due.Count == 0) return;
            due.Sort((left, right) =>
            {
                var order = left.DeliverAt.CompareTo(right.DeliverAt);
                return order != 0 ? order : left.ScheduleOrdinal.CompareTo(right.ScheduleOrdinal);
            });
            blocked.Clear();
            foreach (var frame in due)
            {
                var direction = frame.Direction;
                if (direction.Severed)
                {
                    inFlight.Remove(frame);
                    continue;
                }

                if (frame.IsDatagram)
                {
                    inFlight.Remove(frame);
                    DeliverDatagram(frame, tick);
                    continue;
                }

                // A held frame blocks everything behind it on the same direction, as an unacknowledged frame does.
                if (blocked.Contains(direction) || direction.Held || tick < direction.HeldUntilTick || frame.HeldByTest)
                {
                    blocked.Add(direction);
                    continue;
                }

                inFlight.Remove(frame);
                if (frame.IsCloseMarker)
                {
                    direction.Receiver.State = EngineLinkState.Closed;
                    continue;
                }

                if (frame.LostForever)
                {
                    // A reliable channel cannot skip a frame, so losing one for good breaks the connection.
                    Sever(direction.Receiver);
                    continue;
                }

                if (frame.Sequence <= direction.DeliveredSequence) continue;
                direction.DeliveredSequence = frame.Sequence;
                if (direction.Receiver.State == EngineLinkState.Closed) continue;
                direction.CountDelivered(frame.Channel, frame.Bytes.Length);
                direction.Receiver.Deliver(frame.Channel, frame.Bytes);
            }
        }

        /// <summary>Delivers one datagram, or loses it: on a path that is down, or behind a newer one on its channel.</summary>
        private static void DeliverDatagram(ScheduledFrame frame, ulong tick)
        {
            var direction = frame.Direction;
            var channel = (int)frame.Channel;
            if (direction.Held || tick < direction.HeldUntilTick)
            {
                direction.DatagramsLost[channel]++;
                return;
            }

            if (frame.Sequence <= direction.DatagramDelivered[channel])
            {
                direction.DatagramsStale[channel]++;
                return;
            }

            direction.DatagramDelivered[channel] = frame.Sequence;
            if (direction.Receiver.State == EngineLinkState.Closed) return;
            direction.CountDelivered(frame.Channel, frame.Bytes.Length);
            direction.Receiver.Deliver(frame.Channel, frame.Bytes);
        }

        /// <summary>
        /// Sends one unreliable, sequenced datagram. It shares the link's rate with the session channel but never waits
        /// behind it. A lost datagram stays lost, a delayed one may arrive after a newer one and be dropped, and a
        /// duplicate arrives a tick later and is dropped by its sequence.
        /// </summary>
        private void SendDatagram(VirtualDirection direction, EngineChannel channel, byte[] bytes)
        {
            var sequence = ++direction.DatagramSent[(int)channel];
            frameOrdinal++;
            var baseDelay = (ulong)Math.Max(1, conditions.LatencyTicks);
            if (Hits(conditions.ReorderEvery)) baseDelay++;
            var start = Math.Max(Now, direction.BusyUntilTick);
            var serialization = SerializationTicks(bytes.Length);
            direction.BusyUntilTick = start + serialization;
            direction.CountSent(channel, bytes.Length);
            if (Hits(conditions.DropEvery))
            {
                direction.DatagramsLost[(int)channel]++;
                return;
            }

            var deliverAt = start + serialization + baseDelay;
            inFlight.Add(new ScheduledFrame(deliverAt, ++scheduleOrdinal, direction, sequence, channel, bytes, false));
            if (Hits(conditions.DuplicateEvery))
            {
                inFlight.Add(new ScheduledFrame(deliverAt + 1, ++scheduleOrdinal, direction, sequence, channel, (byte[])bytes.Clone(), false));
            }
        }

        /// <summary>Holds the next <paramref name="frames"/> frames on a direction, and everything behind them, until released.</summary>
        internal void HoldNext(VirtualDirection direction, int frames)
        {
            if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames));
            direction.HoldBudget += frames;
        }

        /// <summary>Releases every frame held on a direction; they arrive in order at the next delivery.</summary>
        internal void Release(VirtualDirection direction)
        {
            direction.HoldBudget = 0;
            foreach (var frame in inFlight)
            {
                if (ReferenceEquals(frame.Direction, direction)) frame.HeldByTest = false;
            }

            direction.HoldsFrames = false;
        }

        /// <summary>Loses the next frame on a direction for good; when it is due the link breaks.</summary>
        internal void DropNextPermanently(VirtualDirection direction) => direction.DropBudget++;

        /// <summary>Re-sends a byte-identical copy of the last frame on a direction; the receiver must discard it.</summary>
        internal bool ReplayLast(VirtualDirection direction)
        {
            if (direction.LastFrame == null || direction.Severed) return false;
            var deliverAt = Math.Max(Now + (ulong)Math.Max(1, conditions.LatencyTicks), direction.LastDeliverAt);
            inFlight.Add(new ScheduledFrame(
                deliverAt,
                ++scheduleOrdinal,
                direction,
                direction.LastSequence,
                direction.LastChannel,
                (byte[])direction.LastFrame.Clone(),
                false));
            return true;
        }

        internal void Send(VirtualLinkEnd from, EngineChannel channel, byte[] bytes)
        {
            var direction = from.Outbound!;
            if (direction.Severed || direction.Closing) return;
            if (channel != EngineChannel.Session)
            {
                SendDatagram(direction, channel, bytes);
                return;
            }

            var sequence = ++direction.SentSequence;
            frameOrdinal++;
            var baseDelay = (ulong)Math.Max(1, conditions.LatencyTicks);
            if (Hits(conditions.ReorderEvery)) baseDelay++;
            var start = Math.Max(Now, direction.BusyUntilTick);
            var serialization = SerializationTicks(bytes.Length);
            direction.BusyUntilTick = start + serialization;
            var arrival = start + serialization + baseDelay + (Hits(conditions.DropEvery) ? (ulong)conditions.RetryDelayTicks : 0);
            var deliverAt = Math.Max(arrival, direction.LastDeliverAt);
            direction.LastDeliverAt = deliverAt;
            direction.CountSent(channel, bytes.Length);
            direction.LastFrame = bytes;
            direction.LastSequence = sequence;
            direction.LastChannel = channel;
            var scheduled = new ScheduledFrame(deliverAt, ++scheduleOrdinal, direction, sequence, channel, bytes, false);
            if (direction.HoldBudget > 0)
            {
                direction.HoldBudget--;
                scheduled.HeldByTest = true;
                direction.HoldsFrames = true;
            }

            if (direction.DropBudget > 0)
            {
                direction.DropBudget--;
                scheduled.LostForever = true;
            }

            inFlight.Add(scheduled);
            if (Hits(conditions.DuplicateEvery))
            {
                var copy = (byte[])bytes.Clone();
                inFlight.Add(new ScheduledFrame(Math.Max(Now + baseDelay + 1, deliverAt), ++scheduleOrdinal, direction, sequence, channel, copy, false));
            }
        }

        /// <summary>Closes one end gracefully: frames it already sent still arrive, then the peer sees the link close.</summary>
        internal void Close(VirtualLinkEnd end)
        {
            if (end.State == EngineLinkState.Closed) return;
            end.State = EngineLinkState.Closed;
            var direction = end.Outbound!;
            if (direction.Severed || direction.Closing) return;
            direction.Closing = true;
            var deliverAt = Math.Max(Now + (ulong)Math.Max(1, conditions.LatencyTicks), direction.LastDeliverAt);
            direction.LastDeliverAt = deliverAt;
            inFlight.Add(new ScheduledFrame(deliverAt, ++scheduleOrdinal, direction, 0, EngineChannel.Session, Array.Empty<byte>(), true));
        }

        private bool Hits(int every) => every > 0 && frameOrdinal % (ulong)every == 0;

        private bool IsBehindTestHold(ScheduledFrame frame)
        {
            if (!frame.Direction.HoldsFrames) return false;
            foreach (var other in inFlight)
            {
                if (other.HeldByTest && ReferenceEquals(other.Direction, frame.Direction) && other.ScheduleOrdinal <= frame.ScheduleOrdinal) return true;
            }

            return false;
        }

        private ulong SerializationTicks(int byteCount)
        {
            if (conditions.BandwidthBytesPerSecond <= 0 || byteCount == 0) return 0;
            var scaled = (long)byteCount * conditions.TicksPerSecond;
            return (ulong)((scaled + conditions.BandwidthBytesPerSecond - 1) / conditions.BandwidthBytesPerSecond);
        }

        private sealed class ScheduledFrame
        {
            internal ScheduledFrame(
                ulong deliverAt,
                ulong scheduleOrdinal,
                VirtualDirection direction,
                ulong sequence,
                EngineChannel channel,
                byte[] bytes,
                bool isCloseMarker)
            {
                DeliverAt = deliverAt;
                ScheduleOrdinal = scheduleOrdinal;
                Direction = direction;
                Sequence = sequence;
                Channel = channel;
                Bytes = bytes;
                IsCloseMarker = isCloseMarker;
            }

            internal ulong DeliverAt { get; }
            internal ulong ScheduleOrdinal { get; }
            internal VirtualDirection Direction { get; }
            internal ulong Sequence { get; }
            internal EngineChannel Channel { get; }
            internal byte[] Bytes { get; }
            internal bool IsCloseMarker { get; }

            /// <summary>Gets whether the frame travels on an unreliable, sequenced channel.</summary>
            internal bool IsDatagram => Channel != EngineChannel.Session && !IsCloseMarker;

            internal bool HeldByTest { get; set; }
            internal bool LostForever { get; set; }
        }
    }

    internal readonly struct VirtualLinkPair
    {
        internal VirtualLinkPair(VirtualLinkEnd hostEnd, VirtualLinkEnd clientEnd)
        {
            HostEnd = hostEnd;
            ClientEnd = clientEnd;
        }

        internal VirtualLinkEnd HostEnd { get; }
        internal VirtualLinkEnd ClientEnd { get; }
    }

    /// <summary>One direction of a virtual link: its send sequence, FIFO schedule and delivery cursor.</summary>
    internal sealed class VirtualDirection
    {
        internal VirtualDirection(VirtualLinkEnd receiver)
        {
            Receiver = receiver;
        }

        internal VirtualLinkEnd Receiver { get; }
        internal ulong SentSequence { get; set; }
        internal ulong DeliveredSequence { get; set; }
        internal ulong LastDeliverAt { get; set; }
        internal bool Severed { get; set; }
        internal bool Closing { get; set; }

        /// <summary>Gets or sets whether due frames are held back, as on an interrupted path.</summary>
        internal bool Held { get; set; }

        /// <summary>Gets or sets the tick before which due frames are held, as during a partition.</summary>
        internal ulong HeldUntilTick { get; set; }

        /// <summary>Gets or sets whether a test is holding frames on this direction until it releases them.</summary>
        internal bool HoldsFrames { get; set; }

        internal int HoldBudget { get; set; }
        internal int DropBudget { get; set; }
        internal ulong BusyUntilTick { get; set; }
        internal byte[]? LastFrame { get; set; }
        internal ulong LastSequence { get; set; }
        internal EngineChannel LastChannel { get; set; }
        /// <summary>Gets the bytes sent on every channel together.</summary>
        internal long BytesSent { get; set; }

        internal long FramesSent { get; set; }

        /// <summary>Gets the bytes delivered on every channel together, not counting dropped copies.</summary>
        internal long BytesDelivered { get; set; }

        internal long FramesDelivered { get; set; }

        /// <summary>Gets each datagram channel's last sequence sent, indexed by <see cref="EngineChannel"/>.</summary>
        internal ulong[] DatagramSent { get; } = new ulong[ChannelCount];

        /// <summary>Gets each datagram channel's newest sequence delivered; anything older is dropped.</summary>
        internal ulong[] DatagramDelivered { get; } = new ulong[ChannelCount];

        /// <summary>Gets each datagram channel's datagrams lost, on the wire or to a path that was down.</summary>
        internal long[] DatagramsLost { get; } = new long[ChannelCount];

        /// <summary>Gets each datagram channel's datagrams dropped for arriving behind a newer one, duplicates included.</summary>
        internal long[] DatagramsStale { get; } = new long[ChannelCount];

        internal long[] ChannelBytesSent { get; } = new long[ChannelCount];
        internal long[] ChannelFramesSent { get; } = new long[ChannelCount];
        internal long[] ChannelBytesDelivered { get; } = new long[ChannelCount];
        internal long[] ChannelFramesDelivered { get; } = new long[ChannelCount];

        private const int ChannelCount = 3;

        internal void CountSent(EngineChannel channel, int bytes)
        {
            BytesSent += bytes;
            FramesSent++;
            ChannelBytesSent[(int)channel] += bytes;
            ChannelFramesSent[(int)channel]++;
        }

        internal void CountDelivered(EngineChannel channel, int bytes)
        {
            BytesDelivered += bytes;
            FramesDelivered++;
            ChannelBytesDelivered[(int)channel] += bytes;
            ChannelFramesDelivered[(int)channel]++;
        }
    }

    /// <summary>One end of a virtual link, as an engine sees it.</summary>
    internal sealed class VirtualLinkEnd : IEngineLink
    {
        private readonly VirtualEngineTransport transport;
        private readonly Queue<KeyValuePair<EngineChannel, byte[]>> inbox = new Queue<KeyValuePair<EngineChannel, byte[]>>();

        private readonly byte[] channelBinding;

        internal VirtualLinkEnd(VirtualEngineTransport transport, string peerIdentity, byte[] channelBinding)
        {
            this.transport = transport;
            PeerIdentity = peerIdentity;
            this.channelBinding = (byte[])channelBinding.Clone();
        }

        public string PeerIdentity { get; }

        public MultiplayerPlayerKey PlayerKey => default;

        public byte[] ChannelBinding => (byte[])channelBinding.Clone();

        public EngineLinkState State { get; internal set; } = EngineLinkState.Open;

        internal VirtualDirection? Outbound { get; set; }

        internal VirtualLinkEnd? Peer { get; set; }

        internal int PendingFrames => inbox.Count;

        public void Send(EngineChannel channel, ArraySegment<byte> frame)
        {
            if (State == EngineLinkState.Closed) return;
            var copy = new byte[frame.Count];
            Buffer.BlockCopy(frame.Array!, frame.Offset, copy, 0, frame.Count);
            transport.Send(this, channel, copy);
        }

        public bool TryReceive(out EngineChannel channel, out byte[] frame)
        {
            if (inbox.Count > 0)
            {
                var next = inbox.Dequeue();
                channel = next.Key;
                frame = next.Value;
                return true;
            }

            channel = EngineChannel.Session;
            frame = Array.Empty<byte>();
            return false;
        }

        public void Close() => transport.Close(this);

        internal void Deliver(EngineChannel channel, byte[] frame) => inbox.Enqueue(new KeyValuePair<EngineChannel, byte[]>(channel, frame));
    }
}
