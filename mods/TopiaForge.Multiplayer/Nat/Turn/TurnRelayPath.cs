using System;
using System.Net;
using System.Net.Sockets;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.Turn
{
    /// <summary>
    /// How a peer reached through this side's own TURN allocation appears to the transport: a
    /// <see cref="TransportPathKind.Relayed"/> path, which relay-only allows. Its key is a type byte (2, relay), the
    /// allocation's number (4 bytes), the peer's address as the relay sees it in IPv6 form (16) and its port (2), all
    /// big-endian: 23 bytes that only the NAT layer and the transport ever see. UDP paths use type 1 (<c>UdpPaths</c>),
    /// so the two can never be confused. The allocation number keeps a peer reached through two allocations, as during
    /// a credential rotation, on two different paths.
    /// </summary>
    internal static class TurnRelayPath
    {
        internal const byte KeyType = 2;
        internal const int KeySize = 23;

        internal static TransportPath For(int allocation, IPEndPoint peer)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var address = peer.Address.AddressFamily == AddressFamily.InterNetwork ? peer.Address.MapToIPv6() : peer.Address;
            if (address.AddressFamily != AddressFamily.InterNetworkV6) throw new ArgumentException("Only IP peers have relayed paths.", nameof(peer));
            var key = new byte[KeySize];
            key[0] = KeyType;
            key[1] = (byte)(allocation >> 24);
            key[2] = (byte)(allocation >> 16);
            key[3] = (byte)(allocation >> 8);
            key[4] = (byte)allocation;
            Buffer.BlockCopy(address.GetAddressBytes(), 0, key, 5, 16);
            key[21] = (byte)(peer.Port >> 8);
            key[22] = (byte)peer.Port;
            return new TransportPath(key, TransportPathKind.Relayed);
        }

        internal static bool IsRelayed(TransportPath? path)
        {
            if (path == null) return false;
            var key = path.KeyBytes;
            return key.Length == KeySize && key[0] == KeyType;
        }

        /// <summary>Gets the allocation a relayed path goes through; false for any other kind of path.</summary>
        internal static bool TryGetAllocation(TransportPath? path, out int allocation)
        {
            allocation = 0;
            if (!IsRelayed(path)) return false;
            var key = path!.KeyBytes;
            allocation = (key[1] << 24) | (key[2] << 16) | (key[3] << 8) | key[4];
            return true;
        }

        /// <summary>Recovers the allocation and the peer a relayed path names; false for any other kind of path.</summary>
        internal static bool TryParse(TransportPath? path, out int allocation, out IPEndPoint peer)
        {
            peer = null!;
            if (!TryGetAllocation(path, out allocation)) return false;
            var key = path!.KeyBytes;
            var address = new byte[16];
            Buffer.BlockCopy(key, 5, address, 0, 16);
            var port = (key[21] << 8) | key[22];
            var ip = new IPAddress(address);
            peer = new IPEndPoint(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, port);
            return true;
        }
    }

    /// <summary>
    /// TURN ChannelData framing (RFC 8656, section 12.4): a channel number and a length, two bytes each, then the data.
    /// Channel numbers are 0x4000 to 0x4FFF, the range RFC 8656 allows, so a ChannelData datagram always starts with a
    /// byte from 64 to 79, the band RFC 7983 assigns it on a shared socket.
    /// </summary>
    internal static class ChannelData
    {
        internal const int HeaderSize = 4;
        internal const ushort FirstChannel = 0x4000;
        internal const ushort LastChannel = 0x4FFF;
        internal const int ChannelCount = LastChannel - FirstChannel + 1;

        internal static bool IsChannel(int channel) => channel >= FirstChannel && channel <= LastChannel;

        /// <summary>
        /// Reads a ChannelData header. The length must fit the datagram; over UDP up to three bytes of padding may follow
        /// it, and anything longer is refused.
        /// </summary>
        internal static bool TryRead(byte[] buffer, int offset, int count, out ushort channel, out int dataOffset, out int dataLength)
        {
            channel = 0;
            dataOffset = 0;
            dataLength = 0;
            if (buffer == null || offset < 0 || count < HeaderSize || offset > buffer.Length - count) return false;
            var number = (buffer[offset] << 8) | buffer[offset + 1];
            var length = (buffer[offset + 2] << 8) | buffer[offset + 3];
            if (!IsChannel(number) || length > count - HeaderSize || count - HeaderSize - length > 3) return false;
            channel = (ushort)number;
            dataOffset = offset + HeaderSize;
            dataLength = length;
            return true;
        }

        /// <summary>Writes a ChannelData message, without padding as UDP allows; returns its length.</summary>
        internal static int Write(byte[] output, ushort channel, byte[] data, int offset, int count)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (!IsChannel(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
            if (count < 0 || count > 0xFFFF || offset < 0 || offset > data.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (output.Length < HeaderSize + count) throw new ArgumentException("The output cannot hold the message.", nameof(output));
            output[0] = (byte)(channel >> 8);
            output[1] = (byte)channel;
            output[2] = (byte)(count >> 8);
            output[3] = (byte)count;
            Buffer.BlockCopy(data, offset, output, HeaderSize, count);
            return HeaderSize + count;
        }
    }
}
