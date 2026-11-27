using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// A 10-character invite code in Crockford base32 (50 random bits), as the control plane issues them. Input is
    /// forgiving: case, hyphens and spaces are ignored, O reads as 0, and I and L read as 1. The canonical spelling is
    /// upper case without separators; people see it as <c>XXXXX-XXXXX</c>.
    /// </summary>
    internal static class InviteCode
    {
        internal const int Length = 10;
        internal const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        internal static bool TryNormalize(string? input, out string code)
        {
            code = string.Empty;
            if (input == null || input.Length > 32) return false;
            var builder = new StringBuilder(Length);
            foreach (var raw in input)
            {
                if (raw == '-' || raw == ' ') continue;

                // Only ASCII letters fold: char.ToUpperInvariant would turn the long s (U+017F) into S.
                var character = raw >= 'a' && raw <= 'z' ? (char)(raw - ('a' - 'A')) : raw;
                if (character == 'O') character = '0';
                else if (character == 'I' || character == 'L') character = '1';
                if (Alphabet.IndexOf(character) < 0 || builder.Length == Length) return false;
                builder.Append(character);
            }

            if (builder.Length != Length) return false;
            code = builder.ToString();
            return true;
        }

        internal static bool IsCanonical(string? code) => code != null && TryNormalize(code, out var normalized) && normalized == code;

        /// <summary>Formats a canonical code for people: two groups of five.</summary>
        internal static string Display(string code)
        {
            if (!IsCanonical(code)) throw new ArgumentException("A canonical invite code is required.", nameof(code));
            return code.Substring(0, 5) + "-" + code.Substring(5);
        }
    }

    internal enum SessionLinkKind
    {
        /// <summary><c>topiaforge://join/&lt;code&gt;</c>: resolved through the control plane.</summary>
        Invite = 0,

        /// <summary><c>topiaforge://join/server/&lt;server id&gt;</c>: a listed server, found through the control plane.</summary>
        Server = 1,

        /// <summary><c>topiaforge://join/direct/&lt;server id&gt;/&lt;host&gt;/&lt;port&gt;</c>: a direct address, pinned by the server id.</summary>
        Direct = 2
    }

    /// <summary>
    /// A <c>topiaforge://join/…</c> session link. The grammar is shared with the launcher and the control plane: no
    /// query, fragment or percent-encoding, at most 512 characters, the scheme in any case and everything else exact.
    /// A direct link carries the host's address, so it lives only in the network layer and the launcher; it never
    /// reaches mods, snapshots, logs or diagnostics. An invite link carries a code that lets anyone who holds it join.
    /// Only <see cref="Canonical"/> spells either; <see cref="ToString"/> names the link's kind alone, as a join target
    /// does, so a link that reaches a log line, an interpolation or an exception message shows neither.
    /// </summary>
    internal sealed class SessionLink
    {
        internal const string Scheme = "topiaforge";
        internal const int MaximumLength = 512;
        private const string Prefix = "topiaforge://join/";

        private SessionLink(SessionLinkKind kind, string? inviteCode, string? serverId, string? host, int port)
        {
            Kind = kind;
            InviteCode = inviteCode;
            ServerId = serverId;
            Host = host;
            Port = port;
        }

        internal SessionLinkKind Kind { get; }

        /// <summary>
        /// Gets an invite link's canonical code. Anyone who holds it may join, so it stays out of logs, diagnostics and
        /// anything mods can read.
        /// </summary>
        internal string? InviteCode { get; }

        internal string? ServerId { get; }

        /// <summary>Gets a direct link's host, a name or an address, which stays in the network layer.</summary>
        internal string? Host { get; }

        internal int Port { get; }

        internal static SessionLink ForInvite(string code)
        {
            if (!Network.InviteCode.TryNormalize(code, out var normalized)) throw new ArgumentException("An invite code is 10 Crockford base32 characters.", nameof(code));
            return new SessionLink(SessionLinkKind.Invite, normalized, null, null, 0);
        }

        internal static SessionLink ForServer(string serverId)
        {
            if (!WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            return new SessionLink(SessionLinkKind.Server, null, serverId, null, 0);
        }

        internal static SessionLink ForDirect(string serverId, string host, int port)
        {
            if (!WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            if (!TryNormalizeHost(host, out var normalizedHost)) throw new ArgumentException("A host is a DNS name or an IP address.", nameof(host));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            return new SessionLink(SessionLinkKind.Direct, null, serverId, normalizedHost, port);
        }

        internal static bool TryParse(string? text, out SessionLink? link)
        {
            link = null;
            if (text == null || text.Length > MaximumLength || text.Length <= Prefix.Length) return false;
            if (string.Compare(text, 0, Prefix, 0, Scheme.Length, StringComparison.OrdinalIgnoreCase) != 0 ||
                string.CompareOrdinal(text, Scheme.Length, Prefix, Scheme.Length, Prefix.Length - Scheme.Length) != 0)
            {
                return false;
            }

            foreach (var character in text)
            {
                if (character <= ' ' || character > '~' || character == '?' || character == '#' || character == '%') return false;
            }

            var segments = text.Substring(Prefix.Length).Split('/');
            try
            {
                if (segments.Length == 1)
                {
                    link = ForInvite(segments[0]);
                }
                else if (segments.Length == 2 && segments[0] == "server")
                {
                    link = ForServer(segments[1]);
                }
                else if (segments.Length == 4 && segments[0] == "direct" && TryParsePort(segments[3], out var port))
                {
                    link = ForDirect(segments[1], segments[2], port);
                }
            }
            catch (ArgumentException)
            {
                link = null;
            }

            return link != null;
        }

        /// <summary>
        /// Gets the canonical link, which <see cref="TryParse"/> reads back as this same link. It spells an invite's code
        /// and a direct link's host and port, so it goes only where the link itself must go, never into a log, a
        /// diagnostic, an exception message or anything mods can read.
        /// </summary>
        internal string Canonical
        {
            get
            {
                switch (Kind)
                {
                    case SessionLinkKind.Invite:
                        return Prefix + InviteCode;
                    case SessionLinkKind.Server:
                        return Prefix + "server/" + ServerId;
                    default:
                        return Prefix + "direct/" + ServerId + "/" + Host + "/" + Port.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        /// <summary>
        /// Names the link's kind alone, in a join target's words: <c>invite</c>, <c>listed server</c> or
        /// <c>direct session</c>. It never shows an invite's code or a direct link's host or port; <see cref="Canonical"/>
        /// spells the link.
        /// </summary>
        public override string ToString()
        {
            switch (Kind)
            {
                case SessionLinkKind.Invite:
                    return "invite";
                case SessionLinkKind.Server:
                    return "listed server";
                default:
                    return "direct session";
            }
        }

        /// <summary>
        /// Normalizes a host: an IPv4 address in dotted decimal, an IPv6 address in the RFC 5952 text form (written
        /// without brackets, since it fills a whole path segment), or a DNS name in lowercase. An IPv4-mapped IPv6
        /// address becomes the IPv4 address it maps.
        /// </summary>
        internal static bool TryNormalizeHost(string? host, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrEmpty(host) || host!.Length > 253) return false;
            if (host.IndexOf(':') >= 0)
            {
                foreach (var character in host)
                {
                    var hex = (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F');
                    if (!hex && character != ':' && character != '.') return false;
                }

                // An embedded IPv4 part follows the IPv4 rules, leading zeros refused: .NET would read 1.2.3.04 as 1.2.3.4.
                var tail = host.Substring(host.LastIndexOf(':') + 1);
                if (tail.IndexOf('.') >= 0 && !IsDottedQuad(tail)) return false;
                if (!IPAddress.TryParse(host, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6) return false;
                normalized = FormatIPv6(v6.GetAddressBytes());
                return true;
            }

            if (IsDottedQuad(host))
            {
                normalized = host;
                return true;
            }

            // Only ASCII letters fold: ToLowerInvariant would turn the Kelvin sign (U+212A) into k.
            var lowered = LowerAscii(host);
            var labels = lowered.Split('.');
            foreach (var label in labels)
            {
                if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-') return false;
                foreach (var character in label)
                {
                    if (!((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '-')) return false;
                }
            }

            // A name of digits and dots only would read as a malformed address, so it is refused rather than resolved;
            // labels may otherwise start with a digit (RFC 1123).
            var numeric = true;
            foreach (var character in host)
            {
                if (character != '.' && (character < '0' || character > '9')) numeric = false;
            }

            if (numeric) return false;
            normalized = lowered;
            return true;
        }

        private static string LowerAscii(string value)
        {
            var characters = value.ToCharArray();
            for (var index = 0; index < characters.Length; index++)
            {
                if (characters[index] >= 'A' && characters[index] <= 'Z') characters[index] = (char)(characters[index] + ('a' - 'A'));
            }

            return new string(characters);
        }

        /// <summary>Writes an IPv6 address as RFC 5952 recommends; an IPv4-mapped address becomes dotted decimal.</summary>
        internal static string FormatIPv6(byte[] address)
        {
            if (address == null || address.Length != 16) throw new ArgumentException("An IPv6 address is 16 bytes.", nameof(address));
            var mapped = address[10] == 0xFF && address[11] == 0xFF;
            for (var index = 0; mapped && index < 10; index++) mapped = address[index] == 0;
            if (mapped)
            {
                return string.Join(".", address[12].ToString(CultureInfo.InvariantCulture), address[13].ToString(CultureInfo.InvariantCulture),
                    address[14].ToString(CultureInfo.InvariantCulture), address[15].ToString(CultureInfo.InvariantCulture));
            }

            var groups = new int[8];
            for (var index = 0; index < 8; index++) groups[index] = (address[index * 2] << 8) | address[(index * 2) + 1];

            // The longest run of two or more zero groups is compressed; the first wins a tie.
            var bestStart = -1;
            var bestLength = 1;
            for (var index = 0; index < 8;)
            {
                if (groups[index] != 0)
                {
                    index++;
                    continue;
                }

                var start = index;
                while (index < 8 && groups[index] == 0) index++;
                if (index - start > bestLength)
                {
                    bestStart = start;
                    bestLength = index - start;
                }
            }

            var builder = new StringBuilder(39);
            for (var index = 0; index < 8; index++)
            {
                if (index == bestStart)
                {
                    builder.Append("::");
                    index += bestLength - 1;
                    continue;
                }

                if (builder.Length > 0 && builder[builder.Length - 1] != ':') builder.Append(':');
                builder.Append(groups[index].ToString("x", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

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

        private static bool TryParsePort(string text, out int port)
        {
            port = 0;
            if (text.Length == 0 || text.Length > 5 || (text.Length > 1 && text[0] == '0')) return false;
            foreach (var character in text)
            {
                if (character < '0' || character > '9') return false;
                port = (port * 10) + (character - '0');
            }

            return port >= 1 && port <= 65535;
        }
    }
}
