using System;
using System.Collections.Generic;

namespace TopiaForge.Multiplayer.Transport.Streams
{
    internal enum StreamAcceptResult
    {
        Accepted = 0,

        /// <summary>The bytes reach past the window this side advertised.</summary>
        FlowControlViolation = 1,

        /// <summary>A frame length exceeds the largest frame the engine sends.</summary>
        FrameTooLarge = 2,

        /// <summary>A frame length is not a minimal varint of at most five bytes.</summary>
        MalformedLength = 3
    }

    /// <summary>
    /// The receiving half of the session stream. Bytes are kept as they arrive: in-order bytes in chunks, out-of-order
    /// ranges until the gap before them fills. Each frame's length is checked as soon as it is in, but a frame's bytes
    /// are copied out only when the reader takes it, and nothing is reserved ahead of the bytes themselves. The memory
    /// held is therefore exactly what <see cref="BufferedBytes"/> reports, within a chunk at each end, and the window
    /// bounds it: consumed offset plus 2 MiB.
    /// </summary>
    internal sealed class SessionReceiveStream
    {
        private const int ChunkSize = 16 * 1024;
        private const int MaximumLengthBytes = 5;

        private readonly List<Segment> pending = new List<Segment>();
        private readonly List<byte[]> chunks = new List<byte[]>();
        private readonly Queue<FrameSpan> frames = new Queue<FrameSpan>();
        private readonly int window;
        private readonly int maximumFrameBytes;
        private ulong chunkBase;
        private ulong consumed;
        private ulong nextFrame;
        private long pendingBytes;

        internal SessionReceiveStream(int window, int maximumFrameBytes)
        {
            this.window = window;
            this.maximumFrameBytes = maximumFrameBytes;
            AdvertisedMaximum = (ulong)window;
        }

        /// <summary>Gets the end of the bytes received in order.</summary>
        internal ulong Delivered { get; private set; }

        /// <summary>Gets the highest offset this side accepts.</summary>
        internal ulong AdvertisedMaximum { get; private set; }

        internal int FrameCount => frames.Count;

        /// <summary>Gets the bytes this stream holds: out of order, or in order and not yet read.</summary>
        internal long BufferedBytes => pendingBytes + (long)(Delivered - consumed);

        /// <summary>Gets whether enough was consumed that a larger window is worth advertising.</summary>
        internal bool IsWindowUpdateDue => consumed + (ulong)window - AdvertisedMaximum >= (ulong)(window / 4);

        /// <summary>
        /// Returns the most <paramref name="data"/> can add to <see cref="BufferedBytes"/>: its bytes past the delivered
        /// end. Bytes already held out of order may make the true growth smaller, never larger.
        /// </summary>
        internal long NewBytes(ulong offset, byte[] data)
        {
            var end = offset + (ulong)data.Length;
            return end <= Delivered ? 0 : (long)(end - Math.Max(offset, Delivered));
        }

        internal StreamAcceptResult Accept(ulong offset, byte[] data)
        {
            var end = offset + (ulong)data.Length;
            if (end > AdvertisedMaximum) return StreamAcceptResult.FlowControlViolation;
            if (end <= Delivered || data.Length == 0) return StreamAcceptResult.Accepted;
            var start = Math.Max(offset, Delivered);
            var skip = (int)(start - offset);
            if (start != Delivered)
            {
                Store(start, data, skip);
                return StreamAcceptResult.Accepted;
            }

            Append(data, skip, data.Length - skip);
            DrainPending();
            return ParseFrames();
        }

        internal bool TryDequeue(out byte[] frame)
        {
            if (frames.Count == 0)
            {
                frame = Array.Empty<byte>();
                return false;
            }

            var span = frames.Dequeue();
            frame = new byte[span.Length];
            CopyOut(span.BodyStart, frame);
            consumed = span.BodyStart + (ulong)span.Length;
            while (chunks.Count > 0 && consumed >= chunkBase + ChunkSize)
            {
                chunks.RemoveAt(0);
                chunkBase += ChunkSize;
            }

            return true;
        }

        /// <summary>Raises the advertised window to the consumed offset plus the window size and returns it.</summary>
        internal ulong NextWindow()
        {
            AdvertisedMaximum = consumed + (ulong)window;
            return AdvertisedMaximum;
        }

        /// <summary>Records every frame whose length and bytes have both arrived, checking each length as it lands.</summary>
        private StreamAcceptResult ParseFrames()
        {
            while (nextFrame < Delivered)
            {
                ulong length = 0;
                var lengthBytes = 0;
                while (true)
                {
                    if (lengthBytes == MaximumLengthBytes) return StreamAcceptResult.MalformedLength;
                    var position = nextFrame + (ulong)lengthBytes;
                    if (position >= Delivered) return StreamAcceptResult.Accepted;
                    var next = ByteAt(position);
                    if (lengthBytes > 0 && next == 0) return StreamAcceptResult.MalformedLength;
                    length |= (ulong)(next & 0x7F) << (7 * lengthBytes);
                    lengthBytes++;
                    if ((next & 0x80) == 0) break;
                }

                if (length > (ulong)maximumFrameBytes) return StreamAcceptResult.FrameTooLarge;
                var bodyStart = nextFrame + (ulong)lengthBytes;
                if (bodyStart + length > Delivered) return StreamAcceptResult.Accepted;
                frames.Enqueue(new FrameSpan(bodyStart, (int)length));
                nextFrame = bodyStart + length;
            }

            return StreamAcceptResult.Accepted;
        }

        private void DrainPending()
        {
            while (pending.Count > 0 && pending[0].Offset <= Delivered)
            {
                var segment = pending[0];
                pending.RemoveAt(0);
                pendingBytes -= segment.Data.Length;
                if (segment.End <= Delivered) continue;
                var skip = (int)(Delivered - segment.Offset);
                Append(segment.Data, skip, segment.Data.Length - skip);
            }
        }

        /// <summary>Keeps an out-of-order range, storing only the bytes no kept range already covers.</summary>
        private void Store(ulong offset, byte[] data, int skip)
        {
            var start = offset;
            var end = offset + (ulong)(data.Length - skip);
            var index = 0;
            while (index < pending.Count && pending[index].End <= start) index++;
            while (start < end)
            {
                if (index < pending.Count && pending[index].Offset <= start)
                {
                    start = Math.Max(start, pending[index].End);
                    index++;
                    continue;
                }

                var pieceEnd = index < pending.Count ? Math.Min(end, pending[index].Offset) : end;
                var piece = new byte[(int)(pieceEnd - start)];
                Buffer.BlockCopy(data, skip + (int)(start - offset), piece, 0, piece.Length);
                pending.Insert(index, new Segment(start, piece));
                pendingBytes += piece.Length;
                index++;
                start = pieceEnd;
            }
        }

        private void Append(byte[] data, int offset, int count)
        {
            while (count > 0)
            {
                var relative = Delivered - chunkBase;
                var index = (int)(relative / ChunkSize);
                if (index == chunks.Count) chunks.Add(new byte[ChunkSize]);
                var within = (int)(relative % ChunkSize);
                var take = Math.Min(count, ChunkSize - within);
                Buffer.BlockCopy(data, offset, chunks[index], within, take);
                offset += take;
                count -= take;
                Delivered += (ulong)take;
            }
        }

        private byte ByteAt(ulong position)
        {
            var relative = position - chunkBase;
            return chunks[(int)(relative / ChunkSize)][(int)(relative % ChunkSize)];
        }

        private void CopyOut(ulong position, byte[] destination)
        {
            var copied = 0;
            while (copied < destination.Length)
            {
                var relative = position - chunkBase;
                var within = (int)(relative % ChunkSize);
                var take = Math.Min(destination.Length - copied, ChunkSize - within);
                Buffer.BlockCopy(chunks[(int)(relative / ChunkSize)], within, destination, copied, take);
                copied += take;
                position += (ulong)take;
            }
        }

        private readonly struct Segment
        {
            internal Segment(ulong offset, byte[] data)
            {
                Offset = offset;
                Data = data;
            }

            internal ulong Offset { get; }

            internal byte[] Data { get; }

            internal ulong End => Offset + (ulong)Data.Length;
        }

        private readonly struct FrameSpan
        {
            internal FrameSpan(ulong bodyStart, int length)
            {
                BodyStart = bodyStart;
                Length = length;
            }

            internal ulong BodyStart { get; }

            internal int Length { get; }
        }
    }
}
