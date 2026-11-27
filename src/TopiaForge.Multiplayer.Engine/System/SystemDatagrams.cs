using System;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// The datagrams of the system channels' realtime lane, on <see cref="EngineChannel.Realtime"/>:
    /// <c>[type 0x01][epoch varint][join generation varint][kind byte][payload bytes to the end]</c>. The epoch and the
    /// generation let a peer drop what belongs to a session it left or a connection it replaced, as for system messages;
    /// the payload is bounded by <see cref="InternalSystemLimits.MaximumDatagramBytes"/>. Reading never throws and never
    /// allocates: a datagram is untrusted, and one that does not parse is dropped and counted.
    /// </summary>
    internal static class SystemDatagrams
    {
        /// <summary>The only datagram type the lane carries so far; another is dropped, so a later version can add one.</summary>
        internal const byte SystemDatagram = 0x01;

        /// <summary>The longest header: the type, two ten-byte varints and the kind.</summary>
        internal const int MaximumHeaderBytes = 1 + 10 + 10 + 1;

        /// <summary>Writes a datagram into <paramref name="buffer"/> and returns its length.</summary>
        internal static int Write(byte[] buffer, ulong epoch, ulong generation, byte kind, ArraySegment<byte> payload)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (payload.Array == null) throw new ArgumentNullException(nameof(payload));
            if (buffer.Length < MaximumHeaderBytes + payload.Count) throw new ArgumentException("The datagram buffer is too small.", nameof(buffer));
            var length = 0;
            buffer[length++] = SystemDatagram;
            length = WriteVarUInt(buffer, length, epoch);
            length = WriteVarUInt(buffer, length, generation);
            buffer[length++] = kind;
            Buffer.BlockCopy(payload.Array, payload.Offset, buffer, length, payload.Count);
            return length + payload.Count;
        }

        /// <summary>
        /// Reads a datagram's header; on success <paramref name="payloadOffset"/> is where its payload starts, which runs to
        /// the end. False for another type, a malformed or non-minimal varint, or a payload past the bound.
        /// </summary>
        internal static bool TryReadHeader(byte[] datagram, out ulong epoch, out ulong generation, out byte kind, out int payloadOffset)
        {
            epoch = 0;
            generation = 0;
            kind = 0;
            payloadOffset = 0;
            if (datagram == null || datagram.Length < 4 || datagram[0] != SystemDatagram) return false;
            var position = 1;
            if (!TryReadVarUInt(datagram, ref position, out epoch) || !TryReadVarUInt(datagram, ref position, out generation)) return false;
            if (position >= datagram.Length) return false;
            kind = datagram[position++];
            if (datagram.Length - position > InternalSystemLimits.MaximumDatagramBytes) return false;
            payloadOffset = position;
            return true;
        }

        private static int WriteVarUInt(byte[] buffer, int position, ulong value)
        {
            while (value >= 0x80)
            {
                buffer[position++] = (byte)(value | 0x80);
                value >>= 7;
            }

            buffer[position++] = (byte)value;
            return position;
        }

        private static bool TryReadVarUInt(byte[] buffer, ref int position, out ulong value)
        {
            value = 0;
            var start = position;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (position >= buffer.Length) return false;
                var next = buffer[position++];
                if (shift == 63 && next > 1) return false;
                value |= (ulong)(next & 0x7F) << shift;
                if ((next & 0x80) != 0) continue;

                // A trailing zero group would give the value a second spelling.
                return next != 0 || position - start == 1;
            }

            return false;
        }
    }
}
