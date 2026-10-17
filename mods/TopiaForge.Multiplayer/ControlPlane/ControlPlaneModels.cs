using System;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>What the service offers right now (<c>GET /v1/status</c>); clients degrade from it before they host or join.</summary>
    internal sealed class ServiceStatus
    {
        internal ServiceStatus(bool hosting, bool invites, bool relay, bool signaling, bool killSwitch, int heartbeatSeconds, int listingTtlSeconds, int credentialTtlSeconds)
        {
            Hosting = hosting;
            Invites = invites;
            Relay = relay;
            Signaling = signaling;
            KillSwitch = killSwitch;
            HeartbeatSeconds = heartbeatSeconds;
            ListingTtlSeconds = listingTtlSeconds;
            CredentialTtlSeconds = credentialTtlSeconds;
        }

        internal bool Hosting { get; }

        internal bool Invites { get; }

        /// <summary>Gets the relay feature as the service reports it, which it already turns off while the kill switch is engaged.</summary>
        internal bool Relay { get; }

        internal bool Signaling { get; }

        internal bool KillSwitch { get; }

        /// <summary>Gets whether a relay credential can be had now: the relay is on and the kill switch released.</summary>
        internal bool RelayAvailable => Relay && !KillSwitch;

        internal int HeartbeatSeconds { get; }

        internal int ListingTtlSeconds { get; }

        internal int CredentialTtlSeconds { get; }

        public override string ToString() => "service status";
    }

    /// <summary>A registration challenge: opaque, bound to the signing key, valid for two minutes and accepted once.</summary>
    internal sealed class HostChallenge
    {
        internal HostChallenge(string challenge, long expiresAt)
        {
            Challenge = challenge;
            ExpiresAt = expiresAt;
        }

        internal string Challenge { get; }

        internal long ExpiresAt { get; }

        public override string ToString() => "host challenge";
    }

    /// <summary>One invite to a hosted session: its canonical code, how people see it, its link and its expiry.</summary>
    internal sealed class ServiceInvite
    {
        internal ServiceInvite(string code, long expiresAt)
        {
            Code = code;
            ExpiresAt = expiresAt;
        }

        /// <summary>Gets the canonical code: ten Crockford base32 characters, upper case.</summary>
        internal string Code { get; }

        /// <summary>Gets the code as people read it, <c>XXXXX-XXXXX</c>.</summary>
        internal string Display => InviteCode.Display(Code);

        /// <summary>Gets the code's <c>topiaforge://join/</c> link.</summary>
        internal string Link => SessionLink.Scheme + "://join/" + Code;

        internal long ExpiresAt { get; }

        /// <summary>Never prints the code: anyone holding it may join.</summary>
        public override string ToString() => "invite";
    }

    /// <summary>A host's registration: its session token, its listing's lifetime and the session's first invite.</summary>
    internal sealed class HostRegistration
    {
        internal HostRegistration(string serverId, string sessionToken, string descriptorSha256, long expiresAt, int heartbeatSeconds, int listingTtlSeconds, bool listed, ServiceInvite invite)
        {
            ServerId = serverId;
            SessionToken = sessionToken;
            DescriptorSha256 = descriptorSha256;
            ExpiresAt = expiresAt;
            HeartbeatSeconds = heartbeatSeconds;
            ListingTtlSeconds = listingTtlSeconds;
            Listed = listed;
            Invite = invite;
        }

        internal string ServerId { get; }

        /// <summary>Gets the host session token. It travels only in an Authorization header, and nothing prints it.</summary>
        internal string SessionToken { get; }

        internal string DescriptorSha256 { get; }

        internal long ExpiresAt { get; }

        internal int HeartbeatSeconds { get; }

        internal int ListingTtlSeconds { get; }

        /// <summary>Gets whether a public server made it into the directory; an invite-only one never does.</summary>
        internal bool Listed { get; }

        internal ServiceInvite Invite { get; }

        public override string ToString() => "host registration";
    }

    /// <summary>What a heartbeat or a descriptor update renews: the listing's expiry and whether the server is listed.</summary>
    internal sealed class HostRenewal
    {
        internal HostRenewal(long expiresAt, bool listed, int? heartbeatSeconds, string? descriptorSha256)
        {
            ExpiresAt = expiresAt;
            Listed = listed;
            HeartbeatSeconds = heartbeatSeconds;
            DescriptorSha256 = descriptorSha256;
        }

        internal long ExpiresAt { get; }

        internal bool Listed { get; }

        /// <summary>Gets the heartbeat interval a heartbeat's answer names; null for a descriptor update.</summary>
        internal int? HeartbeatSeconds { get; }

        /// <summary>Gets the hash a descriptor update's answer names; null for a heartbeat.</summary>
        internal string? DescriptorSha256 { get; }

        public override string ToString() => "host renewal";
    }

    /// <summary>
    /// A server found through the service, by invite or by id: its signed descriptor, verified against its server id
    /// before anything here exists, and the listing fields a joiner needs. The listing is the host's own words beside the
    /// service's; only its privacy flag and player count are kept.
    /// </summary>
    internal sealed class ServiceServer
    {
        internal ServiceServer(string serverId, SignedServerDescriptor descriptor, bool hostRelayOnly, int players)
        {
            ServerId = serverId;
            Descriptor = descriptor;
            HostRelayOnly = hostRelayOnly;
            Players = players;
        }

        internal string ServerId { get; }

        internal SignedServerDescriptor Descriptor { get; }

        /// <summary>Gets whether the host chose relay-only: then no path to it is ever direct.</summary>
        internal bool HostRelayOnly { get; }

        internal int Players { get; }

        public override string ToString() => "service server";
    }

    /// <summary>A join ticket: the joiner's credential for signaling and relay, carrying its privacy choice under the service's MAC.</summary>
    internal sealed class ServiceJoinTicket
    {
        internal ServiceJoinTicket(string ticket, string serverId, long expiresAt, bool relayOnly, bool hostRelayOnly)
        {
            Ticket = ticket;
            ServerId = serverId;
            ExpiresAt = expiresAt;
            RelayOnly = relayOnly;
            HostRelayOnly = hostRelayOnly;
        }

        /// <summary>Gets the ticket itself. It travels only in an Authorization header, and nothing prints it.</summary>
        internal string Ticket { get; }

        internal string ServerId { get; }

        internal long ExpiresAt { get; }

        internal bool RelayOnly { get; }

        internal bool HostRelayOnly { get; }

        public override string ToString() => "join ticket";
    }

    /// <summary>Relay credentials in the <c>iceServers</c> shape, and when they stop working.</summary>
    internal sealed class RelayCredentials
    {
        internal RelayCredentials(IceServerSet servers, long expiresAt)
        {
            Servers = servers;
            ExpiresAt = expiresAt;
        }

        internal IceServerSet Servers { get; }

        internal long ExpiresAt { get; }

        public override string ToString() => "relay credentials";
    }
}
