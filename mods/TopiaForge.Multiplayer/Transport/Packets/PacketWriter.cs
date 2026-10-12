using System;

namespace TopiaForge.Multiplayer.Transport.Packets
{
    /// <summary>
    /// Writes into one fixed buffer, such as a pooled datagram. Writing past the end is a bug in the caller's size
    /// arithmetic, so it throws rather than growing.
    /// </summary>
    internal sealed class PacketWriter
    {
        private byte[] buffer = Array.Empty<byte>();
        private int start;
        private int limit;

        internal int Position { get; private set; }

        internal int Remaining => limit - Position;

        internal int Written => Position - start;

        internal byte[] Buffer => buffer;

        internal void Reset(byte[] target, int offset, int count)
        {
            buffer = target ?? throw new ArgumentNullException(nameof(target));
            if (offset < 0 || count < 0 || offset > target.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            start = offset;
            Position = offset;
            limit = offset + count;
        }

        internal void WriteByte(byte value)
        {
            Require(1);
            buffer[Position++] = value;
        }

        internal void WriteUInt64(ulong value)
        {
            Require(8);
            for (var index = 0; index < 8; index++) buffer[Position++] = (byte)(value >> (8 * index));
        }

        internal void WriteVarUInt(ulong value)
        {
            Require(VarUIntSize(value));
            while (value >= 0x80)
            {
                buffer[Position++] = (byte)(value | 0x80);
                value >>= 7;
            }

            buffer[Position++] = (byte)value;
        }

        internal void WriteBytes(byte[] source, int offset, int count)
        {
            Require(count);
            System.Buffer.BlockCopy(source, offset, buffer, Position, count);
            Position += count;
        }

        internal void WriteZeros(int count)
        {
            Require(count);
            Array.Clear(buffer, Position, count);
            Position += count;
        }

        /// <summary>Moves past <paramref name="count"/> bytes the caller wrote into <see cref="Buffer"/> directly.</summary>
        internal void Advance(int count)
        {
            Require(count);
            Position += count;
        }

        internal static int VarUIntSize(ulong value)
        {
            var size = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                size++;
            }

            return size;
        }

        internal static ulong ReadUInt64(byte[] source, int offset)
        {
            ulong value = 0;
            for (var index = 0; index < 8; index++) value |= (ulong)source[offset + index] << (8 * index);
            return value;
        }

        private void Require(int count)
        {
            if (count > Remaining) throw new InvalidOperationException("A packet outgrew its datagram.");
        }
    }
}
