using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The <c>topiaforge://join/…</c> links this game hands the launcher with <c>Application.OpenURL</c> when a join needs
    /// mods installed first (WS6.7). A link to an invite or a listed server names no address. Only a direct link carries
    /// one, and only the one the player typed: as they typed the link itself, or their host and port pinned by the server
    /// id the check found there. A link opens the launcher's consent flow and never joins on its own.
    /// </summary>
    internal static class LauncherJoinLink
    {
        internal const string Prefix = "topiaforge://join/";
        internal const int MaximumLength = 512;
        internal const int DefaultPort = 27250;
        private const string InviteAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>Gets the link to a server listed by its id, or null for a malformed id.</summary>
        internal static string? ForServer(string? serverId) =>
            InternalText.IsServerId(serverId) ? Prefix + "server/" + serverId : null;

        /// <summary>Gets the link to an invite code, read as forgivingly as the launcher reads one, or null.</summary>
        internal static string? ForInvite(string? code)
        {
            var normalized = NormalizeInviteCode(code);
            return normalized == null ? null : Prefix + normalized;
        }

        /// <summary>
        /// Gets the link for what the player typed in CONNECT: a session link passes through as typed; a host and port
        /// becomes a direct link pinned by <paramref name="serverId"/>, the server a check found there; anything else, or a
        /// typed host without a known server, has no link.
        /// </summary>
        internal static string? ForTyped(string? typed, string? serverId)
        {
            if (typed == null) return null;
            var text = typed.Trim();
            if (IsSessionLink(text)) return text;
            if (NormalizeInviteCode(text) is { } code) return Prefix + code;
            if (!InternalText.IsServerId(serverId) || !TryParseHostPort(text, out var host, out var port)) return null;
            var link = Prefix + "direct/" + serverId + "/" + host + "/" + port.ToString(CultureInfo.InvariantCulture);
            return link.Length <= MaximumLength ? link : null;
        }

        /// <summary>Gets whether <paramref name="text"/> is a session link in the shared grammar.</summary>
        internal static bool IsSessionLink(string? text)
        {
            if (text == null || text.Length > MaximumLength || text.Length <= Prefix.Length) return false;
            foreach (var character in text)
            {
                if (character <= ' ' || character > '~' || character == '?' || character == '#' || character == '%') return false;
            }

            if (!string.Equals(text.Substring(0, 10), "topiaforge", StringComparison.OrdinalIgnoreCase) ||
                string.CompareOrdinal(text, 10, Prefix, 10, Prefix.Length - 10) != 0)
            {
                return false;
            }

            var segments = text.Substring(Prefix.Length).Split('/');
            if (segments.Length == 1) return NormalizeInviteCode(segments[0]) != null;
            if (segments.Length == 2) return segments[0] == "server" && InternalText.IsServerId(segments[1]);
            return segments.Length == 4 && segments[0] == "direct" && InternalText.IsServerId(segments[1]) &&
                   TryNormalizeHost(segments[2], out _) && TryParsePort(segments[3], out _);
        }

        /// <summary>
        /// Reads what a player pasted where an invite code goes: the code, read as <see cref="NormalizeInviteCode"/> reads one,
        /// or the invite's own <c>topiaforge://join/</c> link, with any spaces or line breaks around it ignored. The canonical
        /// code, or null for anything else, a server link included.
        /// </summary>
        internal static string? InviteCodeIn(string? pasted)
        {
            if (pasted == null) return null;
            var text = pasted.Trim();
            if (!IsSessionLink(text)) return NormalizeInviteCode(text);
            var rest = text.Substring(Prefix.Length);
            return rest.IndexOf('/') < 0 ? NormalizeInviteCode(rest) : null;
        }

        /// <summary>Reads an invite code: case, hyphens and spaces ignored, O as 0, I and L as 1; the canonical upper-case code, or null.</summary>
        internal static string? NormalizeInviteCode(string? input)
        {
            if (input == null || input.Length > 32) return null;
            var builder = new StringBuilder(10);
            foreach (var raw in input)
            {
                if (raw == '-' || raw == ' ') continue;
                var character = raw >= 'a' && raw <= 'z' ? (char)(raw - 32) : raw;
                if (character == 'O') character = '0';
                else if (character == 'I' || character == 'L') character = '1';
                if (builder.Length == 10 || InviteAlphabet.IndexOf(character) < 0) return null;
                builder.Append(character);
            }

            return builder.Length == 10 ? builder.ToString() : null;
        }

        /// <summary>Reads <c>host</c>, <c>host:port</c>, <c>[IPv6]:port</c> or a bare IPv6 address; the port defaults to 27250.</summary>
        internal static bool TryParseHostPort(string? text, out string host, out int port)
        {
            host = string.Empty;
            port = DefaultPort;
            if (string.IsNullOrEmpty(text) || text!.Length > 300) return false;
            string hostPart;
            if (text[0] == '[')
            {
                var close = text.IndexOf(']');
                if (close < 0) return false;
                hostPart = text.Substring(1, close - 1);
                var rest = text.Substring(close + 1);
                if (rest.Length > 0 && (rest[0] != ':' || !TryParsePort(rest.Substring(1), out port))) return false;
            }
            else if (text.IndexOf(':') != text.LastIndexOf(':'))
            {
                hostPart = text;
            }
            else
            {
                var colon = text.IndexOf(':');
                hostPart = colon < 0 ? text : text.Substring(0, colon);
                if (colon >= 0 && !TryParsePort(text.Substring(colon + 1), out port)) return false;
            }

            return TryNormalizeHost(hostPart, out host);
        }

        /// <summary>Normalizes a DNS name or an IP literal, refusing anything else; IPv6 literals lose zones.</summary>
        internal static bool TryNormalizeHost(string? text, out string host)
        {
            host = string.Empty;
            if (string.IsNullOrEmpty(text) || text!.Length > 253) return false;
            if (text.IndexOf(':') >= 0)
            {
                if (text.IndexOf('%') >= 0 || !IPAddress.TryParse(text, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6) return false;
                host = v6.ToString();
                return true;
            }

            var parts = text.Split('.');
            if (parts.Length == 4 && Array.TrueForAll(parts, part => part.Length > 0 && part.Length <= 3 && IsDigits(part)))
            {
                if (!IPAddress.TryParse(text, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork) return false;
                host = v4.ToString();
                return true;
            }

            foreach (var label in parts)
            {
                if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-') return false;
                foreach (var character in label)
                {
                    var allowed = (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') ||
                                  (character >= '0' && character <= '9') || character == '-';
                    if (!allowed) return false;
                }
            }

            host = text.ToLowerInvariant();
            return true;
        }

        private static bool TryParsePort(string text, out int port)
        {
            port = 0;
            return text.Length > 0 && text.Length <= 5 && IsDigits(text) &&
                   int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port >= 1 && port <= 65535;
        }

        private static bool IsDigits(string text)
        {
            foreach (var character in text)
            {
                if (character < '0' || character > '9') return false;
            }

            return true;
        }
    }
}
