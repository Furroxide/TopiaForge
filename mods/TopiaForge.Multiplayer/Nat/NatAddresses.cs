using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>
    /// The address rules every candidate and server address passes. Addresses live only inside the network and NAT
    /// layers; nothing here formats one for anything but the signaling wire, which the privacy rules gate.
    /// </summary>
    internal static class NatAddresses
    {
        /// <summary>Returns the plain IPv4 form of an IPv4-mapped address, and any other address unchanged.</summary>
        internal static IPAddress Unmap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        /// <summary>
        /// Gets whether a peer or server may name <paramref name="address"/> at all: never unspecified, loopback,
        /// multicast, broadcast, the IPv4 "this network" or reserved blocks, or an unscoped IPv6 link-local address.
        /// </summary>
        internal static bool IsUsable(IPAddress? address)
        {
            if (address == null) return false;
            address = Unmap(address);
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork) return bytes[0] != 0 && bytes[0] != 127 && bytes[0] < 224;
            if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (IPAddress.IPv6Any.Equals(address) || IPAddress.IPv6Loopback.Equals(address)) return false;
            if (bytes[0] == 0xFF) return false;
            return !(bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }

        /// <summary>Gets whether <paramref name="address"/> is reachable from the Internet: usable, and not private or shared space.</summary>
        internal static bool IsPublic(IPAddress? address)
        {
            if (!IsUsable(address)) return false;
            var plain = Unmap(address!);
            var bytes = plain.GetAddressBytes();
            if (plain.AddressFamily == AddressFamily.InterNetwork)
            {
                var privateRange = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168);
                var shared = bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
                var linkLocal = bytes[0] == 169 && bytes[1] == 254;
                return !privateRange && !shared && !linkLocal;
            }

            return (bytes[0] & 0xFE) != 0xFC;
        }

        /// <summary>Writes the canonical text the control plane expects: dotted IPv4, RFC 5952 IPv6, a mapped address as IPv4.</summary>
        internal static string Format(IPAddress address)
        {
            if (address == null) throw new ArgumentNullException(nameof(address));
            var plain = Unmap(address);
            if (plain.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = plain.GetAddressBytes();
                return bytes[0].ToString(CultureInfo.InvariantCulture) + "." + bytes[1].ToString(CultureInfo.InvariantCulture) + "." +
                       bytes[2].ToString(CultureInfo.InvariantCulture) + "." + bytes[3].ToString(CultureInfo.InvariantCulture);
            }

            return SessionLink.FormatIPv6(plain.GetAddressBytes());
        }

        /// <summary>Parses an address only in its canonical text form, so every address has one spelling on the wire.</summary>
        internal static bool TryParseCanonical(string? text, out IPAddress address)
        {
            address = IPAddress.None;
            if (string.IsNullOrEmpty(text) || text!.Length > 45) return false;
            foreach (var character in text)
            {
                var allowed = (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || character == '.' || character == ':';
                if (!allowed) return false;
            }

            if (!IPAddress.TryParse(text, out var parsed)) return false;
            if (parsed.AddressFamily != AddressFamily.InterNetwork && parsed.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (parsed.AddressFamily == AddressFamily.InterNetworkV6 && parsed.ScopeId != 0) return false;
            if (!string.Equals(Format(parsed), text, StringComparison.Ordinal)) return false;
            address = Unmap(parsed);
            return true;
        }

        /// <summary>Gets whether two endpoints name the same address and port, mapped and plain IPv4 alike.</summary>
        internal static bool SameEndPoint(IPEndPoint left, IPEndPoint right) =>
            left.Port == right.Port && Unmap(left.Address).Equals(Unmap(right.Address));

        /// <summary>Gets whether two addresses are the same, mapped and plain IPv4 alike.</summary>
        internal static bool SameAddress(IPAddress left, IPAddress right) => Unmap(left).Equals(Unmap(right));
    }
}
