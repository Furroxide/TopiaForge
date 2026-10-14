using System;
using System.Net;
using System.Net.Sockets;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// Maps UDP endpoints to the transport's opaque paths and back. A path key is a type byte (1, UDP), the 16-byte
    /// IPv6 form of the address (IPv4 is mapped), the port and the IPv6 scope id, both big-endian: 23 bytes that only
    /// this layer and the transport ever see. The scope keeps a link-local address on the interface it was heard on.
    /// Private, link-local and loopback addresses make LAN paths, which relay-only still allows; every other address
    /// makes a direct path.
    /// </summary>
    internal static class UdpPaths
    {
        internal const byte UdpKeyType = 1;
        internal const int KeySize = 23;

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
            var scope = (uint)address.ScopeId;
            key[19] = (byte)(scope >> 24);
            key[20] = (byte)(scope >> 16);
            key[21] = (byte)(scope >> 8);
            key[22] = (byte)scope;
            return new TransportPath(key, Classify(address));
        }

        /// <summary>Recovers the endpoint a UDP path names, scope and all; false for any other kind of path.</summary>
        internal static bool TryGetEndPoint(TransportPath path, out IPEndPoint endpoint)
        {
            endpoint = null!;
            if (path == null) throw new ArgumentNullException(nameof(path));
            var key = path.KeyBytes;
            if (key.Length != KeySize || key[0] != UdpKeyType) return false;
            var address = new byte[16];
            Buffer.BlockCopy(key, 1, address, 0, 16);
            var scope = ((uint)key[19] << 24) | ((uint)key[20] << 16) | ((uint)key[21] << 8) | key[22];
            endpoint = new IPEndPoint(new IPAddress(address, scope), (key[17] << 8) | key[18]);
            return true;
        }

        /// <summary>
        /// Names the host a UDP path reaches, without its port: the address and its scope, as opaque hexadecimal that
        /// never leaves the network layer. Null for any other kind of path.
        /// </summary>
        internal static string? SourceOf(TransportPath path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var key = path.KeyBytes;
            if (key.Length != KeySize || key[0] != UdpKeyType) return null;
            var source = new byte[20];
            Buffer.BlockCopy(key, 1, source, 0, 16);
            Buffer.BlockCopy(key, 19, source, 16, 4);
            return WireText.ToHex(source);
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
