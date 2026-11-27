using System.Collections.Generic;
using TopiaForge.Multiplayer.Transport.Packets;

namespace TopiaForge.Multiplayer.Transport.Recovery
{
    /// <summary>
    /// The packet numbers a connection has received, as at most 32 ranges from newest to oldest, and when they must
    /// be acknowledged: after two ack-eliciting packets, or within the maximum ack delay of the first.
    /// </summary>
    internal sealed class AckTracker
    {
        internal const int MaximumRanges = 32;

        private readonly List<PacketRange> ranges = new List<PacketRange>();
        private int ackElicitingSinceAck;
        private long firstUnacknowledgedAt = -1;
        private long largestReceivedAt;
        private bool changedSinceAck;

        internal IReadOnlyList<PacketRange> Ranges => ranges;

        /// <summary>Gets whether received packets exist that the last ACK did not cover.</summary>
        internal bool HasNewPackets => changedSinceAck;

        internal void OnPacketReceived(ulong number, bool ackEliciting, long now)
        {
            Insert(number);
            if (ranges[0].Largest == number) largestReceivedAt = now;
            changedSinceAck = true;
            if (!ackEliciting) return;
            ackElicitingSinceAck++;
            if (firstUnacknowledgedAt < 0) firstUnacknowledgedAt = now;
        }

        internal bool IsAckDue(long now, long maximumAckDelay) =>
            ackElicitingSinceAck >= 2 || (ackElicitingSinceAck > 0 && now - firstUnacknowledgedAt >= maximumAckDelay);

        /// <summary>
        /// Gets when an acknowledgement falls due, or <see cref="long.MaxValue"/> when nothing waits. Two waiting
        /// packets make it due at once.
        /// </summary>
        internal long AckDeadline(long maximumAckDelay)
        {
            if (ackElicitingSinceAck == 0) return long.MaxValue;
            return ackElicitingSinceAck >= 2 ? firstUnacknowledgedAt : firstUnacknowledgedAt + maximumAckDelay;
        }

        /// <summary>Gets the delay to report: how long the newest packet has waited.</summary>
        internal ulong AckDelay(long now) => now > largestReceivedAt ? (ulong)(now - largestReceivedAt) : 0UL;

        /// <summary>Records that an ACK covering every range has just been written.</summary>
        internal void OnAckSent()
        {
            ackElicitingSinceAck = 0;
            firstUnacknowledgedAt = -1;
            changedSinceAck = false;
        }

        private void Insert(ulong number)
        {
            for (var index = 0; index < ranges.Count; index++)
            {
                var range = ranges[index];
                if (range.Contains(number)) return;
                if (number > range.Largest)
                {
                    if (number == range.Largest + 1)
                    {
                        ranges[index] = new PacketRange(range.Smallest, number);
                        MergeAbove(index);
                    }
                    else
                    {
                        ranges.Insert(index, new PacketRange(number, number));
                        MergeAbove(index);
                    }

                    Trim();
                    return;
                }

                if (number + 1 == range.Smallest)
                {
                    ranges[index] = new PacketRange(number, range.Largest);
                    MergeBelow(index);
                    return;
                }
            }

            ranges.Add(new PacketRange(number, number));
            Trim();
        }

        /// <summary>Joins the range at <paramref name="index"/> with the newer one before it when they touch.</summary>
        private void MergeAbove(int index)
        {
            if (index == 0) return;
            var newer = ranges[index - 1];
            var current = ranges[index];
            if (current.Largest + 1 < newer.Smallest) return;
            ranges[index - 1] = new PacketRange(current.Smallest, newer.Largest);
            ranges.RemoveAt(index);
        }

        /// <summary>Joins the range at <paramref name="index"/> with the older one after it when they touch.</summary>
        private void MergeBelow(int index)
        {
            if (index + 1 >= ranges.Count) return;
            var older = ranges[index + 1];
            var current = ranges[index];
            if (older.Largest + 1 < current.Smallest) return;
            ranges[index] = new PacketRange(older.Smallest, current.Largest);
            ranges.RemoveAt(index + 1);
        }

        private void Trim()
        {
            while (ranges.Count > MaximumRanges) ranges.RemoveAt(ranges.Count - 1);
        }
    }
}
