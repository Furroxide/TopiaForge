using System;
using System.Collections.Generic;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>How a player opens their session to others.</summary>
    internal sealed class HostSettings
    {
        internal const int DefaultPort = LanBeacon.DefaultGamePort;

        internal HostSettings(string name, int maxPlayers = 8, ServerVisibility visibility = ServerVisibility.Lan)
        {
            if (!WireText.IsDisplayText(name, ServerDescriptor.MaximumNameLength)) throw new ArgumentException("A server name is 1 to 48 printable characters.", nameof(name));
            if (maxPlayers < ServerDescriptor.MinimumPlayers || maxPlayers > ServerDescriptor.MaximumPlayers) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
            Name = name;
            MaxPlayers = maxPlayers;
            Visibility = visibility;
        }

        internal string Name { get; }
        internal int MaxPlayers { get; }
        internal ServerVisibility Visibility { get; }

        /// <summary>Gets or sets the game port: 27250 by default, or zero for any free port.</summary>
        internal int Port { get; set; } = DefaultPort;

        /// <summary>Gets or sets whether another port may be taken when the chosen one is busy.</summary>
        internal bool FallBackToAnyPort { get; set; } = true;

        /// <summary>
        /// Gets or sets the session password, or null for an open session. It is never stored. A password that breaks
        /// <see cref="PasswordProof.Rule"/> is refused here, before hosting starts.
        /// </summary>
        internal string? Password
        {
            get => password;
            set => password = PasswordSetting.Check(value);
        }

        /// <summary>
        /// Gets or sets whether players outside the LAN may connect directly, which shows them this host's address. The
        /// player must choose it for this session; relay-only is the default.
        /// </summary>
        internal bool AllowDirect { get; set; }

        /// <summary>
        /// Gets or sets whether a host that allows direct connections asks its router to forward the game port while it
        /// hosts through the TopiaForge service. Off by default; a relay-only host never maps anything.
        /// </summary>
        internal bool MapPorts { get; set; }

        internal bool Voice { get; set; }
        internal string? Gamemode { get; set; }
        internal string? World { get; set; }

        private string? password;
    }

    /// <summary>Checks a password a player sets for hosting or joining, so a bad one fails at once with the rule it breaks.</summary>
    internal static class PasswordSetting
    {
        internal static string? Check(string? value)
        {
            if (value != null && !PasswordProof.IsValidPassword(value)) throw new ArgumentException(PasswordProof.Rule, nameof(value));
            return value;
        }
    }

    internal enum JoinTargetKind
    {
        /// <summary>A host name or address and a port, typed by hand or carried by a direct link.</summary>
        Direct = 0,

        /// <summary>A session heard on the local network, named by its server id.</summary>
        Lan = 1,

        /// <summary>An invite code, which the TopiaForge service resolves.</summary>
        Invite = 2,

        /// <summary>A server the TopiaForge service lists, named by its server id.</summary>
        Listed = 3
    }

    /// <summary>Where a join goes. A direct target's address stays in the network layer; nothing above sees it.</summary>
    internal sealed class JoinTarget
    {
        private JoinTarget(JoinTargetKind kind, string? host, int port, string? serverId, string? inviteCode = null)
        {
            Kind = kind;
            Host = host;
            Port = port;
            ServerId = serverId;
            InviteCode = inviteCode;
        }

        internal JoinTargetKind Kind { get; }
        internal string? Host { get; }
        internal int Port { get; }

        /// <summary>Gets the server the target must be, when a link, listing or beacon named it.</summary>
        internal string? ServerId { get; }

        /// <summary>Gets an invite target's canonical code.</summary>
        internal string? InviteCode { get; }

        /// <summary>Gets whether only the TopiaForge service can find the target: an invite or a listed server.</summary>
        internal bool NeedsService => Kind == JoinTargetKind.Invite || Kind == JoinTargetKind.Listed;

        internal static JoinTarget Direct(string host, int port, string? serverId = null)
        {
            if (!SessionLink.TryNormalizeHost(host, out var normalized)) throw new ArgumentException("A host is a DNS name or an IP address.", nameof(host));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (serverId != null && !WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            return new JoinTarget(JoinTargetKind.Direct, normalized, port, serverId);
        }

        internal static JoinTarget Lan(string serverId)
        {
            if (!WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            return new JoinTarget(JoinTargetKind.Lan, null, 0, serverId);
        }

        /// <summary>
        /// An invite. <paramref name="serverId"/> pins the server it must lead to, as a session launch that verified it does;
        /// the code itself is never printed.
        /// </summary>
        internal static JoinTarget Invite(string code, string? serverId = null)
        {
            if (!Network.InviteCode.IsCanonical(code)) throw new ArgumentException("An invite code is ten canonical Crockford base32 characters.", nameof(code));
            if (serverId != null && !WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            return new JoinTarget(JoinTargetKind.Invite, null, 0, serverId, code);
        }

        internal static JoinTarget Listed(string serverId)
        {
            if (!WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            return new JoinTarget(JoinTargetKind.Listed, null, 0, serverId);
        }

        /// <summary>Never shows the address, or an invite's code.</summary>
        public override string ToString() =>
            Kind == JoinTargetKind.Lan ? "LAN session" : Kind == JoinTargetKind.Invite ? "invite" : Kind == JoinTargetKind.Listed ? "listed server" : "direct session";
    }

    /// <summary>How a player joins.</summary>
    internal sealed class JoinSettings
    {
        internal JoinSettings(string displayName)
        {
            if (!WireText.IsDisplayText(displayName, 24)) throw new ArgumentException("A display name is 1 to 24 printable characters.", nameof(displayName));
            DisplayName = displayName;
        }

        internal string DisplayName { get; }

        /// <summary>
        /// Gets or sets the password, when the player typed one in advance; otherwise the join asks for it. A password
        /// that breaks <see cref="PasswordProof.Rule"/> is refused here, before the join starts.
        /// </summary>
        internal string? Password
        {
            get => password;
            set => password = PasswordSetting.Check(value);
        }

        private string? password;

        /// <summary>
        /// Gets or sets whether a direct path outside the LAN may be used, which shows the host this player's address.
        /// The player chooses it for this session only.
        /// </summary>
        internal bool AllowDirect { get; set; }

        /// <summary>
        /// Gets or sets the one server the player accepted after a typed address served a different server than last
        /// time, or one it could not be checked against (<see cref="LiveJoin.ChangedServerId"/>). Only that server is
        /// trusted there; any other is refused again.
        /// </summary>
        internal string? TrustedServerId
        {
            get => trustedServerId;
            set
            {
                if (value != null && !WireText.IsHex(value, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(value));
                trustedServerId = value;
            }
        }

        private string? trustedServerId;
    }

    internal enum JoinPhase
    {
        Resolving = 0,
        Checking = 1,
        AwaitingPassword = 2,
        PreparingProof = 3,
        Connecting = 4,
        Synchronizing = 5,
        InSession = 6,
        Reconnecting = 7,
        Ended = 8
    }

    /// <summary>Why a join did not happen or a joined session ended. It never carries an address.</summary>
    internal enum LiveFailure
    {
        None = 0,
        Cancelled = 1,
        Left = 2,
        Unreachable = 3,
        TimedOut = 4,
        DescriptorInvalid = 5,
        HostIdentityChanged = 6,
        WrongGameBuild = 7,
        ProtocolMismatch = 8,
        ContentMismatch = 9,
        DirectNotAllowed = 10,
        HandshakeFailed = 11,
        Rejected = 12,
        Kicked = 13,
        Banned = 14,
        HostEnded = 15,
        ConnectionLost = 16,
        PortInUse = 17,
        NotFound = 18,

        /// <summary>This player's multiplayer identity could not be read or created.</summary>
        IdentityUnavailable = 19,

        /// <summary>The host's network stopped after too many failures in a row, ending every player's session.</summary>
        NetworkStopped = 20,

        /// <summary>The TopiaForge service could not be reached, failed, or answered in a way this game cannot read.</summary>
        ServiceUnavailable = 21,

        /// <summary>
        /// This game's packages could not be read, as when another program holds a file of one open, so hosting or the join
        /// stopped before anything connected. Every failure has the internal control's number for it.
        /// </summary>
        ContentUnreadable = 22,

        /// <summary>No TopiaForge service is configured; nothing was contacted.</summary>
        ServiceNotConfigured = 23,

        /// <summary>The service's operator turned hosting, invites or Internet joining off.</summary>
        ServiceDisabled = 24,

        /// <summary>The service turned this network's requests away for a while.</summary>
        ServiceRateLimited = 25,

        InviteNotFound = 26,

        ServerNotListed = 27,

        /// <summary>The relay is unavailable and the session keeps to it; nothing falls back to a direct path.</summary>
        RelayUnavailable = 28,

        UdpBlocked = 29,

        HostOffline = 30,

        HostNotAnswering = 31,

        NoRoute = 32,

        SignalingFailed = 33,

        /// <summary>The service stopped honouring the join ticket: its invite was withdrawn or expired, or the server stopped being public.</summary>
        InviteWithdrawn = 34,

        ServerBlocked = 35,

        /// <summary>The service refused a request this game made.</summary>
        ServiceRefused = 36,

        /// <summary>The hosted session holds as many live invites as the service allows.</summary>
        InviteLimit = 37,

        /// <summary>
        /// The record of the servers typed addresses served could not be read just now, as when another program holds it,
        /// so the server a typed address serves could not be checked; nothing was recorded. Trying again once the record
        /// reads goes on, and so does trusting that one server for the join while the record still cannot be read; once it
        /// reads, an address that served another server is <see cref="HostIdentityChanged"/> again.
        /// </summary>
        KnownHostsUnreadable = 38
    }

    /// <summary>What a content check found missing or different before a join, for the player to see.</summary>
    internal sealed class ContentCheck
    {
        internal static readonly ContentCheck Ready = new ContentCheck(Array.Empty<string>());

        internal ContentCheck(IReadOnlyList<string> problems, bool unreadable = false)
        {
            Problems = problems ?? throw new ArgumentNullException(nameof(problems));
            if (unreadable && problems.Count == 0) throw new ArgumentException("An unreadable check says why.", nameof(problems));
            IsUnreadable = unreadable;
        }

        /// <summary>Gets one line per package that is missing or differs; empty when the player can join.</summary>
        internal IReadOnlyList<string> Problems { get; }

        internal bool IsReady => Problems.Count == 0;

        /// <summary>Gets whether this game's own packages could not be read, so nothing could be compared.</summary>
        internal bool IsUnreadable { get; }
    }
}
