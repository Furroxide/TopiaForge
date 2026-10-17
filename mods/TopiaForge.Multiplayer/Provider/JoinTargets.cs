using System;
using System.Globalization;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// A join target as the internal control hands it out. What the player typed stays inside: the handle shows only its
    /// kind, a description without the address, and the server id a link or listing named.
    /// </summary>
    internal sealed class ProviderJoinTarget : IInternalJoinTarget
    {
        private ProviderJoinTarget(InternalJoinTargetKind kind, JoinTarget network, string description, string? serverId, string? inviteCode)
        {
            Kind = kind;
            NetworkTarget = network;
            Description = description;
            ServerId = serverId;
            InviteCode = inviteCode;
        }

        public InternalJoinTargetKind Kind { get; }

        public string Description { get; }

        public string? ServerId { get; }

        /// <summary>Gets the network layer's target; an invite or a listed server is one only the TopiaForge service resolves.</summary>
        internal JoinTarget NetworkTarget { get; }

        /// <summary>Gets the canonical invite code of an invite target.</summary>
        internal string? InviteCode { get; }

        internal static ProviderJoinTarget ForLan(string serverId) =>
            new ProviderJoinTarget(InternalJoinTargetKind.Lan, JoinTarget.Lan(serverId), "LAN session", serverId, null);

        /// <summary>
        /// Reads what the player typed or followed, ignoring surrounding spaces: a <c>topiaforge://join/</c> link, an invite
        /// code, or a host with an optional port (an IPv6 address in brackets when it has one). A port defaults to 27250.
        /// Ten characters that read as an invite code are one, unless they hold a dot or a colon.
        /// </summary>
        internal static bool TryParse(string? text, out ProviderJoinTarget? target)
        {
            target = null;
            if (text == null) return false;
            var value = text.Trim(' ', '\t');
            if (value.Length == 0 || value.Length > SessionLink.MaximumLength) return false;
            if (value.StartsWith(SessionLink.Scheme + ":", StringComparison.OrdinalIgnoreCase)) return TryFromLink(value, out target);
            if (value.IndexOf('.') < 0 && value.IndexOf(':') < 0 && Network.InviteCode.TryNormalize(value, out var code))
            {
                target = ForInvite(code);
                return true;
            }

            if (!TrySplitHost(value, out var host, out var port) || !SessionLink.TryNormalizeHost(host, out _)) return false;
            target = new ProviderJoinTarget(InternalJoinTargetKind.Direct, JoinTarget.Direct(host, port), "direct session", null, null);
            return true;
        }

        /// <summary>
        /// Never shows the address, nor an invite's code: anyone holding the code may join, so a logged target keeps it out.
        /// The interface shows the player the code they typed through <see cref="Description"/>.
        /// </summary>
        public override string ToString() => Kind == InternalJoinTargetKind.Invite ? "invite" : Description;

        private static bool TryFromLink(string value, out ProviderJoinTarget? target)
        {
            target = null;
            if (!SessionLink.TryParse(value, out var link)) return false;
            switch (link!.Kind)
            {
                case SessionLinkKind.Invite:
                    target = ForInvite(link.InviteCode!);
                    return true;
                case SessionLinkKind.Server:
                    target = new ProviderJoinTarget(InternalJoinTargetKind.ListedServer, JoinTarget.Listed(link.ServerId!), "listed server", link.ServerId, null);
                    return true;
                default:
                    target = new ProviderJoinTarget(InternalJoinTargetKind.Direct, JoinTarget.Direct(link.Host!, link.Port, link.ServerId), "direct session", link.ServerId, null);
                    return true;
            }
        }

        private static ProviderJoinTarget ForInvite(string code) =>
            new ProviderJoinTarget(InternalJoinTargetKind.Invite, JoinTarget.Invite(code), "invite " + Network.InviteCode.Display(code), null, code);

        private static bool TrySplitHost(string value, out string host, out int port)
        {
            host = value;
            port = HostSettings.DefaultPort;
            if (value[0] == '[')
            {
                var close = value.IndexOf(']');
                if (close < 2) return false;
                host = value.Substring(1, close - 1);
                if (close == value.Length - 1) return true;
                return value[close + 1] == ':' && TryReadPort(value.Substring(close + 2), out port);
            }

            var first = value.IndexOf(':');
            if (first < 0) return true;

            // Two colons or more without brackets is a bare IPv6 address, on the default port.
            if (value.IndexOf(':', first + 1) >= 0) return true;
            host = value.Substring(0, first);
            return host.Length > 0 && TryReadPort(value.Substring(first + 1), out port);
        }

        private static bool TryReadPort(string text, out int port)
        {
            port = 0;
            if (text.Length == 0 || text.Length > 5 || (text.Length > 1 && text[0] == '0')) return false;
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port >= 1 && port <= 65535;
        }
    }
}
