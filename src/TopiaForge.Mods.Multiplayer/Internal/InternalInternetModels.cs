using System;

namespace TopiaForge.Mods.Internal
{
    /// <summary>Where a hosted session stands with the TopiaForge service.</summary>
    internal enum InternalListingPhase
    {
        /// <summary>Not with the service: not hosting, hosting on the LAN only, or no service configured.</summary>
        Off = 0,

        Registering = 1,

        /// <summary>Registered: an invite works, and a public server is listed.</summary>
        Online = 2,

        /// <summary>The service could not be reached or turned the host away for now; the host tries again by itself.</summary>
        Retrying = 3,

        /// <summary>The service refused this host; it tries again only when hosting starts again.</summary>
        Failed = 4
    }

    /// <summary>How a session's connection runs. Neither value names an address.</summary>
    internal enum InternalConnectionRoute
    {
        None = 0,

        /// <summary>Through the relay: neither side sees the other's address.</summary>
        Relayed = 1,

        /// <summary>Directly, which shows each side the other's address; only ever after the player allowed it for this session.</summary>
        Direct = 2
    }

    /// <summary>
    /// An invite to a hosted session: its canonical code, how people read it, its <c>topiaforge://join/</c> link and its
    /// expiry. Anyone holding the code may join, so the interface shows it to the host only, and never logs it.
    /// </summary>
    internal sealed class InternalInvite
    {
        internal InternalInvite(string code, string display, string link, long expiresAtUnixSeconds)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Display = display ?? throw new ArgumentNullException(nameof(display));
            Link = link ?? throw new ArgumentNullException(nameof(link));
            ExpiresAtUnixSeconds = expiresAtUnixSeconds;
        }

        internal string Code { get; }

        /// <summary>Gets the code as people read it, <c>XXXXX-XXXXX</c>.</summary>
        internal string Display { get; }

        internal string Link { get; }

        internal long ExpiresAtUnixSeconds { get; }

        /// <summary>Never prints the code.</summary>
        public override string ToString() => "invite";
    }

    /// <summary>
    /// A session's standing with the TopiaForge service at one moment. As a host: the listing's phase and failure, the
    /// current invite, whether the server is listed, the relay, and how many Internet players run on each route. As a
    /// joiner: the route and whether the host accepts direct connections. Nothing here names an address.
    /// </summary>
    internal sealed class InternalInternetStatus
    {
        /// <summary>Gets whether a TopiaForge service is configured. Without one, nothing is contacted and only LAN and direct play exist.</summary>
        internal bool ServiceConfigured { get; set; }

        internal InternalListingPhase ListingPhase { get; set; }

        /// <summary>Gets why the listing is retrying or failed; <see cref="InternalLiveFailure.None"/> otherwise.</summary>
        internal InternalLiveFailure ListingFailure { get; set; }

        /// <summary>Gets the invite players join with, or null while there is none.</summary>
        internal InternalInvite? Invite { get; set; }

        /// <summary>
        /// Gets why the host's last <see cref="IInternalMultiplayerControl.CreateInvite"/> or
        /// <see cref="IInternalMultiplayerControl.RevokeInvite"/> failed once the service answered, such as
        /// <see cref="InternalLiveFailure.InviteLimit"/>; none while a request is pending or after one that worked.
        /// </summary>
        internal InternalLiveFailure InviteFailure { get; set; }

        /// <summary>Gets whether the server is in the public browser: never for an invite-only session.</summary>
        internal bool Listed { get; set; }

        /// <summary>Gets whether this side holds a relay allocation now.</summary>
        internal bool RelayReady { get; set; }

        internal int RelayedPlayers { get; set; }

        internal int DirectPlayers { get; set; }

        /// <summary>Gets how long the service asked to wait, after a refusal that named a delay; zero otherwise.</summary>
        internal int RetryAfterSeconds { get; set; }

        /// <summary>Gets the joined session's route, once its connection is up.</summary>
        internal InternalConnectionRoute JoinRoute { get; set; }

        /// <summary>
        /// Gets whether the host accepts direct connections. When a relay-only join fails as
        /// <see cref="InternalLiveFailure.RelayUnavailable"/> and this is true, the interface may offer the approved "Allow a
        /// direct connection?" prompt; when it is false, a direct connection would not help either.
        /// </summary>
        internal bool HostAllowsDirect { get; set; }
    }
}
