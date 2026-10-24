using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Turns a launch's session, from launch request V5 or from a live join the launcher staged, into the internal launch
    /// session the multiplayer provider plans (<see cref="IInternalMultiplayerControl.PlanLaunchSession"/>). Direct play is
    /// allowed only with the player's consent for this very session, given under the address disclosure this game shows
    /// now; any other launch is relay-only, whatever this game's own settings say. Nothing here logs the target.
    /// </summary>
    internal static class LaunchSessionOffer
    {
        /// <summary>Gets whether <paramref name="session"/> may use direct paths: only with a consent for itself, under the current disclosure.</summary>
        internal static bool AllowsDirect(LaunchSessionRequest session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var consent = session.Transport.DirectConsent;
            return session.Transport.AllowsDirect && consent != null && string.Equals(consent.IntentId, session.IntentId, StringComparison.Ordinal)
                && consent.Disclosure >= MultiplayerPrivacyText.DisclosureVersion;
        }

        internal static InternalLaunchSession ToSession(LaunchSessionRequest session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var host = session.Host;
            var target = session.Target;
            return new InternalLaunchSession(session.IntentId, session.DisplayName, AllowsDirect(session),
                host == null ? null : new InternalLaunchHost(host.Name, host.MaxPlayers, VisibilityOf(host.Visibility), host.Voice,
                    host.PasswordRequired, host.ServePackages),
                target == null ? null : TargetOf(target))
            {
                Port = session.Transport.Port,
                RouterForwarding = session.Transport.Upnp,
                DeveloperMode = session.DeveloperMode
            };
        }

        private static InternalLaunchTarget TargetOf(LaunchSessionTarget target)
        {
            switch (target.Kind)
            {
                case "invite":
                    return InternalLaunchTarget.Invite(target.InviteCode!, target.ServerId, target.DescriptorSha256);
                case "listing":
                    return InternalLaunchTarget.Listing(target.ServerId, target.DescriptorSha256, lan: target.Source == "lan");
                default:
                    return InternalLaunchTarget.Direct(target.Host!, target.Port, target.ServerId, target.DescriptorSha256);
            }
        }

        private static InternalSessionVisibility VisibilityOf(string visibility) =>
            visibility == "public" ? InternalSessionVisibility.Public
            : visibility == "invite" ? InternalSessionVisibility.Invite
            : InternalSessionVisibility.Lan;
    }
}
