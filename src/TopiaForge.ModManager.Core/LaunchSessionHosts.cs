using System;
using System.Collections.Generic;
using System.Text;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// The hosts a direct session target may name, in the one spelling the launcher writes: dotted-decimal IPv4, RFC 5952
    /// IPv6 without brackets, or a lowercase DNS name; and whether reaching one stays on the local network. The same
    /// rules as the launcher's <c>NetworkHost</c> and the provider's session links. A host is an address, so nothing here
    /// ever repeats one in a message.
    /// </summary>
    internal static class LaunchSessionHosts
    {
        internal const int MaximumLength = 253;

        /// <summary>Normalizes <paramref name="host"/>, or returns null when it is not an IP address or a DNS name.</summary>
        internal static string? TryNormalize(string? host)
        {
            if (string.IsNullOrEmpty(host) || host!.Length > MaximumLength) return null;
            if (host.IndexOf(':') >= 0)
            {
                foreach (var character in host)
                {
                    if (HexDigit(character) < 0 && character != ':' && character != '.') return null;
                }

                var address = TryParseIPv6(host);
                return address == null ? null : FormatIPv6(address);
            }

            return IsDottedQuad(host) ? host : TryNormalizeName(host);
        }

        /// <summary>
        /// Whether contacting the canonical <paramref name="host"/> stays on the local network: a private, link-local,
        /// unique-local or loopback IP literal, or a name that resolves without asking anyone beyond it (<c>localhost</c>,
        /// <c>*.localhost</c>, <c>*.local</c>). Carrier-grade NAT space is not local.
        /// </summary>
        internal static bool IsLocalNetwork(string host)
        {
            if (host.IndexOf(':') >= 0)
            {
                var address = TryParseIPv6(host);
                return address != null && IsLocalAddress(address);
            }

            if (IsDottedQuad(host))
            {
                var parts = host.Split('.');
                var bytes = new byte[4];
                for (var index = 0; index < 4; index++) bytes[index] = byte.Parse(parts[index], System.Globalization.CultureInfo.InvariantCulture);
                return IsLocalAddress(bytes);
            }

            return host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal);
        }

        private static bool IsLocalAddress(byte[] address)
        {
            if (address.Length == 16)
            {
                var mapped = address[10] == 0xFF && address[11] == 0xFF;
                for (var index = 0; mapped && index < 10; index++) mapped = address[index] == 0;
                if (mapped) address = new[] { address[12], address[13], address[14], address[15] };
            }

            if (address.Length == 4)
            {
                return address[0] == 10 || address[0] == 127 || (address[0] == 172 && address[1] >= 16 && address[1] <= 31)
                    || (address[0] == 192 && address[1] == 168) || (address[0] == 169 && address[1] == 254);
            }

            var loopback = address[15] == 1;
            for (var index = 0; loopback && index < 15; index++) loopback = address[index] == 0;
            var linkLocal = address[0] == 0xFE && (address[1] & 0xC0) == 0x80;
            var uniqueLocal = (address[0] & 0xFE) == 0xFC;
            return loopback || linkLocal || uniqueLocal;
        }

        /// <summary>RFC 5952: lowercase, no leading zeros, the longest run of two or more zero groups compressed.</summary>
        private static string FormatIPv6(byte[] address)
        {
            var mapped = address[10] == 0xFF && address[11] == 0xFF;
            for (var index = 0; mapped && index < 10; index++) mapped = address[index] == 0;
            if (mapped)
            {
                var invariant = System.Globalization.CultureInfo.InvariantCulture;
                return address[12].ToString(invariant) + "." + address[13].ToString(invariant) + "." + address[14].ToString(invariant) + "."
                    + address[15].ToString(invariant);
            }

            var groups = new int[8];
            for (var index = 0; index < 8; index++) groups[index] = (address[index * 2] << 8) | address[(index * 2) + 1];
            var bestStart = -1;
            var bestLength = 1;
            for (var index = 0; index < 8;)
            {
                if (groups[index] != 0) { index++; continue; }
                var start = index;
                while (index < 8 && groups[index] == 0) index++;
                if (index - start > bestLength) { bestStart = start; bestLength = index - start; }
            }

            var text = new StringBuilder();
            for (var index = 0; index < 8; index++)
            {
                if (index == bestStart) { text.Append("::"); index += bestLength - 1; continue; }
                if (text.Length > 0 && text[text.Length - 1] != ':') text.Append(':');
                text.Append(groups[index].ToString("x", System.Globalization.CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        /// <summary>RFC 4291 text: hexadecimal groups, at most one <c>::</c>, and an optional dotted-decimal tail.</summary>
        private static byte[]? TryParseIPv6(string text)
        {
            var gap = text.IndexOf("::", StringComparison.Ordinal);
            if (gap >= 0 && text.IndexOf("::", gap + 1, StringComparison.Ordinal) >= 0) return null;
            var head = gap < 0 ? text : text.Substring(0, gap);
            var tail = gap < 0 ? string.Empty : text.Substring(gap + 2);
            var headGroups = Groups(head, last: gap < 0);
            var tailGroups = Groups(tail, last: true);
            if (headGroups == null || tailGroups == null) return null;
            var explicitCount = headGroups.Count + tailGroups.Count;
            if (gap < 0 ? explicitCount != 8 : explicitCount > 7) return null;
            var groups = new List<int>(headGroups);
            for (var index = explicitCount; index < 8; index++) groups.Add(0);
            groups.AddRange(tailGroups);
            var address = new byte[16];
            for (var index = 0; index < 8; index++)
            {
                address[index * 2] = (byte)(groups[index] >> 8);
                address[(index * 2) + 1] = (byte)(groups[index] & 0xFF);
            }

            return address;
        }

        private static List<int>? Groups(string text, bool last)
        {
            var groups = new List<int>();
            if (text.Length == 0) return groups;
            var parts = text.Split(':');
            for (var index = 0; index < parts.Length; index++)
            {
                var part = parts[index];
                if (part.IndexOf('.') >= 0)
                {
                    if (!last || index != parts.Length - 1 || !IsDottedQuad(part)) return null;
                    var octets = part.Split('.');
                    var values = new int[4];
                    for (var octet = 0; octet < 4; octet++) values[octet] = int.Parse(octets[octet], System.Globalization.CultureInfo.InvariantCulture);
                    groups.Add((values[0] << 8) | values[1]);
                    groups.Add((values[2] << 8) | values[3]);
                    continue;
                }

                if (part.Length == 0 || part.Length > 4) return null;
                var value = 0;
                foreach (var character in part)
                {
                    var nibble = HexDigit(character);
                    if (nibble < 0) return null;
                    value = (value << 4) | nibble;
                }

                groups.Add(value);
            }

            return groups;
        }

        /// <summary>Four parts of one to three decimal digits, each at most 255, with no leading zeros.</summary>
        private static bool IsDottedQuad(string host)
        {
            var parts = host.Split('.');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 3 || (part.Length > 1 && part[0] == '0')) return false;
                var value = 0;
                foreach (var character in part)
                {
                    if (character < '0' || character > '9') return false;
                    value = (value * 10) + (character - '0');
                }

                if (value > 255) return false;
            }

            return true;
        }

        /// <summary>A DNS name of letter-digit-hyphen labels, lowercased; digits and dots alone would read as an address.</summary>
        private static string? TryNormalizeName(string host)
        {
            var name = new StringBuilder(host.Length);
            var onlyDigitsAndDots = true;
            foreach (var original in host)
            {
                var character = original >= 'A' && original <= 'Z' ? (char)(original + 32) : original;
                var letter = character >= 'a' && character <= 'z';
                var digit = character >= '0' && character <= '9';
                if (!letter && !digit && character != '-' && character != '.') return null;
                if (!digit && character != '.') onlyDigitsAndDots = false;
                name.Append(character);
            }

            if (onlyDigitsAndDots) return null;
            var text = name.ToString();
            foreach (var label in text.Split('.'))
            {
                if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-') return null;
            }

            return text;
        }

        private static int HexDigit(char character)
        {
            if (character >= '0' && character <= '9') return character - '0';
            if (character >= 'a' && character <= 'f') return character - 'a' + 10;
            if (character >= 'A' && character <= 'F') return character - 'A' + 10;
            return -1;
        }
    }
}
