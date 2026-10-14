using System;
using System.Collections.Generic;

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
    /// Session frames. Datagrams serialize in a lane of their own, as the real transport writes queued datagrams first
    /// in every packet, but their bytes still count against the link, so later Session frames wait for them. A datagram
    /// the link cannot carry in time is shed rather than queued, as the real transport drops datagrams first: see
    /// <see cref="DatagramBacklogTicks"/>. Time on the link is kept in millionths of a tick and rounded up only to the
    /// tick a frame is delivered in, so a small frame costs its bytes, never a whole tick. In-flight frames are dropped
    /// when a link is severed. The test rig drives it; the bytes are the engine's own.
    /// </summary>
    internal sealed class VirtualEngineTransport
    {
        /// <summary>The fractions of a tick in which a direction's busy times are kept.</summary>
        internal const ulong SubTicksPerTick = 1_000_000;

        /// <summary>
        /// How far behind now a lane may already be for a datagram to join it: three ticks, 50 ms at 60 ticks a second.
        /// A datagram whose lane is further behind is shed and counted, as the real connection drops the datagrams its
        /// queue cannot hold. While the Session lane is further behind than this, each datagram channel may also take
        /// only its share of the link (<see cref="VoiceShareOf256"/>, <see cref="RealtimeShareOf256"/>), so Session
        /// keeps the rest. 50 ms is longer than the 40 ms between one talker's datagrams, so a link fast enough for all
        /// its talkers clears a burst of one datagram from each before the next arrives.
        /// </summary>
        internal const ulong DatagramBacklogTicks = 3;

        /// <summary>
        /// Voice's share of a link whose Session lane is behind, in 256ths: the real transport caps Voice at 96 of each
        /// connection's 256 kbit/s.
        /// </summary>
        internal const ulong VoiceShareOf256 = 96;

        /// <summary>Realtime's share of a link whose Session lane is behind, in 256ths: the real transport's 64 of 256 kbit/s.</summary>
        internal const ulong RealtimeShareOf256 = 64;

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

        /// <summary>Gets how many datagrams every link together has shed; each direction counts its own per channel.</summary>
        internal long ShedDatagrams { get; private set; }

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
        /// behind it. One the link cannot carry in time is shed: it never reaches the wire, costs the link nothing and
        /// takes no sequence. A lost datagram stays lost, a delayed one may arrive after a newer one and be dropped, and a
        /// duplicate arrives a tick later and is dropped by its sequence.
        /// </summary>
        private void SendDatagram(VirtualDirection direction, EngineChannel channel, byte[] bytes)
        {
            var now = Now * SubTicksPerTick;
            var serialization = SerializationSubTicks(bytes.Length);
            if (!CanCarry(direction, channel, now, serialization))
            {
                direction.DatagramsShed[(int)channel]++;
                ShedDatagrams++;
                return;
            }

            var sequence = ++direction.DatagramSent[(int)channel];
            frameOrdinal++;
            var baseDelay = (ulong)Math.Max(1, conditions.LatencyTicks);
            if (Hits(conditions.ReorderEvery)) baseDelay++;
            var sent = Math.Max(now, direction.DatagramBusyUntil) + serialization;
            direction.DatagramBusyUntil = sent;
            direction.SessionBusyUntil = Math.Max(direction.SessionBusyUntil, now) + serialization;
            direction.CountSent(channel, bytes.Length);
            if (Hits(conditions.DropEvery))
            {
                direction.DatagramsLost[(int)channel]++;
                return;
            }

            var deliverAt = CeilingTick(sent) + baseDelay;
            inFlight.Add(new ScheduledFrame(deliverAt, ++scheduleOrdinal, direction, sequence, channel, bytes, false));
            if (Hits(conditions.DuplicateEvery))
            {
                inFlight.Add(new ScheduledFrame(deliverAt + 1, ++scheduleOrdinal, direction, sequence, channel, (byte[])bytes.Clone(), false));
            }
        }

        /// <summary>
        /// Decides whether the link carries a datagram of <paramref name="serialization"/> sub-ticks. Its lane must not be
        /// more than <see cref="DatagramBacklogTicks"/> behind now. While the Session lane is that far behind, the channel
        /// must also have its share left: a bucket as deep as the bound, refilled at the channel's share of the link. A link
        /// with no rate carries everything.
        /// </summary>
        private static bool CanCarry(VirtualDirection direction, EngineChannel channel, ulong now, ulong serialization)
        {
            if (serialization == 0) return true;
            const ulong Bound = DatagramBacklogTicks * SubTicksPerTick;
            if (direction.DatagramBusyUntil > now + Bound) return false;
            var index = (int)channel;
            var share = channel == EngineChannel.Voice ? VoiceShareOf256 : RealtimeShareOf256;
            if (now > direction.ShareRefilledAt[index])
            {
                // A bound's worth of time at the full rate refills any bucket, so longer gaps cannot overflow.
                var elapsed = Math.Min(now - direction.ShareRefilledAt[index], Bound * 256);
                direction.ShareTokens[index] = Math.Min(Bound, direction.ShareTokens[index] + (elapsed * share / 256));
                direction.ShareRefilledAt[index] = now;
            }

            if (direction.SessionBusyUntil <= now + Bound) return true;
            if (direction.ShareTokens[index] < serialization) return false;
            direction.ShareTokens[index] -= serialization;
            return true;
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
            var sent = Math.Max(Now * SubTicksPerTick, direction.SessionBusyUntil) + SerializationSubTicks(bytes.Length);
            direction.SessionBusyUntil = sent;
            var arrival = CeilingTick(sent) + baseDelay + (Hits(conditions.DropEvery) ? (ulong)conditions.RetryDelayTicks : 0);
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

        /// <summary>Gets the first tick by which <paramref name="subTicks"/> of link time have passed.</summary>
        internal static ulong CeilingTick(ulong subTicks) => (subTicks / SubTicksPerTick) + (subTicks % SubTicksPerTick == 0 ? 0UL : 1UL);

        /// <summary>
        /// Gets how long <paramref name="byteCount"/> bytes take on the link, in millionths of a tick, rounded up: exact for
        /// any rate that divides evenly, and otherwise within a millionth of a tick for each frame.
        /// </summary>
        private ulong SerializationSubTicks(int byteCount)
        {
            if (conditions.BandwidthBytesPerSecond <= 0 || conditions.TicksPerSecond <= 0 || byteCount <= 0) return 0;
            var scaled = (ulong)byteCount * (ulong)conditions.TicksPerSecond;
            var rate = (ulong)conditions.BandwidthBytesPerSecond;
            var whole = scaled / rate;
            var rest = scaled % rate;
            return checked((whole * SubTicksPerTick) + (((rest * SubTicksPerTick) + rate - 1) / rate));
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
}
