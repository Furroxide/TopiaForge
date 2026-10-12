using System;
using System.Net;
using System.Net.Sockets;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// Maps UDP endpoints to the transport's opaque paths and back. A path key is a type byte (1, UDP), the 16-byte
    /// IPv6 form of the address (IPv4 is mapped) and the port, big-endian: 19 bytes that only this layer and the
    /// transport ever see. Private, link-local and loopback addresses make LAN paths, which relay-only still allows;
    /// every other address makes a direct path.
    /// </summary>
    internal static class UdpPaths
    {
        internal const byte UdpKeyType = 1;
        internal const int KeySize = 19;

        internal static TransportPath For(IPEndPoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            var address = endpoint.Address.AddressFamily == AddressFamily.InterNetwork ? endpoint.Address.MapToIPv6() : endpoint.Address;
            if (address.AddressFamily != AddressFamily.InterNetworkV6) throw new ArgumentException("Only IP endpoints have UDP paths.", nameof(endpoint));
            var key = new byte[KeySize];
            key[0] = UdpKeyType;
            Buffer.BlockCopy(address.GetAddressBytes(), 0, key, 1, 16);
            key[17] = (byte)(endpoint.Port >> 8);
            key[18] = (byte)endpoint.Port;
            return new TransportPath(key, Classify(address));
        }

        /// <summary>Recovers the endpoint a UDP path names; false for any other kind of path.</summary>
        internal static bool TryGetEndPoint(TransportPath path, out IPEndPoint endpoint)
        {
            endpoint = null!;
            if (path == null) throw new ArgumentNullException(nameof(path));
            var key = path.KeyBytes;
            if (key.Length != KeySize || key[0] != UdpKeyType) return false;
            var address = new byte[16];
            Buffer.BlockCopy(key, 1, address, 0, 16);
            endpoint = new IPEndPoint(new IPAddress(address), (key[17] << 8) | key[18]);
            return true;
        }

        /// <summary>Whether <paramref name="address"/> is on the local network: private, link-local, unique local or loopback.</summary>
        internal static TransportPathKind Classify(IPAddress address)
        {
            if (address == null) throw new ArgumentNullException(nameof(address));
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var local = bytes[0] == 10 || bytes[0] == 127 ||
                            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                            (bytes[0] == 192 && bytes[1] == 168) ||
                            (bytes[0] == 169 && bytes[1] == 254);
                return local ? TransportPathKind.Lan : TransportPathKind.Direct;
            }

            var loopback = IPAddress.IPv6Loopback.Equals(address);
            var linkLocal = bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80;
            var uniqueLocal = (bytes[0] & 0xFE) == 0xFC;
            return loopback || linkLocal || uniqueLocal ? TransportPathKind.Lan : TransportPathKind.Direct;
        }
    }
}
