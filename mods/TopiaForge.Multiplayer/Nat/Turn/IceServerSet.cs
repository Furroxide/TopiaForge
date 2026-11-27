using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.Stun;

namespace TopiaForge.Multiplayer.NatTraversal.Turn
{
    /// <summary>One STUN or TURN server as the control plane names it: a DNS name and a UDP port, never an address.</summary>
    internal readonly struct IceServerAddress : IEquatable<IceServerAddress>
    {
        internal const int DefaultPort = 3478;

        internal IceServerAddress(string host, int port)
        {
            Host = host;
            Port = port;
        }

        internal string Host { get; }

        internal int Port { get; }

        public bool Equals(IceServerAddress other) => string.Equals(Host, other.Host, StringComparison.Ordinal) && Port == other.Port;

        public override bool Equals(object? obj) => obj is IceServerAddress other && Equals(other);

        public override int GetHashCode() => (Host?.GetHashCode() ?? 0) ^ Port;

        public override string ToString() => "ICE server";
    }

    /// <summary>
    /// The relay credentials the control plane mints (<c>POST /v1/turn/credentials</c>): Cloudflare's
    /// <c>iceServers</c> shape plus a time to live. Only UDP servers are kept, since TURN over TCP and TLS is later
    /// work; servers must be DNS names, as the service promises. The credential never leaves this object except to
    /// derive a TURN key, and nothing prints it.
    /// </summary>
    internal sealed class IceServerSet
    {
        internal const int MaximumBytes = 16 * 1024;
        internal const int MaximumServers = 8;
        internal const int MaximumUrls = 16;
        internal const int MaximumCredentialLength = 512;

        private IceServerSet(IReadOnlyList<IceServerAddress> stun, IReadOnlyList<IceServerAddress> turn, string? username, string? credential, long ttlSeconds)
        {
            StunServers = stun;
            TurnServers = turn;
            Username = username;
            Credential = credential;
            TtlSeconds = ttlSeconds;
        }

        internal IReadOnlyList<IceServerAddress> StunServers { get; }

        /// <summary>Gets the UDP TURN servers that share <see cref="Username"/> and <see cref="Credential"/>.</summary>
        internal IReadOnlyList<IceServerAddress> TurnServers { get; }

        internal string? Username { get; }

        internal string? Credential { get; }

        /// <summary>Gets how long the credential lasts from when it was minted: the allocation ends with it.</summary>
        internal long TtlSeconds { get; }

        internal bool HasRelay => TurnServers.Count > 0 && Username != null && Credential != null;

        /// <summary>Builds a set directly, for the host and join flows and for tests.</summary>
        internal static IceServerSet Create(IReadOnlyList<IceServerAddress> stun, IReadOnlyList<IceServerAddress> turn, string? username, string? credential, long ttlSeconds)
        {
            if (stun == null) throw new ArgumentNullException(nameof(stun));
            if (turn == null) throw new ArgumentNullException(nameof(turn));
            if (turn.Count > 0 && (!StunCredentials.IsPrintableAscii(username, MaximumCredentialLength) || !StunCredentials.IsPrintableAscii(credential, MaximumCredentialLength)))
            {
                throw new ArgumentException("A TURN server needs a printable ASCII username and credential.", nameof(credential));
            }

            if (ttlSeconds < 1 || ttlSeconds > 86_400) throw new ArgumentOutOfRangeException(nameof(ttlSeconds));
            return new IceServerSet(stun, turn, turn.Count > 0 ? username : null, turn.Count > 0 ? credential : null, ttlSeconds);
        }

        /// <summary>Parses the service's answer; false when it is malformed, oversized or names a server by address.</summary>
        internal static bool TryParse(byte[]? body, out IceServerSet set)
        {
            set = null!;
            if (body == null || body.Length == 0 || body.Length > MaximumBytes) return false;
            if (!CanonicalJson.TryParse(body, MaximumBytes, out var node, out _)) return false;
            if (!(node is JsonObjectNode root) || !root.TryGet("iceServers", out var serversNode) || !(serversNode is JsonArrayNode servers)) return false;
            if (!root.TryGet("ttlSeconds", out var ttlNode) || !(ttlNode is JsonIntegerNode ttl) || ttl.Value < 1 || ttl.Value > 86_400) return false;
            if (servers.Items.Count == 0 || servers.Items.Count > MaximumServers) return false;
            var stun = new List<IceServerAddress>();
            var turn = new List<IceServerAddress>();
            string? username = null;
            string? credential = null;
            foreach (var item in servers.Items)
            {
                if (!(item is JsonObjectNode entry) || !entry.TryGet("urls", out var urlsNode) || !(urlsNode is JsonArrayNode urls)) return false;
                if (urls.Items.Count == 0 || urls.Items.Count > MaximumUrls) return false;
                var hasUser = entry.TryGet("username", out var userNode);
                var hasCredential = entry.TryGet("credential", out var credentialNode);
                if (hasUser != hasCredential) return false;
                if (hasUser)
                {
                    if (username != null) return false;
                    if (!(userNode is JsonStringNode user) || !(credentialNode is JsonStringNode secret)) return false;
                    if (!StunCredentials.IsPrintableAscii(user.Value, MaximumCredentialLength) || !StunCredentials.IsPrintableAscii(secret.Value, MaximumCredentialLength)) return false;
                    username = user.Value;
                    credential = secret.Value;
                }

                foreach (var urlNode in urls.Items)
                {
                    if (!(urlNode is JsonStringNode url) || !TryParseUrl(url.Value, out var scheme, out var address, out var udp)) return false;
                    if (!udp) continue;
                    if (scheme == "stun" && !hasUser) AddOnce(stun, address);
                    else if (scheme == "turn" && hasUser) AddOnce(turn, address);
                }
            }

            if (turn.Count == 0)
            {
                username = null;
                credential = null;
            }

            if (stun.Count == 0 && turn.Count == 0) return false;
            set = new IceServerSet(stun, turn, username, credential, ttl.Value);
            return true;
        }

        public override string ToString() => "ICE servers";

        /// <summary>
        /// Parses <c>stun:host[:port]</c> and <c>turn(s):host[:port][?transport=udp|tcp]</c>, as the service's own filter
        /// allows them: a lowercase DNS name of two or more labels, the last holding a letter. <paramref name="udp"/> is
        /// false for TLS and TCP relays, which this client does not use.
        /// </summary>
        internal static bool TryParseUrl(string? url, out string scheme, out IceServerAddress address, out bool udp)
        {
            scheme = string.Empty;
            address = default;
            udp = false;
            if (string.IsNullOrEmpty(url) || url!.Length > 300) return false;
            var colon = url.IndexOf(':');
            if (colon <= 0) return false;
            scheme = url.Substring(0, colon);
            if (scheme != "stun" && scheme != "turn" && scheme != "turns") return false;
            var rest = url.Substring(colon + 1);
            var transport = "udp";
            var query = rest.IndexOf('?');
            if (query >= 0)
            {
                var parameter = rest.Substring(query + 1);
                if (parameter != "transport=udp" && parameter != "transport=tcp") return false;
                transport = parameter.Substring("transport=".Length);
                rest = rest.Substring(0, query);
            }

            var port = IceServerAddress.DefaultPort;
            var portSeparator = rest.IndexOf(':');
            if (portSeparator >= 0)
            {
                var digits = rest.Substring(portSeparator + 1);
                if (digits.Length == 0 || digits.Length > 5) return false;
                foreach (var character in digits)
                {
                    if (character < '0' || character > '9') return false;
                }

                port = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
                if (port < 1 || port > 65535) return false;
                rest = rest.Substring(0, portSeparator);
            }

            if (!IsDnsName(rest)) return false;
            address = new IceServerAddress(rest, port);
            udp = scheme != "turns" && transport == "udp";
            return true;
        }

        private static bool IsDnsName(string host)
        {
            if (host.Length == 0 || host.Length > 253) return false;
            var labels = host.Split('.');
            if (labels.Length < 2) return false;
            foreach (var label in labels)
            {
                if (label.Length == 0 || label.Length > 63) return false;
                foreach (var character in label)
                {
                    if (!((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '-')) return false;
                }
            }

            // A name, never an address: the last label must hold a letter.
            foreach (var character in labels[labels.Length - 1])
            {
                if (character >= 'a' && character <= 'z') return true;
            }

            return false;
        }

        private static void AddOnce(List<IceServerAddress> list, IceServerAddress address)
        {
            if (!list.Contains(address) && list.Count < MaximumServers) list.Add(address);
        }
    }
}
