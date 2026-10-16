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
                session.DevInstance?.Index ?? -1, session.DevInstance?.Count ?? 0, session.DeveloperMode);
        }

        /// <summary>Offers the session's ticket. Withdraw the offering once startup ends.</summary>
        internal static LaunchSessionOffering Offer(LaunchSessionRequest session)
        {
            var ticket = ToTicket(session);
            LaunchSessionTickets.Offer(ticket);
            return new LaunchSessionOffering(ticket);
        }
    }

    /// <summary>One offered ticket, withdrawn when the provider did not take it during startup.</summary>
    internal sealed class LaunchSessionOffering : IDisposable
    {
        private LaunchSessionTicket? ticket;

        internal LaunchSessionOffering(LaunchSessionTicket ticket) => this.ticket = ticket;

        /// <summary>Withdraws the ticket if the provider has not taken it; true when it had not.</summary>
        internal bool Withdraw()
        {
            var offered = ticket;
            ticket = null;
            return offered != null && LaunchSessionTickets.Withdraw(offered);
        }

        public void Dispose() => Withdraw();
    }
}
