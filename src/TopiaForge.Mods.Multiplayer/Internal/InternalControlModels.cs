using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Internal
{
    /// <summary>How a hosted session can be found.</summary>
    internal enum InternalSessionVisibility
    {
        /// <summary>Listed in the Internet browser and announced on the local network.</summary>
        Public = 0,

        /// <summary>Reachable only through an invite.</summary>
        Invite = 1,

        /// <summary>Announced on the local network only.</summary>
        Lan = 2
    }

    /// <summary>How a player opens their session to others.</summary>
    internal sealed class InternalHostSettings
    {
        internal const int MinimumPlayers = 2;
        internal const int MaximumPlayers = 16;
        internal const int MaximumNameLength = 48;

        private string? gamemode;
        private string? world;

        internal InternalHostSettings(string name, int maxPlayers = 8, InternalSessionVisibility visibility = InternalSessionVisibility.Lan)
        {
            if (!InternalText.IsDisplayText(name, MaximumNameLength)) throw new ArgumentException("A server name is 1 to 48 printable characters.", nameof(name));
            if (maxPlayers < MinimumPlayers || maxPlayers > MaximumPlayers) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
            if (!Enum.IsDefined(typeof(InternalSessionVisibility), visibility)) throw new ArgumentOutOfRangeException(nameof(visibility));
            Name = name;
            MaxPlayers = maxPlayers;
            Visibility = visibility;
        }

        internal string Name { get; }

        internal int MaxPlayers { get; }

        internal InternalSessionVisibility Visibility { get; }

        /// <summary>Gets or sets the session password, or null for an open session. It is never stored.</summary>
        internal string? Password { get; set; }

        /// <summary>
        /// Gets or sets whether players beyond the local network may connect directly, which shows them this host's
        /// address. The player chooses it for this session; relay-only is the default.
        /// </summary>
        internal bool AllowDirect { get; set; }

        /// <summary>
        /// Gets or sets whether a host that allows direct connections asks its router to forward the game port, through
        /// NAT-PMP or UPnP, while it hosts through the TopiaForge service. Off by default, and ignored for a relay-only host.
        /// </summary>
        internal bool RouterForwarding { get; set; }

        /// <summary>
        /// Gets or sets whether the session offers voice chat. It holds for the whole hosting run, as the descriptor is
        /// signed with it once: a session that offers none carries no voice at all, the host's own player's included, and
        /// every joining player learns so before it could send. Hosting again applies the setting given then.
        /// </summary>
        internal bool Voice { get; set; }

        /// <summary>Gets or sets the gamemode's display name, shown to browsing players.</summary>
        internal string? Gamemode
        {
            get => gamemode;
            set => gamemode = InternalText.RequireOptionalDisplayText(value, MaximumNameLength, nameof(Gamemode));
        }

        /// <summary>Gets or sets the world's display name, shown to browsing players.</summary>
        internal string? World
        {
            get => world;
            set => world = InternalText.RequireOptionalDisplayText(value, MaximumNameLength, nameof(World));
        }
    }

    /// <summary>How a player joins.</summary>
    internal sealed class InternalJoinSettings
    {
        internal const int MaximumDisplayNameLength = 24;

        private string? displayName;
        private string? trustedServerId;

        /// <summary>Gets or sets the name to join as; null uses the runtime's display name.</summary>
        internal string? DisplayName
        {
            get => displayName;
            set => displayName = InternalText.RequireOptionalDisplayText(value, MaximumDisplayNameLength, nameof(DisplayName));
        }

        /// <summary>Gets or sets the password, when the player typed one in advance; otherwise the join asks for it.</summary>
        internal string? Password { get; set; }

        /// <summary>
        /// Gets or sets whether a direct path beyond the local network may be used, which shows the host this player's
        /// address. The player chooses it for this join only.
        /// </summary>
        internal bool AllowDirect { get; set; }

        /// <summary>
        /// Gets or sets the one server the player accepted after a typed host served a different server than last time,
        /// or one its record could not be read to check (<see cref="InternalLiveStatus.ChangedServerId"/>). Only that
        /// server is trusted there.
        /// </summary>
        internal string? TrustedServerId
        {
            get => trustedServerId;
            set
            {
                if (value != null && !InternalText.IsServerId(value)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(value));
                trustedServerId = value;
            }
        }
    }

    /// <summary>What a join target names.</summary>
    internal enum InternalJoinTargetKind
    {
        /// <summary>A session heard on the local network.</summary>
        Lan = 0,

        /// <summary>A host and port, typed or carried by a direct link.</summary>
        Direct = 1,

        /// <summary>An invite code, which the TopiaForge service resolves.</summary>
        Invite = 2,

        /// <summary>A server listed by the TopiaForge service.</summary>
        ListedServer = 3
    }

    /// <summary>
    /// Where a join goes, as an opaque handle the provider made. Whatever the player typed stays inside it: nothing here
    /// shows an address.
    /// </summary>
    internal interface IInternalJoinTarget
    {
        InternalJoinTargetKind Kind { get; }

        /// <summary>Gets what the interface may show for the target: "LAN session", "direct session" or an invite code.</summary>
        string Description { get; }

        /// <summary>Gets the server the target must be, when a link or listing named one; null for a typed host.</summary>
        string? ServerId { get; }
    }

    /// <summary>
    /// A session heard on the local network. Its name, build, gamemode and world are the announcing host's words: shown only
    /// as the host's, and never logged.
    /// </summary>
    internal sealed class InternalLanListing
    {
        internal InternalLanListing(IInternalJoinTarget target, string serverId)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            ServerId = serverId ?? throw new ArgumentNullException(nameof(serverId));
        }

        /// <summary>Gets the handle that joins or queries this session.</summary>
        internal IInternalJoinTarget Target { get; }

        internal string ServerId { get; }

        /// <summary>Gets whether the host has answered with its session info; until then only the server id is known.</summary>
        internal bool HasInfo { get; set; }

        /// <summary>Gets whether the host speaks another transport version, so it cannot be joined.</summary>
        internal bool Incompatible { get; set; }

        internal string Name { get; set; } = string.Empty;
        internal string GameBuild { get; set; } = string.Empty;
        internal string RuntimeVersion { get; set; } = string.Empty;
        internal string ProtocolVersion { get; set; } = string.Empty;
        internal string? Gamemode { get; set; }
        internal string? World { get; set; }
        internal int Players { get; set; }
        internal int MaxPlayers { get; set; }
        internal bool PasswordRequired { get; set; }
        internal bool Voice { get; set; }

        /// <summary>Gets the round trip of the host's last answer, rounded to 10 ms, or null before it answered.</summary>
        internal int? PingMilliseconds { get; set; }
    }

    /// <summary>One identity this host banned.</summary>
    internal sealed class InternalBanEntry
    {
        internal InternalBanEntry(string banId, string displayName, long bannedAtUnixSeconds)
        {
            BanId = banId ?? throw new ArgumentNullException(nameof(banId));
            DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            BannedAtUnixSeconds = bannedAtUnixSeconds;
        }

        /// <summary>Gets the opaque id <see cref="IInternalMultiplayerControl.Unban"/> takes. It is not the player's key.</summary>
        internal string BanId { get; }

        internal string DisplayName { get; }

        internal long BannedAtUnixSeconds { get; }
    }

    internal enum InternalHostPhase
    {
        Idle = 0,
        Preparing = 1,
        Hosting = 2,
        Failed = 3
    }

    internal enum InternalJoinPhase
    {
        /// <summary>No join has run since the last solo start.</summary>
        None = 0,
        Resolving = 1,
        Checking = 2,
        AwaitingPassword = 3,
        PreparingProof = 4,
        Connecting = 5,
        Synchronizing = 6,
        InSession = 7,
        Reconnecting = 8,
        Ended = 9
    }

    /// <summary>Why hosting or a join did not happen, or why a joined session ended. It never carries an address.</summary>
    internal enum InternalLiveFailure
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
        IdentityUnavailable = 19,
        NetworkStopped = 20,

        /// <summary>The TopiaForge service could not be reached, failed, or answered in a way this game cannot read; try again shortly.</summary>
        ServiceUnavailable = 21,

        /// <summary>
        /// This game's packages could not be read, as when another program holds a file of one open, so hosting or the join
        /// stopped before anything connected; trying again once the file is free can succeed.
        /// </summary>
        ContentUnreadable = 22,

        /// <summary>This game has no TopiaForge service configured, so only LAN and direct play exist; nothing was contacted.</summary>
        ServiceNotConfigured = 23,

        /// <summary>The service's operator turned hosting, invites or Internet joining off for now.</summary>
        ServiceDisabled = 24,

        /// <summary>The service is turning away requests from this network for a while; <see cref="InternalInternetStatus.RetryAfterSeconds"/> says how long.</summary>
        ServiceRateLimited = 25,

        /// <summary>The invite code does not work: mistyped, expired or withdrawn, or its session ended.</summary>
        InviteNotFound = 26,

        /// <summary>The server is not listed any more, or never was.</summary>
        ServerNotListed = 27,

        /// <summary>
        /// The relay is unavailable, and the join or host keeps to it: relay-only is honoured whatever it costs, so nothing
        /// fell back to a direct path. <see cref="InternalInternetStatus.HostAllowsDirect"/> says whether a direct connection could help.
        /// </summary>
        RelayUnavailable = 28,

        /// <summary>This network blocks the UDP traffic Internet play needs.</summary>
        UdpBlocked = 29,

        /// <summary>The host is not connected to the service.</summary>
        HostOffline = 30,

        /// <summary>The host never answered this player's offer.</summary>
        HostNotAnswering = 31,

        /// <summary>No path to the host worked, through the relay or directly.</summary>
        NoRoute = 32,

        /// <summary>The connection to the service's signaling was lost before the host was reached.</summary>
        SignalingFailed = 33,

        /// <summary>The host withdrew the invite this player joined with, or the server stopped being public.</summary>
        InviteWithdrawn = 34,

        /// <summary>The service's operator holds this server back for now.</summary>
        ServerBlocked = 35,

        /// <summary>The service refused a request this game made; the game may need an update.</summary>
        ServiceRefused = 36,

        /// <summary>
        /// The hosted session already holds as many live invites as the service allows, eight; withdrawing one, or waiting
        /// for one to expire, makes room.
        /// </summary>
        InviteLimit = 37,

        /// <summary>
        /// This game's record of the servers typed addresses served could not be read just now, as when another program
        /// holds it, so the server a typed host serves could not be checked; nothing was recorded or written over.
        /// Trying again once the record reads goes on, and so does trusting that one server
        /// (<see cref="InternalLiveStatus.ChangedServerId"/>) for the join while the record still cannot be read; once
        /// it reads, a typed host that served another server ends as <see cref="HostIdentityChanged"/> again.
        /// </summary>
        KnownHostsUnreadable = 38
    }

    /// <summary>The reason code of a host's admission report.</summary>
    internal enum InternalAdmissionCode
    {
        Other = 0,
        Full = 1,
        Protocol = 2,
        PasswordRequired = 3,
        PasswordWrong = 4,
        TooManyAttempts = 5,
        Banned = 6,
        Content = 7,

        /// <summary>The host's runtime could not decide yet, or failed to; the player may try again shortly.</summary>
        NotReady = 8
    }

    /// <summary>
    /// A host's verified description of its session. Verified means the host signed it, not that its words are safe: every
    /// text in it is host-authored and may hold anything, so it is shown only as the host's and never logged.
    /// </summary>
    internal sealed class InternalServerInfo
    {
        internal InternalServerInfo(string serverId, string name, IReadOnlyList<InternalHostPackage> packages)
        {
            ServerId = serverId ?? throw new ArgumentNullException(nameof(serverId));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Packages = packages ?? throw new ArgumentNullException(nameof(packages));
        }

        internal string ServerId { get; }
        internal string Name { get; }

        /// <summary>Gets the packages every player must have, or may use, sorted by id.</summary>
        internal IReadOnlyList<InternalHostPackage> Packages { get; }

        internal string GameBuild { get; set; } = string.Empty;
        internal string RuntimeVersion { get; set; } = string.Empty;
        internal string ProtocolVersion { get; set; } = string.Empty;
        internal int MaxPlayers { get; set; }
        internal bool PasswordRequired { get; set; }
        internal bool Voice { get; set; }
        internal InternalSessionVisibility Visibility { get; set; }
        internal string? Gamemode { get; set; }
        internal string? World { get; set; }

        /// <summary>Gets the SHA-256 of the package list, which a join plan pins.</summary>
        internal string PackageDigest { get; set; } = string.Empty;
    }

    /// <summary>
    /// Hosting, joining and browsing at one moment. This game's own texts never hold an address. Text the host wrote is
    /// kept apart, in <see cref="HostMessage"/>, in <see cref="ContentProblems"/> when
    /// <see cref="ContentProblemsFromHost"/> says so, and in <see cref="Server"/>: it may hold anything, an address
    /// included, so it is shown only as the host's words and never logged.
    /// </summary>
    internal sealed class InternalLiveStatus
    {
        internal InternalSessionRole Role { get; set; }
        internal bool IsLiveAvailable { get; set; }
        internal InternalHostPhase HostPhase { get; set; }
        internal InternalLiveFailure HostFailure { get; set; }
        internal InternalJoinPhase JoinPhase { get; set; }
        internal InternalLiveFailure JoinFailure { get; set; }

        /// <summary>Gets what went wrong with the join, for the player, in this game's own words; empty while nothing did.</summary>
        internal string JoinDetail { get; set; } = string.Empty;

        /// <summary>
        /// Gets what the host said when it removed, refused or stopped this player, such as a kick message; empty when it
        /// said nothing. Host-authored: show it only as the host's words, and never log it.
        /// </summary>
        internal string HostMessage { get; set; } = string.Empty;

        /// <summary>Gets the code of the host's admission report when it refused the join.</summary>
        internal InternalAdmissionCode RejectionCode { get; set; }

        /// <summary>Gets the packages that are missing or differ, from this game's check or the host's report.</summary>
        internal IReadOnlyList<string> ContentProblems { get; set; } = Array.Empty<string>();

        /// <summary>Gets whether <see cref="ContentProblems"/> came from the host's admission report, and so are host-authored.</summary>
        internal bool ContentProblemsFromHost { get; set; }

        /// <summary>
        /// Gets, when a typed host now serves a different server, or one that could not be checked because the record
        /// could not be read, that server's id. Joining again with it as <see cref="InternalJoinSettings.TrustedServerId"/>
        /// trusts exactly that server there.
        /// </summary>
        internal string? ChangedServerId { get; set; }

        /// <summary>Gets the joined or joining host's verified description, once resolved.</summary>
        internal InternalServerInfo? Server { get; set; }

        internal bool IsBrowsing { get; set; }

        /// <summary>Gets the players connected to this session, this player included.</summary>
        internal int ConnectedPlayers { get; set; }

        /// <summary>Gets the most players this session takes while hosting; zero otherwise.</summary>
        internal int MaxPlayers { get; set; }

        internal bool IsReadinessHeld { get; set; }

        /// <summary>Gets the session's standing with the TopiaForge service: its listing and invite as a host, its route as a joiner.</summary>
        internal InternalInternetStatus Internet { get; set; } = new InternalInternetStatus();
    }

    /// <summary>A lookup of one server without joining it. Main thread; it completes at a later pump.</summary>
    internal interface IInternalServerQuery : IDisposable
    {
        bool IsCompleted { get; }

        /// <summary>Gets why the lookup failed; <see cref="InternalLiveFailure.None"/> once it succeeded.</summary>
        InternalLiveFailure Failure { get; }

        /// <summary>Gets what went wrong, for the player; empty after success.</summary>
        string Detail { get; }

        /// <summary>Gets the server's verified description after success.</summary>
        InternalServerInfo? Server { get; }

        /// <summary>Gets what this game would need to change to join; empty when it fits.</summary>
        IReadOnlyList<string> ContentProblems { get; }

        /// <summary>Gets, when a typed host now serves a different server, or one that could not be checked, that server's id.</summary>
        string? ChangedServerId { get; }
    }

    /// <summary>The provider's counters for diagnostics. They name participants and channels, never addresses.</summary>
    internal sealed class InternalMultiplayerStatistics
    {
        internal InternalSessionRole Role { get; set; }
        internal int Participants { get; set; }
        internal int ConnectedPlayers { get; set; }
        internal int ReadinessHolds { get; set; }
        internal int ServerMutes { get; set; }
        internal IReadOnlyList<InternalSystemChannelStatistics> SystemChannels { get; set; } = Array.Empty<InternalSystemChannelStatistics>();

        /// <summary>Gets the voice counters, or null while voice is unavailable.</summary>
        internal InternalVoiceStatistics? Voice { get; set; }
    }

    /// <summary>Validation the SPI's own types share. The provider validates again with its wire rules.</summary>
    internal static class InternalText
    {
        /// <summary>1 to <paramref name="maximum"/> UTF-16 units, no surrounding whitespace, no control characters, no lone surrogates.</summary>
        internal static bool IsDisplayText(string? value, int maximum)
        {
            if (value == null || value.Length == 0 || value.Length > maximum) return false;
            if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1])) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsControl(character)) return false;
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return false;
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
            }

            return true;
        }

        internal static string? RequireOptionalDisplayText(string? value, int maximum, string name)
        {
            if (value != null && !IsDisplayText(value, maximum)) throw new ArgumentException("Display text is 1 to " + maximum + " printable characters.", name);
            return value;
        }

        /// <summary>A server id: 64 lowercase hexadecimal characters.</summary>
        internal static bool IsServerId(string? value) => IsLowerHex(value, 64);

        /// <summary>A SHA-256 digest: 64 lowercase hexadecimal characters.</summary>
        internal static bool IsSha256(string? value) => IsLowerHex(value, 64);

        private static bool IsLowerHex(string? value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (var character in value)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            }

            return true;
        }
    }
}
