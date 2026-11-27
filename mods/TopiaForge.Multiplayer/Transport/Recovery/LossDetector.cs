using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Transport.Packets;

namespace TopiaForge.Multiplayer.Transport.Recovery
{
    /// <summary>A range of the session stream, carried by a STREAM frame.</summary>
    internal readonly struct StreamRange
    {
        internal StreamRange(ulong offset, int length)
        {
            Offset = offset;
            Length = length;
        }

        internal ulong Offset { get; }

        internal int Length { get; }

        internal ulong End => Offset + (ulong)Length;
    }

    /// <summary>What one sent packet carried that must be resent or released when it is acknowledged or lost.</summary>
    internal sealed class SentPacket
    {
        internal ulong Number { get; set; }

        internal long SentAt { get; set; }

        internal int Size { get; set; }

        internal bool AckEliciting { get; set; }

        internal bool CarriesWindow { get; set; }

        internal List<StreamRange> Streams { get; } = new List<StreamRange>(2);
    }

    /// <summary>
    /// The packets in flight and RFC 9002 loss detection: a packet is lost once a packet three numbers newer is
    /// acknowledged, or once it is older than 9/8 of the round trip; a probe timeout with exponential backoff covers
    /// the tail.
    /// </summary>
    internal sealed class LossDetector
    {
        internal const int PacketThreshold = 3;
        private const int MaximumBackoffShift = 10;

        private readonly List<SentPacket> inFlight = new List<SentPacket>();
        private readonly Stack<SentPacket> pool = new Stack<SentPacket>();
        private bool anyAcknowledged;

        internal ulong LargestAcknowledged { get; private set; }

        internal int ProbeCount { get; private set; }

        internal long LossTime { get; private set; } = long.MaxValue;

        internal long LastAckElicitingSentAt { get; private set; }

        internal int InFlightCount => inFlight.Count;

        internal bool HasAckElicitingInFlight
        {
            get
            {
                foreach (var packet in inFlight)
                {
                    if (packet.AckEliciting) return true;
                }

                return false;
            }
        }

        internal SentPacket Rent(ulong number, long now)
        {
            var packet = pool.Count > 0 ? pool.Pop() : new SentPacket();
            packet.Number = number;
            packet.SentAt = now;
            packet.Size = 0;
            packet.AckEliciting = false;
            packet.CarriesWindow = false;
            packet.Streams.Clear();
            return packet;
        }

        /// <summary>Hands back a packet that was rented but never sent.</summary>
        internal void Return(SentPacket packet) => Recycle(packet);

        /// <summary>Tracks a sent packet. Packets that ask for nothing are recycled at once.</summary>
        internal void OnSent(SentPacket packet)
        {
            if (!packet.AckEliciting)
            {
                Recycle(packet);
                return;
            }

            inFlight.Add(packet);
            LastAckElicitingSentAt = packet.SentAt;
        }

        /// <summary>Applies an ACK: acknowledged packets go to <paramref name="onAcknowledged"/>, newly lost ones to <paramref name="onLost"/>.</summary>
        internal void OnAck(
            List<PacketRange> ranges,
            long ackDelay,
            long now,
            RttEstimator rtt,
            long maximumAckDelay,
            Action<SentPacket> onAcknowledged,
            Action<SentPacket> onLost)
        {
            var largest = ranges[0].Largest;
            SentPacket? largestNewlyAcknowledged = null;
            var newlyAcknowledged = false;
            var ackElicitingAcknowledged = false;
            for (var index = 0; index < inFlight.Count; index++)
            {
                var packet = inFlight[index];
                if (!Covers(ranges, packet.Number)) continue;
                inFlight.RemoveAt(index--);
                newlyAcknowledged = true;
                ackElicitingAcknowledged |= packet.AckEliciting;
                if (packet.Number == largest) largestNewlyAcknowledged = packet;
                else
                {
                    onAcknowledged(packet);
                    Recycle(packet);
                }
            }

            if (!anyAcknowledged || largest > LargestAcknowledged)
            {
                LargestAcknowledged = largest;
                anyAcknowledged = true;
            }

            if (largestNewlyAcknowledged != null)
            {
                if (ackElicitingAcknowledged) rtt.OnSample(now - largestNewlyAcknowledged.SentAt, ackDelay, maximumAckDelay);
                onAcknowledged(largestNewlyAcknowledged);
                Recycle(largestNewlyAcknowledged);
            }

            if (newlyAcknowledged) ProbeCount = 0;
            DetectLost(now, rtt, onLost);
        }

        /// <summary>Gets when the loss or probe timer fires, or <see cref="long.MaxValue"/> when nothing is in flight.</summary>
        internal long TimerDeadline(RttEstimator rtt, long maximumAckDelay)
        {
            if (LossTime != long.MaxValue) return LossTime;
            if (!HasAckElicitingInFlight) return long.MaxValue;
            var timeout = rtt.ProbeTimeout(maximumAckDelay) << Math.Min(ProbeCount, MaximumBackoffShift);
            return LastAckElicitingSentAt + timeout;
        }

        /// <summary>Runs the loss or probe timer; returns true when the probe timeout fired and a probe should go out.</summary>
        internal bool OnTimer(long now, RttEstimator rtt, long maximumAckDelay, Action<SentPacket> onLost)
        {
            if (LossTime != long.MaxValue)
            {
                if (now >= LossTime) DetectLost(now, rtt, onLost);
                return false;
            }

            if (now < TimerDeadline(rtt, maximumAckDelay)) return false;
            ProbeCount++;
            return true;
        }

        /// <summary>Forgets everything in flight; the connection is closing for good.</summary>
        internal void Clear()
        {
            foreach (var packet in inFlight) Recycle(packet);
            inFlight.Clear();
            LossTime = long.MaxValue;
        }

        private void DetectLost(long now, RttEstimator rtt, Action<SentPacket> onLost)
        {
            LossTime = long.MaxValue;
            if (!anyAcknowledged) return;
            var lossDelay = rtt.LossDelay();
            for (var index = 0; index < inFlight.Count; index++)
            {
                var packet = inFlight[index];
                if (packet.Number > LargestAcknowledged) break;
                if (now - packet.SentAt >= lossDelay || LargestAcknowledged - packet.Number >= PacketThreshold)
                {
                    inFlight.RemoveAt(index--);
                    onLost(packet);
                    Recycle(packet);
                    continue;
                }

                LossTime = Math.Min(LossTime, packet.SentAt + lossDelay);
            }
        }

        private void Recycle(SentPacket packet)
        {
            if (pool.Count < 256) pool.Push(packet);
        }

        private static bool Covers(List<PacketRange> ranges, ulong number)
        {
            foreach (var range in ranges)
            {
                if (range.Contains(number)) return true;
                if (number > range.Largest) return false;
            }

            return false;
        }
    }
}
