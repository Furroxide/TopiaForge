using System;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>Why a launched session cannot be planned.</summary>
    internal enum LaunchSessionUnsupported
    {
        None = 0,

        /// <summary>Invites, Internet listings and public or invite-only hosting need the TopiaForge service, and none is configured.</summary>
        NeedsService = 1,

        /// <summary>The session does not describe a host or a join the network layer can read.</summary>
        Invalid = 2
    }

    /// <summary>
    /// A session the launcher asked this process to host or join (launch request V5, or a live join of a running game),
    /// mapped onto live sessions: host settings, or a join target and the launch's own privacy. Building a plan starts
    /// nothing; the runtime opens or joins it through the realm, as the in-game flows do.
    /// <list type="bullet">
    /// <item>A join's target is pinned by the server id the launcher verified and by the SHA-256 of the descriptor it planned
    /// from: a direct target checks the descriptor against the server and never falls back to trust on first use, and any
    /// host serving another descriptor ends the join as admission changed.</item>
    /// <item>Hosting with <see cref="Host"/>, and a join to <see cref="Target"/>, keep <see cref="AllowDirect"/>, the launch's
    /// own choice, whatever settings they start with: nothing turns direct play on that the player did not choose.</item>
    /// <item>A host's game port is the launch's: zero takes any free port, and any other is used exactly, so hosting fails
    /// rather than move to a port the launcher does not know.</item>
    /// <item>Passwords are typed in game: a host with <see cref="PasswordRequired"/> asks for one before it opens.</item>
    /// <item>The plan names the target's address only inside the network layer; <see cref="ToString"/> never shows it.</item>
    /// </list>
    /// </summary>
    internal sealed class LaunchSessionPlan : IInternalLaunchPlan
    {
        private LaunchSessionPlan(InternalLaunchSession session, InternalHostSettings? host, ProviderJoinTarget? target)
        {
            IntentId = session.IntentId;
            DisplayName = session.DisplayName;
            AllowDirect = session.AllowDirect;
            Host = host;
            Target = target;
            PasswordRequired = session.Host?.PasswordRequired ?? false;
            DeveloperMode = session.DeveloperMode;
            // The runtime's reader already refuses served packages outside developer mode; this holds without it too.
            ServePackages = (session.Host?.ServePackages ?? false) && session.DeveloperMode;
            Port = session.Port;
            RouterForwarding = session.RouterForwarding && session.AllowDirect;
        }

        public string IntentId { get; }

        public string DisplayName { get; }

        public bool AllowDirect { get; }

        public InternalHostSettings? Host { get; }

        public bool PasswordRequired { get; }

        IInternalJoinTarget? IInternalLaunchPlan.Target => Target;

        /// <summary>Gets the pinned join target; null for a host.</summary>
        internal ProviderJoinTarget? Target { get; }

        /// <summary>Gets the game port a host listens on: zero for any free one.</summary>
        internal int Port { get; }

        /// <summary>Gets whether a host asks its router to forward its port; never for a relay-only host.</summary>
        internal bool RouterForwarding { get; }

        /// <summary>Gets whether the host serves its packages to joining players, which only developer mode allows.</summary>
        internal bool ServePackages { get; }

        internal bool DeveloperMode { get; }

        /// <summary>Gets the descriptor hash a join is pinned to; null for a host.</summary>
        internal string? ExpectedDescriptorSha256 => Target?.ExpectedDescriptorSha256;

        /// <summary>Holds live host settings made from <see cref="Host"/> to the launch's game port and privacy.</summary>
        internal void ApplyTo(HostSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Port = Port;
            settings.FallBackToAnyPort = false;
            settings.AllowDirect = AllowDirect;
            settings.MapPorts = RouterForwarding;
            settings.DeveloperMode = DeveloperMode;
            settings.ServePackages = ServePackages;
        }

        /// <summary>
        /// Plans <paramref name="session"/>; null with the reason when it cannot be. <paramref name="hasService"/> says whether
        /// a TopiaForge service is configured, which invites, Internet listings and public or invite-only hosting need.
        /// </summary>
        internal static LaunchSessionPlan? From(InternalLaunchSession? session, bool hasService, out LaunchSessionUnsupported unsupported)
        {
            unsupported = LaunchSessionUnsupported.Invalid;
            if (session == null) return null;
            try
            {
                var host = session.Host;
                if (host != null)
                {
                    if (host.Visibility != InternalSessionVisibility.Lan && !hasService)
                    {
                        unsupported = LaunchSessionUnsupported.NeedsService;
                        return null;
                    }

                    var settings = new InternalHostSettings(host.Name, host.MaxPlayers, host.Visibility)
                    {
                        AllowDirect = session.AllowDirect,
                        RouterForwarding = session.RouterForwarding && session.AllowDirect,
                        Voice = host.Voice
                    };
                    unsupported = LaunchSessionUnsupported.None;
                    return new LaunchSessionPlan(session, settings, null);
                }

                var target = session.Target!;
                JoinTarget network;
                switch (target.Kind)
                {
                    case InternalJoinTargetKind.Direct:
                        network = JoinTarget.Direct(target.Host!, target.Port, target.ServerId);
                        break;
                    case InternalJoinTargetKind.Lan:
                        network = JoinTarget.Lan(target.ServerId);
                        break;
                    case InternalJoinTargetKind.Invite when hasService:
                        network = JoinTarget.Invite(target.InviteCode!, target.ServerId);
                        break;
                    case InternalJoinTargetKind.ListedServer when hasService:
                        network = JoinTarget.Listed(target.ServerId);
                        break;
                    case InternalJoinTargetKind.Invite:
                    case InternalJoinTargetKind.ListedServer:
                        unsupported = LaunchSessionUnsupported.NeedsService;
                        return null;
                    default:
                        return null;
                }

                unsupported = LaunchSessionUnsupported.None;
                return new LaunchSessionPlan(session, null,
                    ProviderJoinTarget.ForLaunch(target.Kind, network, target.ServerId, target.DescriptorSha256, session.AllowDirect));
            }
            catch (ArgumentException)
            {
                unsupported = LaunchSessionUnsupported.Invalid;
                return null;
            }
        }

        public override string ToString() => Host != null ? "launch plan (host)" : "launch plan (join " + Target + ")";
    }
}
