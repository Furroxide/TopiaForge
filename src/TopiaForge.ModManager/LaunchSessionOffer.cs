using System;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Hands a consumed launch request V5's session to the multiplayer provider: the runtime offers one ticket before mods
    /// load, and the provider takes it once. Starting the session from it belongs to the in-game flows; nothing here
    /// hosts, joins or contacts anyone, and nothing here logs the target.
    /// </summary>
    internal static class LaunchSessionOffer
    {
        internal static LaunchSessionTicket ToTicket(LaunchSessionRequest session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var target = session.Target;
            var host = session.Host;
            return new LaunchSessionTicket(session.Role, session.IntentId, session.DisplayName, session.Transport.AllowsDirect,
                session.Transport.Port, session.Transport.Upnp, session.Transport.Lan, session.ServiceOrigin,
                target == null ? null : new LaunchSessionTicketTarget(target.Kind, target.ServerId, target.DescriptorSha256,
                    target.PackageDigest, target.InviteCode, target.Source, target.Host, target.Port),
                host == null ? null : new LaunchSessionTicketHost(host.Name, host.MaxPlayers, host.Visibility, host.Voice,
                    host.PasswordRequired, host.ServePackages),
                session.DevInstance?.Index ?? -1, session.DevInstance?.Count ?? 0);
        }

        internal static void Offer(LaunchSessionRequest session) => LaunchSessionTickets.Offer(ToTicket(session));
    }
}
