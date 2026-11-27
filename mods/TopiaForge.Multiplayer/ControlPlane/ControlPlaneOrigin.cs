using System;
using System.Globalization;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>
    /// The TopiaForge service's origin, the one place this client sends anything. It is read in the one spelling launch
    /// request V5 and the launcher's preferences carry: <c>https://</c>, a lowercase DNS name, and a port of 1 to 65535 in
    /// plain decimal, never the default 443, with no path, query, fragment or trailing slash. An address is refused, so
    /// none can pass as the service: an IP literal, and a name whose last label is a number, decimal or <c>0x</c>
    /// hexadecimal, which resolvers read as an IPv4 address. Plain http exists only through <see cref="Loopback"/>, for a
    /// control plane on this machine during development and tests. The origin names the project's own service, never a
    /// player, yet <see cref="ToString"/> still prints only what it is.
    /// </summary>
    internal sealed class ControlPlaneOrigin
    {
        internal const int MaximumLength = 256;
        private const string SecureScheme = "https://";

        private ControlPlaneOrigin(bool secure, string host, int port, string text)
        {
            IsSecure = secure;
            Host = host;
            Port = port;
            Text = text;
        }

        /// <summary>Gets whether requests go over TLS: always, except to a loopback control plane.</summary>
        internal bool IsSecure { get; }

        /// <summary>Gets the DNS name TLS verifies, or a loopback literal for a development control plane.</summary>
        internal string Host { get; }

        internal int Port { get; }

        /// <summary>Gets the origin as V5 spells it, such as <c>https://mp.example.org</c>.</summary>
        internal string Text { get; }

        /// <summary>Gets what a request's Host header names: the host, and the port when it is not the scheme's own.</summary>
        internal string Authority =>
            (Host.IndexOf(':') >= 0 ? "[" + Host + "]" : Host) +
            ((IsSecure && Port == 443) || (!IsSecure && Port == 80) ? string.Empty : ":" + Port.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Reads a service origin; false when it is anything but https, a lowercase DNS name and an optional port other
        /// than 443. Surrounding white space is never trimmed: V5 and the preferences already hold the exact spelling.
        /// </summary>
        internal static bool TryParse(string? text, out ControlPlaneOrigin origin)
        {
            origin = null!;
            if (text == null || text.Length <= SecureScheme.Length || text.Length > MaximumLength) return false;
            if (!text.StartsWith(SecureScheme, StringComparison.Ordinal)) return false;
            var authority = text.Substring(SecureScheme.Length);
            var colon = authority.IndexOf(':');
            var host = colon < 0 ? authority : authority.Substring(0, colon);
            var port = 443;
            if (colon >= 0)
            {
                var digits = authority.Substring(colon + 1);
                if (!TryReadPort(digits, out port) || port == 443) return false;
            }

            if (!IsServiceName(host)) return false;
            origin = new ControlPlaneOrigin(true, host, port, text);
            return true;
        }

        /// <summary>A control plane on this machine over plain http, as <c>wrangler dev</c> serves one and the tests do.</summary>
        internal static ControlPlaneOrigin Loopback(int port, bool ipv6 = false)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            var host = ipv6 ? "::1" : "127.0.0.1";
            var text = "http://" + (ipv6 ? "[::1]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);
            return new ControlPlaneOrigin(false, host, port, text);
        }

        /// <summary>Never shows the origin; it is configuration, and logs have no use for it.</summary>
        public override string ToString() => "TopiaForge service";

        /// <summary>
        /// Whether <paramref name="host"/> is a DNS name in its canonical spelling that no resolver reads as an address: the
        /// link grammar's lowercase name, never an IPv6 or dotted-quad literal, and never one whose last label is a number.
        /// </summary>
        private static bool IsServiceName(string host)
        {
            if (host.Length == 0 || host.IndexOf(':') >= 0) return false;
            if (!SessionLink.TryNormalizeHost(host, out var normalized) || !string.Equals(normalized, host, StringComparison.Ordinal)) return false;
            var last = host.Substring(host.LastIndexOf('.') + 1);
            var hex = last.StartsWith("0x", StringComparison.Ordinal);
            var numeric = last.Length > (hex ? 2 : 0);
            foreach (var character in hex ? last.Substring(2) : last)
            {
                if ((character < '0' || character > '9') && !(hex && character >= 'a' && character <= 'f')) numeric = false;
            }

            return !numeric;
        }

        private static bool TryReadPort(string digits, out int port)
        {
            port = 0;
            if (digits.Length == 0 || digits.Length > 5 || digits[0] == '0') return false;
            foreach (var character in digits)
            {
                if (character < '0' || character > '9') return false;
                port = (port * 10) + (character - '0');
            }

            return port >= 1 && port <= 65535;
        }
    }
}
