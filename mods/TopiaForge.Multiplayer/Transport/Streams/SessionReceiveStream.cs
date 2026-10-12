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
        FrameTooLarge = 2
    }

    /// <summary>
    /// The receiving half of the session stream. It puts ranges back in order, cuts the stream into engine frames, and
    /// advertises a window of the bytes consumed so far plus 2 MiB, so undelivered and unread bytes never exceed it.
    /// </summary>
    internal sealed class SessionReceiveStream
    {
        private readonly List<Segment> pending = new List<Segment>();
        private readonly Queue<byte[]> frames = new Queue<byte[]>();
        private readonly Queue<int> footprints = new Queue<int>();
        private readonly int window;
        private readonly int maximumFrameBytes;
        private ulong consumed;
        private long pendingBytes;
        private ulong lengthValue;
        private int lengthShift;
        private int headerBytes;
        private byte[]? body;
        private int bodyFilled;

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

        /// <summary>Gets the bytes this stream holds: out of order, partly parsed, or parsed but unread.</summary>
        internal long BufferedBytes => pendingBytes + (long)(Delivered - consumed) + (body == null ? 0 : body.Length - bodyFilled);

        /// <summary>Gets whether enough was consumed that a larger window is worth advertising.</summary>
        internal bool IsWindowUpdateDue => consumed + (ulong)window - AdvertisedMaximum >= (ulong)(window / 4);

        /// <summary>Returns the bytes <paramref name="data"/> would add to the buffer, so the endpoint can refuse them.</summary>
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
            if (start == Delivered)
            {
                if (!Feed(data, skip, data.Length - skip)) return StreamAcceptResult.FrameTooLarge;
                return DrainPending();
            }

            Store(start, data, skip);
            return StreamAcceptResult.Accepted;
        }

        internal bool TryDequeue(out byte[] frame)
        {
            if (frames.Count == 0)
            {
                frame = Array.Empty<byte>();
                return false;
            }

            frame = frames.Dequeue();
            consumed += (ulong)footprints.Dequeue();
            return true;
        }

        /// <summary>Raises the advertised window to the consumed offset plus the window size and returns it.</summary>
        internal ulong NextWindow()
        {
            AdvertisedMaximum = consumed + (ulong)window;
            return AdvertisedMaximum;
        }

        private StreamAcceptResult DrainPending()
        {
            while (pending.Count > 0 && pending[0].Offset <= Delivered)
            {
                var segment = pending[0];
                pending.RemoveAt(0);
                pendingBytes -= segment.Data.Length;
                var end = segment.Offset + (ulong)segment.Data.Length;
                if (end <= Delivered) continue;
                var skip = (int)(Delivered - segment.Offset);
                if (!Feed(segment.Data, skip, segment.Data.Length - skip)) return StreamAcceptResult.FrameTooLarge;
            }

            return StreamAcceptResult.Accepted;
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

        /// <summary>Parses in-order bytes into frames: a varint length, then that many bytes.</summary>
        private bool Feed(byte[] data, int offset, int count)
        {
            var position = offset;
            var end = offset + count;
            while (position < end)
            {
                if (body == null)
                {
                    var next = data[position++];
                    headerBytes++;
                    if (lengthShift > 28) return false;
                    lengthValue |= (ulong)(next & 0x7F) << lengthShift;
                    lengthShift += 7;
                    if ((next & 0x80) != 0) continue;
                    if (lengthValue > (ulong)maximumFrameBytes) return false;
                    body = new byte[(int)lengthValue];
                    bodyFilled = 0;
                    if (body.Length == 0) CompleteFrame();
                    continue;
                }

                var take = Math.Min(end - position, body.Length - bodyFilled);
                Buffer.BlockCopy(data, position, body, bodyFilled, take);
                bodyFilled += take;
                position += take;
                if (bodyFilled == body.Length) CompleteFrame();
            }

            Delivered += (ulong)count;
            return true;
        }

        private void CompleteFrame()
        {
            frames.Enqueue(body!);
            footprints.Enqueue(headerBytes + body!.Length);
            body = null;
            bodyFilled = 0;
            lengthValue = 0;
            lengthShift = 0;
            headerBytes = 0;
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
    }
}
