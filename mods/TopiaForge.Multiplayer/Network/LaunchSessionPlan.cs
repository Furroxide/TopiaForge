using System;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>Why a launched session cannot be planned yet.</summary>
    internal enum LaunchSessionUnsupported
    {
        None = 0,

        /// <summary>Invites and Internet listings are found through the control plane, and the ticket named no service origin for it.</summary>
        NeedsService = 1,

        /// <summary>The ticket does not describe a host or a join this provider knows.</summary>
        Invalid = 2
    }

    /// <summary>
    /// A session the launcher asked this process to host or join (launch request V5), mapped onto live sessions: host
    /// settings, or a join target and settings. Building a plan starts nothing; the in-game flows decide when to call
    /// <see cref="LiveSessions.StartHosting"/> or <see cref="LiveSessions.StartJoin"/> with it.
    /// <list type="bullet">
    /// <item>A direct target is pinned by the server id the launcher verified, so the join checks the descriptor against it
    /// and never falls back to trust on first use. The join must also refuse a descriptor whose SHA-256 is not
    /// <see cref="ExpectedDescriptorSha256"/>, as admission changed.</item>
    /// <item>Passwords are typed in game: a host with <see cref="PasswordRequired"/> asks for one before it opens.</item>
    /// <item>An invite or an Internet listing is found through the TopiaForge service at <see cref="ServiceOrigin"/>, which the
    /// launcher names in the request; an invite stays pinned to the server the launcher verified.</item>
    /// <item>The plan names the target's address only inside the network layer; <see cref="ToString"/> never shows it.</item>
    /// </list>
    /// </summary>
    internal sealed class LaunchSessionPlan
    {
        private LaunchSessionPlan(LaunchSessionTicket ticket, HostSettings? host, JoinTarget? target, JoinSettings? join)
        {
            IntentId = ticket.IntentId;
            Host = host;
            Target = target;
            Join = join;
            PasswordRequired = ticket.Host?.PasswordRequired ?? false;
            DeveloperMode = ticket.DeveloperMode;
            // The runtime's reader already refuses served packages outside developer mode; this holds without it too.
            ServePackages = (ticket.Host?.ServePackages ?? false) && ticket.DeveloperMode;
            Upnp = ticket.Upnp;
            Lan = ticket.Lan;
            ExpectedDescriptorSha256 = ticket.Target?.DescriptorSha256;
            ExpectedPackageDigest = ticket.Target?.PackageDigest;
            DevInstanceIndex = ticket.DevInstanceIndex;
            ServiceOrigin = ticket.ServiceOrigin;
        }

        internal string IntentId { get; }
        internal HostSettings? Host { get; }
        internal JoinTarget? Target { get; }
        internal JoinSettings? Join { get; }
        internal bool PasswordRequired { get; }

        /// <summary>Gets whether the host serves its packages to joining players, which only developer mode allows.</summary>
        internal bool ServePackages { get; }
        internal bool DeveloperMode { get; }
        internal bool Upnp { get; }
        internal bool Lan { get; }
        internal string? ExpectedDescriptorSha256 { get; }
        internal string? ExpectedPackageDigest { get; }
        internal int DevInstanceIndex { get; }

        /// <summary>Gets the TopiaForge service's origin the launcher named for this session, or null when it named none.</summary>
        internal string? ServiceOrigin { get; }

        /// <summary>Maps <paramref name="ticket"/>; null with the reason when it cannot be planned yet.</summary>
        internal static LaunchSessionPlan? FromTicket(LaunchSessionTicket? ticket, out LaunchSessionUnsupported unsupported)
        {
            unsupported = LaunchSessionUnsupported.Invalid;
            if (ticket == null) return null;
            try
            {
                if (ticket.Role == "host" && ticket.Host != null)
                {
                    unsupported = LaunchSessionUnsupported.None;
                    return new LaunchSessionPlan(ticket, HostFor(ticket, ticket.Host), null, null);
                }

                var target = ticket.Target;
                if (ticket.Role != "join" || target == null) return null;
                JoinTarget joinTarget;
                switch (target.Kind)
                {
                    case "direct":
                        joinTarget = JoinTarget.Direct(target.Host!, target.Port, target.ServerId);
                        break;
                    case "listing" when target.Source == "lan":
                        joinTarget = JoinTarget.Lan(target.ServerId);
                        break;
                    case "invite" when ticket.ServiceOrigin != null:
                        joinTarget = JoinTarget.Invite(target.InviteCode!, target.ServerId);
                        break;
                    case "listing" when ticket.ServiceOrigin != null:
                        joinTarget = JoinTarget.Listed(target.ServerId);
                        break;
                    case "invite":
                    case "listing":
                        unsupported = LaunchSessionUnsupported.NeedsService;
                        return null;
                    default:
                        return null;
                }

                unsupported = LaunchSessionUnsupported.None;
                return new LaunchSessionPlan(ticket, null, joinTarget, new JoinSettings(ticket.DisplayName) { AllowDirect = ticket.AllowDirect });
            }
            catch (ArgumentException)
            {
                unsupported = LaunchSessionUnsupported.Invalid;
                return null;
            }
        }

        public override string ToString() => Host != null ? "launch plan (host)" : "launch plan (join " + Target + ")";

        private static HostSettings HostFor(LaunchSessionTicket ticket, LaunchSessionTicketHost host)
        {
            var visibility = host.Visibility switch
            {
                "public" => ServerVisibility.Public,
                "invite" => ServerVisibility.Invite,
                "lan" => ServerVisibility.Lan,
                _ => throw new ArgumentException("Unknown visibility.", nameof(host))
            };
            return new HostSettings(host.Name, host.MaxPlayers, visibility)
            {
                Port = ticket.Port,
                AllowDirect = ticket.AllowDirect,
                Voice = host.Voice
            };
        }
    }
}
