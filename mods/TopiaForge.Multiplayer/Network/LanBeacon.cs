using System;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// The announcement a LAN host broadcasts to UDP port 27251 every two seconds, from its game socket: <c>E6</c>, the
    /// beacon version, the 32-byte server id, and the game port (two bytes, little-endian). A browser that hears one
    /// sends an info query to the sender's address at that port, then fetches and checks the signed descriptor before
    /// trusting anything. Beacons are unauthenticated hints; the server id they carry is what pins the host.
    /// </summary>
    internal readonly struct LanBeacon
    {
        internal const byte PacketType = 0xE6;
        internal const byte Version = 1;
        internal const int Size = 36;
        internal const int DiscoveryPort = 27251;
        internal const int DefaultGamePort = 27250;
        internal const long IntervalMicros = 2_000_000;

        internal LanBeacon(string serverId, int gamePort)
        {
            if (!WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            if (gamePort < 1 || gamePort > 65535) throw new ArgumentOutOfRangeException(nameof(gamePort));
            ServerId = serverId;
            GamePort = gamePort;
        }

        internal string ServerId { get; }

        internal int GamePort { get; }

        internal byte[] Encode()
        {
            var datagram = new byte[Size];
            datagram[0] = PacketType;
            datagram[1] = Version;
            WireText.TryParseHex(ServerId, 32, out var serverId);
            Buffer.BlockCopy(serverId, 0, datagram, 2, 32);
            datagram[34] = (byte)GamePort;
            datagram[35] = (byte)(GamePort >> 8);
            return datagram;
        }

        /// <summary>Reads a beacon. Anything of another size, type or version is not one.</summary>
        internal static bool TryDecode(byte[] datagram, int offset, int count, out LanBeacon beacon)
        {
            beacon = default;
            if (datagram == null) throw new ArgumentNullException(nameof(datagram));
            if (offset < 0 || count < 0 || offset > datagram.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (count != Size || datagram[offset] != PacketType || datagram[offset + 1] != Version) return false;
            var serverId = new byte[32];
            Buffer.BlockCopy(datagram, offset + 2, serverId, 0, 32);
            var port = datagram[offset + 34] | (datagram[offset + 35] << 8);
            if (port == 0) return false;
            beacon = new LanBeacon(WireText.ToHex(serverId), port);
            return true;
        }
    }
}
