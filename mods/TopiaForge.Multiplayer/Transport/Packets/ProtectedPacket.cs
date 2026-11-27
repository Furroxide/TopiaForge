using TopiaForge.Multiplayer.Transport.Crypto;

namespace TopiaForge.Multiplayer.Transport.Packets
{
    /// <summary>
    /// Layout of a protected packet: <c>E8</c>, the receiver's connection id, the packet number (little-endian), then
    /// the frames sealed with ChaCha20-Poly1305. The 17 header bytes are the associated data and the packet number is
    /// the nonce.
    /// </summary>
    internal static class ProtectedPacket
    {
        internal const int HeaderSize = 1 + HandshakePackets.ConnectionIdSize + 8;
        internal const int Overhead = HeaderSize + CipherState.TagSize;

        /// <summary>Gets the most frame bytes one packet carries: 1,167.</summary>
        internal const int MaximumFramesBytes = TransportOptions.MaximumDatagramBytes - Overhead;

        /// <summary>Gets the smallest valid packet: a header, one frame byte and the tag.</summary>
        internal const int MinimumSize = Overhead + 1;

        /// <summary>Gets the largest DATAGRAM payload that fits an empty packet whatever its sequence: 1,153 bytes.</summary>
        internal const int MaximumDatagramPayload = MaximumFramesBytes - 2 - 10 - 2;

        internal static void WriteHeader(byte[] buffer, ulong receiverConnectionId, ulong packetNumber)
        {
            buffer[0] = PacketType.Protected;
            for (var index = 0; index < 8; index++)
            {
                buffer[1 + index] = (byte)(receiverConnectionId >> (8 * index));
                buffer[9 + index] = (byte)(packetNumber >> (8 * index));
            }
        }

        internal static ulong ReadConnectionId(byte[] data, int offset) => PacketWriter.ReadUInt64(data, offset + 1);

        internal static ulong ReadPacketNumber(byte[] data, int offset) => PacketWriter.ReadUInt64(data, offset + 9);
    }
}
