using System;

namespace TopiaForge.Multiplayer.Transport.Packets
{
    /// <summary>
    /// The unauthenticated info query a browser sends before it joins, and the reply. The query fills a whole 1,200-byte
    /// datagram and the reply never exceeds it, so the exchange cannot amplify traffic toward a spoofed source. The
    /// reply carries the responder's protocol version, so a joiner learns about a mismatch before it connects.
    /// </summary>
    internal static class InfoPackets
    {
        internal const int QuerySize = TransportOptions.MaximumDatagramBytes;
        internal const int IdSize = 8;
        internal const int ReplyHeaderSize = 1 + 1 + IdSize + 2;

        /// <summary>Gets the most info a reply carries: 1,188 bytes.</summary>
        internal const int MaximumInfoBytes = TransportOptions.MaximumDatagramBytes - ReplyHeaderSize;

        internal static void WriteQuery(PacketWriter writer, ulong queryId)
        {
            writer.WriteByte(PacketType.InfoQuery);
            writer.WriteByte(HandshakePackets.Version);
            writer.WriteUInt64(queryId);
            writer.WriteZeros(writer.Remaining);
        }

        internal static bool TryReadQuery(byte[] data, int offset, int count, out byte version, out ulong queryId)
        {
            version = 0;
            queryId = 0;
            if (count != QuerySize || data[offset] != PacketType.InfoQuery) return false;
            version = data[offset + 1];
            queryId = PacketWriter.ReadUInt64(data, offset + 2);
            return true;
        }

        internal static void WriteReply(PacketWriter writer, ulong queryId, byte[] info)
        {
            if (info.Length > MaximumInfoBytes) throw new ArgumentException("Session info must fit one reply.", nameof(info));
            writer.WriteByte(PacketType.InfoReply);
            writer.WriteByte(HandshakePackets.Version);
            writer.WriteUInt64(queryId);
            writer.WriteByte((byte)info.Length);
            writer.WriteByte((byte)(info.Length >> 8));
            writer.WriteBytes(info, 0, info.Length);
        }

        internal static bool TryReadReply(byte[] data, int offset, int count, out byte version, out ulong queryId, out byte[] info)
        {
            version = 0;
            queryId = 0;
            info = Array.Empty<byte>();
            if (count < ReplyHeaderSize || data[offset] != PacketType.InfoReply) return false;
            var length = data[offset + 10] | (data[offset + 11] << 8);
            if (length != count - ReplyHeaderSize) return false;
            version = data[offset + 1];
            queryId = PacketWriter.ReadUInt64(data, offset + 2);
            info = new byte[length];
            Buffer.BlockCopy(data, offset + ReplyHeaderSize, info, 0, length);
            return true;
        }
    }
}
