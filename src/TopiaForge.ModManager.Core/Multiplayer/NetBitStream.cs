using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// Packs values into a fixed buffer least significant bit first: stream bit <c>i</c> is bit <c>i % 8</c> of byte
    /// <c>i / 8</c>, and a value's lowest bit is written first. Unused bits of the last byte stay zero. The writer is
    /// reusable and never allocates after construction.
    /// </summary>
    internal sealed class NetBitWriter
    {
        public const int MaximumCapacityBytes = 1 << 20;

        private readonly byte[] buffer;
        private int bitLength;

        public NetBitWriter(int capacityBytes)
        {
            if (capacityBytes < 1 || capacityBytes > MaximumCapacityBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(capacityBytes));
            }

            buffer = new byte[capacityBytes];
        }

        public int CapacityBits => buffer.Length * 8;

        public int BitLength => bitLength;

        public int ByteLength => (bitLength + 7) >> 3;

        public int RemainingBits => CapacityBits - bitLength;

        public void Reset()
        {
            Array.Clear(buffer, 0, ByteLength);
            bitLength = 0;
        }

        /// <summary>Writes the low <paramref name="bitCount"/> bits of <paramref name="value"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The count is outside 1 to 32, or the value does not fit.</exception>
        /// <exception cref="InvalidOperationException">The bits do not fit; nothing is written.</exception>
        public void WriteBits(uint value, int bitCount)
        {
            if (bitCount < 1 || bitCount > 32) throw new ArgumentOutOfRangeException(nameof(bitCount));
            if (bitCount < 32 && value >> bitCount != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The value does not fit in " + bitCount + " bits.");
            }

            if (bitCount > RemainingBits) throw new InvalidOperationException("The bit writer is full.");
            var position = bitLength;
            var remaining = bitCount;
            while (remaining > 0)
            {
                var offset = position & 7;
                var take = Math.Min(8 - offset, remaining);
                buffer[position >> 3] |= (byte)((value & ((1u << take) - 1u)) << offset);
                value >>= take;
                remaining -= take;
                position += take;
            }

            bitLength = position;
        }

        public void WriteBool(bool value) => WriteBits(value ? 1u : 0u, 1);

        public ArraySegment<byte> AsSegment() => new ArraySegment<byte>(buffer, 0, ByteLength);

        public byte[] ToArray()
        {
            var copy = new byte[ByteLength];
            Buffer.BlockCopy(buffer, 0, copy, 0, copy.Length);
            return copy;
        }
    }

    /// <summary>
    /// Reads what <see cref="NetBitWriter"/> wrote. Input is untrusted, so reads report failure instead of throwing,
    /// and a failed read consumes nothing.
    /// </summary>
    internal sealed class NetBitReader
    {
        private byte[] data = Array.Empty<byte>();
        private int offset;
        private int bitCount;
        private int position;

        public NetBitReader()
        {
        }

        public NetBitReader(ArraySegment<byte> segment)
        {
            Reset(segment);
        }

        public int Position => position;

        public int RemainingBits => bitCount - position;

        /// <summary>
        /// True when fewer than eight bits remain and all of them are zero, which is how a writer ends a record.
        /// </summary>
        public bool IsAtCanonicalEnd
        {
            get
            {
                var remaining = RemainingBits;
                if (remaining >= 8) return false;
                if (remaining == 0) return true;
                return data[offset + (position >> 3)] >> (position & 7) == 0;
            }
        }

        public void Reset(ArraySegment<byte> segment)
        {
            if (segment.Array == null) throw new ArgumentException("The segment has no array.", nameof(segment));
            if (segment.Count > NetBitWriter.MaximumCapacityBytes) throw new ArgumentOutOfRangeException(nameof(segment));
            data = segment.Array;
            offset = segment.Offset;
            bitCount = segment.Count * 8;
            position = 0;
        }

        /// <summary>Moves the read position, so a composite read can undo a partial read.</summary>
        public void Seek(int bitPosition)
        {
            if (bitPosition < 0 || bitPosition > bitCount) throw new ArgumentOutOfRangeException(nameof(bitPosition));
            position = bitPosition;
        }

        public bool TryReadBits(int count, out uint value)
        {
            if (count < 1 || count > 32) throw new ArgumentOutOfRangeException(nameof(count));
            value = 0;
            if (count > RemainingBits) return false;
            var shift = 0;
            var remaining = count;
            var cursor = position;
            while (remaining > 0)
            {
                var bitOffset = cursor & 7;
                var take = Math.Min(8 - bitOffset, remaining);
                var bits = ((uint)data[offset + (cursor >> 3)] >> bitOffset) & ((1u << take) - 1u);
                value |= bits << shift;
                shift += take;
                remaining -= take;
                cursor += take;
            }

            position = cursor;
            return true;
        }

        public bool TryReadBool(out bool value)
        {
            var read = TryReadBits(1, out var bit);
            value = bit != 0;
            return read;
        }
    }
}
