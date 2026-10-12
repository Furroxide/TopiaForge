using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Transport.Recovery;

namespace TopiaForge.Multiplayer.Transport.Streams
{
    /// <summary>
    /// The sending half of the session stream. Each engine frame is a varint length and its bytes. Bytes stay buffered
    /// until acknowledged; lost ranges are queued to travel again in new packets.
    /// </summary>
    internal sealed class SessionSendStream
    {
        private const int ChunkSize = 16 * 1024;

        private readonly List<byte[]> chunks = new List<byte[]>();
        private readonly Stack<byte[]> spare = new Stack<byte[]>();
        private readonly List<StreamRange> retransmit = new List<StreamRange>();
        private readonly List<StreamRange> acknowledged = new List<StreamRange>();
        private readonly byte[] lengthPrefix = new byte[10];
        private ulong chunkBase;

        internal SessionSendStream(ulong peerMaximumOffset)
        {
            PeerMaximumOffset = peerMaximumOffset;
        }

        /// <summary>Gets the offset below which every byte is acknowledged.</summary>
        internal ulong AcknowledgedFloor { get; private set; }

        /// <summary>Gets the first byte never sent.</summary>
        internal ulong NextOffset { get; private set; }

        /// <summary>Gets the end of the bytes written so far.</summary>
        internal ulong WriteOffset { get; private set; }

        /// <summary>Gets the highest offset the receiver accepts.</summary>
        internal ulong PeerMaximumOffset { get; private set; }

        internal long Backlog => (long)(WriteOffset - AcknowledgedFloor);

        internal bool IsFullyAcknowledged => AcknowledgedFloor == WriteOffset;

        internal bool HasDataToSend => retransmit.Count > 0 || (NextOffset < WriteOffset && NextOffset < PeerMaximumOffset);

        /// <summary>Gets whether the window, not a lack of data, is what holds new bytes back.</summary>
        internal bool IsBlockedByWindow => retransmit.Count == 0 && NextOffset < WriteOffset && NextOffset >= PeerMaximumOffset;

        /// <summary>Appends one engine frame as its length and bytes.</summary>
        internal void AppendFrame(byte[] data, int offset, int count)
        {
            var prefix = 0;
            var value = (ulong)count;
            while (value >= 0x80)
            {
                lengthPrefix[prefix++] = (byte)(value | 0x80);
                value >>= 7;
            }

            lengthPrefix[prefix++] = (byte)value;
            Write(lengthPrefix, 0, prefix);
            Write(data, offset, count);
        }

        internal void OnWindow(ulong maximumOffset)
        {
            if (maximumOffset > PeerMaximumOffset) PeerMaximumOffset = maximumOffset;
        }

        /// <summary>
        /// Drops every buffered byte for good, as when the connection has ended and can never send them. The stream
        /// then has nothing to send and nothing owed.
        /// </summary>
        internal void Release()
        {
            chunks.Clear();
            spare.Clear();
            retransmit.Clear();
            acknowledged.Clear();
            AcknowledgedFloor = WriteOffset;
            NextOffset = WriteOffset;
            chunkBase = WriteOffset;
        }

        /// <summary>Picks the next range to send, lost bytes first, at most <paramref name="maximumLength"/> long.</summary>
        internal bool TryTake(int maximumLength, out StreamRange range)
        {
            range = default;
            if (maximumLength <= 0) return false;
            while (retransmit.Count > 0)
            {
                var lost = retransmit[0];
                retransmit.RemoveAt(0);
                lost = TrimAcknowledged(lost);
                if (lost.Length == 0) continue;
                if (lost.Length > maximumLength)
                {
                    AddMerged(retransmit, new StreamRange(lost.Offset + (ulong)maximumLength, lost.Length - maximumLength));
                    lost = new StreamRange(lost.Offset, maximumLength);
                }

                range = lost;
                return true;
            }

            var limit = Math.Min(WriteOffset, PeerMaximumOffset);
            if (NextOffset >= limit) return false;
            var length = (int)Math.Min((ulong)maximumLength, limit - NextOffset);
            range = new StreamRange(NextOffset, length);
            NextOffset += (ulong)length;
            return true;
        }

        internal void CopyTo(StreamRange range, byte[] destination, int destinationOffset)
        {
            var position = range.Offset;
            var remaining = range.Length;
            while (remaining > 0)
            {
                var relative = position - chunkBase;
                var chunk = chunks[(int)(relative / ChunkSize)];
                var within = (int)(relative % ChunkSize);
                var take = Math.Min(remaining, ChunkSize - within);
                Buffer.BlockCopy(chunk, within, destination, destinationOffset, take);
                destinationOffset += take;
                position += (ulong)take;
                remaining -= take;
            }
        }

        internal void OnAcknowledged(StreamRange range)
        {
            if (range.End <= AcknowledgedFloor) return;
            AddMerged(acknowledged, TrimBelowFloor(range));
            while (acknowledged.Count > 0 && acknowledged[0].Offset <= AcknowledgedFloor)
            {
                if (acknowledged[0].End > AcknowledgedFloor) AcknowledgedFloor = acknowledged[0].End;
                acknowledged.RemoveAt(0);
            }

            while (chunks.Count > 0 && AcknowledgedFloor >= chunkBase + ChunkSize)
            {
                if (spare.Count < 4) spare.Push(chunks[0]);
                chunks.RemoveAt(0);
                chunkBase += ChunkSize;
            }
        }

        internal void OnLost(StreamRange range)
        {
            range = TrimAcknowledged(range);
            if (range.Length > 0) AddMerged(retransmit, range);
        }

        /// <summary>
        /// Queues the oldest unacknowledged bytes to travel again, for a probe with nothing new to carry. They stay in
        /// flight too, so whichever copy is acknowledged first settles them.
        /// </summary>
        internal void QueueProbe(int maximumLength)
        {
            if (maximumLength <= 0 || retransmit.Count > 0 || NextOffset <= AcknowledgedFloor) return;
            var length = (int)Math.Min((ulong)maximumLength, NextOffset - AcknowledgedFloor);
            AddMerged(retransmit, new StreamRange(AcknowledgedFloor, length));
        }

        private void Write(byte[] data, int offset, int count)
        {
            while (count > 0)
            {
                var relative = WriteOffset - chunkBase;
                var index = (int)(relative / ChunkSize);
                if (index == chunks.Count) chunks.Add(spare.Count > 0 ? spare.Pop() : new byte[ChunkSize]);
                var within = (int)(relative % ChunkSize);
                var take = Math.Min(count, ChunkSize - within);
                Buffer.BlockCopy(data, offset, chunks[index], within, take);
                offset += take;
                count -= take;
                WriteOffset += (ulong)take;
            }
        }

        private StreamRange TrimBelowFloor(StreamRange range)
        {
            if (range.Offset >= AcknowledgedFloor) return range;
            return range.End <= AcknowledgedFloor ? new StreamRange(AcknowledgedFloor, 0) : new StreamRange(AcknowledgedFloor, (int)(range.End - AcknowledgedFloor));
        }

        /// <summary>Drops the parts of a lost range that were acknowledged meanwhile, keeping the first unacknowledged part.</summary>
        private StreamRange TrimAcknowledged(StreamRange range)
        {
            range = TrimBelowFloor(range);
            foreach (var done in acknowledged)
            {
                if (range.Length == 0 || done.Offset >= range.End) break;
                if (done.End <= range.Offset) continue;
                if (done.Offset <= range.Offset)
                {
                    if (done.End >= range.End) return new StreamRange(range.End, 0);
                    range = new StreamRange(done.End, (int)(range.End - done.End));
                    continue;
                }

                // An acknowledged hole inside the lost range: send the part before it now, requeue the rest.
                AddMerged(retransmit, new StreamRange(done.End, (int)(range.End > done.End ? range.End - done.End : 0)));
                return new StreamRange(range.Offset, (int)(done.Offset - range.Offset));
            }

            return range;
        }

        private static void AddMerged(List<StreamRange> ranges, StreamRange range)
        {
            if (range.Length <= 0) return;
            var start = range.Offset;
            var end = range.End;
            var index = 0;
            while (index < ranges.Count && ranges[index].End < start) index++;
            while (index < ranges.Count && ranges[index].Offset <= end)
            {
                start = Math.Min(start, ranges[index].Offset);
                end = Math.Max(end, ranges[index].End);
                ranges.RemoveAt(index);
            }

            ranges.Insert(index, new StreamRange(start, (int)(end - start)));
        }
    }
}
